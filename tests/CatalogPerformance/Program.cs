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

using var fallbackCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 1000 });
var fallbackHandler = new FakeHandler(uri => Task.FromResult("{\"data\":[]}")) { FailDirect = true };
using var fallbackHttp = new HttpClient(fallbackHandler);
var fallbackCatalog = new Catalog(fallbackHttp, fallbackCache, new TruyenGg(fallbackHttp, fallbackCache));
await fallbackCatalog.Get("/manga?limit=1");
Check(fallbackHandler.Requests.Select(x => x.Host).SequenceEqual(new[] { "api.mangadex.org", "api-proxy.truyendex.cc" }), "Proxy fallback must remain available");
Console.WriteLine("PASS: proxy fallback on direct-source failure");

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
