using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Text.Json.Nodes;
using StackExchange.Redis;
using Meilisearch;
using TruyenDex.Api;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", true, true).AddEnvironmentVariables();
var key = builder.Configuration["Jwt:Key"] ?? throw new InvalidOperationException("Run scripts/setup.ps1 or configure Jwt__Key.");
if (key.Length < 32) throw new InvalidOperationException("Jwt key must be at least 32 characters.");
builder.Services.AddDbContext<AppDb>(o => o.UseNpgsql(builder.Configuration.GetConnectionString("Database")));
builder.Services.AddMemoryCache(o => o.SizeLimit = 1000);
builder.Services.AddHttpClient();

// Redis registration
var redisConn = builder.Configuration.GetConnectionString("Redis");
if (!string.IsNullOrWhiteSpace(redisConn)) {
    try {
        var muxer = ConnectionMultiplexer.Connect(redisConn);
        builder.Services.AddSingleton<IConnectionMultiplexer>(muxer);
    } catch (Exception ex) {
        Console.WriteLine($"[WARN] Could not connect to Redis at {redisConn}: {ex.Message}");
    }
}

// Meilisearch registration
var meiliUrl = builder.Configuration["Meilisearch:Url"];
var meiliKey = builder.Configuration["Meilisearch:ApiKey"];
if (!string.IsNullOrWhiteSpace(meiliUrl)) {
    builder.Services.AddSingleton(new MeilisearchClient(meiliUrl, meiliKey));
}

