using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using StackExchange.Redis;
using Meilisearch;

namespace TruyenDex.Api;

public record ChapterCard(Guid Id, Guid MangaId, string Title, decimal Number, string Language, DateTime PublishedAt, string Group = "");
public class MangaCard
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public string AlternativeTitle { get; set; } = "";
    public string Author { get; set; } = "";
    public string Cover { get; set; } = "";
    public string Description { get; set; } = "";
    public string[] Genres { get; set; } = [];
    public string Status { get; set; } = "";
    public string Country { get; set; } = "";
    public string Demographic { get; set; } = "";
    public string ContentRating { get; set; } = "safe";
    public int? Year { get; set; }
    public double Rating { get; set; }
    public int Follows { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<ChapterCard> Chapters { get; set; } = [];
}
public record CatalogPage(List<MangaCard> Items, int Total, int Page, int PageSize);
public record ChapterPage(List<ChapterCard> Items, int Total, int Page, int PageSize);
public record ReaderData(ChapterCard Chapter, MangaCard Manga, string[] Pages, string[] DataSaverPages, string? ExternalUrl, List<ChapterCard> Navigation);
public class UpstreamException(string message, int status = 502) : Exception(message) { public int Status { get; } = status; }

// Read-only adapter. Integrates MangaDex/TruyenDex and TruyenGGVN with title deduplication.
public class Catalog(HttpClient http, IMemoryCache cache, TruyenGg truyengg, IConnectionMultiplexer? redis = null, MeilisearchClient? meili = null)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly string[] SiteOrigins = ["https://api.truyendex.cc", "https://api.truyendex.xyz"];
    private static readonly string[] Origins = ["https://api-proxy.truyendex.cc/mangadex", "https://api-proxy.truyendex.xyz/mangadex", "https://api.mangadex.org"];
    private static string S(JsonNode? n) => n?.ToString() ?? "";
    private static string E(string s) => Uri.EscapeDataString(s);
    private static DateTime Date(JsonNode? n) => DateTime.TryParse(S(n), out var d) ? d.ToUniversalTime() : DateTime.UtcNow;
    private static string Localized(JsonNode? n) => S(n?["vi"] ?? n?["en"] ?? (n as JsonObject)?.FirstOrDefault().Value);

    private async Task<T?> CacheGet<T>(string key) where T : class
    {
        if (redis != null && redis.IsConnected)
        {
            try {
                var v = await redis.GetDatabase().StringGetAsync(key);
                if (v.HasValue)
                    return typeof(T) == typeof(string) ? (T)(object)v.ToString() : JsonSerializer.Deserialize<T>(v.ToString());
            } catch { }
        }
        return cache.TryGetValue<T>(key, out var cached) ? cached : null;
    }

    private async Task CacheSet<T>(string key, T value, TimeSpan expiry) where T : class
    {
        if (redis != null && redis.IsConnected)
        {
            try {
                var json = typeof(T) == typeof(string) ? (string)(object)value : JsonSerializer.Serialize(value);
                await redis.GetDatabase().StringSetAsync(key, json, expiry);
            } catch { }
        }
        cache.Set(key, value, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = expiry, Size = 1 });
    }

    public async Task<JsonNode> Get(string path, bool site = false)
    {
        var key = $"upstream:{site}:{path}";
        var cached = await CacheGet<string>(key);
        if (cached != null) return JsonNode.Parse(cached)!;
        await Gate.WaitAsync();
        try
        {
            cached = await CacheGet<string>(key);
            if (cached != null) return JsonNode.Parse(cached)!;
            var origins = site ? SiteOrigins : Origins;
            foreach (var origin in origins)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(7));
                    using var response = await http.GetAsync(origin + path, cts.Token);
                    if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                        throw new UpstreamException("Không tìm thấy truyện hoặc chương ở nguồn.", 404);
                    if (!response.IsSuccessStatusCode) continue;
                    var json = await response.Content.ReadAsStringAsync(cts.Token);
                    var node = JsonNode.Parse(json) ?? throw new JsonException();
                    var expiry = path.Contains("/homepage") ? TimeSpan.FromMinutes(15)
                        : path.Contains("/manga/tag") ? TimeSpan.FromHours(2)
                        : path.Contains("/aggregate") ? TimeSpan.FromMinutes(30)
                        : path.Contains("/statistics/") ? TimeSpan.FromMinutes(15)
                        : TimeSpan.FromMinutes(10);
                    await CacheSet(key, json, expiry);
                    return node;
                }
                catch (UpstreamException) { throw; }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException) { }
            }
            throw new UpstreamException("Nguồn truyện đang tạm thời không phản hồi. Vui lòng thử lại sau.");
        }
        finally { await Task.Delay(200); Gate.Release(); }
    }

    private MangaCard Map(JsonNode n, bool isThumbnail = true)
    {
        var a = n["attributes"]!;
        var rel = n["relationships"]!.AsArray();
        var id = Guid.Parse(S(n["id"]));

        var altTitlesList = a["altTitles"]?.AsArray()
            .SelectMany(x => (x as JsonObject)?.Select(kv => S(kv.Value)) ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct()
            .ToList() ?? [];

        var primaryTitle = Localized(a["title"]);
        var viTitle = a["altTitles"]?.AsArray().Select(x => x?["vi"]).FirstOrDefault(x => x != null);
        var displayTitle = viTitle != null ? S(viTitle) : (string.IsNullOrWhiteSpace(primaryTitle) ? altTitlesList.FirstOrDefault() ?? "" : primaryTitle);
        var altTitleDisplay = string.Join(" / ", new[] { primaryTitle }.Concat(altTitlesList).Where(s => !string.IsNullOrWhiteSpace(s) && s != displayTitle).Distinct());

        var file = S(rel.FirstOrDefault(x => S(x?["type"]) == "cover_art")?["attributes"]?["fileName"]);
        var sizeExt = isThumbnail ? ".256.jpg" : ".512.jpg";
        var coverUrl = $"https://mangadex.org/covers/{id}/{file}{sizeExt}";
        return new MangaCard {
            Id = id,
            Title = displayTitle,
            AlternativeTitle = altTitleDisplay,
            Author = string.Join(" / ", rel.Where(x => S(x?["type"]) == "author").Select(x => S(x?["attributes"]?["name"]))),
            Cover = file.Length > 0 ? "https://services.f-ck.me/v1/image/" + Convert.ToBase64String(Encoding.UTF8.GetBytes(coverUrl)).Replace('+', '-').Replace('/', '_') : "/cover-placeholder.svg",
            Description = Localized(a["description"]),
            Status = S(a["status"]),
            Country = S(a["originalLanguage"]),
            Demographic = S(a["publicationDemographic"]),
            ContentRating = S(a["contentRating"]),
            Year = (int?)a["year"],
            Genres = a["tags"]!.AsArray().Select(x => Localized(x?["attributes"]?["name"])).ToArray(),
            UpdatedAt = Date(a["updatedAt"])
        };
    }

    private async Task Stats(List<MangaCard> items)
    {
        if (items.Count == 0) return;
        try {
            var stats = await Get("/statistics/manga?" + string.Join("&", items.Select(x => "manga[]=" + x.Id)));
            foreach (var m in items) {
                var s = stats["statistics"]?[m.Id.ToString()];
                m.Rating = (double?)(s?["rating"]?["bayesian"]) ?? 0;
                m.Follows = (int?)s?["follows"] ?? 0;
            }
        } catch (UpstreamException) { /* Statistics must not block reading. */ }
    }

    public static bool IsManhwaOrManhua(MangaCard m)
    {
        if (m == null) return false;
        if (string.Equals(m.Country, "ko", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(m.Country, "zh", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(m.Country, "zh-hk", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(m.Country, "zh-ro", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (m.Genres != null && m.Genres.Length > 0)
        {
            foreach (var g in m.Genres)
            {
                if (string.IsNullOrWhiteSpace(g)) continue;
                var tag = g.Trim();
                if (tag.Equals("Manhwa", StringComparison.OrdinalIgnoreCase) ||
                    tag.Equals("Manhua", StringComparison.OrdinalIgnoreCase) ||
                    tag.Equals("Truyện Hàn Quốc", StringComparison.OrdinalIgnoreCase) ||
                    tag.Equals("Truyện Trung Quốc", StringComparison.OrdinalIgnoreCase) ||
                    tag.Equals("Truyen Han Quoc", StringComparison.OrdinalIgnoreCase) ||
                    tag.Equals("Truyen Trung Quoc", StringComparison.OrdinalIgnoreCase) ||
                    tag.Equals("Korean", StringComparison.OrdinalIgnoreCase) ||
                    tag.Equals("Chinese", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static readonly Regex Chapter1Regex = new(
        @"(?:^|[^\w\d])(?:chương|chapter|chap|ch|c)[\s\._-]*0*1(?:\.0+|\.5)?(?=[^\d]|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex OneshotRegex = new(
        @"(?:oneshot|one-shot|one\s+shot|truyện\s+ngắn)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static bool HasChapter1(MangaCard? m)
    {
        if (m == null || m.Chapters == null || m.Chapters.Count == 0) return false;
        foreach (var c in m.Chapters)
        {
            if (c == null) continue;
            if (c.Number >= 0.5m && c.Number <= 1.5m) return true;
            if (string.IsNullOrWhiteSpace(c.Title)) continue;
            var t = c.Title.Trim();
            if (t.Equals("Oneshot", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("One-shot", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("Chương 1", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("Chapter 1", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("Chap 1", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("C1", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (Chapter1Regex.IsMatch(t) || OneshotRegex.IsMatch(t))
            {
                var numMatch = Regex.Match(t, @"(?:chương|chapter|chap|ch|c)\s*(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
                if (numMatch.Success && decimal.TryParse(numMatch.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var n))
                {
                    if (n >= 0.5m && n <= 1.5m) return true;
                    if (n > 1.5m) continue;
                }
                return true;
            }
        }
        return false;
    }

    public static bool ShouldPreferCandidate(MangaCard candidate, MangaCard existing)
    {
        bool candHasCh1 = HasChapter1(candidate);
        bool existHasCh1 = HasChapter1(existing);
        if (candHasCh1 && !existHasCh1) return true;
        if (!candHasCh1 && existHasCh1) return false;

        int candChaps = candidate.Chapters?.Count ?? 0;
        int existChaps = existing.Chapters?.Count ?? 0;
        if (candChaps > 0 && existChaps == 0) return true;
        if (existChaps > 0 && candChaps == 0) return false;

        if (candChaps > 0 && existChaps > 0)
        {
            var candMin = candidate.Chapters!.Min(c => c.Number > 0 ? c.Number : 99999m);
            var existMin = existing.Chapters!.Min(c => c.Number > 0 ? c.Number : 99999m);
            if (candMin < existMin) return true;
        }

        return false;
    }

    public static List<ChapterCard> DeduplicateChapters(IEnumerable<ChapterCard> chapters, bool ascending = false)
    {
        if (chapters == null) return [];

        var grouped = chapters
            .GroupBy(c => c.Number > 0 ? (object)c.Number : (object)c.Title.Trim().ToLowerInvariant())
            .Select(g => g
                .OrderByDescending(c => !string.IsNullOrWhiteSpace(c.Group))
                .ThenByDescending(c => c.Title.Contains(" · "))
                .ThenByDescending(c => c.PublishedAt)
                .First()
            );

        if (ascending)
        {
            return grouped.OrderBy(c => c.Number).ThenBy(c => c.PublishedAt).ToList();
        }
        else
        {
            return grouped.OrderByDescending(c => c.Number).ThenByDescending(c => c.PublishedAt).ToList();
        }
    }

    private async Task EnsureTopChapters(List<MangaCard> items, int targetCount = 3)
    {
        if (items.Count == 0) return;
        var sem = new SemaphoreSlim(8);
        var tasks = items.Where(m => m.Chapters.Count < targetCount).Select(async m =>
        {
            await sem.WaitAsync();
            try
            {
                var slug = await truyengg.ResolveSlug(m.Id);
                if (!string.IsNullOrEmpty(slug))
                {
                    var chaps = await truyengg.GetChapters(m.Id, 1, targetCount, ascending: false);
                    if (chaps != null && chaps.Items.Count > 0)
                    {
                        m.Chapters = chaps.Items.Take(targetCount).ToList();
                    }
                }
                else
                {
                    var chaps = await Chapters(m.Id, "vi", 1, ascending: false);
                    if (chaps != null && chaps.Items.Count > 0)
                    {
                        m.Chapters = chaps.Items.Take(targetCount).ToList();
                    }
                    else
                    {
                        var enChaps = await Chapters(m.Id, "en", 1, ascending: false);
                        if (enChaps != null && enChaps.Items.Count > 0)
                        {
                            m.Chapters = enChaps.Items.Take(targetCount).ToList();
                        }
                    }
                }
            }
            catch { }
            finally
            {
                sem.Release();
            }
        });

        await Task.WhenAll(tasks);
    }

    public async Task<CatalogPage> Home(int page, int size)
    {
        var cacheKey = $"catalog:home:v12:{page}:{size}";
        var cachedPage = await CacheGet<CatalogPage>(cacheKey);
        if (cachedPage != null) return cachedPage;

        size = 28;
        var mangaDexItems = new List<MangaCard>();
        int total = 0;

        try
        {
            var home = await Get($"/api/series/homepage?page={page}&limit={size}", true);
            var rows = home["data"]?.AsArray();
            if (rows != null && rows.Count > 0)
            {
                var ids = rows.Select(x => S(x?["uuid"])).Where(x => !string.IsNullOrEmpty(x)).ToArray();
                var response = await Get("/manga?limit=100&includes[]=cover_art&includes[]=author&" + string.Join("&", ids.Select(x => "ids[]=" + x)));
                var map = response["data"]!.AsArray().Select(x => Map(x!, isThumbnail: true)).ToDictionary(x => x.Id.ToString());
                foreach (var row in rows) {
                    if (!map.TryGetValue(S(row?["uuid"]), out var m)) continue;
                    if (IsManhwaOrManhua(m)) continue;
                    // Preserve homepage order: last chapter update descending, not manga metadata update.
                    m.UpdatedAt = Date(row?["last_chapter_updated_at"]);
                    var rawChaps = row?["chapters"]?.AsArray().Select(c => {
                        var chapTitle = S(c?["title"]);
                        var numMatch = Regex.Match(chapTitle, @"(?:\b|[^\w\d])(?:chương|chapter|chap|ch|c)?[\s\._-]*(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
                        decimal num = 0;
                        if (numMatch.Success)
                        {
                            decimal.TryParse(numMatch.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out num);
                        }
                        return new ChapterCard(Guid.Parse(S(c?["uuid"])), m.Id, chapTitle, num, "vi", Date(c?["md_updated_at"]));
                    }).ToList() ?? [];
                    m.Chapters = DeduplicateChapters(rawChaps, ascending: false).Take(3).ToList();
                    mangaDexItems.Add(m);
                }
                await Stats(mangaDexItems);
                total = (int?)home["total"] ?? mangaDexItems.Count;
            }
        }
        catch (Exception)
        {
            // Fallback to MangaDex latest uploaded chapters feed if TruyenDex custom homepage endpoint is temporarily unavailable
            try
            {
                var fallback = await Search(page, size, null, null, null, "ja", null, "vi", "latest", null);
                if (fallback.Items.Count > 0)
                {
                    mangaDexItems = fallback.Items.Where(m => !IsManhwaOrManhua(m)).ToList();
                    total = fallback.Total;
                }
            }
            catch { }
        }

        // Fetch latest items from TruyenGGVN (country=4 Manga)
        List<MangaCard> ggItems = [];
        try
        {
            var rawGg = await truyengg.GetLatest(page);
            ggItems = rawGg.Where(m => !IsManhwaOrManhua(m)).ToList();
        }
        catch { }

        // Deduplication: if title or alternative title matches any existing item, prioritize the one with Chapter 1
        var merged = new List<MangaCard>();
        foreach (var m in mangaDexItems)
        {
            bool isDuplicate = false;
            for (int i = 0; i < merged.Count; i++)
            {
                var existing = merged[i];
                if (TruyenGg.IsSameManga(existing.Title, existing.AlternativeTitle, m.Title, m.AlternativeTitle))
                {
                    isDuplicate = true;
                    // Prioritize story with chapter 1
                    if (ShouldPreferCandidate(m, existing))
                    {
                        merged[i] = m;
                    }
                    break;
                }
            }
            if (!isDuplicate)
            {
                merged.Add(m);
            }
        }

        foreach (var gg in ggItems)
        {
            bool isDuplicate = false;
            for (int i = 0; i < merged.Count; i++)
            {
                var existing = merged[i];
                if (TruyenGg.IsSameManga(existing.Title, existing.AlternativeTitle, gg.Title, gg.AlternativeTitle))
                {
                    isDuplicate = true;
                    // Prioritize story with chapter 1
                    if (ShouldPreferCandidate(gg, existing))
                    {
                        merged[i] = gg;
                    }
                    break;
                }
            }
            if (!isDuplicate)
            {
                merged.Add(gg);
            }
        }

        // Exclude any remaining Manhwa/Manhua items, sort by newest chapter update descending, and take page size
        merged = merged.Where(m => !IsManhwaOrManhua(m)).OrderByDescending(x => x.UpdatedAt).Take(size).ToList();
        await EnsureTopChapters(merged, 3);

        var result = new CatalogPage(merged, total + ggItems.Count, page, size);
        await CacheSet(cacheKey, result, TimeSpan.FromMinutes(5));
        return result;
    }

    public async Task<CatalogPage> Featured(int size = 20)
    {
        var cacheKey = $"catalog:featured:manhwa_manhua:v3:{size}";
        var cached = await CacheGet<CatalogPage>(cacheKey);
        if (cached != null) return cached;

        // 1. Fetch from TruyenGG (Korean Manhwa & Chinese Manhua)
        List<MangaCard> ggItems = [];
        try
        {
            ggItems = await truyengg.GetLatestManhwaManhua(1);
        }
        catch { }

        // 2. Fetch from MangaDex (ko & zh) with Vietnamese translation
        List<MangaCard> mdItems = [];
        try
        {
            var path = $"/manga?limit={size}&includes[]=cover_art&includes[]=author&contentRating[]=safe&contentRating[]=suggestive&contentRating[]=erotica&order[latestUploadedChapter]=desc&originalLanguage[]=ko&originalLanguage[]=zh&availableTranslatedLanguage[]=vi";
            var result = await Get(path);
            var rawItems = result["data"]!.AsArray().Select(x => Map(x!, isThumbnail: true)).ToList();
            await Stats(rawItems);
            mdItems = rawItems;
        }
        catch { }

        // 3. Merge & Deduplicate
        var merged = new List<MangaCard>();
        foreach (var m in mdItems)
        {
            if (!IsManhwaOrManhua(m)) continue;
            bool isDuplicate = false;
            for (int i = 0; i < merged.Count; i++)
            {
                var existing = merged[i];
                if (TruyenGg.IsSameManga(existing.Title, existing.AlternativeTitle, m.Title, m.AlternativeTitle))
                {
                    isDuplicate = true;
                    if (ShouldPreferCandidate(m, existing))
                    {
                        merged[i] = m;
                    }
                    break;
                }
            }
            if (!isDuplicate)
            {
                merged.Add(m);
            }
        }

        foreach (var gg in ggItems)
        {
            if (!IsManhwaOrManhua(gg)) continue;
            bool isDuplicate = false;
            for (int i = 0; i < merged.Count; i++)
            {
                var existing = merged[i];
                if (TruyenGg.IsSameManga(existing.Title, existing.AlternativeTitle, gg.Title, gg.AlternativeTitle))
                {
                    isDuplicate = true;
                    if (ShouldPreferCandidate(gg, existing))
                    {
                        merged[i] = gg;
                    }
                    break;
                }
            }
            if (!isDuplicate)
            {
                merged.Add(gg);
            }
        }

        var finalItems = merged.OrderByDescending(x => x.UpdatedAt).Take(size).ToList();
        await EnsureTopChapters(finalItems, 3);
        var res = new CatalogPage(finalItems, finalItems.Count, 1, size);
        await CacheSet(cacheKey, res, TimeSpan.FromMinutes(5));
        return res;
    }

    public async Task<CatalogPage> Search(int page, int size, string? q, string? genre, string? status, string? country, string? demographic, string? language, string? sort, int? year)
    {
        var cacheKey = $"catalog:search:v7:{page}:{size}:{q}:{genre}:{status}:{country}:{demographic}:{language}:{sort}:{year}";
        var cachedSearch = await CacheGet<CatalogPage>(cacheKey);
        if (cachedSearch != null) return cachedSearch;

        if (!string.IsNullOrWhiteSpace(q) && meili != null)
        {
            try {
                var index = meili.Index("mangas");
                var meiliHits = await index.SearchAsync<MangaCard>(q.Trim(), new SearchQuery { Limit = size, Offset = (page - 1) * size });
                if (meiliHits?.Hits?.Any() == true)
                {
                    var totalHits = (meiliHits as SearchResult<MangaCard>)?.EstimatedTotalHits ?? meiliHits.Hits.Count;
                    var resHits = new CatalogPage(meiliHits.Hits.ToList(), totalHits, page, size);
                    await CacheSet(cacheKey, resHits, TimeSpan.FromMinutes(5));
                    return resHits;
                }
            } catch { }
        }

        var order = sort switch { "rating" => "rating", "hot" => "followedCount", "title" => "title", "new" => "createdAt", _ => "latestUploadedChapter" };
        var path = $"/manga?limit={size}&offset={(page - 1) * size}&includes[]=cover_art&includes[]=author&contentRating[]=safe&contentRating[]=suggestive&contentRating[]=erotica&order[{order}]={(order == "title" ? "asc" : "desc")}";
        if (!string.IsNullOrWhiteSpace(q)) path += "&title=" + E(q.Trim());
        if (Guid.TryParse(genre, out var tag)) path += "&includedTags[]=" + tag;
        if (new[] { "ongoing", "completed", "hiatus", "cancelled" }.Contains(status)) path += "&status[]=" + status;
        if (!string.IsNullOrWhiteSpace(country))
        {
            var countries = country.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var c in countries)
            {
                if (new[] { "ja", "ko", "zh", "en" }.Contains(c))
                    path += "&originalLanguage[]=" + c;
                else if (c.Equals("manhwa", StringComparison.OrdinalIgnoreCase))
                    path += "&originalLanguage[]=ko";
                else if (c.Equals("manhua", StringComparison.OrdinalIgnoreCase))
                    path += "&originalLanguage[]=zh";
            }
        }
        if (new[] { "shounen", "shoujo", "josei", "seinen" }.Contains(demographic)) path += "&publicationDemographic[]=" + demographic;
        path += "&availableTranslatedLanguage[]=" + (language == "en" ? "en" : "vi");
        if (year is >= 1900 and <= 2100) path += "&year=" + year;

        var items = new List<MangaCard>();
        int total = 0;
        try
        {
            var result = await Get(path);
            var rawItems = result["data"]!.AsArray().Select(x => Map(x!, isThumbnail: true)).ToList();
            await Stats(rawItems);
            total = Math.Min((int?)result["total"] ?? 0, 10000);
            foreach (var m in rawItems)
            {
                bool isDuplicate = false;
                for (int i = 0; i < items.Count; i++)
                {
                    var existing = items[i];
                    if (TruyenGg.IsSameManga(existing.Title, existing.AlternativeTitle, m.Title, m.AlternativeTitle))
                    {
                        isDuplicate = true;
                        if (ShouldPreferCandidate(m, existing))
                        {
                            items[i] = m;
                        }
                        break;
                    }
                }
                if (!isDuplicate)
                {
                    items.Add(m);
                }
            }
        }
        catch { }

        // Also search TruyenGGVN if query is provided
        if (!string.IsNullOrWhiteSpace(q))
        {
            try
            {
                var ggResults = await truyengg.Search(q, page);
                foreach (var gg in ggResults)
                {
                    bool isDuplicate = false;
                    for (int i = 0; i < items.Count; i++)
                    {
                        var existing = items[i];
                        if (TruyenGg.IsSameManga(existing.Title, existing.AlternativeTitle, gg.Title, gg.AlternativeTitle))
                        {
                            isDuplicate = true;
                            if (ShouldPreferCandidate(gg, existing))
                            {
                                items[i] = gg;
                            }
                            break;
                        }
                    }
                    if (!isDuplicate)
                    {
                        items.Add(gg);
                    }
                }
            }
            catch { }
        }

        await EnsureTopChapters(items, 3);
        var res = new CatalogPage(items, total, page, size);
        await CacheSet(cacheKey, res, TimeSpan.FromMinutes(5));

        if (meili != null && items.Count > 0)
        {
            _ = Task.Run(async () => {
                try {
                    var index = meili.Index("mangas");
                    await index.AddDocumentsAsync(items);
                } catch { }
            });
        }

        return res;
    }

    public async Task<MangaCard> Detail(Guid id)
    {
        var cacheKey = $"catalog:detail:v4:{id}";
        var cachedManga = await CacheGet<MangaCard>(cacheKey);
        if (cachedManga != null) return cachedManga;

        var ggManga = await truyengg.GetDetail(id);
        if (ggManga != null)
        {
            await CacheSet(cacheKey, ggManga, TimeSpan.FromMinutes(10));
            return ggManga;
        }

        var data = await Get($"/manga/{id}?includes[]=cover_art&includes[]=author");
        var m = Map(data["data"]!, isThumbnail: false);
        await Stats([m]);
        await CacheSet(cacheKey, m, TimeSpan.FromMinutes(10));
        return m;
    }

    private ChapterCard MapChapter(JsonNode c)
    {
        var a = c["attributes"]!;
        var rel = c["relationships"]!.AsArray();
        decimal.TryParse(S(a["chapter"]), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var num);
        var chapter = S(a["chapter"]);
        var title = S(a["title"]);
        return new(Guid.Parse(S(c["id"])), Guid.Parse(S(rel.First(x => S(x?["type"]) == "manga")?["id"])),
            (chapter.Length > 0 ? "Chương " + chapter : "Oneshot") + (title.Length > 0 ? " · " + title : ""), num,
            S(a["translatedLanguage"]), Date(a["publishAt"]), S(rel.FirstOrDefault(x => S(x?["type"]) == "scanlation_group")?["attributes"]?["name"]));
    }

    public async Task<List<ChapterCard>> GetAllChapters(Guid id, string language)
    {
        var cacheKey = $"catalog:all_chapters:v3:{id}:{language}";
        var cached = await CacheGet<List<ChapterCard>>(cacheKey);
        if (cached != null && cached.Count > 0) return cached;

        var ggChaps = await truyengg.GetChapters(id, 1, 1000, ascending: true);
        if (ggChaps != null && ggChaps.Items.Count > 0)
        {
            var cleanGg = DeduplicateChapters(ggChaps.Items, ascending: true);
            await CacheSet(cacheKey, cleanGg, TimeSpan.FromMinutes(10));
            return cleanGg;
        }

        var mdChapters = new List<ChapterCard>();
        try
        {
            var r = await Get($"/manga/{id}/feed?limit=500&offset=0&translatedLanguage[]={E(language == "en" ? "en" : "vi")}&includes[]=scanlation_group&order[chapter]=asc&includeExternalUrl=0");
            if (r["data"]?.AsArray() is JsonArray arr)
            {
                mdChapters.AddRange(arr.Select(x => MapChapter(x!)));
            }
        }
        catch { }

        if (language == "vi" || string.IsNullOrEmpty(language))
        {
            try
            {
                var manga = await Detail(id);
                if (manga != null)
                {
                    var extraChaps = await truyengg.FindMatchingChapters(id, manga.Title, manga.AlternativeTitle);
                    if (extraChaps != null && extraChaps.Count > 0)
                    {
                        mdChapters.AddRange(extraChaps);
                    }
                }
            }
            catch { }
        }

        var deduplicated = DeduplicateChapters(mdChapters, ascending: true);
        if (deduplicated.Count > 0)
        {
            await CacheSet(cacheKey, deduplicated, TimeSpan.FromMinutes(10));
        }
        return deduplicated;
    }

    public async Task<ChapterPage> Chapters(Guid id, string language, int page, bool ascending = false)
    {
        var allChapters = await GetAllChapters(id, language);
        var ordered = ascending
            ? allChapters.OrderBy(c => c.Number).ThenBy(c => c.PublishedAt).ToList()
            : allChapters.OrderByDescending(c => c.Number).ThenByDescending(c => c.PublishedAt).ToList();

        const int size = 100;
        var total = ordered.Count;
        var pagedItems = ordered.Skip((page - 1) * size).Take(size).ToList();
        return new ChapterPage(pagedItems, total, page, size);
    }

    public async Task<ReaderData> Read(Guid id)
    {
        var cacheKey = $"catalog:reader:v7:{id}";
        var cachedReader = await CacheGet<ReaderData>(cacheKey);
        if (cachedReader != null) return cachedReader;

        var ggReader = await truyengg.GetReader(id);
        if (ggReader != null)
        {
            var mangaId = ggReader.Chapter.MangaId;
            var manga = mangaId != Guid.Empty ? await Detail(mangaId) : null;
            var nav = mangaId != Guid.Empty ? await GetAllChapters(mangaId, "vi") : null;
            if (nav == null || nav.Count == 0)
            {
                nav = ggReader.Navigation;
            }
            if (!nav.Any(x => x.Id == id))
            {
                nav.Add(ggReader.Chapter);
                nav = DeduplicateChapters(nav, ascending: true);
            }

            var finalReader = new ReaderData(
                ggReader.Chapter,
                manga ?? ggReader.Manga,
                ggReader.Pages,
                ggReader.DataSaverPages,
                ggReader.ExternalUrl,
                nav
            );
            await CacheSet(cacheKey, finalReader, TimeSpan.FromMinutes(15));
            return finalReader;
        }

        var chapter = (await Get($"/chapter/{id}?includes[]=scanlation_group"))["data"]!;
        var c = MapChapter(chapter);
        var m = await Detail(c.MangaId);

        var navigation = await GetAllChapters(c.MangaId, c.Language);
        if (navigation == null || navigation.Count == 0)
        {
            navigation = [c];
        }
        else if (!navigation.Any(x => x.Id == id))
        {
            navigation.Add(c);
            navigation = DeduplicateChapters(navigation, ascending: true);
        }

        var external = S(chapter["attributes"]?["externalUrl"]);
        if (external.Length > 0) return new(c, m, [], [], external.StartsWith("https://") ? external : null, navigation);
        var r = await Get($"/at-home/server/{id}");
        var baseUrl = S(r["baseUrl"]).TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https") throw new UpstreamException("Địa chỉ máy chủ ảnh không hợp lệ.");
        var hash = S(r["chapter"]?["hash"]);
        string ProxyUrl(string url) => "https://services.f-ck.me/v1/image/" + Convert.ToBase64String(Encoding.UTF8.GetBytes(url)).Replace('+', '-').Replace('/', '_');
        string[] Pages(string key, string folder) => r["chapter"]?[key]?.AsArray().Select(x => ProxyUrl($"{baseUrl}/{folder}/{hash}/{E(S(x))}")).ToArray() ?? [];
        var result = new ReaderData(c, m, Pages("data", "data"), Pages("dataSaver", "data-saver"), null, navigation);
        await CacheSet(cacheKey, result, TimeSpan.FromMinutes(20));
        return result;
    }
    public async Task<object> Tags() => (await Get("/manga/tag"))["data"]!.AsArray().Select(x => new { id = S(x?["id"]), name = Localized(x?["attributes"]?["name"]) }).OrderBy(x => x.name).ToArray();
}
