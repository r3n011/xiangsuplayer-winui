using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PixelPlayer.WinUI.Models;

namespace PixelPlayer.WinUI.Services.Netease;

/// <summary>网易云登录用户信息。</summary>
public sealed class NeteaseUserProfile
{
    public long UserId { get; init; }
    public string Nickname { get; init; } = "";
    public string? AvatarUrl { get; init; }
    public string? Signature { get; init; }
}

/// <summary>
/// 网易云账号服务（对齐参照项目 NeteaseRepository 账号部分）：
/// - 手机号 + 短信验证码登录（weapi captcha + eapi login）
/// - Cookie 持久化（DPAPI 加密存 %LocalAppData%\PixelPlayer.WinUI\netease_cookies.bin）
/// - 启动自动恢复登录态，用户信息 / weapi / 歌词 / 歌单同步都带 Cookie
/// </summary>
public sealed class NeteaseAccountService
{
    // 注意：静态字段初始化器按声明顺序执行，Instance 必须放在依赖的字段之后，
    // 否则构造函数读到尚未初始化的 null（曾导致启动 stowed exception 闪退）。
    private static readonly string DataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PixelPlayer.WinUI");
    private static readonly string CookieFile = Path.Combine(DataDir, "netease_cookies.bin");

    public static NeteaseAccountService Instance { get; } = new();

    private readonly NeteaseApi _api = new();

    public NeteaseUserProfile? Profile { get; private set; }
    public bool IsLoggedIn => Profile is not null;

    public event EventHandler? AccountChanged;

    private static readonly JsonSerializerOptions _json = new() { WriteIndented = false };

    private NeteaseAccountService()
    {
        Directory.CreateDirectory(DataDir);
        RestoreFromDisk();
    }

    // ─── Cookie 持久化（DPAPI CurrentUser）────────────────────────────

    private void RestoreFromDisk()
    {
        try
        {
            if (!File.Exists(CookieFile)) return;
            var protectedBytes = File.ReadAllBytes(CookieFile);
            var plain = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            var doc = JsonSerializer.Deserialize<Dictionary<string, string>>(plain);
            if (doc is null || doc.Count == 0) return;
            _api.SetCookies(doc);
            _ = RestoreProfileAsync();
        }
        catch
        {
            // Cookie 损坏则视为未登录
        }
    }

    private void SaveCookiesToDisk()
    {
        try
        {
            var plain = JsonSerializer.SerializeToUtf8Bytes(_api.Cookies, _json);
            var protectedBytes = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(CookieFile, protectedBytes);
        }
        catch
        {
            // 忽略保存失败
        }
    }

