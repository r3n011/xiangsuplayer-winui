using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PixelPlayer.WinUI.Models;
using PixelPlayer.WinUI.Services.Lx;
using PixelPlayer.WinUI.Services.Online;

namespace PixelPlayer.WinUI.Services;

/// <summary>
/// 歌词在线获取链（对应参照项目 LyricsRepositoryImpl 的获取顺序）：
/// 源自带歌词（网易云/QQ）→ LRCLIB → 缓存（内存 + 磁盘）。
/// </summary>
public static class LyricsFetchService
{
    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    static LyricsFetchService()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("PixelPlayer/1.0 (desktop)");
    }

    public sealed record LyricsResult
    {
        public string Lrc { get; init; } = "";
        public string? Translation { get; init; }
        public string Origin { get; init; } = "";
    }

    private static readonly ConcurrentDictionary<string, LyricsResult?> _memCache = new();

    private static string CacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PixelPlayer.WinUI", "lyricscache");

    public static void ClearMemoryCache() => _memCache.Clear();

    /// <summary>主入口：在线获取歌词（含缓存）。失败返回 null。</summary>
    public static async Task<LyricsResult?> FetchAsync(Track track, CancellationToken ct = default)
    {
        var cacheKey = $"{track.SourceId}|{track.OnlineKey}|{track.Title}|{track.Artist}";
        if (_memCache.TryGetValue(cacheKey, out var hit)) return hit;

        // 磁盘缓存
        var diskFile = Path.Combine(CacheDir, Hash(cacheKey) + ".json");
        try
        {
            if (File.Exists(diskFile))
            {
                var cached = JsonSerializer.Deserialize<LyricsResult>(await File.ReadAllTextAsync(diskFile, ct));
                if (cached is { Lrc.Length: > 0 })
                {
                    _memCache[cacheKey] = cached;
                    return cached;
                }
            }
        }
        catch { /* 缓存读取失败继续在线获取 */ }

        var result = await FetchChainAsync(track, ct);
        _memCache[cacheKey] = result;

        if (result is not null)
        {
            try
            {
                Directory.CreateDirectory(CacheDir);
                await File.WriteAllTextAsync(diskFile, JsonSerializer.Serialize(result), ct);
            }
            catch { /* 缓存写入失败忽略 */ }
        }

        return result;
    }

    private static async Task<LyricsResult?> FetchChainAsync(Track track, CancellationToken ct)
    {
        // 1. LX 音源脚本歌词（平台音源全部由脚本提供，应用不内置平台 API）
        if (track.IsOnline)
        {
            var lxKey = LxPlatformMap.KeyOf(track.SourceId);
            if (lxKey is not null && LxJsEngine.Instance.IsReady)
            {
                try
                {
                    var own = await LxJsEngine.Instance.GetLyricAsync(lxKey, LxPlatformMap.ToLxSong(track));
                    if (own is not null && own.IsUsable())
                    {
                        return new LyricsResult
                        {
                            Lrc = own.Lyric,
                            Translation = own.Tlyric ?? "",
                            Origin = LxPlatformMap.DisplayName(lxKey),
                        };
                    }
                }
                catch { /* 继续下一链路 */ }
            }
        }

        // 2. LRCLIB
        try
        {
            var lrc = await FetchLrcLibAsync(track, ct);
            if (lrc is not null) return lrc;
        }
        catch { /* 继续下一链路 */ }

        return null;
    }

    private static async Task<LyricsResult?> FetchLrcLibAsync(Track track, CancellationToken ct)
    {
        // /api/get 精确匹配
        var duration = (int)Math.Round(track.DurationSeconds);
        var url = "https://lrclib.net/api/get?" +
                  $"track_name={Uri.EscapeDataString(track.Title)}" +
                  $"&artist_name={Uri.EscapeDataString(track.Artist)}" +
                  (string.IsNullOrEmpty(track.Album) ? "" : $"&album_name={Uri.EscapeDataString(track.Album)}") +
                  (duration > 0 ? $"&duration={duration}" : "");

        var json = await TryGetStringAsync(url, ct);
        if (json is not null)
        {
            var r = ParseLrcLib(json);
            if (r is not null) return r with { Origin = "LRCLIB" };
        }

        // /api/search 模糊匹配
        var searchUrl = "https://lrclib.net/api/search?" +
                        $"track_name={Uri.EscapeDataString(track.Title)}" +
                        $"&artist_name={Uri.EscapeDataString(track.Artist)}";
        json = await TryGetStringAsync(searchUrl, ct);
        if (json is null) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.ValueKind.Equals(JsonValueKind.Array) || doc.RootElement.GetArrayLength() == 0) return null;

            LyricsResult? best = null;
            var bestScore = -1;
            foreach (var item in doc.RootElement.EnumerateArray().Take(10))
            {
                var synced = item.TryGetProperty("syncedLyrics", out var sy) && sy.ValueKind == JsonValueKind.String
                    ? sy.GetString() : null;
                if (string.IsNullOrWhiteSpace(synced)) continue;

                var tName = item.TryGetProperty("trackName", out var tn) ? tn.GetString() ?? "" : "";
                var aName = item.TryGetProperty("artistName", out var an) ? an.GetString() ?? "" : "";
                var score = ScoreMatch(track.Title, tName) + ScoreMatch(track.Artist, aName);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = new LyricsResult { Lrc = synced, Origin = "LRCLIB" };
                }
            }
            return best;
        }
        catch
        {
            return null;
        }
    }

    private static LyricsResult? ParseLrcLib(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? synced = root.TryGetProperty("syncedLyrics", out var sy) && sy.ValueKind == JsonValueKind.String
                ? sy.GetString() : null;
            if (string.IsNullOrWhiteSpace(synced)) return null;
            return new LyricsResult { Lrc = synced };
        }
        catch
        {
            return null;
        }
    }

    private static int ScoreMatch(string a, string b)
    {
        a = (a ?? "").Trim().ToLowerInvariant();
        b = (b ?? "").Trim().ToLowerInvariant();
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a == b) return 2;
        return a.Contains(b) || b.Contains(a) ? 1 : 0;
    }

    private static async Task<string?> TryGetStringAsync(string url, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync(ct);
        }
        catch
        {
            return null;
        }
    }

    private static string Hash(string s)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes)[..24].ToLowerInvariant();
    }
}
