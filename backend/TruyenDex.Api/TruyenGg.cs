using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using StackExchange.Redis;

namespace TruyenDex.Api;

public class TruyenGg(HttpClient http, IMemoryCache cache, IConnectionMultiplexer? redis = null)
{
    private const string BaseUrl = "https://truyenggvn.com";
    private static readonly ConcurrentDictionary<Guid, string> MangaSlugMap = new();
    private static readonly ConcurrentDictionary<Guid, string> ChapterUrlMap = new();
    private static readonly ConcurrentDictionary<Guid, Guid> ChapterMangaMap = new();

    public static Guid CreateGuid(string key)
    {
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(key));
        return new Guid(hash);
    }

    public static string NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        var s = title.Trim().ToLowerInvariant();
        var sb = new StringBuilder();
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c))
                sb.Append(c);
        }
        return sb.ToString();
    }

    private async Task<string?> CacheGetString(string key)
    {
        if (redis != null && redis.IsConnected)
        {
            try {
                var v = await redis.GetDatabase().StringGetAsync(key);
                if (v.HasValue) return v.ToString();
            } catch { }
        }
        return cache.TryGetValue<string>(key, out var cached) ? cached : null;
    }

    private async Task CacheSetString(string key, string value, TimeSpan expiry)
    {
        if (redis != null && redis.IsConnected)
        {
            try {
                await redis.GetDatabase().StringSetAsync(key, value, expiry);
            } catch { }
        }
        cache.Set(key, value, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = expiry, Size = 1 });
    }

    private async Task<string?> FetchHtml(string url)
    {
        var cacheKey = $"truyengg:html:{url}";
        var cached = await CacheGetString(cacheKey);
        if (cached != null) return cached;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
            req.Headers.Referrer = new Uri(BaseUrl);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var res = await http.SendAsync(req, cts.Token);
            if (!res.IsSuccessStatusCode) return null;
            var html = await res.Content.ReadAsStringAsync(cts.Token);
            if (!string.IsNullOrEmpty(html))
            {
                await CacheSetString(cacheKey, html, TimeSpan.FromMinutes(10));
            }
            return html;
        }
        catch
        {
            return null;
        }
    }

    private static DateTime ParseTimeAgo(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return DateTime.UtcNow;
        s = s.Trim().ToLowerInvariant();
        if (s.Contains("vừa xong") || s.Contains("vài giây")) return DateTime.UtcNow;

        var m = Regex.Match(s, @"(\d+)\s*(phút|giờ|ngày|tháng|năm|giây)");
        if (m.Success && int.TryParse(m.Groups[1].Value, out var num))
        {
            var unit = m.Groups[2].Value;
            return unit switch
            {
                "giây" => DateTime.UtcNow.AddSeconds(-num),
                "phút" => DateTime.UtcNow.AddMinutes(-num),
                "giờ" => DateTime.UtcNow.AddHours(-num),
                "ngày" => DateTime.UtcNow.AddDays(-num),
                "tháng" => DateTime.UtcNow.AddDays(-num * 30),
                "năm" => DateTime.UtcNow.AddDays(-num * 365),
                _ => DateTime.UtcNow
            };
        }
        if (DateTime.TryParseExact(s, ["dd/MM/yyyy", "dd/MM/yyyy HH:mm", "yyyy-MM-dd"], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var dt))
        {
            return dt.ToUniversalTime();
        }
        if (DateTime.TryParse(s, out var d)) return d.ToUniversalTime();
        return DateTime.UtcNow;
    }

    private static string StripHtml(string input)
    {
        if (string.IsNullOrEmpty(input)) return "";
        return Regex.Replace(input, "<.*?>", string.Empty).Trim();
    }

    private List<MangaCard> ParseGridHtml(string html)
    {
        var items = new List<MangaCard>();
        var liMatches = Regex.Matches(html, @"<li>\s*<div class=""book_avatar"">([\s\S]*?)<\/li>");
        foreach (Match m in liMatches)
        {
            var block = m.Value;
            var linkMatch = Regex.Match(block, @"href=""(/truyen-tranh/[^""]+)""");
            if (!linkMatch.Success) continue;
            var href = linkMatch.Groups[1].Value;
            var slug = href.Replace("/truyen-tranh/", "").Trim('/');
            if (string.IsNullOrEmpty(slug)) continue;

            var titleMatch = Regex.Match(block, @"class=""book_name[^""]*""[^>]*>\s*<h3><a[^>]*>(.*?)<\/a>");
            var title = titleMatch.Success ? StripHtml(titleMatch.Groups[1].Value) : "";
            if (string.IsNullOrEmpty(title))
            {
                var altTitleMatch = Regex.Match(block, @"alt=""([^""]+)""");
                if (altTitleMatch.Success) title = altTitleMatch.Groups[1].Value;
            }
            if (string.IsNullOrEmpty(title)) continue;

            var otherTitleMatch = Regex.Match(block, @"class=""title-more-other"">([^<]+)<\/div>");
            var otherTitle = otherTitleMatch.Success ? otherTitleMatch.Groups[1].Value.Replace("Tên khác:", "").Trim() : "";

            var imgMatch = Regex.Match(block, @"<img[^>]*src=""([^""]+)""");
            var coverUrl = imgMatch.Success ? imgMatch.Groups[1].Value : "";
            if (coverUrl.StartsWith("//")) coverUrl = "https:" + coverUrl;
            else if (coverUrl.StartsWith("/")) coverUrl = BaseUrl + coverUrl;

            var timeMatch = Regex.Match(block, @"class=""time-ago"">([^<]+)<\/span>");
            var updatedAt = timeMatch.Success ? ParseTimeAgo(timeMatch.Groups[1].Value) : DateTime.UtcNow;

            var followMatch = Regex.Match(block, @"<i class=""fa fa-bookmark""><\/i>\s*([\d,]+)");
            int.TryParse(followMatch.Groups[1].Value.Replace(",", ""), out var follows);

            var tagMatches = Regex.Matches(block, @"<p class=""blue"">([^<]+)<\/p>");
            var genres = tagMatches.Select(t => t.Groups[1].Value.Trim()).Where(g => !string.IsNullOrEmpty(g)).ToArray();

            var lastChapMatch = Regex.Match(block, @"class=""last_chapter"">\s*<a href=""([^""]+)""[^>]*>([^<]+)<\/a>");
            var mangaId = CreateGuid("truyengg:manga:" + slug);
            MangaSlugMap[mangaId] = slug;

            var chapters = new List<ChapterCard>();
            if (lastChapMatch.Success)
            {
                var chapHref = lastChapMatch.Groups[1].Value;
                var chapTitle = StripHtml(lastChapMatch.Groups[2].Value);
                var chapId = CreateGuid("truyengg:chap:" + chapHref);
                ChapterUrlMap[chapId] = chapHref;
                ChapterMangaMap[chapId] = mangaId;
                chapters.Add(new ChapterCard(chapId, mangaId, chapTitle, 0, "vi", updatedAt, "TruyenGG"));
            }

            var card = new MangaCard
            {
                Id = mangaId,
                Title = title,
                AlternativeTitle = string.IsNullOrEmpty(otherTitle) ? title : otherTitle,
                Author = "Đang cập nhật",
                Cover = !string.IsNullOrEmpty(coverUrl) ? "/api/catalog/image-proxy?url=" + Uri.EscapeDataString(coverUrl) : "/cover-placeholder.svg",
                Description = "",
                Genres = genres,
                Status = block.Contains("Hoàn thành") ? "completed" : "ongoing",
                Country = genres.Contains("Manhwa") ? "ko" : genres.Contains("Manhua") ? "zh" : genres.Contains("Manga") ? "ja" : "vi",
                ContentRating = "safe",
                Follows = follows,
                Rating = 8.5,
                UpdatedAt = updatedAt,
                Chapters = chapters
            };

            items.Add(card);
        }

        return items;
    }

    public async Task<List<MangaCard>> GetLatest(int page = 1)
    {
        var url = page <= 1 
            ? $"{BaseUrl}/truyen-moi-cap-nhat.html" 
            : $"{BaseUrl}/truyen-moi-cap-nhat/trang-{page}.html";

        var html = await FetchHtml(url);
        if (string.IsNullOrEmpty(html)) return [];
        return ParseGridHtml(html);
    }

    public async Task<List<MangaCard>> Search(string q, int page = 1)
    {
        if (string.IsNullOrWhiteSpace(q)) return await GetLatest(page);
        var url = page <= 1
            ? $"{BaseUrl}/tim-kiem.html?q={Uri.EscapeDataString(q)}"
            : $"{BaseUrl}/tim-kiem/trang-{page}.html?q={Uri.EscapeDataString(q)}";
        var html = await FetchHtml(url);
        if (string.IsNullOrEmpty(html)) return [];
        return ParseGridHtml(html);
    }

    public async Task<MangaCard?> GetDetail(Guid id)
    {
        if (!MangaSlugMap.TryGetValue(id, out var slug)) return null;
        var url = $"{BaseUrl}/truyen-tranh/{slug}";
        var html = await FetchHtml(url);
        if (string.IsNullOrEmpty(html)) return null;

        var titleMatch = Regex.Match(html, @"<h1 itemprop=""name"">([^<]+)<\/h1>");
        var title = titleMatch.Success ? titleMatch.Groups[1].Value.Trim() : slug;

        var altTitleMatch = Regex.Match(html, @"<h2 class=""book-othername"">([^<]+)<\/h2>");
        var altTitle = altTitleMatch.Success ? altTitleMatch.Groups[1].Value.Trim() : title;

        var imgMatch = Regex.Match(html, @"class=""book_avatar""[^>]*>\s*<img[^>]*src=""([^""]+)""");
        var coverUrl = imgMatch.Success ? imgMatch.Groups[1].Value : "";
        if (coverUrl.StartsWith("//")) coverUrl = "https:" + coverUrl;
        else if (coverUrl.StartsWith("/")) coverUrl = BaseUrl + coverUrl;

        var authorMatch = Regex.Match(html, @"Tác giả<\/span>\s*<span class=""book-counts__value"">\s*<a[^>]*>([^<]+)<\/a>");
        var author = authorMatch.Success ? authorMatch.Groups[1].Value.Trim() : "Đang cập nhật";

        var statusMatch = Regex.Match(html, @"Tình trạng<\/span>\s*<span class=""book-counts__value"">([^<]+)<\/span>");
        var status = statusMatch.Success && statusMatch.Groups[1].Value.Contains("Hoàn thành") ? "completed" : "ongoing";

        var followMatch = Regex.Match(html, @"Theo dõi<\/span>\s*<span class=""book-counts__value"">([\d,]+)<\/span>");
        int.TryParse(followMatch.Groups[1].Value.Replace(",", ""), out var follows);

        var genreMatches = Regex.Matches(html, @"class=""book-genres"">[\s\S]*?<ul class=""list01"">([\s\S]*?)<\/ul>");
        var genres = new List<string>();
        if (genreMatches.Count > 0)
        {
            var gNames = Regex.Matches(genreMatches[0].Value, @"<a[^>]*>([^<]+)<\/a>");
            foreach (Match gm in gNames) genres.Add(gm.Groups[1].Value.Trim());
        }

        var descMatch = Regex.Match(html, @"class=""story-detail-info[^""]*"">([\s\S]*?)<\/div>");
        var desc = descMatch.Success ? StripHtml(descMatch.Groups[1].Value) : "";

        return new MangaCard
        {
            Id = id,
            Title = title,
            AlternativeTitle = altTitle,
            Author = author,
            Cover = !string.IsNullOrEmpty(coverUrl) ? "/api/catalog/image-proxy?url=" + Uri.EscapeDataString(coverUrl) : "/cover-placeholder.svg",
            Description = desc,
            Genres = genres.ToArray(),
            Status = status,
            Country = genres.Contains("Manhwa") ? "ko" : genres.Contains("Manhua") ? "zh" : genres.Contains("Manga") ? "ja" : "vi",
            ContentRating = "safe",
            Follows = follows,
            Rating = 8.5,
            UpdatedAt = DateTime.UtcNow
        };
    }

    public async Task<ChapterPage?> GetChapters(Guid id, int page = 1, int size = 100, bool ascending = false)
    {
        if (!MangaSlugMap.TryGetValue(id, out var slug)) return null;
        var url = $"{BaseUrl}/truyen-tranh/{slug}";
        var html = await FetchHtml(url);
        if (string.IsNullOrEmpty(html)) return null;

        var chapMatches = Regex.Matches(html, @"<div class=""works-chapter-item"">\s*<div class=""col-md-10[^""]*name-chap"">\s*<a[^>]*href=""([^""]+)""[^>]*>([^<]+)<\/a>\s*<\/div>\s*<div class=""col-md-2[^""]*time-chap"">\s*([^<]+)\s*<\/div>");
        var allChapters = new List<ChapterCard>();
        foreach (Match cm in chapMatches)
        {
            var chapHref = cm.Groups[1].Value;
            var chapTitle = StripHtml(cm.Groups[2].Value);
            var timeStr = cm.Groups[3].Value.Trim();
            var publishedAt = ParseTimeAgo(timeStr);

            var numMatch = Regex.Match(chapTitle, @"\d+(\.\d+)?");
            decimal.TryParse(numMatch.Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var num);

            var chapId = CreateGuid("truyengg:chap:" + chapHref);
            ChapterUrlMap[chapId] = chapHref;
            ChapterMangaMap[chapId] = id;

            allChapters.Add(new ChapterCard(chapId, id, chapTitle, num, "vi", publishedAt, "TruyenGG"));
        }

        if (ascending)
        {
            allChapters = allChapters.OrderBy(c => c.Number).ThenBy(c => c.PublishedAt).ToList();
        }
        else
        {
            allChapters = allChapters.OrderByDescending(c => c.Number).ThenByDescending(c => c.PublishedAt).ToList();
        }

        var total = allChapters.Count;
        var paged = allChapters.Skip((page - 1) * size).Take(size).ToList();
        return new ChapterPage(paged, total, page, size);
    }

    public async Task<ReaderData?> GetReader(Guid chapterId)
    {
        if (!ChapterUrlMap.TryGetValue(chapterId, out var chapHref)) return null;
        var mangaId = ChapterMangaMap.GetValueOrDefault(chapterId);
        var url = chapHref.StartsWith("http") ? chapHref : BaseUrl + (chapHref.StartsWith("/") ? "" : "/") + chapHref;

        var html = await FetchHtml(url);
        if (string.IsNullOrEmpty(html)) return null;

        var imgMatches = Regex.Matches(html, @"class=""page-chapter""[^>]*>\s*<img[^>]*>");
        var pages = new List<string>();
        foreach (Match im in imgMatches)
        {
            var tag = im.Value;
            var srcMatch = Regex.Match(tag, @"data-original=""([^""]+)""");
            if (!srcMatch.Success) srcMatch = Regex.Match(tag, @"data-cdn=""([^""]+)""");
            if (!srcMatch.Success) srcMatch = Regex.Match(tag, @"src=""([^""]+)""");
            if (srcMatch.Success)
            {
                var src = srcMatch.Groups[1].Value;
                if (src.StartsWith("//")) src = "https:" + src;
                else if (src.StartsWith("/")) src = BaseUrl + src;
                pages.Add(src);
            }
        }

        var titleMatch = Regex.Match(html, @"<h1 class=""detail-title[^""]*"">\s*<a[^>]*href=""([^""]+)""[^>]*>([^<]+)<\/a>\s*-\s*([^<]+)<\/h1>");
        var mangaTitle = titleMatch.Success ? StripHtml(titleMatch.Groups[2].Value) : "Truyện Tranh";
        var chapterTitle = titleMatch.Success ? StripHtml(titleMatch.Groups[3].Value) : "Chương";

        var mangaCard = new MangaCard
        {
            Id = mangaId,
            Title = mangaTitle,
            AlternativeTitle = mangaTitle,
            Cover = "/cover-placeholder.svg",
            Author = "TruyenGG"
        };

        var currentChap = new ChapterCard(chapterId, mangaId, chapterTitle, 0, "vi", DateTime.UtcNow, "TruyenGG");

        // Navigation chapters
        var navigation = new List<ChapterCard>();
        if (mangaId != Guid.Empty)
        {
            var allChaps = await GetChapters(mangaId, 1, 500, true);
            if (allChaps != null && allChaps.Items.Count > 0)
            {
                navigation = allChaps.Items;
            }
        }
        if (!navigation.Any(x => x.Id == chapterId))
        {
            navigation.Add(currentChap);
        }

        return new ReaderData(currentChap, mangaCard, pages.ToArray(), pages.ToArray(), null, navigation);
    }

    public static bool IsTruyenGgManga(Guid id) => MangaSlugMap.ContainsKey(id);
    public static bool IsTruyenGgChapter(Guid id) => ChapterUrlMap.ContainsKey(id);
}