    /// <summary>登录后 / 启动恢复时拉取用户信息（验证 Cookie 是否仍有效）。</summary>
    public async Task<bool> RestoreProfileAsync(CancellationToken ct = default)
    {
        try
        {
            var raw = await _api.GetCurrentUserAccountAsync(ct);
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (!root.TryGetProperty("code", out var codeEl) || codeEl.GetInt32() != 200) return false;
            if (!root.TryGetProperty("profile", out var profile) || profile.ValueKind != JsonValueKind.Object) return false;

            Profile = new NeteaseUserProfile
            {
                UserId = profile.TryGetProperty("userId", out var uid) && uid.TryGetInt64(out var uidV) ? uidV : 0,
                Nickname = profile.TryGetProperty("nickname", out var nick) ? nick.GetString() ?? "" : "",
                AvatarUrl = profile.TryGetProperty("avatarUrl", out var av) && av.ValueKind == JsonValueKind.String ? av.GetString() : null,
                Signature = profile.TryGetProperty("signature", out var sig) && sig.ValueKind == JsonValueKind.String ? sig.GetString() : null,
            };
            AccountChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ─── 登录（手机号 + 验证码，对齐参照项目 sendCaptcha/loginWithPhoneCaptcha）───

    public async Task SendCaptchaAsync(string phone, int ctcode = 86, CancellationToken ct = default)
    {
        await _api.SendCaptchaAsync(phone, ctcode, ct);
    }

    /// <summary>验证码登录；成功返回用户信息，失败抛异常（消息含服务端 msg）。</summary>
    public async Task<NeteaseUserProfile> LoginByCaptchaAsync(string phone, string captcha, int ctcode = 86, CancellationToken ct = default)
    {
        var raw = await _api.LoginByCaptchaAsync(phone, captcha, ctcode, ct);
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        var code = root.TryGetProperty("code", out var c) ? c.GetInt32() : -1;
        if (code != 200 && code != 301)
        {
            var msg = root.TryGetProperty("msg", out var m) ? m.GetString() :
                      root.TryGetProperty("message", out var m2) ? m2.GetString() : null;
            throw new InvalidOperationException(msg ?? $"登录失败（code={code}）");
        }

        // 合并 Set-Cookie 由 HttpClient 自动处理？FormUrlEncodedContent 不带容器 Cookie，
        // 参照项目从响应头手工收集 Cookie —— 这里同样手工收集。
        MergeCookiesFromResponse(raw, root);

        var ok = await RestoreProfileAsync(ct);
        if (!ok) throw new InvalidOperationException("登录 Cookie 无效，请重试");
        SaveCookiesToDisk();
        return Profile!;
    }

    /// <summary>导入网页 Cookie 字符串（MUSIC_U=xxx; __csrf=yyy；WebView 登录兜底）。</summary>
    public void ImportCookies(string cookieString)
    {
        var dict = new Dictionary<string, string>();
        foreach (var part in cookieString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var i = part.IndexOf('=');
            if (i <= 0) continue;
            dict[part[..i].Trim()] = part[(i + 1)..].Trim();
        }
        if (dict.Count == 0) throw new InvalidOperationException("Cookie 格式不正确");
        _api.SetCookies(dict);
        SaveCookiesToDisk();
        _ = RestoreProfileAsync();
    }

    private void MergeCookiesFromResponse(string raw, JsonElement root)
    {
        // eapi 登录响应体里直接带 cookie 字段（数组或字符串）
        try
        {
            if (root.TryGetProperty("cookie", out var cookieEl))
            {
                IEnumerable<string> parts = cookieEl.ValueKind == JsonValueKind.Array
                    ? cookieEl.EnumerateArray().Select(e => e.GetString() ?? "")
                    : new[] { cookieEl.GetString() ?? "" };
                foreach (var part in parts)
                {
                    foreach (var kv in part.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        var i = kv.IndexOf('=');
                        if (i <= 0) continue;
                        var name = kv[..i].Trim();
                        var value = kv[(i + 1)..].Trim();
                        if (name is "MUSIC_U" or "__csrf" or "os" or "osver" or "appver" or "versioncode" or "channel")
                        {
                            _api.SetCookies(new Dictionary<string, string>(_api.Cookies) { [name] = value });
                        }
                    }
                }
            }
        }
        catch
        {
            // ignore
        }
    }

    public void Logout()
    {
        _api.SetCookies(new Dictionary<string, string>());
        Profile = null;
        try { if (File.Exists(CookieFile)) File.Delete(CookieFile); } catch { }
        AccountChanged?.Invoke(this, EventArgs.Empty);
    }

    // ─── 给其它服务用 ───────────────────────────────────────────────

    public NeteaseApi Api => _api;

    /// <summary>给匿名接口用的 Cookie 头（未登录返回空）。</summary>
    public string? CookieHeader =>
        IsLoggedIn ? string.Join("; ", _api.Cookies.Select(kv => $"{kv.Key}={kv.Value}")) : null;
}

/// <summary>
/// 网易云歌单同步（对齐参照项目 NeteaseRepository syncUserPlaylists/syncPlaylistSongs）：
/// - 分页拉取用户歌单（limit 50，最多 200 页）
/// - 歌单详情 /api/v6/playlist/detail + trackIds 补全 /api/v3/song/detail（批 500）
/// - 同步到本地云端歌单缓存（JSON 文件），自动节流 1 小时
/// </summary>
public sealed class NeteasePlaylistService
{
    private const int PlaylistPageSize = 50;
    private const int MaxPlaylistPages = 200;
    private const int SongDetailBatchSize = 500;
    private static readonly TimeSpan AutoSyncInterval = TimeSpan.FromHours(1);

    private static readonly string DataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PixelPlayer.WinUI", "netease_playlists");

    private static readonly JsonSerializerOptions _json = new() { WriteIndented = false };

    public static NeteasePlaylistService Instance { get; } = new();

    /// <summary>云端歌单（同步结果，UI 直接绑定）。</summary>
    public sealed class CloudPlaylist
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public string? CoverUrl { get; set; }
        public int SongCount { get; set; }
        public List<Track> Songs { get; set; } = new();
        public DateTimeOffset LastSyncTime { get; set; }
        [System.Text.Json.Serialization.JsonIgnore]
        public string CountText => $"{SongCount} 首";
    }

    public List<CloudPlaylist> Playlists { get; private set; } = new();

    public event EventHandler? SyncCompleted;
    public bool IsSyncing { get; private set; }

    private NeteasePlaylistService()
    {
        Directory.CreateDirectory(DataDir);
        LoadFromDisk();
        NeteaseAccountService.Instance.AccountChanged += (_, __) =>
        {
            if (!NeteaseAccountService.Instance.IsLoggedIn) ClearAll();
        };
    }

    // ─── 磁盘缓存 ───────────────────────────────────────────────────

    private void LoadFromDisk()
    {
        try
        {
            var list = new List<CloudPlaylist>();
            foreach (var file in Directory.GetFiles(DataDir, "*.json"))
            {
                var pl = JsonSerializer.Deserialize<CloudPlaylist>(File.ReadAllText(file), _json);
                if (pl is not null) list.Add(pl);
            }
            Playlists = list.OrderByDescending(p => p.LastSyncTime).ToList();
        }
        catch
        {
            Playlists = new List<CloudPlaylist>();
        }
    }

    private void SaveToDisk(CloudPlaylist pl)
    {
        try
        {
            File.WriteAllText(Path.Combine(DataDir, $"{pl.Id}.json"), JsonSerializer.Serialize(pl, _json));
        }
        catch { }
    }

    private void ClearAll()
    {
        Playlists = new List<CloudPlaylist>();
        try { foreach (var f in Directory.GetFiles(DataDir, "*.json")) File.Delete(f); } catch { }
        SyncCompleted?.Invoke(this, EventArgs.Empty);
    }

    // ─── 同步 ───────────────────────────────────────────────────────

    /// <summary>全量同步用户歌单 + 歌曲条目（对齐 syncAllPlaylistsAndSongs）。</summary>
    public async Task<(int PlaylistCount, int SongCount)> SyncAllAsync(CancellationToken ct = default)
    {
        var account = NeteaseAccountService.Instance;
        if (!account.IsLoggedIn || account.Profile is null) return (0, 0);
        if (IsSyncing) return (0, 0);

        IsSyncing = true;
        try
        {
            var api = account.Api;
            var playlists = new List<(long Id, string Name, string? Cover, int Count)>();
            for (var page = 0; page < MaxPlaylistPages; page++)
            {
                var raw = await api.GetUserPlaylistsAsync(account.Profile.UserId, offset: page * PlaylistPageSize, ct: ct);
                using var doc = JsonDocument.Parse(raw);
                var arr = doc.RootElement.TryGetProperty("playlist", out var plArr) ? plArr : default;
                if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0) break;
                foreach (var pl in arr.EnumerateArray())
                {
                    var id = pl.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var idV) ? idV : 0;
                    if (id == 0) continue;
                    var name = pl.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    string? cover = pl.TryGetProperty("coverImgUrl", out var cv) && cv.ValueKind == JsonValueKind.String ? cv.GetString() : null;
                    var count = pl.TryGetProperty("trackCount", out var tc) && tc.TryGetInt32(out var tcV) ? tcV : 0;
                    playlists.Add((id, name, cover, count));
                }
                if (arr.GetArrayLength() < PlaylistPageSize) break;
            }

            var totalSongs = 0;
            var synced = new List<CloudPlaylist>();
            foreach (var (id, name, cover, count) in playlists)
            {
                ct.ThrowIfCancellationRequested();
                var songs = await SyncPlaylistSongsAsync(api, id, ct);
                totalSongs += songs.Count;
                var existing = Playlists.FirstOrDefault(p => p.Id == id);
                var pl = new CloudPlaylist
                {
                    Id = id,
                    Name = name,
                    CoverUrl = cover,
                    SongCount = Math.Max(count, songs.Count),
                    Songs = songs,
                    LastSyncTime = DateTimeOffset.Now,
                };
                if (existing is not null && songs.Count == 0)
                {
                    // 远端异常返回空时保留旧歌曲数据，避免误清
                    pl.Songs = existing.Songs;
                }
                SaveToDisk(pl);
                synced.Add(pl);
            }
            Playlists = synced.OrderByDescending(p => p.LastSyncTime).ToList();
            SyncCompleted?.Invoke(this, EventArgs.Empty);
            return (synced.Count, totalSongs);
        }
        finally
        {
            IsSyncing = false;
        }
    }

    /// <summary>拉取单个歌单的歌曲条目（对齐 syncPlaylistSongs：tracks + trackIds 补全）。</summary>
    private async Task<List<Track>> SyncPlaylistSongsAsync(NeteaseApi api, long playlistId, CancellationToken ct)
    {
        var raw = await api.GetPlaylistDetailAsync(playlistId, ct);
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        var plEl = root.TryGetProperty("playlist", out var pl) ? pl : root;

        var tracks = new List<Track>();
        var missingIds = new List<long>();

        if (plEl.TryGetProperty("trackIds", out var trackIds) && trackIds.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in trackIds.EnumerateArray())
            {
                if (t.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var id)) missingIds.Add(id);
            }
        }

        var trackMaps = new List<JsonElement>();
        if (plEl.TryGetProperty("tracks", out var embedded) && embedded.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in embedded.EnumerateArray())
            {
                if (t.ValueKind == JsonValueKind.Object) trackMaps.Add(t);
            }
        }
        // 去掉已内嵌的 id
        var embeddedIds = trackMaps.Select(t => t.TryGetProperty("id", out var e) && e.TryGetInt64(out var ev) ? ev : 0).ToHashSet();
        missingIds = missingIds.Where(id => !embeddedIds.Contains(id)).Distinct().ToList();

        for (var i = 0; i < missingIds.Count; i += SongDetailBatchSize)
        {
            var batch = missingIds.Skip(i).Take(SongDetailBatchSize).ToList();
            var detailRaw = await api.GetSongDetailsAsync(batch, ct);
            using var detailDoc = JsonDocument.Parse(detailRaw);
            if (detailDoc.RootElement.TryGetProperty("songs", out var songs) && songs.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in songs.EnumerateArray())
                {
                    if (t.ValueKind == JsonValueKind.Object) trackMaps.Add(t);
                }
            }
        }

        foreach (var t in trackMaps) ParseTrack(t, tracks);
        return tracks;
    }

    private static void ParseTrack(JsonElement s, List<Track> into)
    {
        try
        {
            var id = s.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var idV) ? idV : 0;
            var name = s.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (id == 0 || name.Length == 0) return;

            var artist = "";
            if (s.TryGetProperty("ar", out var ar) && ar.ValueKind == JsonValueKind.Array)
            {
                artist = string.Join(" / ", ar.EnumerateArray()
                    .Select(a => a.TryGetProperty("name", out var an) ? an.GetString() ?? "" : "")
                    .Where(x => x.Length > 0));
            }
            var album = s.TryGetProperty("al", out var al) ? al.TryGetProperty("name", out var an2) ? an2.GetString() ?? "" : "" : "";
            string? pic = s.TryGetProperty("al", out var al2) && al2.TryGetProperty("picUrl", out var picEl) && picEl.ValueKind == JsonValueKind.String
                ? picEl.GetString() : null;
            var dur = s.TryGetProperty("dt", out var dt) && dt.TryGetInt64(out var dtV) ? dtV / 1000.0 : 0.0;

            into.Add(new Track
            {
                Path = $"netease://{id}",
                SourceId = "netease",
                OnlineKey = id.ToString(),
                Title = name,
                Artist = artist,
                Album = album,
                DurationSeconds = dur,
                PicUrl = pic,
            });
        }
        catch
        {
            // 跳过坏数据
        }
    }

    /// <summary>取歌单的歌曲列表（供播放）。</summary>
    public CloudPlaylist? Find(long playlistId) => Playlists.FirstOrDefault(p => p.Id == playlistId);
}
