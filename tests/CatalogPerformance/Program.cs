using System.Net;
using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using TruyenDex.Api;

const string mangaId = "11111111-1111-1111-1111-111111111111";
const string chapterId = "22222222-2222-2222-2222-222222222222";

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static string Manga(string country) => """
{"data":[{"id":"{{mangaId}}","attributes":{"title":{"vi":"Truyện kiểm thử"},"altTitles":[],"description":{},"originalLanguage":"{{country}}","tags":[],"status":"ongoing","updatedAt":"2026-10-12T00:00:00Z"},"relationships":[{"type":"cover_art","attributes":{"fileName":"cover.jpg"}}]}],"total":100}
""".Replace("{{mangaId}}", mangaId).Replace("{{chapterId}}", chapterId).Replace("{{country}}", country);

using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 1000 });
var ggStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var mdStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
bool featured = false;
var handler = new FakeHandler(async uri =>
{
    if (uri.Host.StartsWith("api-proxy.")) throw new Exception("An unavailable proxy was attempted before the direct API");
    if (uri.Host == "truyenggvn.com")
    {
        Check(!uri.AbsolutePath.StartsWith("/truyen-tranh/"), "List requests must not scrape individual story details");
        if (featured)
        {
            ggStarted.TrySetResult();
            await mdStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        return " ";
    }
    if (uri.AbsolutePath == "/api/series/homepage")
        return """{"data":[{"uuid":"{{mangaId}}","last_chapter_updated_at":"2026-10-12T00:00:00Z","chapters":[{"uuid":"{{chapterId}}","title":"Chương 1","md_updated_at":"2026-10-12T00:00:00Z"}]}],"total":100}""".Replace("{{mangaId}}", mangaId).Replace("{{chapterId}}", chapterId);
    if (uri.AbsolutePath == "/manga")
    {
        if (featured)
        {
            mdStarted.TrySetResult();
            await ggStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        return Manga(featured ? "ko" : "ja");
    }
    if (uri.AbsolutePath == "/statistics/manga") return "{\"statistics\":{}}";
    if (uri.AbsolutePath == "/chapter")
    {
        Check(!uri.Query.Contains("manga[]") && !uri.Query.Contains("manga%5B%5D"), "Chapter batch must not send an unsupported manga array filter");
        return """{"data":[{"id":"{{chapterId}}","attributes":{"chapter":"1","title":"","translatedLanguage":"vi","publishAt":"2026-10-12T00:00:00Z"},"relationships":[{"type":"manga","id":"{{mangaId}}"}]}]}""".Replace("{{mangaId}}", mangaId).Replace("{{chapterId}}", chapterId);
    }
    throw new Exception("Unexpected per-story request: " + uri);
});
using var http = new HttpClient(handler);
var catalog = new Catalog(http, cache, new TruyenGg(http, cache));
var home = await catalog.Home(1, 4);
Check(home.Items.Count == 1 && home.Items[0].Chapters.Count == 1, "Homepage must preserve source chapters");
Check(home.PageSize == 28 && home.Total == 100, "Homepage pagination must be preserved");
var homeRequestCount = handler.Requests.Count;
await catalog.Home(1, 28);
Check(handler.Requests.Count == homeRequestCount, "Normalized page size must reuse the same cache entry");
Console.WriteLine("PASS: direct API, homepage chapters, no per-story scraping, normalized cache");

featured = true;
var recommendations = await catalog.Featured(20);
Check(mdStarted.Task.IsCompleted && ggStarted.Task.IsCompleted, "Recommendation sources must start concurrently");
Check(recommendations.Items.Count == 1 && recommendations.Items[0].Chapters.Count == 1, "Recommendations must keep batched chapters");
Check(handler.Requests.Count(x => x.AbsolutePath == "/chapter") == 1, "Recommendations must use one chapter batch");
var featuredRequestCount = handler.Requests.Count;
await catalog.Featured(20);
Check(handler.Requests.Count == featuredRequestCount, "Warm recommendations must use cache");
Console.WriteLine("PASS: concurrent sources, batched recommendation chapters, warm cache");

var secondFeaturedPage = await catalog.Featured(20, 2);
Check(secondFeaturedPage.Page == 2 && secondFeaturedPage.PageSize == 20 && secondFeaturedPage.Total == 100,
    "Featured pagination must retain upstream total and the requested page");
Check(handler.Requests.Any(uri => uri.AbsolutePath == "/manga" && uri.Query.Contains("offset=20")),
    "Featured page two must fetch the next upstream page");
var secondPageRequestCount = handler.Requests.Count;
await catalog.Featured(20, 2);
Check(handler.Requests.Count == secondPageRequestCount, "Featured pages must have separate reusable cache entries");
Console.WriteLine("PASS: featured pagination, upstream total, offsets and separate page cache");

using var fallbackCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 1000 });
var fallbackHandler = new FakeHandler(uri => Task.FromResult("{\"data\":[]}")) { FailDirect = true };
using var fallbackHttp = new HttpClient(fallbackHandler);
var fallbackCatalog = new Catalog(fallbackHttp, fallbackCache, new TruyenGg(fallbackHttp, fallbackCache));
await fallbackCatalog.Get("/manga?limit=1");
Check(fallbackHandler.Requests.Select(x => x.Host).SequenceEqual(new[] { "api.mangadex.org", "api-proxy.truyendex.cc" }), "Proxy fallback must remain available");
Console.WriteLine("PASS: proxy fallback on direct-source failure");