builder.Services.AddHttpClient<Catalog>(c => { c.Timeout = TimeSpan.FromSeconds(15); c.DefaultRequestHeaders.UserAgent.ParseAdd("TruyenDexClone/1.0"); });
builder.Services.AddHttpClient<TruyenGg>(c => { c.Timeout = TimeSpan.FromSeconds(15); c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36"); });
// AddHttpClient already registers these typed clients. Re-registering the
// classes here replaces their configured clients with the default HttpClient
// (without User-Agent/timeout), which MangaDex rejects with HTTP 400.
builder.Services.AddScoped<PasswordHasher<AppUser>>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o => {
    o.TokenValidationParameters = new() {
    ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true, ValidateIssuerSigningKey = true,
    ValidIssuer = "truyendex-local", ValidAudience = "truyendex-web", IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), ClockSkew = TimeSpan.FromSeconds(30)
    };
    o.Events = new JwtBearerEvents {
        OnTokenValidated = async ctx => {
            if (!Guid.TryParse(ctx.Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id)) {
                ctx.Fail("Invalid user.");
                return;
            }
            var db = ctx.HttpContext.RequestServices.GetRequiredService<AppDb>();
            var u = await db.Users.AsNoTracking().Where(x => x.Id == id)
                .Select(x => new { x.PasswordVersion, x.IsBanned }).SingleOrDefaultAsync(ctx.HttpContext.RequestAborted);
            var version = ctx.Principal?.FindFirstValue("pwd_ver") ?? "0";
            if (u is null || u.IsBanned || version != u.PasswordVersion.ToString(System.Globalization.CultureInfo.InvariantCulture))
                ctx.Fail("Session expired.");
        }
    };
});
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(o => {
    o.RejectionStatusCode = 429;
    o.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new() { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var app = builder.Build();
app.Use(async (ctx, next) => {
    ctx.Response.Headers.XContentTypeOptions = "nosniff";
    try { await next(); }
    catch (UpstreamException e) { ctx.Response.StatusCode = e.Status; await ctx.Response.WriteAsJsonAsync(new { message = e.Message }); }
    catch (DbUpdateException e) when (e.InnerException is Npgsql.PostgresException { SqlState: "23505" }) {
        ctx.Response.StatusCode = 409; await ctx.Response.WriteAsJsonAsync(new { message = "Dữ liệu đã tồn tại. Vui lòng tải lại và thử lại." });
    }
    catch (Exception e) when (e is not OperationCanceledException) {
        app.Logger.LogError(e, "Request failed");
        ctx.Response.StatusCode = 500; await ctx.Response.WriteAsJsonAsync(new { message = "Có lỗi xử lý yêu cầu. Vui lòng thử lại." });
    }
});
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseStaticFiles();
Guid UserId(ClaimsPrincipal u) => Guid.Parse(u.FindFirstValue(ClaimTypes.NameIdentifier)!);
object Session(AppUser u) {
    var jwt = new JwtSecurityToken("truyendex-local", "truyendex-web", [new(ClaimTypes.NameIdentifier, u.Id.ToString()), new(ClaimTypes.Name, u.Name), new(ClaimTypes.Role, u.Role), new("pwd_ver", u.PasswordVersion.ToString(System.Globalization.CultureInfo.InvariantCulture))],
        expires: DateTime.UtcNow.AddDays(7), signingCredentials: new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256));
    return new { token = new JwtSecurityTokenHandler().WriteToken(jwt), user = new { u.Id, u.Name, u.Email, u.Role, u.Coins, u.IsBanned, canChangePassword = !string.IsNullOrEmpty(u.PasswordHash) } };
}
async Task Remember(AppDb db, MangaCard m, MeilisearchClient? meili = null) {
    try {
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Mangas" ("Id","Title","AlternativeTitle","Author","Cover","Description","Genres","Status","Country","Demographic","Year","Featured","IsDemo","Views","UpdatedAt")
            VALUES ({m.Id},{m.Title},{m.AlternativeTitle},{m.Author},{m.Cover},{m.Description},{m.Genres},{m.Status},{m.Country},{m.Demographic},{m.Year ?? 0},false,false,0,{m.UpdatedAt})
            ON CONFLICT ("Id") DO UPDATE SET "Title"=EXCLUDED."Title", "Cover"=EXCLUDED."Cover", "UpdatedAt"=EXCLUDED."UpdatedAt"
            """);
        if (meili != null) {
            _ = Task.Run(async () => {
                try {
                    var index = meili.Index("mangas");
                    await index.AddDocumentsAsync(new[] { m });
                } catch { }
            });
        }
    } catch { }
}
async Task EnsureWelcomeNotification(AppDb db, Guid userId, string userName) {
    try {
        var hasWelcome = await db.Notifications.AnyAsync(n => n.UserId == userId && n.Type == "welcome");
        if (!hasWelcome) {
            db.Notifications.Add(new Notification {
                UserId = userId,
                ActorId = null,
                ActorName = "DokiTruyen",
                MangaId = Guid.Empty,
                MangaTitle = "Hệ thống DokiTruyen",
                CommentId = null,
                Type = "welcome",
                Message = $"Chào mừng {userName} đã đến với DokiTruyen! Chúc bạn có những phút giây đọc truyện thật vui vẻ và thư giãn 🎉",
                IsRead = false,
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }
    } catch { }
}
app.MapGet("/api/health", async (AppDb db, IServiceProvider sp) => {
    var dbOk = await db.Database.CanConnectAsync();
    var redis = sp.GetService<IConnectionMultiplexer>();
    var redisOk = redis != null && redis.IsConnected;
    var meili = sp.GetService<MeilisearchClient>();
    var meiliOk = false;
    if (meili != null) {
        try { meiliOk = await meili.IsHealthyAsync(); } catch { }
    }
    return Results.Ok(new {
        status = dbOk ? "healthy" : "degraded",
        database = dbOk ? "connected" : "disconnected",
        redis = redisOk ? "connected" : "disabled/unreachable",
        meilisearch = meiliOk ? "connected" : "disabled/unreachable",
        ports = new {
            redis = 6379,
            meilisearch = 7709,
            postgres = 54329
        },
        source = "TruyenDex / MangaDex / TruyenGGVN"
    });
});
app.MapPost("/api/auth/register", async (RegisterRequest req, AppDb db, PasswordHasher<AppUser> hasher) => {
    var email = (req.Email ?? "").Trim().ToLowerInvariant(); var name = (req.Name ?? "").Trim();
    if (email.Length > 254 || !System.Net.Mail.MailAddress.TryCreate(email, out var parsed) || parsed.Address != email || name.Length is < 2 or > 60 || (req.Password?.Length ?? 0) is < 10 or > 128)
        return Results.BadRequest(new { message = "Tên từ 2–60 ký tự, email hợp lệ và mật khẩu từ 10–128 ký tự." });
    if (await db.Users.AnyAsync(u => u.Email == email)) return Results.Conflict(new { message = "Email này đã được đăng ký." });
    var u = new AppUser { Email = email, Name = name }; u.PasswordHash = hasher.HashPassword(u, req.Password!);
    db.Users.Add(u); await db.SaveChangesAsync();
    await EnsureWelcomeNotification(db, u.Id, u.Name);
    return Results.Ok(Session(u));
}).RequireRateLimiting("auth");
app.MapPost("/api/auth/login", async (LoginRequest req, AppDb db, PasswordHasher<AppUser> hasher) => {
    if (req.Password is null || req.Password.Length > 128) return Results.BadRequest(new { message = "Thông tin đăng nhập không hợp lệ." });
    var email = (req.Email ?? "").Trim().ToLowerInvariant();
    var u = await db.Users.SingleOrDefaultAsync(x => x.Email == email);
    if (u is null && email == "admin")
    {
        u = await db.Users.FirstOrDefaultAsync(x => x.Role == "admin");
    }

    if (u is null || u.IsBanned || string.IsNullOrEmpty(u.PasswordHash) || hasher.VerifyHashedPassword(u, u.PasswordHash, req.Password) == PasswordVerificationResult.Failed)
        return Results.Json(new { message = "Email hoặc mật khẩu không đúng." }, statusCode: 401);
    await EnsureWelcomeNotification(db, u.Id, u.Name);
    return Results.Ok(Session(u));
}).RequireRateLimiting("auth");

var googleClientId = builder.Configuration["Google:ClientId"] ?? builder.Configuration["GOOGLE_CLIENT_ID"] ?? "";
var googleClientSecret = builder.Configuration["Google:ClientSecret"] ?? builder.Configuration["GOOGLE_CLIENT_SECRET"] ?? "";

app.MapGet("/api/auth/google/config", () => Results.Ok(new { clientId = googleClientId }));

app.MapPost("/api/auth/google", async (GoogleAuthRequest req, AppDb db, IHttpClientFactory httpFactory) => {
    var client = httpFactory.CreateClient();
    string? email = null;
    string? name = null;

    if (!string.IsNullOrWhiteSpace(req.Credential))
    {
        try
        {
            var res = await client.GetAsync($"https://oauth2.googleapis.com/tokeninfo?id_token={Uri.EscapeDataString(req.Credential.Trim())}");
            if (res.IsSuccessStatusCode)
            {
                var info = await res.Content.ReadFromJsonAsync<JsonNode>();
                var aud = info?["aud"]?.ToString();
                if (aud == googleClientId)
                {
                    email = info?["email"]?.ToString();
                    name = info?["name"]?.ToString();
                }
            }
        }
        catch { }
    }
    else if (!string.IsNullOrWhiteSpace(req.Code))
    {
        try
        {
            var tokenRes = await client.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(new Dictionary<string, string> {
                { "code", req.Code.Trim() },
                { "client_id", googleClientId },
                { "client_secret", googleClientSecret },
                { "redirect_uri", req.RedirectUri ?? "" },
                { "grant_type", "authorization_code" }
            }));
            if (tokenRes.IsSuccessStatusCode)
            {
                var tokenJson = await tokenRes.Content.ReadFromJsonAsync<JsonNode>();
                var accessToken = tokenJson?["access_token"]?.ToString();
                var idToken = tokenJson?["id_token"]?.ToString();

                if (!string.IsNullOrEmpty(accessToken))
                {
                    using var userReq = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/oauth2/v3/userinfo");
                    userReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
                    using var userRes = await client.SendAsync(userReq);
                    if (userRes.IsSuccessStatusCode)
                    {
                        var userInfo = await userRes.Content.ReadFromJsonAsync<JsonNode>();
                        email = userInfo?["email"]?.ToString();
                        name = userInfo?["name"]?.ToString();
                    }
                }
                if (string.IsNullOrEmpty(email) && !string.IsNullOrEmpty(idToken))
                {
                    var infoRes = await client.GetAsync($"https://oauth2.googleapis.com/tokeninfo?id_token={Uri.EscapeDataString(idToken)}");
                    if (infoRes.IsSuccessStatusCode)
                    {
                        var info = await infoRes.Content.ReadFromJsonAsync<JsonNode>();
                        email = info?["email"]?.ToString();
                        name = info?["name"]?.ToString();
                    }
                }
            }
        }
        catch { }
    }

    if (string.IsNullOrWhiteSpace(email))
    {
        return Results.BadRequest(new { message = "Không thể xác thực thông tin đăng nhập Google." });
    }

    email = email.Trim().ToLowerInvariant();
    var u = await db.Users.SingleOrDefaultAsync(x => x.Email == email);
    if (u is null)
    {
        var displayName = string.IsNullOrWhiteSpace(name) ? email.Split('@')[0] : name.Trim();
        if (displayName.Length > 60) displayName = displayName[..60];
        u = new AppUser { Email = email, Name = displayName, PasswordHash = "", Role = "reader" };
        db.Users.Add(u);
        await db.SaveChangesAsync();
    }

    await EnsureWelcomeNotification(db, u.Id, u.Name);
    return Results.Ok(Session(u));
}).RequireRateLimiting("auth");

app.MapGet("/api/auth/google/login", (string? returnUrl, HttpContext ctx) => {
    var scheme = ctx.Request.Scheme;
    var host = ctx.Request.Host.Value;
    if (ctx.Request.Headers.TryGetValue("X-Forwarded-Proto", out var proto)) scheme = proto.ToString();
    if (ctx.Request.Headers.TryGetValue("X-Forwarded-Host", out var fHost)) host = fHost.ToString();
    var redirectUri = $"{scheme}://{host}/api/auth/google/callback";
    var state = Uri.EscapeDataString(returnUrl ?? "/");
    var url = $"https://accounts.google.com/o/oauth2/v2/auth?client_id={Uri.EscapeDataString(googleClientId)}&redirect_uri={Uri.EscapeDataString(redirectUri)}&response_type=code&scope=openid%20email%20profile&state={state}&prompt=select_account";
    return Results.Redirect(url);
});

app.MapGet("/api/auth/google/callback", async (string? code, string? state, string? error, HttpContext ctx, AppDb db, IHttpClientFactory httpFactory) => {
    if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code))
    {
        return Results.Redirect("/dang-nhap?error=" + Uri.EscapeDataString(error ?? "Đăng nhập Google thất bại"));
    }

    var scheme = ctx.Request.Scheme;
    var host = ctx.Request.Host.Value;
    if (ctx.Request.Headers.TryGetValue("X-Forwarded-Proto", out var proto)) scheme = proto.ToString();
    if (ctx.Request.Headers.TryGetValue("X-Forwarded-Host", out var fHost)) host = fHost.ToString();
    var redirectUri = $"{scheme}://{host}/api/auth/google/callback";

    var client = httpFactory.CreateClient();
    var tokenRes = await client.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(new Dictionary<string, string> {
        { "code", code },
        { "client_id", googleClientId },
        { "client_secret", googleClientSecret },
        { "redirect_uri", redirectUri },
        { "grant_type", "authorization_code" }
    }));

    if (!tokenRes.IsSuccessStatusCode)
    {
        return Results.Redirect("/dang-nhap?error=" + Uri.EscapeDataString("Không thể xác thực mã từ Google"));
    }

    var tokenJson = await tokenRes.Content.ReadFromJsonAsync<JsonNode>();
    var accessToken = tokenJson?["access_token"]?.ToString();
    var idToken = tokenJson?["id_token"]?.ToString();

    string? email = null;
    string? name = null;

    if (!string.IsNullOrEmpty(accessToken))
    {
        using var userReq = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/oauth2/v3/userinfo");
        userReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        using var userRes = await client.SendAsync(userReq);
        if (userRes.IsSuccessStatusCode)
        {
            var userInfo = await userRes.Content.ReadFromJsonAsync<JsonNode>();
            email = userInfo?["email"]?.ToString();
            name = userInfo?["name"]?.ToString();
        }
    }

    if (string.IsNullOrEmpty(email) && !string.IsNullOrEmpty(idToken))
    {
        var infoRes = await client.GetAsync($"https://oauth2.googleapis.com/tokeninfo?id_token={Uri.EscapeDataString(idToken)}");
        if (infoRes.IsSuccessStatusCode)
        {
            var info = await infoRes.Content.ReadFromJsonAsync<JsonNode>();
            email = info?["email"]?.ToString();
            name = info?["name"]?.ToString();
        }
    }

    if (string.IsNullOrEmpty(email))
    {
        return Results.Redirect("/dang-nhap?error=" + Uri.EscapeDataString("Không lấy được email từ tài khoản Google"));
    }

    email = email.Trim().ToLowerInvariant();
    var u = await db.Users.SingleOrDefaultAsync(x => x.Email == email);
    if (u is null)
    {
        var displayName = string.IsNullOrWhiteSpace(name) ? email.Split('@')[0] : name.Trim();
        if (displayName.Length > 60) displayName = displayName[..60];
        u = new AppUser { Email = email, Name = displayName, PasswordHash = "", Role = "reader" };
        db.Users.Add(u);
        await db.SaveChangesAsync();
    }

    var sess = Session(u);
    var tokenProp = sess.GetType().GetProperty("token")?.GetValue(sess)?.ToString();
    var targetUrl = !string.IsNullOrEmpty(state) ? Uri.UnescapeDataString(state) : "/";
    if (!targetUrl.StartsWith("/")) targetUrl = "/";

    return Results.Redirect($"/dang-nhap?token={Uri.EscapeDataString(tokenProp ?? "")}&returnUrl={Uri.EscapeDataString(targetUrl)}");
});
app.MapGet("/api/auth/me", async (ClaimsPrincipal user, AppDb db) => {
    var u = await db.Users.FindAsync(UserId(user));
    return u is null ? Results.Unauthorized() : Results.Ok(new { u.Id, u.Name, u.Email, u.Role, u.Coins, u.IsBanned, canChangePassword = !string.IsNullOrEmpty(u.PasswordHash) });
}).RequireAuthorization();
app.MapPost("/api/auth/change-password", async (ChangePasswordRequest req, ClaimsPrincipal user, AppDb db, PasswordHasher<AppUser> hasher) => {
    if ((req.CurrentPassword?.Length ?? 0) is < 1 or > 128 || (req.NewPassword?.Length ?? 0) is < 10 or > 128)
        return Results.BadRequest(new { message = "Mật khẩu mới phải có từ 10–128 ký tự." });
    if (req.NewPassword != req.ConfirmPassword)
        return Results.BadRequest(new { message = "Mật khẩu xác nhận chưa trùng khớp." });
    if (req.CurrentPassword == req.NewPassword)
        return Results.BadRequest(new { message = "Mật khẩu mới phải khác mật khẩu hiện tại." });
    var u = await db.Users.FindAsync(UserId(user));
    if (u is null || u.IsBanned) return Results.Unauthorized();
    if (string.IsNullOrEmpty(u.PasswordHash))
        return Results.BadRequest(new { message = "Tài khoản này đăng nhập bằng Google. Hãy đổi mật khẩu trong tài khoản Google của bạn." });
    if (hasher.VerifyHashedPassword(u, u.PasswordHash, req.CurrentPassword!) == PasswordVerificationResult.Failed)
        return Results.BadRequest(new { message = "Mật khẩu hiện tại không đúng." });

    var previousHash = u.PasswordHash;
    var previousVersion = u.PasswordVersion;
    var newHash = hasher.HashPassword(u, req.NewPassword!);
    var updated = await db.Users.Where(x => x.Id == u.Id && x.PasswordHash == previousHash && x.PasswordVersion == previousVersion)
        .ExecuteUpdateAsync(s => s.SetProperty(x => x.PasswordHash, newHash).SetProperty(x => x.PasswordVersion, previousVersion + 1));
    if (updated != 1)
        return Results.Conflict(new { message = "Mật khẩu đã được thay đổi ở phiên khác. Vui lòng đăng nhập lại." });
    u.PasswordHash = newHash;
    u.PasswordVersion = previousVersion + 1;
    return Results.Ok(Session(u));
}).RequireAuthorization().RequireRateLimiting("auth");
app.MapGet("/api/catalog/home", async (int? page, int? pageSize, Catalog catalog) => {
    if ((page ?? 1) is < 1 or > 5000) return Results.BadRequest(new { message = "Trang không hợp lệ." });
    return Results.Ok(await catalog.Home(page ?? 1, Math.Clamp(pageSize ?? 28, 1, 28)));
});
app.MapGet("/api/catalog/featured", async (int? limit, Catalog catalog) => {
    return Results.Ok(await catalog.Featured(Math.Clamp(limit ?? 20, 1, 50)));
});
app.MapGet("/api/catalog/search", async (string? page, string? pageSize, string? q, string? author, string? genre, string? status, string? country, string? demographic, string? language, string? sort, string? year, Catalog catalog) => {
    var p = int.TryParse(page, out var pi) ? Math.Max(1, pi) : 1;
    var size = int.TryParse(pageSize, out var si) ? Math.Clamp(si, 1, 28) : 24;
    var y = int.TryParse(year, out var yi) && yi is >= 1900 and <= 2100 ? yi : (int?)null;
    if (p < 1 || (long)p * size > 10000 || q?.Length > 250 || author?.Length > 250) return Results.BadRequest(new { message = "Bộ lọc hoặc trang không hợp lệ." });
    return Results.Ok(await catalog.Search(p, size, q, author, genre, status, country, demographic, language, sort, y));
});
app.MapGet("/api/catalog/author/{name}", async (string name, string? page, string? pageSize, string? sort, Catalog catalog) => {
    var p = int.TryParse(page, out var pi) ? Math.Max(1, pi) : 1;
    var size = int.TryParse(pageSize, out var si) ? Math.Clamp(si, 1, 28) : 24;
    if (string.IsNullOrWhiteSpace(name) || name.Length > 250) return Results.BadRequest(new { message = "Tên tác giả không hợp lệ." });
    return Results.Ok(await catalog.GetAuthorManga(name.Trim(), p, size, sort));
});
app.MapGet("/api/catalog/author", async (string? name, string? page, string? pageSize, string? sort, Catalog catalog) => {
    if (string.IsNullOrWhiteSpace(name)) return Results.Ok(new CatalogPage([], 0, 1, 24));
    var p = int.TryParse(page, out var pi) ? Math.Max(1, pi) : 1;
    var size = int.TryParse(pageSize, out var si) ? Math.Clamp(si, 1, 28) : 24;
    return Results.Ok(await catalog.GetAuthorManga(name.Trim(), p, size, sort));
});
app.MapGet("/api/catalog/tags", (Catalog catalog) => catalog.Tags());
app.MapGet("/api/catalog/{id:guid}", async (Guid id, Catalog catalog, AppDb db) => { var m = await catalog.Detail(id); await Remember(db, m); return m; });
app.MapGet("/api/catalog/{id:guid}/chapters", async (Guid id, string? language, int? page, bool? ascending, Catalog catalog) => {
    if ((page ?? 1) is < 1 or > 100) return Results.BadRequest(new { message = "Trang chương không hợp lệ." });
    return Results.Ok(await catalog.Chapters(id, language ?? "vi", page ?? 1, ascending ?? false));
});
app.MapGet("/api/chapters/{id:guid}", async (Guid id, Catalog catalog, AppDb db) => {
    var r = await catalog.Read(id);
    try { await Remember(db, r.Manga); } catch { }
    var c = r.Chapter;
    try {
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Chapters" ("Id","MangaId","Number","Title","Language","Pages","PublishedAt")
            VALUES ({c.Id},{c.MangaId},{c.Number},{c.Title},{c.Language},{Array.Empty<string>()},{c.PublishedAt}) ON CONFLICT ("Id") DO NOTHING
            """);
    } catch { }
    return r;
});
app.MapGet("/api/library", async (ClaimsPrincipal user, AppDb db) => {
    var uid = UserId(user);
    var follows = await db.Follows.Where(x => x.UserId == uid).OrderByDescending(x => x.CreatedAt).Select(x => new { x.MangaId, x.Manga.Title, x.Manga.Cover, x.Manga.Author, x.CreatedAt }).ToListAsync();
    var history = await db.Histories.Where(x => x.UserId == uid).OrderByDescending(x => x.ReadAt).Take(200).Select(x => new { x.MangaId, x.Manga.Title, x.Manga.Cover, x.ChapterId, chapterTitle = x.Chapter.Title, x.ReadAt }).ToListAsync();
    return Results.Ok(new { follows, history });
}).RequireAuthorization();
app.MapPut("/api/library/follows/{id:guid}", async (Guid id, FollowRequest req, ClaimsPrincipal user, AppDb db, Catalog catalog) => {
    var uid = UserId(user); await Remember(db, await catalog.Detail(id));
    if (req.Followed) await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"Follows\" (\"UserId\",\"MangaId\",\"CreatedAt\") VALUES ({uid},{id},{DateTime.UtcNow}) ON CONFLICT DO NOTHING");
    else await db.Follows.Where(x => x.UserId == uid && x.MangaId == id).ExecuteDeleteAsync();
    return Results.Ok(new { followed = req.Followed });
}).RequireAuthorization();
app.MapPut("/api/library/history/{chapterId:guid}", async (Guid chapterId, ClaimsPrincipal user, AppDb db) => {
    var c = await db.Chapters.FindAsync(chapterId); if (c is null) return Results.NotFound();
    var uid = UserId(user);
    await db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO "Histories" ("UserId","MangaId","ChapterId","ReadAt") VALUES ({uid},{c.MangaId},{c.Id},{DateTime.UtcNow})
        ON CONFLICT ("UserId","MangaId") DO UPDATE SET "ChapterId"=EXCLUDED."ChapterId", "ReadAt"=EXCLUDED."ReadAt"
        """);
    return Results.NoContent();
}).RequireAuthorization();
app.MapDelete("/api/library/history/{id:guid}", async (Guid id, ClaimsPrincipal user, AppDb db) => { var uid = UserId(user); await db.Histories.Where(x => x.UserId == uid && x.MangaId == id).ExecuteDeleteAsync(); return Results.NoContent(); }).RequireAuthorization();
app.MapGet("/api/comments", async (Guid? mangaId, int? page, AppDb db) => {
    var query = db.Comments.AsNoTracking(); if (mangaId.HasValue) query = query.Where(x => x.MangaId == mangaId);
    var p = Math.Clamp(page ?? 1, 1, 10000);
    var total = await query.CountAsync();
    var items = await query.OrderByDescending(x => x.CreatedAt).Skip((p - 1) * 20).Take(20).Select(x => new {
        x.Id,
        x.MangaId,
        mangaTitle = x.Manga.Title,
        x.UserId,
        name = x.User.Name,
        x.ParentId,
        replyToName = x.Parent != null ? x.Parent.User.Name : null,
        x.Body,
        x.CreatedAt
    }).ToListAsync();
    return Results.Ok(new { items, total, page = p, pageSize = 20 });
});
app.MapPost("/api/catalog/{id:guid}/comments", async (Guid id, CommentRequest req, ClaimsPrincipal user, AppDb db, Catalog catalog) => {
    var body = (req.Body ?? "").Trim();
    if (body.Length is < 1 or > 2000) return Results.BadRequest(new { message = "Bình luận từ 1–2.000 ký tự." });
    
    var currentUserId = UserId(user);
    var isAdmin = user.IsInRole("admin") || user.IsInRole("superadmin");

    if (!isAdmin)
    {
        // 1. Kiểm tra thời gian chờ giữa 2 bình luận liên tiếp (cooldown 15 giây)
        var lastComment = await db.Comments.AsNoTracking()
            .Where(c => c.UserId == currentUserId)
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => new { c.CreatedAt, c.Body })
            .FirstOrDefaultAsync();

        if (lastComment != null)
        {
            var elapsed = DateTime.UtcNow - lastComment.CreatedAt;
            var cooldownSeconds = 15;
            if (elapsed.TotalSeconds < cooldownSeconds)
            {
                var waitLeft = (int)Math.Ceiling(cooldownSeconds - elapsed.TotalSeconds);
                return Results.Json(new { message = $"Vui lòng đợi {waitLeft} giây trước khi gửi bình luận tiếp theo." }, statusCode: 429);
            }

            // 2. Chặn spam nội dung trùng lặp trong vòng 5 phút
            if (elapsed.TotalMinutes < 5 && string.Equals(lastComment.Body.Trim(), body, StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new { message = "Nội dung bình luận bị trùng lặp. Vui lòng không spam nội dung giống nhau." });
            }
        }

        // 3. Giới hạn tối đa 4 bình luận trong vòng 1 phút
        var oneMinuteAgo = DateTime.UtcNow.AddMinutes(-1);
        var recentCount = await db.Comments.CountAsync(c => c.UserId == currentUserId && c.CreatedAt >= oneMinuteAgo);
        if (recentCount >= 4)
        {
            return Results.Json(new { message = "Bạn đang gửi bình luận quá nhanh. Vui lòng thử lại sau 1 phút." }, statusCode: 429);
        }
    }

    var lowerBody = body.ToLowerInvariant();
    var bannedWords = await db.BannedKeywords.Select(k => k.Keyword).ToListAsync();
    bool flagged = bannedWords.Any(w => !string.IsNullOrWhiteSpace(w) && lowerBody.Contains(w));
    var detail = await catalog.Detail(id);
    await Remember(db, detail);
    var currentUserName = user.FindFirstValue(ClaimTypes.Name) ?? "Thành viên";
    if (string.IsNullOrWhiteSpace(currentUserName) || currentUserName == "Thành viên") {
        var u = await db.Users.FindAsync(currentUserId);
        if (u != null) currentUserName = u.Name;
    }

    Comment? parentComment = null;
    if (req.ParentId.HasValue) {
        parentComment = await db.Comments.Include(c => c.Manga).Include(c => c.User).FirstOrDefaultAsync(c => c.Id == req.ParentId.Value);
    }

    var comment = new Comment {
        MangaId = id,
        UserId = currentUserId,
        ParentId = parentComment?.Id,
        Body = body,
        IsFlagged = flagged
    };
    db.Comments.Add(comment);

    if (parentComment != null && parentComment.UserId != currentUserId) {
        var mangaTitle = !string.IsNullOrWhiteSpace(detail?.Title) ? detail.Title : (parentComment.Manga?.Title ?? "truyện");
        var preview = body.Length > 80 ? body[..77] + "..." : body;
        var notif = new Notification {
            UserId = parentComment.UserId,
            ActorId = currentUserId,
            ActorName = currentUserName,
            MangaId = id,
            MangaTitle = mangaTitle,
            CommentId = comment.Id,
            Type = "comment_reply",
            Message = $"{currentUserName} đã trả lời bình luận của bạn: \"{preview}\"",
            IsRead = false,
            CreatedAt = DateTime.UtcNow
        };
        db.Notifications.Add(notif);
    }

    await db.SaveChangesAsync(); 
    return Results.Created("/api/comments?mangaId=" + id, new { message = flagged ? "Bình luận đã gửi và đang chờ duyệt từ ngữ." : "Đã gửi bình luận." });
}).RequireAuthorization().RequireRateLimiting("auth");
app.MapDelete("/api/comments/{id:guid}", async (Guid id, ClaimsPrincipal user, AppDb db) => {
    var comment = await db.Comments.FindAsync(id); if (comment is null) return Results.NotFound();
    if (!user.IsInRole("admin") && !user.IsInRole("superadmin")) return Results.Forbid();
    db.Comments.Remove(comment); await db.SaveChangesAsync(); return Results.NoContent();
}).RequireAuthorization();

app.MapGet("/api/notifications", async (ClaimsPrincipal user, AppDb db) => {
    var uid = UserId(user);
    var uName = user.FindFirstValue(ClaimTypes.Name) ?? "bạn";
    await EnsureWelcomeNotification(db, uid, uName);
    var unreadCount = await db.Notifications.CountAsync(x => x.UserId == uid && !x.IsRead);
    var items = await db.Notifications.AsNoTracking()
        .Where(x => x.UserId == uid)
        .OrderByDescending(x => x.CreatedAt)
        .Take(30)
        .Select(x => new {
            x.Id,
            x.ActorId,
            x.ActorName,
            x.MangaId,
            x.MangaTitle,
            x.CommentId,
            x.Type,
            x.Message,
            x.IsRead,
            x.CreatedAt
        })
        .ToListAsync();
    return Results.Ok(new { items, unreadCount });
}).RequireAuthorization();

app.MapPut("/api/notifications/{id:guid}/read", async (Guid id, ClaimsPrincipal user, AppDb db) => {
    var uid = UserId(user);
    var notif = await db.Notifications.FirstOrDefaultAsync(x => x.Id == id && x.UserId == uid);
    if (notif != null && !notif.IsRead) {
        notif.IsRead = true;
        await db.SaveChangesAsync();
    }
    return Results.NoContent();
}).RequireAuthorization();

app.MapPut("/api/notifications/read-all", async (ClaimsPrincipal user, AppDb db) => {
    var uid = UserId(user);
    await db.Notifications.Where(x => x.UserId == uid && !x.IsRead)
        .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsRead, true));
    return Results.NoContent();
}).RequireAuthorization();
app.MapPut("/api/catalog/{id:guid}/rating", async (Guid id, RatingRequest req, ClaimsPrincipal user, AppDb db, Catalog catalog) => {
    if (req.Score is < 1 or > 10) return Results.BadRequest(new { message = "Điểm đánh giá từ 1–10." });
    await Remember(db, await catalog.Detail(id)); var uid = UserId(user);
    await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"Ratings\" (\"UserId\",\"MangaId\",\"Score\") VALUES ({uid},{id},{req.Score}) ON CONFLICT (\"UserId\",\"MangaId\") DO UPDATE SET \"Score\"=EXCLUDED.\"Score\"");
    return Results.NoContent();
}).RequireAuthorization();
app.MapGet("/api/catalog/{id:guid}/community", async (Guid id, ClaimsPrincipal user, AppDb db) => {
    Guid? uid = user.Identity?.IsAuthenticated == true ? UserId(user) : null;
    return Results.Ok(new { rating = await db.Ratings.Where(x => x.MangaId == id).Select(x => (double?)x.Score).AverageAsync() ?? 0, votes = await db.Ratings.CountAsync(x => x.MangaId == id), myRating = await db.Ratings.Where(x => x.MangaId == id && x.UserId == uid).Select(x => x.Score).FirstOrDefaultAsync() });
});
app.MapGet("/api/catalog/image-proxy", async (string url, HttpContext ctx, IHttpClientFactory factory, CancellationToken ct) => {
    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        return Results.BadRequest(new { message = "Địa chỉ ảnh không hợp lệ." });
    var client = factory.CreateClient();
    client.Timeout = TimeSpan.FromSeconds(25);
    var req = new HttpRequestMessage(HttpMethod.Get, url);
    req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
    if (uri.Host.Contains("truyengg") || uri.Host.Contains("hinhhinh") || uri.Host.Contains("hinhtruyen") || uri.Host.Contains("truyenvua") || uri.Host.Contains("tintruyen") || uri.Host.Contains("blogspot") || uri.Host.Contains("bp.blogspot") || uri.Host.Contains("nettruyen") || uri.Host.Contains("nettrom"))
    {
        req.Headers.Referrer = new Uri("https://truyenggvn.com/");
    }
    else
    {
        req.Headers.Referrer = new Uri($"{uri.Scheme}://{uri.Host}/");
    }
    var res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
    if (!res.IsSuccessStatusCode) return Results.StatusCode((int)res.StatusCode);
    var contentType = res.Content.Headers.ContentType?.ToString() ?? "image/jpeg";
    ctx.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
    return Results.Stream(await res.Content.ReadAsStreamAsync(ct), contentType);
});
static string EscapeXml(string? input) => string.IsNullOrEmpty(input) ? "" : input.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&apos;");

async Task<IResult> GenerateSitemap(AppDb db, Catalog catalog, HttpContext ctx)
{
    const string baseUrl = "https://dokitruyen.com";
    var sb = new StringBuilder();
    sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
    sb.AppendLine("<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\" xmlns:image=\"http://www.google.com/schemas/sitemap-image/1.1\">");
    
    var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
    sb.AppendLine($"  <url><loc>{baseUrl}/</loc><lastmod>{today}</lastmod><changefreq>hourly</changefreq><priority>1.0</priority></url>");
    sb.AppendLine($"  <url><loc>{baseUrl}/tim-truyen-nang-cao</loc><lastmod>{today}</lastmod><changefreq>daily</changefreq><priority>0.9</priority></url>");
    sb.AppendLine($"  <url><loc>{baseUrl}/tim-truyen-nang-cao?country=jp</loc><lastmod>{today}</lastmod><changefreq>daily</changefreq><priority>0.8</priority></url>");
    sb.AppendLine($"  <url><loc>{baseUrl}/tim-truyen-nang-cao?country=kr</loc><lastmod>{today}</lastmod><changefreq>daily</changefreq><priority>0.8</priority></url>");
    sb.AppendLine($"  <url><loc>{baseUrl}/tim-truyen-nang-cao?country=cn</loc><lastmod>{today}</lastmod><changefreq>daily</changefreq><priority>0.8</priority></url>");
    sb.AppendLine($"  <url><loc>{baseUrl}/tim-truyen-nang-cao?sort=rating</loc><lastmod>{today}</lastmod><changefreq>daily</changefreq><priority>0.8</priority></url>");
    sb.AppendLine($"  <url><loc>{baseUrl}/tim-truyen-nang-cao?sort=views</loc><lastmod>{today}</lastmod><changefreq>daily</changefreq><priority>0.8</priority></url>");
    sb.AppendLine($"  <url><loc>{baseUrl}/tac-gia</loc><lastmod>{today}</lastmod><changefreq>weekly</changefreq><priority>0.7</priority></url>");

    var seenIds = new HashSet<Guid>();
    var seenAuthors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    try
    {
        var mangas = await db.Mangas
            .AsNoTracking()
            .Where(m => !m.IsHidden && !m.IsDraft)
            .OrderByDescending(m => m.UpdatedAt)
            .Take(5000)
            .Select(m => new {
                m.Id,
                m.Title,
                m.Cover,
                m.Author,
                m.UpdatedAt,
                Chapters = m.Chapters.OrderByDescending(c => c.Number).Take(5).Select(c => new { c.Id, c.Number, c.PublishedAt }).ToList()
            })
            .ToListAsync();

        foreach (var m in mangas)
        {
            seenIds.Add(m.Id);
            var lastmod = m.UpdatedAt.ToString("yyyy-MM-dd");
            sb.AppendLine("  <url>");
            sb.AppendLine($"    <loc>{baseUrl}/truyen-tranh/{m.Id}</loc>");
            sb.AppendLine($"    <lastmod>{lastmod}</lastmod>");
            sb.AppendLine("    <changefreq>daily</changefreq>");
            sb.AppendLine("    <priority>0.9</priority>");
            if (!string.IsNullOrWhiteSpace(m.Cover))
            {
                var coverUrl = m.Cover.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? m.Cover
                    : $"{baseUrl}{m.Cover}";
                sb.AppendLine("    <image:image>");
                sb.AppendLine($"      <image:loc>{EscapeXml(coverUrl)}</image:loc>");
                sb.AppendLine($"      <image:title>{EscapeXml(m.Title)}</image:title>");
                sb.AppendLine("    </image:image>");
            }
            sb.AppendLine("  </url>");

            foreach (var ch in m.Chapters)
            {
                var chMod = ch.PublishedAt.ToString("yyyy-MM-dd");
                sb.AppendLine($"  <url><loc>{baseUrl}/chuong/{ch.Id}</loc><lastmod>{chMod}</lastmod><changefreq>weekly</changefreq><priority>0.7</priority></url>");
            }

            if (!string.IsNullOrWhiteSpace(m.Author) && seenAuthors.Add(m.Author.Trim()))
            {
                var encAuthor = Uri.EscapeDataString(m.Author.Trim());
                sb.AppendLine($"  <url><loc>{baseUrl}/tac-gia/{encAuthor}</loc><lastmod>{lastmod}</lastmod><changefreq>weekly</changefreq><priority>0.6</priority></url>");
            }
        }
    }
    catch { }

    try
    {
        var home = await catalog.Home(1, 28);
        foreach (var m in home.Items)
        {
            if (seenIds.Add(m.Id))
            {
                var lastmod = m.UpdatedAt.ToString("yyyy-MM-dd");
                sb.AppendLine("  <url>");
                sb.AppendLine($"    <loc>{baseUrl}/truyen-tranh/{m.Id}</loc>");
                sb.AppendLine($"    <lastmod>{lastmod}</lastmod>");
                sb.AppendLine("    <changefreq>daily</changefreq>");
                sb.AppendLine("    <priority>0.8</priority>");
                if (!string.IsNullOrWhiteSpace(m.Cover))
                {
                    var coverUrl = m.Cover.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                        ? m.Cover
                        : $"{baseUrl}{m.Cover}";
                    sb.AppendLine("    <image:image>");
                    sb.AppendLine($"      <image:loc>{EscapeXml(coverUrl)}</image:loc>");
                    sb.AppendLine($"      <image:title>{EscapeXml(m.Title)}</image:title>");
                    sb.AppendLine("    </image:image>");
                }
                sb.AppendLine("  </url>");
            }
        }
    }
    catch { }

    sb.AppendLine("</urlset>");
    ctx.Response.Headers.CacheControl = "public, max-age=1800, s-maxage=3600";
    return Results.Content(sb.ToString(), "application/xml", Encoding.UTF8);
}
app.MapGet("/api/sitemap.xml", GenerateSitemap);
app.MapGet("/sitemap.xml", GenerateSitemap);
app.MapGet("/sitemap_index.xml", GenerateSitemap);
using (var scope = app.Services.CreateScope()) {
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
    await db.Database.MigrateAsync();
    try {
        await db.Database.ExecuteSqlRawAsync(@"
            ALTER TABLE ""Comments"" ADD COLUMN IF NOT EXISTS ""ParentId"" uuid NULL;
            CREATE TABLE IF NOT EXISTS ""Notifications"" (
                ""Id"" uuid NOT NULL PRIMARY KEY,
                ""UserId"" uuid NOT NULL,
                ""ActorId"" uuid NULL,
                ""ActorName"" character varying(200) NOT NULL DEFAULT '',
                ""MangaId"" uuid NOT NULL,
                ""MangaTitle"" character varying(500) NOT NULL DEFAULT '',
                ""CommentId"" uuid NULL,
                ""Type"" character varying(50) NOT NULL DEFAULT 'comment_reply',
                ""Message"" character varying(1000) NOT NULL DEFAULT '',
                ""IsRead"" boolean NOT NULL DEFAULT FALSE,
                ""CreatedAt"" timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP,
                CONSTRAINT ""FK_Notifications_Users_UserId"" FOREIGN KEY (""UserId"") REFERENCES ""Users"" (""Id"") ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS ""IX_Notifications_UserId_CreatedAt"" ON ""Notifications"" (""UserId"", ""CreatedAt"" DESC);
        ");
    } catch { }
    var hasher = scope.ServiceProvider.GetRequiredService<PasswordHasher<AppUser>>();
    var adminUser = await db.Users.FirstOrDefaultAsync(x => x.Email == "admin")
                 ?? await db.Users.FirstOrDefaultAsync(x => x.Role == "admin");
    if (adminUser == null) {
        adminUser = new AppUser { Email = (builder.Configuration["Seed:AdminEmail"] ?? "admin").Trim().ToLowerInvariant(), Name = "Quản trị viên", Role = "admin" };
        var initialPassword = builder.Configuration["Seed:AdminPassword"] ?? throw new InvalidOperationException("Configure Seed__AdminPassword for the first administrator.");
        adminUser.PasswordHash = hasher.HashPassword(adminUser, initialPassword);
        db.Users.Add(adminUser);
    }
    await AdminEndpoints.SeedDefaultAdminDataAsync(db);
    await db.SaveChangesAsync();
}
app.MapAdminEndpoints();
app.Run();
