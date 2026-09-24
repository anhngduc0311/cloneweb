using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using TruyenDex.Api;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", true, true).AddEnvironmentVariables();
var key = builder.Configuration["Jwt:Key"] ?? throw new InvalidOperationException("Run scripts/setup.ps1 or configure Jwt__Key.");
if (key.Length < 32) throw new InvalidOperationException("Jwt key must be at least 32 characters.");
builder.Services.AddDbContext<AppDb>(o => o.UseNpgsql(builder.Configuration.GetConnectionString("Database")));
builder.Services.AddMemoryCache(o => o.SizeLimit = 1000);
builder.Services.AddHttpClient<Catalog>(c => { c.Timeout = TimeSpan.FromSeconds(15); c.DefaultRequestHeaders.UserAgent.ParseAdd("TruyenDexClone/1.0"); });
builder.Services.AddScoped<PasswordHasher<AppUser>>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o => o.TokenValidationParameters = new() {
    ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true, ValidateIssuerSigningKey = true,
    ValidIssuer = "truyendex-local", ValidAudience = "truyendex-web", IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), ClockSkew = TimeSpan.FromSeconds(30)
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
Guid UserId(ClaimsPrincipal u) => Guid.Parse(u.FindFirstValue(ClaimTypes.NameIdentifier)!);
object Session(AppUser u) {
    var jwt = new JwtSecurityToken("truyendex-local", "truyendex-web", [new(ClaimTypes.NameIdentifier, u.Id.ToString()), new(ClaimTypes.Name, u.Name), new(ClaimTypes.Role, u.Role)],
        expires: DateTime.UtcNow.AddDays(7), signingCredentials: new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256));
    return new { token = new JwtSecurityTokenHandler().WriteToken(jwt), user = new { u.Id, u.Name, u.Email, u.Role } };
}
async Task Remember(AppDb db, MangaCard m) {
    await db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO "Mangas" ("Id","Title","AlternativeTitle","Author","Cover","Description","Genres","Status","Country","Demographic","Year","Featured","IsDemo","Views","UpdatedAt")
        VALUES ({m.Id},{m.Title},{m.AlternativeTitle},{m.Author},{m.Cover},{m.Description},{m.Genres},{m.Status},{m.Country},{m.Demographic},{m.Year ?? 0},false,false,0,{m.UpdatedAt})
        ON CONFLICT ("Id") DO UPDATE SET "Title"=EXCLUDED."Title", "Cover"=EXCLUDED."Cover", "UpdatedAt"=EXCLUDED."UpdatedAt"
        """);
}
app.MapGet("/api/health", async (AppDb db) => await db.Database.CanConnectAsync() ? Results.Ok(new { status = "healthy", database = "PostgreSQL", source = "TruyenDex / MangaDex" }) : Results.StatusCode(503));
app.MapPost("/api/auth/register", async (RegisterRequest req, AppDb db, PasswordHasher<AppUser> hasher) => {
    var email = (req.Email ?? "").Trim().ToLowerInvariant(); var name = (req.Name ?? "").Trim();
    if (email.Length > 254 || !System.Net.Mail.MailAddress.TryCreate(email, out var parsed) || parsed.Address != email || name.Length is < 2 or > 60 || (req.Password?.Length ?? 0) is < 10 or > 128)
        return Results.BadRequest(new { message = "Tên từ 2–60 ký tự, email hợp lệ và mật khẩu từ 10–128 ký tự." });
    if (await db.Users.AnyAsync(u => u.Email == email)) return Results.Conflict(new { message = "Email này đã được đăng ký." });
    var u = new AppUser { Email = email, Name = name }; u.PasswordHash = hasher.HashPassword(u, req.Password!);
    db.Users.Add(u); await db.SaveChangesAsync(); return Results.Ok(Session(u));
}).RequireRateLimiting("auth");
app.MapPost("/api/auth/login", async (LoginRequest req, AppDb db, PasswordHasher<AppUser> hasher) => {
    if (req.Password is null || req.Password.Length > 128) return Results.BadRequest(new { message = "Thông tin đăng nhập không hợp lệ." });
    var email = (req.Email ?? "").Trim().ToLowerInvariant();
    var u = await db.Users.SingleOrDefaultAsync(x => x.Email == email);
    if (u is null || hasher.VerifyHashedPassword(u, u.PasswordHash, req.Password) == PasswordVerificationResult.Failed)
        return Results.Json(new { message = "Email hoặc mật khẩu không đúng." }, statusCode: 401);
    return Results.Ok(Session(u));
}).RequireRateLimiting("auth");
app.MapGet("/api/auth/me", async (ClaimsPrincipal user, AppDb db) => {
    var u = await db.Users.FindAsync(UserId(user));
    return u is null ? Results.Unauthorized() : Results.Ok(new { u.Id, u.Name, u.Email, u.Role });
}).RequireAuthorization();
app.MapGet("/api/catalog/home", async (int? page, int? pageSize, Catalog catalog) => {
    if ((page ?? 1) is < 1 or > 5000) return Results.BadRequest(new { message = "Trang không hợp lệ." });
    return Results.Ok(await catalog.Home(page ?? 1, Math.Clamp(pageSize ?? 28, 1, 28)));
});
app.MapGet("/api/catalog/search", async (int? page, int? pageSize, string? q, string? genre, string? status, string? country, string? demographic, string? language, string? sort, int? year, Catalog catalog) => {
    var size = Math.Clamp(pageSize ?? 24, 1, 28); var p = page ?? 1;
    if (p < 1 || (long)p * size > 10000 || q?.Length > 250) return Results.BadRequest(new { message = "Bộ lọc hoặc trang không hợp lệ." });
    return Results.Ok(await catalog.Search(p, size, q, genre, status, country, demographic, language, sort, year));
});
app.MapGet("/api/catalog/tags", (Catalog catalog) => catalog.Tags());
app.MapGet("/api/catalog/{id:guid}", async (Guid id, Catalog catalog, AppDb db) => { var m = await catalog.Detail(id); await Remember(db, m); return m; });
app.MapGet("/api/catalog/{id:guid}/chapters", async (Guid id, string? language, int? page, bool? ascending, Catalog catalog) => {
    if ((page ?? 1) is < 1 or > 100) return Results.BadRequest(new { message = "Trang chương không hợp lệ." });
    return Results.Ok(await catalog.Chapters(id, language ?? "vi", page ?? 1, ascending ?? false));
});
app.MapGet("/api/chapters/{id:guid}", async (Guid id, Catalog catalog, AppDb db) => {
    var r = await catalog.Read(id); await Remember(db, r.Manga);
    var c = r.Chapter;
    await db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO "Chapters" ("Id","MangaId","Number","Title","Language","Pages","PublishedAt")
        VALUES ({c.Id},{c.MangaId},{c.Number},{c.Title},{c.Language},{Array.Empty<string>()},{c.PublishedAt}) ON CONFLICT ("Id") DO NOTHING
        """);
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
    var items = await query.OrderByDescending(x => x.CreatedAt).Skip((p - 1) * 20).Take(20).Select(x => new { x.Id, x.MangaId, mangaTitle = x.Manga.Title, x.UserId, name = x.User.Name, x.Body, x.CreatedAt }).ToListAsync();
    return Results.Ok(new { items, total, page = p, pageSize = 20 });
});
app.MapPost("/api/catalog/{id:guid}/comments", async (Guid id, CommentRequest req, ClaimsPrincipal user, AppDb db, Catalog catalog) => {
    var body = (req.Body ?? "").Trim(); if (body.Length is < 1 or > 2000) return Results.BadRequest(new { message = "Bình luận từ 1–2.000 ký tự." });
    await Remember(db, await catalog.Detail(id)); db.Comments.Add(new() { MangaId = id, UserId = UserId(user), Body = body }); await db.SaveChangesAsync(); return Results.Created("/api/comments?mangaId=" + id, new { message = "Đã gửi bình luận." });
}).RequireAuthorization().RequireRateLimiting("auth");
app.MapDelete("/api/comments/{id:guid}", async (Guid id, ClaimsPrincipal user, AppDb db) => {
    var comment = await db.Comments.FindAsync(id); if (comment is null) return Results.NotFound();
    if (comment.UserId != UserId(user) && !user.IsInRole("admin")) return Results.Forbid();
    db.Comments.Remove(comment); await db.SaveChangesAsync(); return Results.NoContent();
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
app.MapGet("/api/catalog/image-proxy", async (string url, IHttpClientFactory factory, CancellationToken ct) => {
    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || 
        (!uri.Host.EndsWith(".mangadex.network") && !uri.Host.EndsWith("mangadex.org") && uri.Host != "services.f-ck.me"))
        return Results.BadRequest(new { message = "Địa chỉ ảnh không hợp lệ." });
    var client = factory.CreateClient();
    client.Timeout = TimeSpan.FromSeconds(25);
    var req = new HttpRequestMessage(HttpMethod.Get, url);
    req.Headers.UserAgent.ParseAdd("TruyenDexClone/1.0");
    var res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
    if (!res.IsSuccessStatusCode) return Results.StatusCode((int)res.StatusCode);
    var contentType = res.Content.Headers.ContentType?.ToString() ?? "image/jpeg";
    return Results.Stream(await res.Content.ReadAsStreamAsync(ct), contentType);
});
using (var scope = app.Services.CreateScope()) {
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
    await db.Database.MigrateAsync();
    var email = builder.Configuration["Seed:AdminEmail"]; var password = builder.Configuration["Seed:AdminPassword"];
    if (!string.IsNullOrEmpty(email) && !string.IsNullOrEmpty(password) && !await db.Users.AnyAsync(x => x.Email == email)) {
        var u = new AppUser { Email = email, Name = "Quản trị viên", Role = "admin" };
        u.PasswordHash = scope.ServiceProvider.GetRequiredService<PasswordHasher<AppUser>>().HashPassword(u, password);
        db.Users.Add(u); await db.SaveChangesAsync();
    }
}
app.Run();