using var aliasCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 1000 });
using var aliasHandler = new AliasHandler();
using var aliasHttp = new HttpClient(aliasHandler);
var aliasSource = new TruyenGg(aliasHttp, aliasCache);
var aliasMangaId = Guid.NewGuid();
var aliasChapters = await aliasSource.FindMatchingChapters(aliasMangaId, "Tên tiếng Việt kiểm thử",
    "その着せ替え人形は恋をする / Sono Bisque Doll wa Koi o Suru / My Dress-Up Darling / Unused alias");
Check(aliasChapters is { Count: 1 } && aliasChapters[0].Number == 111 && aliasChapters[0].MangaId == aliasMangaId,
    "Chapter fallback must search later aliases and preserve the original manga ID");
Check(aliasHandler.Queries.SequenceEqual(new[] { "Tên tiếng Việt kiểm thử", "Sono Bisque Doll wa Koi o Suru", "My Dress-Up Darling" }),
    "Fallback must continue past unrelated results, skip unsupported scripts, and stop at a verified match");
var registeredChapters = await aliasSource.GetChapters(aliasMangaId);
Check(registeredChapters?.Items.Count == 1, "Matched alias must remain registered for later chapter requests");
Console.WriteLine("PASS: later title aliases, unrelated search results, verified matching, persistent chapter mapping");

using var readerCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 1000 });
using var readerHandler = new FakeHandler(uri => Task.FromResult("""
<div class="page-chapter"><img data-original="https://s135.hinhhinh.com/19852/1/0.jpg?gt=hdfgdfg"></div>
<div class="page-chapter"><img data-cdn="//s136.hinhhinh.com/19852/1/0.jpg?gt=changed"></div>
<div class="page-chapter"><img src="https://s135.hinhhinh.com/19852/1/1.jpg"></div>
<div class="page-chapter"><img src="https://s135.hinhhinh.com/19852/2/0.jpg"></div>
<div class="page-chapter"><img src="https://s135.hinhhinh.com/99999/1/0.jpg"></div>
<div class="page-chapter"><img src="https://other.example/19852/1/0.jpg"></div>
<div class="page-chapter"><img src="https://hinhhinh.com.other.example/19852/1/0.jpg"></div>
"""));
using var readerHttp = new HttpClient(readerHandler);
var readerSource = new TruyenGg(readerHttp, readerCache);
var readerChapterId = Guid.NewGuid();
readerSource.RegisterChapter(readerChapterId, "/truyen-tranh/banner-regression/chapter-1", Guid.Empty);
var reader = await readerSource.GetReader(readerChapterId);
var expectedPages = new[]
{
    "https://s135.hinhhinh.com/19852/1/1.jpg",
    "https://s135.hinhhinh.com/19852/2/0.jpg",
    "https://s135.hinhhinh.com/99999/1/0.jpg",
    "https://other.example/19852/1/0.jpg",
    "https://hinhhinh.com.other.example/19852/1/0.jpg"
}.Select(url => "/api/catalog/image-proxy?url=" + Uri.EscapeDataString(url)).ToArray();
Check(reader != null && reader.Pages.SequenceEqual(expectedPages),
    "Reader must skip the confirmed banner across CDN hosts and query strings, preserving real pages in order");
Check(reader!.DataSaverPages.SequenceEqual(expectedPages), "Data saver must also skip banner pages");
Console.WriteLine("PASS: reader banner filtering, CDN variants, preserved page zero and page order, data saver");

sealed class AliasHandler : HttpMessageHandler
{
    public List<string> Queries { get; } = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        string html;
        if (request.Method == HttpMethod.Post)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            var query = WebUtility.UrlDecode(body["search=".Length..]);
            Queries.Add(query);
            html = query switch
            {
                "Tên tiếng Việt kiểm thử" => "<li><a href=\"https://truyenggvn.com/truyen-tranh/unrelated\"><p class=\"name\">Unrelated story</p></a></li>",
                "Sono Bisque Doll wa Koi o Suru" => "<li><div class=\"no_result\">No results</div></li>",
                "My Dress-Up Darling" => "<li><a href=\"https://truyenggvn.com/truyen-tranh/alias-regression-test\"><p class=\"name\">My Dress-Up Darling</p></a></li>",
                _ => throw new Exception("Unexpected alias query: " + query)
            };
        }
        else
        {
            CheckPath(request.RequestUri!.AbsolutePath);
            html = "<div class=\"works-chapter-item\"><div class=\"col-md-10 name-chap\"><a href=\"/truyen-tranh/alias-regression-test/chapter-111\">Chương 111</a></div><div class=\"col-md-2 time-chap\">1 ngày trước</div>";
        }
        return new(HttpStatusCode.OK) { Content = new StringContent(html) };
    }
    private static void CheckPath(string path)
    {
        if (path != "/truyen-tranh/alias-regression-test") throw new Exception("Unexpected story: " + path);
    }
}

sealed class FakeHandler(Func<Uri, Task<string>> respond) : HttpMessageHandler
{
    public ConcurrentQueue<Uri> Requests { get; } = new();
    public bool FailDirect { get; init; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!;
        Requests.Enqueue(uri);
        if (FailDirect && uri.Host == "api.mangadex.org") return new(HttpStatusCode.ServiceUnavailable);
        return new(HttpStatusCode.OK) { Content = new StringContent(await respond(uri)) };
    }
}
