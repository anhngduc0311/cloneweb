using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;

namespace TruyenDex.Api;

public class GoogleDriveConfig
{
    public string FolderId { get; set; } = "1vXTYGlxj_X3Oc-jfLawkS-_r1O8JUPG9";
    public string RefreshToken { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string ConnectedEmail { get; set; } = "";
    public string ConnectedName { get; set; } = "";
    public DateTime? ConnectedAt { get; set; }
}

public class GoogleDriveUploadResult
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string DirectUrl { get; set; } = "";
    public string ProxyUrl { get; set; } = "";
    public string ThumbnailUrl { get; set; } = "";
}

public class GoogleDriveFileInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string MimeType { get; set; } = "";
    public long Size { get; set; }
    public string DirectUrl { get; set; } = "";
    public string ProxyUrl { get; set; } = "";
}

public class GoogleDriveService
{
    private readonly HttpClient _http;
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly IMemoryCache _cache;
    private readonly string _configFile;
    private GoogleDriveConfig _driveConfig;

    private string? _cachedAccessToken;
    private DateTime _tokenExpiry = DateTime.MinValue;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    public const string DefaultFolderId = "1vXTYGlxj_X3Oc-jfLawkS-_r1O8JUPG9";

    public GoogleDriveService(HttpClient http, IConfiguration config, IWebHostEnvironment env, IMemoryCache cache)
    {
        _http = http;
        _config = config;
        _env = env;
        _cache = cache;
        _configFile = Path.Combine(env.ContentRootPath, "google-drive.json");
        _driveConfig = LoadConfig();
    }

