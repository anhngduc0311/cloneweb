using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;

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

// Read-only adapter. Only fixed upstream origins and validated IDs are used.
public class Catalog(HttpClient http, IMemoryCache cache)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly string[] Origins = ["https://api-proxy.truyendex.cc/mangadex", "https://api-proxy.truyendex.xyz/mangadex", "https://api.mangadex.org"];
    private static string S(JsonNode? n) => n?.ToString() ?? "";
    private static string E(string s) => Uri.EscapeDataString(s);
    private static DateTime Date(JsonNode? n) => DateTime.TryParse(S(n), out var d) ? d.ToUniversalTime() : DateTime.UtcNow;
    private static string Localized(JsonNode? n) => S(n?["vi"] ?? n?["en"] ?? (n as JsonObject)?.FirstOrDefault().Value);
    public async Task<JsonNode> Get(string path, bool site = false)
    {
        var key = $"upstream:{site}:{path}";
        if (cache.TryGetValue<string>(key, out var cached)) return JsonNode.Parse(cached!)!;
        await Gate.WaitAsync();
        try
        {
            if (cache.TryGetValue<string>(key, out cached)) return JsonNode.Parse(cached!)!;
            foreach (var origin in site ? new[] { "https://api.truyendex.cc" } : Origins)
            {
                try
                {
                    using var response = await http.GetAsync(origin + path);
                    if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                        throw new UpstreamException("Không tìm thấy truyện hoặc chương ở nguồn.", 404);
                    if (!response.IsSuccessStatusCode) continue;
                    var json = await response.Content.ReadAsStringAsync();
                    var node = JsonNode.Parse(json) ?? throw new JsonException();
                    cache.Set(key, json, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2), Size = 1 });
                    return node;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { }
            }
            throw new UpstreamException("Nguồn truyện đang tạm thời không phản hồi. Vui lòng thử lại sau.");
        }
        finally { await Task.Delay(350); Gate.Release(); }
    }
    private MangaCard Map(JsonNode n)
    {
        var a = n["attributes"]!;
        var rel = n["relationships"]!.AsArray();
        var id = Guid.Parse(S(n["id"]));
        var title = a["altTitles"]?.AsArray().Select(x => x?["vi"]).FirstOrDefault(x => x != null);
        var file = S(rel.FirstOrDefault(x => S(x?["type"]) == "cover_art")?["attributes"]?["fileName"]);
        var coverUrl = $"https://mangadex.org/covers/{id}/{file}.512.jpg";
        return new MangaCard {
            Id = id, Title = title == null ? Localized(a["title"]) : S(title),
            AlternativeTitle = Localized(a["title"]), Author = string.Join(" / ", rel.Where(x => S(x?["type"]) == "author").Select(x => S(x?["attributes"]?["name"]))),
            Cover = file.Length > 0 ? "https://services.f-ck.me/v1/image/" + Convert.ToBase64String(Encoding.UTF8.GetBytes(coverUrl)).Replace('+', '-').Replace('/', '_') : "/cover-placeholder.svg",
            Description = Localized(a["description"]), Status = S(a["status"]), Country = S(a["originalLanguage"]),
            Demographic = S(a["publicationDemographic"]), ContentRating = S(a["contentRating"]), Year = (int?)a["year"],
            Genres = a["tags"]!.AsArray().Select(x => Localized(x?["attributes"]?["name"])).ToArray(), UpdatedAt = Date(a["updatedAt"])
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
    public async Task<CatalogPage> Home(int page, int size)
    {
        // The public homepage endpoint always returns 28 records, even with a different limit.
        size = 28;
        var home = await Get($"/api/series/homepage?page={page}&limit={size}", true);
        var rows = home["data"]!.AsArray();
        if (rows.Count == 0) return new([], (int?)home["total"] ?? 0, page, size);
        var ids = rows.Select(x => S(x?["uuid"])).ToArray();
        var response = await Get("/manga?limit=100&includes[]=cover_art&includes[]=author&" + string.Join("&", ids.Select(x => "ids[]=" + x)));
        var map = response["data"]!.AsArray().Select(x => Map(x!)).ToDictionary(x => x.Id.ToString());
        var items = new List<MangaCard>();
        foreach (var row in rows) {
            if (!map.TryGetValue(S(row?["uuid"]), out var m)) continue;
            // Preserve homepage order: last chapter update descending, not manga metadata update.
            m.UpdatedAt = Date(row?["last_chapter_updated_at"]);
            m.Chapters = row?["chapters"]?.AsArray().Select(c => new ChapterCard(Guid.Parse(S(c?["uuid"])), m.Id, S(c?["title"]), 0, "vi", Date(c?["md_updated_at"]))).ToList() ?? [];
            items.Add(m);
        }
        await Stats(items);
        return new(items, (int?)home["total"] ?? items.Count, page, size);
    }
    public async Task<CatalogPage> Search(int page, int size, string? q, string? genre, string? status, string? country, string? demographic, string? language, string? sort, int? year)
    {
        var order = sort switch { "rating" => "rating", "hot" => "followedCount", "title" => "title", "new" => "createdAt", _ => "latestUploadedChapter" };
        var path = $"/manga?limit={size}&offset={(page - 1) * size}&includes[]=cover_art&includes[]=author&contentRating[]=safe&contentRating[]=suggestive&order[{order}]={(order == "title" ? "asc" : "desc")}";
        if (!string.IsNullOrWhiteSpace(q)) path += "&title=" + E(q.Trim());
        if (Guid.TryParse(genre, out var tag)) path += "&includedTags[]=" + tag;
        if (new[] { "ongoing", "completed", "hiatus", "cancelled" }.Contains(status)) path += "&status[]=" + status;
        if (new[] { "ja", "ko", "zh", "en" }.Contains(country)) path += "&originalLanguage[]=" + country;
        if (new[] { "shounen", "shoujo", "josei", "seinen" }.Contains(demographic)) path += "&publicationDemographic[]=" + demographic;
        path += "&availableTranslatedLanguage[]=" + (language == "en" ? "en" : "vi");
        if (year is >= 1900 and <= 2100) path += "&year=" + year;
        var result = await Get(path);
        var items = result["data"]!.AsArray().Select(x => Map(x!)).ToList();
        await Stats(items);
        return new(items, Math.Min((int?)result["total"] ?? 0, 10000), page, size);
    }
    public async Task<MangaCard> Detail(Guid id)
    {
        var data = await Get($"/manga/{id}?includes[]=cover_art&includes[]=author");
        var m = Map(data["data"]!);
        await Stats([m]);
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
    public async Task<ChapterPage> Chapters(Guid id, string language, int page, bool ascending = false)
    {
        const int size = 100;
        var r = await Get($"/manga/{id}/feed?limit={size}&offset={(page - 1) * size}&translatedLanguage[]={E(language == "en" ? "en" : "vi")}&includes[]=scanlation_group&order[chapter]={(ascending ? "asc" : "desc")}&includeExternalUrl=0");
        return new(r["data"]!.AsArray().Select(x => MapChapter(x!)).ToList(), Math.Min((int?)r["total"] ?? 0, 10000), page, size);
    }
    public async Task<ReaderData> Read(Guid id)
    {
        var chapter = (await Get($"/chapter/{id}?includes[]=scanlation_group"))["data"]!;
        var c = MapChapter(chapter);
        var m = await Detail(c.MangaId);
        var navigation = new List<ChapterCard>();
        var aggregate = await Get($"/manga/{c.MangaId}/aggregate?translatedLanguage[]={E(c.Language)}");
        if (aggregate["volumes"] is JsonObject volumes) foreach (var volume in volumes) {
            if (volume.Value?["chapters"] is not JsonObject chapters) continue;
            foreach (var ch in chapters) {
                var number = S(ch.Value?["chapter"]);
                decimal.TryParse(number, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var numeric);
                var chapterId = S(ch.Value?["id"]);
                if (ch.Value?["others"]?.AsArray().Any(x => S(x) == id.ToString()) == true) chapterId = id.ToString();
                if (Guid.TryParse(chapterId, out var cid)) navigation.Add(new(cid, c.MangaId, "Chương " + number, numeric, c.Language, c.PublishedAt));
            }
        }
        if (!navigation.Any(x => x.Id == id)) navigation.Add(c);
        navigation = navigation.OrderBy(x => x.Number).DistinctBy(x => x.Id).ToList();
        var external = S(chapter["attributes"]?["externalUrl"]);
        if (external.Length > 0) return new(c, m, [], [], external.StartsWith("https://") ? external : null, navigation);
        var r = await Get($"/at-home/server/{id}");
        var baseUrl = S(r["baseUrl"]).TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https") throw new UpstreamException("Địa chỉ máy chủ ảnh không hợp lệ.");
        var hash = S(r["chapter"]?["hash"]);
        string[] Pages(string key, string folder) => r["chapter"]?[key]?.AsArray().Select(x => $"{baseUrl}/{folder}/{hash}/{E(S(x))}").ToArray() ?? [];
        return new(c, m, Pages("data", "data"), Pages("dataSaver", "data-saver"), null, navigation);
    }
    public async Task<object> Tags() => (await Get("/manga/tag"))["data"]!.AsArray().Select(x => new { id = S(x?["id"]), name = Localized(x?["attributes"]?["name"]) }).OrderBy(x => x.name).ToArray();
}
