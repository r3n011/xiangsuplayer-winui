using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PixelPlayer.WinUI.Models;

namespace PixelPlayer.WinUI.Services.Online;

/// <summary>
/// 在线音源抽象（对应参照项目 OnlineSourceResolver + CloudStreamProxy 三层抽象）。
/// 每个源提供：搜索 / 直链解析 / 歌词（可选）。
/// </summary>
public abstract class OnlineSource
{
    public abstract string Id { get; }
    public abstract string Name { get; }

    /// <summary>需要用户配置（服务器/凭据）时为 false，UI 可提示。</summary>
    public virtual bool IsConfigured => true;

    /// <summary>搜索歌曲，返回带 SourceId/OnlineKey 的 Track 列表。</summary>
    public abstract Task<List<Track>> SearchAsync(string keyword, int limit, CancellationToken ct = default);

    /// <summary>解析播放直链；不可用返回 null（上层自动跳下一首）。</summary>
    public abstract Task<string?> GetStreamUrlAsync(Track track, CancellationToken ct = default);

    /// <summary>歌词（可选）：返回 (lrc, 翻译lrc)。</summary>
    public virtual Task<(string Lrc, string Translation)?> GetLyricAsync(Track track, CancellationToken ct = default)
        => Task.FromResult<(string, string)?>(null);

    protected static HttpClient CreateClient(string referer, string ua)
    {
        var c = new HttpClient();
        if (!string.IsNullOrEmpty(referer)) c.DefaultRequestHeaders.Referrer = new Uri(referer);
        c.DefaultRequestHeaders.UserAgent.ParseAdd(string.IsNullOrEmpty(ua)
            ? "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120 PixelPlayer/1.0"
            : ua);
        c.Timeout = TimeSpan.FromSeconds(12);
        return c;
    }

    protected static string StripTags(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s ?? "", "<[^>]+>", "").Trim();

    protected static string TrimText(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";

    protected static double TrimMs(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.TryGetInt64(out var v) ? v / 1000.0 : 0;
}

/// <summary>音源注册中心：按 Id 解析 + 直链缓存（对应 PlaybackUrlCache）。</summary>
public static class OnlineSourceRegistry
{
    public static List<OnlineSource> Sources { get; } = new()
    {
        new NeteaseSource(),
        new QqSource(),
        new KugouSource(),
        new KuwoSource(),
        new BilibiliSource(),
        new JellyfinSource(),
        new NavidromeSource(),
    };

    public static OnlineSource? Resolve(string? id) =>
        string.IsNullOrEmpty(id) ? null : Sources.FirstOrDefault(s => s.Id == id);

    /// <summary>当前选中音源（未配置的回落到网易云）。</summary>
    public static OnlineSource Active
    {
        get
        {
            var s = Resolve(SettingsService.Instance.Settings.OnlineSourceId);
            return s is { IsConfigured: true } ? s : Sources[0];
        }
    }

    /// <summary>直链缓存（对应 PlaybackUrlCache，20 分钟过期）。</summary>
    private static readonly ConcurrentDictionary<string, (string Url, DateTime Expire)> _cache = new();

    public static async Task<string?> ResolveStreamUrlAsync(Track track, CancellationToken ct = default)
    {
        var key = $"{track.SourceId}:{track.OnlineKey}";
        if (_cache.TryGetValue(key, out var hit) && hit.Expire > DateTime.Now) return hit.Url;

        var src = Resolve(track.SourceId) ?? Active;
        var url = await src.GetStreamUrlAsync(track, ct);
        if (!string.IsNullOrEmpty(url))
        {
            _cache[key] = (url, DateTime.Now.AddMinutes(20));
            track.StreamUrl = url;
        }
        return url;
    }

    public static void ClearCache() => _cache.Clear();
}