    public string FolderId
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_driveConfig.FolderId) && _driveConfig.FolderId != DefaultFolderId && _driveConfig.FolderId != "root")
            {
                return _driveConfig.FolderId;
            }
            var envFolder = _config["Google:DriveFolderId"] ?? Environment.GetEnvironmentVariable("GOOGLE_DRIVE_FOLDER_ID");
            if (!string.IsNullOrWhiteSpace(envFolder) && envFolder != DefaultFolderId && envFolder != "root")
            {
                return envFolder;
            }
            return "";
        }
    }
    public string ClientId => _config["Google:ClientId"] ?? Environment.GetEnvironmentVariable("GOOGLE_CLIENT_ID") ?? "";
    public string ClientSecret => _config["Google:ClientSecret"] ?? Environment.GetEnvironmentVariable("GOOGLE_CLIENT_SECRET") ?? "";

    private GoogleDriveConfig LoadConfig()
    {
        try
        {
            if (File.Exists(_configFile))
            {
                var json = File.ReadAllText(_configFile);
                var loaded = JsonSerializer.Deserialize<GoogleDriveConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (loaded != null)
                {
                    if (string.IsNullOrWhiteSpace(loaded.FolderId))
                    {
                        loaded.FolderId = _config["Google:DriveFolderId"] ?? Environment.GetEnvironmentVariable("GOOGLE_DRIVE_FOLDER_ID") ?? DefaultFolderId;
                    }
                    if (string.IsNullOrWhiteSpace(loaded.RefreshToken))
                    {
                        loaded.RefreshToken = _config["Google:DriveRefreshToken"] ?? Environment.GetEnvironmentVariable("GOOGLE_DRIVE_REFRESH_TOKEN") ?? "";
                    }
                    return loaded;
                }
            }
        }
        catch { }

        return new GoogleDriveConfig
        {
            FolderId = _config["Google:DriveFolderId"] ?? Environment.GetEnvironmentVariable("GOOGLE_DRIVE_FOLDER_ID") ?? DefaultFolderId,
            RefreshToken = _config["Google:DriveRefreshToken"] ?? Environment.GetEnvironmentVariable("GOOGLE_DRIVE_REFRESH_TOKEN") ?? ""
        };
    }

    public async Task SaveConfigAsync(string? folderId, string? refreshToken = null, string? apiKey = null, string? email = null, string? name = null)
    {
        if (!string.IsNullOrWhiteSpace(folderId))
        {
            _driveConfig.FolderId = ExtractFolderId(folderId.Trim());
        }
        if (refreshToken != null)
        {
            _driveConfig.RefreshToken = refreshToken.Trim();
        }
        if (apiKey != null)
        {
            _driveConfig.ApiKey = apiKey.Trim();
        }
        if (email != null)
        {
            _driveConfig.ConnectedEmail = email.Trim();
        }
        if (name != null)
        {
            _driveConfig.ConnectedName = name.Trim();
        }

        if (!string.IsNullOrWhiteSpace(_driveConfig.RefreshToken) && string.IsNullOrWhiteSpace(_driveConfig.ConnectedEmail))
        {
            _driveConfig.ConnectedAt = DateTime.UtcNow;
        }

        var json = JsonSerializer.Serialize(_driveConfig, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(_configFile, json);

        // Invalidate token cache
        _cachedAccessToken = null;
        _tokenExpiry = DateTime.MinValue;
    }

    public async Task DisconnectAsync()
    {
        _driveConfig.RefreshToken = "";
        _driveConfig.ConnectedEmail = "";
        _driveConfig.ConnectedName = "";
        _driveConfig.ConnectedAt = null;
        _cachedAccessToken = null;
        _tokenExpiry = DateTime.MinValue;

        var json = JsonSerializer.Serialize(_driveConfig, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(_configFile, json);
    }

    public async Task<bool> IsConfiguredAsync()
    {
        if (!string.IsNullOrWhiteSpace(_driveConfig.RefreshToken)) return true;
        var token = await GetAccessTokenAsync();
        return !string.IsNullOrWhiteSpace(token);
    }

    public string GetAuthUrl(string redirectUri, string? state = null)
    {
        const string scopes = "https://www.googleapis.com/auth/drive openid email profile";
        var url = $"https://accounts.google.com/o/oauth2/v2/auth?client_id={Uri.EscapeDataString(ClientId)}&redirect_uri={Uri.EscapeDataString(redirectUri)}&response_type=code&scope={Uri.EscapeDataString(scopes)}&access_type=offline&prompt=consent";
        if (!string.IsNullOrWhiteSpace(state))
        {
            url += $"&state={Uri.EscapeDataString(state)}";
        }
        return url;
    }

    public async Task<bool> ExchangeCodeAsync(string code, string redirectUri)
    {
        if (string.IsNullOrWhiteSpace(ClientId) || string.IsNullOrWhiteSpace(ClientSecret))
        {
            throw new InvalidOperationException("Chưa cấu hình Google Client ID hoặc Client Secret trong hệ thống.");
        }

        using var client = new HttpClient();
        var tokenRes = await client.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            { "code", code.Trim() },
            { "client_id", ClientId },
            { "client_secret", ClientSecret },
            { "redirect_uri", redirectUri.Trim() },
            { "grant_type", "authorization_code" }
        }));

        if (!tokenRes.IsSuccessStatusCode)
        {
            var err = await tokenRes.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Lỗi xác thực Google: {err}");
        }

        var tokenJson = await tokenRes.Content.ReadFromJsonAsync<JsonNode>();
        var refreshToken = tokenJson?["refresh_token"]?.ToString() ?? "";
        var accessToken = tokenJson?["access_token"]?.ToString() ?? "";
        var expiresIn = (int?)tokenJson?["expires_in"] ?? 3600;

        if (string.IsNullOrEmpty(refreshToken) && !string.IsNullOrEmpty(_driveConfig.RefreshToken))
        {
            refreshToken = _driveConfig.RefreshToken;
        }

        _cachedAccessToken = accessToken;
        _tokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn - 60);

        string email = "";
        string name = "";
        try
        {
            using var userReq = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/oauth2/v3/userinfo");
            userReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            var userRes = await client.SendAsync(userReq);
            if (userRes.IsSuccessStatusCode)
            {
                var userJson = await userRes.Content.ReadFromJsonAsync<JsonNode>();
                email = userJson?["email"]?.ToString() ?? "";
                name = userJson?["name"]?.ToString() ?? "";
            }
        }
        catch { }

        await SaveConfigAsync(_driveConfig.FolderId, refreshToken, _driveConfig.ApiKey, email, name);
        return true;
    }

    public async Task<string?> GetAccessTokenAsync()
    {
        if (!string.IsNullOrEmpty(_cachedAccessToken) && DateTime.UtcNow < _tokenExpiry)
        {
            return _cachedAccessToken;
        }

        await _tokenLock.WaitAsync();
        try
        {
            if (!string.IsNullOrEmpty(_cachedAccessToken) && DateTime.UtcNow < _tokenExpiry)
            {
                return _cachedAccessToken;
            }

            var refreshToken = _driveConfig.RefreshToken;
            if (string.IsNullOrWhiteSpace(refreshToken))
            {
                return null;
            }

            using var client = new HttpClient();
            var refreshRes = await client.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                { "client_id", ClientId },
                { "client_secret", ClientSecret },
                { "refresh_token", refreshToken },
                { "grant_type", "refresh_token" }
            }));

            if (!refreshRes.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await refreshRes.Content.ReadFromJsonAsync<JsonNode>();
            var newAccessToken = json?["access_token"]?.ToString();
            var expiresIn = (int?)json?["expires_in"] ?? 3600;

            if (!string.IsNullOrEmpty(newAccessToken))
            {
                _cachedAccessToken = newAccessToken;
                _tokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn - 60);
                return _cachedAccessToken;
            }

            return null;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    public async Task<object> GetStatusAsync()
    {
        var token = await GetAccessTokenAsync();
        var isConnected = !string.IsNullOrEmpty(token);

        object? quota = null;
        if (isConnected)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/drive/v3/about?fields=user,storageQuota");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                var res = await _http.SendAsync(req);
                if (res.IsSuccessStatusCode)
                {
                    var json = await res.Content.ReadFromJsonAsync<JsonNode>();
                    var userNode = json?["user"];
                    var quotaNode = json?["storageQuota"];
                    if (userNode != null && string.IsNullOrEmpty(_driveConfig.ConnectedEmail))
                    {
                        _driveConfig.ConnectedEmail = userNode["emailAddress"]?.ToString() ?? "";
                        _driveConfig.ConnectedName = userNode["displayName"]?.ToString() ?? "";
                    }
                    if (quotaNode != null)
                    {
                        long.TryParse(quotaNode["limit"]?.ToString(), out var limit);
                        long.TryParse(quotaNode["usage"]?.ToString(), out var usage);
                        quota = new { limit, usage, usageInDrive = quotaNode["usageInDrive"]?.ToString() };
                    }
                }
            }
            catch { }
        }

        if (isConnected && string.IsNullOrWhiteSpace(FolderId))
        {
            try
            {
                var appFolderId = await GetOrCreateAppFolderAsync("akatruyen");
                if (!string.IsNullOrEmpty(appFolderId))
                {
                    _driveConfig.FolderId = appFolderId;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GoogleDrive] Cannot auto-init akatruyen folder: {ex.Message}");
            }
        }

        var activeFolderId = FolderId;
        return new
        {
            connected = isConnected,
            folderId = activeFolderId,
            folderUrl = !string.IsNullOrEmpty(activeFolderId) ? $"https://drive.google.com/drive/folders/{activeFolderId}" : "https://drive.google.com/drive/my-drive",
            email = _driveConfig.ConnectedEmail,
            name = _driveConfig.ConnectedName,
            connectedAt = _driveConfig.ConnectedAt,
            hasClientId = !string.IsNullOrWhiteSpace(ClientId),
            hasApiKey = !string.IsNullOrWhiteSpace(_driveConfig.ApiKey),
            quota
        };
    }

    public async Task<string> GetOrCreateAppFolderAsync(string folderName = "akatruyen")
    {
        var token = await GetAccessTokenAsync() ?? throw new InvalidOperationException("Chưa kết nối Google Drive.");

        // If explicitly configured with another folder (not empty/root/default), use that
        if (!string.IsNullOrWhiteSpace(_driveConfig.FolderId) &&
            _driveConfig.FolderId != DefaultFolderId &&
            _driveConfig.FolderId != "root")
        {
            return _driveConfig.FolderId;
        }

        // 1. Search for 'akatruyen' in root
        var foundId = await SearchFolderInParentAsync(folderName, "root", token);
        if (string.IsNullOrEmpty(foundId))
        {
            // Search anywhere in user's Drive
            try
            {
                var safeName = folderName.Replace("'", "\\'");
                var query = $"name = '{safeName}' and mimeType = 'application/vnd.google-apps.folder' and trashed = false";
                using var searchReq = new HttpRequestMessage(HttpMethod.Get, $"https://www.googleapis.com/drive/v3/files?q={Uri.EscapeDataString(query)}&fields=files(id,name)&pageSize=1&supportsAllDrives=true&includeItemsFromAllDrives=true");
                searchReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                var searchRes = await _http.SendAsync(searchReq);
                if (searchRes.IsSuccessStatusCode)
                {
                    var searchJson = await searchRes.Content.ReadFromJsonAsync<JsonNode>();
                    var existingFiles = searchJson?["files"]?.AsArray();
                    if (existingFiles != null && existingFiles.Count > 0)
                    {
                        foundId = existingFiles[0]?["id"]?.ToString();
                    }
                }
            }
            catch { }
        }

        // 2. Create if not found
        if (string.IsNullOrEmpty(foundId))
        {
            foundId = await CreateFolderInternalAsync(folderName, "root", token, allowFallback: false);
        }

        // 3. Save as active FolderId and auto-relocate any root manga folders into akatruyen
        if (!string.IsNullOrEmpty(foundId))
        {
            _driveConfig.FolderId = foundId;
            var targetAppFolder = foundId;
            _ = Task.Run(async () =>
            {
                try
                {
                    await SaveConfigAsync(targetAppFolder, _driveConfig.RefreshToken, _driveConfig.ApiKey, _driveConfig.ConnectedEmail, _driveConfig.ConnectedName);
                    await MoveRootMangaFoldersToAppFolderAsync(targetAppFolder, token);
                }
                catch { }
            });
        }

        return foundId;
    }

    public async Task<string> FindOrCreateFolderAsync(string folderName, string? parentFolderId = null)
    {
        var token = await GetAccessTokenAsync() ?? throw new InvalidOperationException("Chưa kết nối Google Drive.");
        var parent = !string.IsNullOrWhiteSpace(parentFolderId) ? parentFolderId : FolderId;
        if (string.IsNullOrWhiteSpace(parent) || parent == "root" || parent == DefaultFolderId)
        {
            parent = await GetOrCreateAppFolderAsync("akatruyen");
        }

        var p = !string.IsNullOrWhiteSpace(parent) && parent != "root" ? parent : "root";

        // 1. Search in target parent folder (e.g. inside akatruyen)
        var foundId = await SearchFolderInParentAsync(folderName, p, token);
        if (!string.IsNullOrEmpty(foundId)) return foundId;

        // 2. If parent != "root", check if folder exists in "root" (e.g. previously created in root)
        if (p != "root")
        {
            var rootFoundId = await SearchFolderInParentAsync(folderName, "root", token);
            if (!string.IsNullOrEmpty(rootFoundId))
            {
                // Auto-move it from root into parent (akatruyen)!
                await MoveFileOrFolderAsync(rootFoundId, p, "root", token);
                return rootFoundId;
            }
        }

        return await CreateFolderAsync(folderName, parent);
    }

    public async Task<bool> MoveFileOrFolderAsync(string fileId, string newParentId, string oldParentId, string? token = null)
    {
        token ??= await GetAccessTokenAsync();
        if (string.IsNullOrEmpty(token)) return false;

        try
        {
            var removeParam = !string.IsNullOrWhiteSpace(oldParentId) ? $"&removeParents={Uri.EscapeDataString(oldParentId)}" : "";
            using var req = new HttpRequestMessage(HttpMethod.Patch,
                $"https://www.googleapis.com/drive/v3/files/{fileId}?addParents={Uri.EscapeDataString(newParentId)}{removeParam}&supportsAllDrives=true");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            var res = await _http.SendAsync(req);
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task MoveRootMangaFoldersToAppFolderAsync(string appFolderId, string token)
    {
        try
        {
            var query = "'root' in parents and mimeType = 'application/vnd.google-apps.folder' and trashed = false";
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://www.googleapis.com/drive/v3/files?q={Uri.EscapeDataString(query)}&fields=files(id,name,parents)&pageSize=100&supportsAllDrives=true&includeItemsFromAllDrives=true");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var res = await _http.SendAsync(req);
            if (!res.IsSuccessStatusCode) return;

            var json = await res.Content.ReadFromJsonAsync<JsonNode>();
            var files = json?["files"]?.AsArray();
            if (files == null) return;

            foreach (var f in files)
            {
                var id = f?["id"]?.ToString();
                var name = f?["name"]?.ToString() ?? "";
                if (string.IsNullOrEmpty(id) || id == appFolderId) continue;

                // Move if name ends with "- Ảnh bìa", "Ảnh bìa", or contains "- Chap "
                if (name.EndsWith(" - Ảnh bìa", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Ảnh bìa", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains(" - Chap ", StringComparison.OrdinalIgnoreCase))
                {
                    var parent = f?["parents"]?[0]?.ToString() ?? "root";
                    await MoveFileOrFolderAsync(id, appFolderId, parent, token);
                    Console.WriteLine($"[GoogleDrive] Moved '{name}' ({id}) into akatruyen ({appFolderId})");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GoogleDrive] Error moving root folders: {ex.Message}");
        }
    }

    private async Task<string?> SearchFolderInParentAsync(string folderName, string parentId, string token)
    {
        try
        {
            var p = !string.IsNullOrWhiteSpace(parentId) && parentId != "root" ? parentId : "root";
            var safeName = folderName.Replace("'", "\\'");
            var query = $"'{p}' in parents and name = '{safeName}' and mimeType = 'application/vnd.google-apps.folder' and trashed = false";
            using var searchReq = new HttpRequestMessage(HttpMethod.Get, $"https://www.googleapis.com/drive/v3/files?q={Uri.EscapeDataString(query)}&fields=files(id,name)&pageSize=1&supportsAllDrives=true&includeItemsFromAllDrives=true");
            searchReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var searchRes = await _http.SendAsync(searchReq);
            if (searchRes.IsSuccessStatusCode)
            {
                var searchJson = await searchRes.Content.ReadFromJsonAsync<JsonNode>();
                var existingFiles = searchJson?["files"]?.AsArray();
                if (existingFiles != null && existingFiles.Count > 0)
                {
                    return existingFiles[0]?["id"]?.ToString();
                }
            }
        }
        catch { }
        return null;
    }

    public async Task<string> CreateFolderAsync(string folderName, string? parentFolderId = null)
    {
        var token = await GetAccessTokenAsync() ?? throw new InvalidOperationException("Chưa kết nối Google Drive.");
        var parent = !string.IsNullOrWhiteSpace(parentFolderId) ? parentFolderId : FolderId;

        return await CreateFolderInternalAsync(folderName, parent, token, allowFallback: true);
    }

    private async Task<string> CreateFolderInternalAsync(string folderName, string? parent, string token, bool allowFallback)
    {
        var metadata = new JsonObject
        {
            ["name"] = folderName,
            ["mimeType"] = "application/vnd.google-apps.folder"
        };
        if (!string.IsNullOrWhiteSpace(parent) && parent != "root")
        {
            metadata["parents"] = new JsonArray { parent };
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://www.googleapis.com/drive/v3/files?supportsAllDrives=true");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = new StringContent(metadata.ToJsonString(), Encoding.UTF8, "application/json");

        var res = await _http.SendAsync(req);
        if (!res.IsSuccessStatusCode)
        {
            var err = await res.Content.ReadAsStringAsync();
            if (allowFallback && (res.StatusCode == System.Net.HttpStatusCode.NotFound || res.StatusCode == System.Net.HttpStatusCode.Forbidden || err.Contains("notFound") || err.Contains("File not found")))
            {
                return await CreateFolderInternalAsync(folderName, "root", token, allowFallback: false);
            }
            throw new InvalidOperationException($"Lỗi tạo thư mục Google Drive: {err}");
        }

        var json = await res.Content.ReadFromJsonAsync<JsonNode>();
        var folderId = json?["id"]?.ToString() ?? throw new InvalidOperationException("Không nhận được folder ID từ Google.");

        await MakePublicAsync(folderId, token);
        return folderId;
    }

    public async Task<GoogleDriveUploadResult> UploadImageAsync(Stream stream, string fileName, string contentType, string? parentFolderId = null)
    {
        var token = await GetAccessTokenAsync() ?? throw new InvalidOperationException("Chưa kết nối Google Drive. Vui lòng kết nối Google Drive trước khi upload.");
        var parent = !string.IsNullOrWhiteSpace(parentFolderId) ? parentFolderId : FolderId;

        byte[] bytes;
        if (stream is MemoryStream ms)
        {
            bytes = ms.ToArray();
        }
        else
        {
            using var mem = new MemoryStream();
            if (stream.CanSeek) stream.Position = 0;
            await stream.CopyToAsync(mem);
            bytes = mem.ToArray();
        }

        return await UploadImageInternalAsync(bytes, fileName, contentType, parent, token, allowFallback: true);
    }

    private async Task<GoogleDriveUploadResult> UploadImageInternalAsync(byte[] bytes, string fileName, string contentType, string? parent, string token, bool allowFallback)
    {
        var boundary = "TruyenDexBoundary" + Guid.NewGuid().ToString("N");
        using var content = new MultipartContent("related", boundary);

        // 1. Metadata part
        var metadata = new JsonObject
        {
            ["name"] = fileName
        };
        if (!string.IsNullOrWhiteSpace(parent) && parent != "root")
        {
            metadata["parents"] = new JsonArray { parent };
        }
        var metaContent = new StringContent(metadata.ToJsonString(), Encoding.UTF8, "application/json");
        metaContent.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "UTF-8" };
        metaContent.Headers.ContentDisposition = null;
        content.Add(metaContent);

        // 2. Media part
        var mediaContent = new ByteArrayContent(bytes);
        mediaContent.Headers.ContentType = new MediaTypeHeaderValue(string.IsNullOrWhiteSpace(contentType) ? "image/jpeg" : contentType);
        mediaContent.Headers.ContentDisposition = null;
        content.Add(mediaContent);

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://www.googleapis.com/upload/drive/v3/files?uploadType=multipart&supportsAllDrives=true");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = content;

        var res = await _http.SendAsync(req);
        if (!res.IsSuccessStatusCode)
        {
            var err = await res.Content.ReadAsStringAsync();
            if (allowFallback && (res.StatusCode == System.Net.HttpStatusCode.NotFound || res.StatusCode == System.Net.HttpStatusCode.Forbidden || err.Contains("notFound") || err.Contains("File not found")))
            {
                return await UploadImageInternalAsync(bytes, fileName, contentType, "root", token, allowFallback: false);
            }
            throw new InvalidOperationException($"Lỗi tải ảnh lên Google Drive: {err}");
        }

        var resJson = await res.Content.ReadFromJsonAsync<JsonNode>();
        var fileId = resJson?["id"]?.ToString() ?? throw new InvalidOperationException("Không nhận được ID file tải lên từ Google Drive.");

        await MakePublicAsync(fileId, token);

        return new GoogleDriveUploadResult
        {
            Id = fileId,
            Name = fileName,
            DirectUrl = $"https://lh3.googleusercontent.com/d/{fileId}",
            ProxyUrl = $"/api/drive/image/{fileId}",
            ThumbnailUrl = $"https://drive.google.com/thumbnail?id={fileId}&sz=w1600"
        };
    }

    public async Task<bool> MakePublicAsync(string fileOrFolderId, string? token = null)
    {
        try
        {
            token ??= await GetAccessTokenAsync();
            if (string.IsNullOrEmpty(token)) return false;

            var perm = new JsonObject
            {
                ["role"] = "reader",
                ["type"] = "anyone"
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, $"https://www.googleapis.com/drive/v3/files/{fileOrFolderId}/permissions?supportsAllDrives=true");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Content = new StringContent(perm.ToJsonString(), Encoding.UTF8, "application/json");

            var res = await _http.SendAsync(req);
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<object> ScanFolderAsync(string folderIdOrUrl)
    {
        var targetFolderId = ExtractFolderId(folderIdOrUrl);
        var token = await GetAccessTokenAsync();

        string url;
        if (!string.IsNullOrEmpty(token))
        {
            url = $"https://www.googleapis.com/drive/v3/files?q='{targetFolderId}'+in+parents+and+trashed=false&fields=files(id,name,mimeType,size)&pageSize=1000&supportsAllDrives=true&includeItemsFromAllDrives=true";
        }
        else if (!string.IsNullOrEmpty(_driveConfig.ApiKey))
        {
            url = $"https://www.googleapis.com/drive/v3/files?q='{targetFolderId}'+in+parents+and+trashed=false&fields=files(id,name,mimeType,size)&pageSize=1000&key={_driveConfig.ApiKey}&supportsAllDrives=true&includeItemsFromAllDrives=true";
        }
        else
        {
            throw new InvalidOperationException("Cần kết nối tài khoản Google Drive hoặc cấu hình Google API Key để quét thư mục.");
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(token))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        var res = await _http.SendAsync(req);
        if (!res.IsSuccessStatusCode)
        {
            var err = await res.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Không thể quét thư mục Google Drive: {err}");
        }

        var json = await res.Content.ReadFromJsonAsync<JsonNode>();
        var filesNode = json?["files"]?.AsArray();
        var imageFiles = new List<GoogleDriveFileInfo>();

        if (filesNode != null)
        {
            foreach (var node in filesNode)
            {
                var id = node?["id"]?.ToString() ?? "";
                var name = node?["name"]?.ToString() ?? "";
                var mimeType = node?["mimeType"]?.ToString() ?? "";
                long.TryParse(node?["size"]?.ToString(), out var size);

                var isImage = mimeType.StartsWith("image/") ||
                              name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                              name.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                              name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                              name.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ||
                              name.EndsWith(".avif", StringComparison.OrdinalIgnoreCase) ||
                              name.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);

                if (isImage && !string.IsNullOrEmpty(id))
                {
                    imageFiles.Add(new GoogleDriveFileInfo
                    {
                        Id = id,
                        Name = name,
                        MimeType = mimeType,
                        Size = size,
                        DirectUrl = $"https://lh3.googleusercontent.com/d/{id}",
                        ProxyUrl = $"/api/drive/image/{id}"
                    });
                }
            }
        }

        // Natural sort by file name: "01.jpg", "2.jpg", "10.jpg"
        var sorted = imageFiles
            .OrderBy(f => Regex.Replace(f.Name, @"\d+", m => m.Value.PadLeft(10, '0')))
            .ToList();

        // Make all discovered files public reader in background
        if (!string.IsNullOrEmpty(token))
        {
            _ = Task.Run(async () =>
            {
                foreach (var f in sorted)
                {
                    try { await MakePublicAsync(f.Id, token); } catch { }
                }
            });
        }

        return new
        {
            folderId = targetFolderId,
            folderUrl = $"https://drive.google.com/drive/folders/{targetFolderId}",
            total = sorted.Count,
            items = sorted,
            urls = sorted.Select(x => x.DirectUrl).ToArray()
        };
    }

    public async Task<(Stream Stream, string ContentType)> GetFileStreamAsync(string fileId, CancellationToken ct)
    {
        var token = await GetAccessTokenAsync();
        var url = $"https://www.googleapis.com/drive/v3/files/{fileId}?alt=media&supportsAllDrives=true";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(token))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode)
        {
            // Fallback to public uc download endpoint
            var fallbackUrl = $"https://drive.google.com/uc?id={fileId}&export=download";
            var fallbackRes = await _http.GetAsync(fallbackUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            if (fallbackRes.IsSuccessStatusCode)
            {
                var ctFallback = fallbackRes.Content.Headers.ContentType?.ToString() ?? "image/jpeg";
                return (await fallbackRes.Content.ReadAsStreamAsync(ct), ctFallback);
            }
            throw new InvalidOperationException($"Không thể tải ảnh từ Google Drive (mã lỗi {res.StatusCode}).");
        }

        var contentType = res.Content.Headers.ContentType?.ToString() ?? "image/jpeg";
        return (await res.Content.ReadAsStreamAsync(ct), contentType);
    }

    public static string ExtractFolderId(string folderUrlOrId)
    {
        if (string.IsNullOrWhiteSpace(folderUrlOrId)) return DefaultFolderId;
        var clean = folderUrlOrId.Trim();

        // Match folder URL
        var match = Regex.Match(clean, @"(?:drive\.google\.com\/drive\/folders\/|drive\/u\/\d+\/folders\/|id=)([a-zA-Z0-9_-]{25,})");
        if (match.Success) return match.Groups[1].Value;

        // Match file URL
        var matchFile = Regex.Match(clean, @"(?:file\/d\/|open\?id=|uc\?id=)([a-zA-Z0-9_-]{25,})");
        if (matchFile.Success) return matchFile.Groups[1].Value;

        // Plain ID
        if (Regex.IsMatch(clean, @"^[a-zA-Z0-9_-]{25,}$")) return clean;

        return clean;
    }

    public static string FormatViewUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;
        var clean = url.Trim();

        // If it's a Google Drive preview/open/uc/view link, extract ID and convert to direct CDN url
        var match = Regex.Match(clean, @"(?:drive\.google\.com\/(?:file\/d\/|open\?id=|uc\?id=)|lh3\.googleusercontent\.com\/d\/)([a-zA-Z0-9_-]{25,})");
        if (match.Success)
        {
            var id = match.Groups[1].Value;
            return $"https://lh3.googleusercontent.com/d/{id}";
        }

        return clean;
    }
}
