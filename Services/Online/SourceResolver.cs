using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PixelPlayer.WinUI.Models;
using PixelPlayer.WinUI.Services.Lx;

namespace PixelPlayer.WinUI.Services.Online;

/// <summary>失败类型（对齐参照项目 FailureKind）：供外层选择退避时间。</summary>
public enum SourceFailureKind
{
    NoUrl,
    RateLimited,
    Network,
    NoSource,
}

/// <summary>解析结果。</summary>
public sealed class SourceResolveOutcome
{
    public bool Success { get; init; }
    public string? Url { get; init; }
    public string? SourceId { get; init; }
    public string Quality { get; init; } = "";
    public bool FromCache { get; init; }
    public bool Switched { get; init; }
    public SourceFailureKind Kind { get; init; }
    public string Message { get; init; } = "";

    public static SourceResolveOutcome Ok(string url, string sourceId, string quality, bool fromCache, bool switched) => new()
    {
        Success = true, Url = url, SourceId = sourceId, Quality = quality, FromCache = fromCache, Switched = switched,
    };

    public static SourceResolveOutcome Fail(SourceFailureKind kind, string message) => new()
    {
        Success = false, Kind = kind, Message = message,
    };
}

/// <summary>
/// 统一在线音源解析核心（对齐参照项目 OnlineSourceResolver，lx-music 同款链路）：
/// 平台音源全部来自 LX 音源脚本（像素音源市场下载），应用不内置任何平台 API。
/// ① 当前/指定平台：脚本 getPlayUrl 按音质链逐个解析（缓存命中直接返回）
/// ② 跨源换源：拿「歌名+歌手+专辑+时长」用脚本在其它平台重新搜索同一首歌（9 档排序）
/// ③ 全部失败如实报错，外层按失败类型退避后重试一次
/// </summary>
public static class SourceResolver
{
    private static readonly HttpClient _rangeHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(8),
    };

    /// <summary>解析直链；preferredSource 非空时优先只试这个源，失败再回退其它源。</summary>
    public static async Task<SourceResolveOutcome> ResolveAsync(
        Track track,
        string? preferredSource = null,
        bool forceRefresh = false,
        string quality = "320k",
        CancellationToken ct = default)
    {
        // LX 音源脚本优先就绪（对齐参照 awaitReady(15s)：首次播放也走落雪音源）。
        // Jint 初始化 13 个脚本可能要几十秒（大脚本 eval + 逐个 init 等待），放宽到 60s。
        var lxReady = await LxJsEngine.Instance.AwaitReadyAsync(60_000);
        App.LogCrash("Resolve", new Exception(
            $"start: {track.Title} [{track.SourceId}/{track.OnlineKey}] lxReady={lxReady} lxSources={LxJsEngine.Instance.GetSources().Count}"));

        var sourceOrder = BuildSourceOrder(track, preferredSource, lxReady);
        if (sourceOrder.Count == 0)
        {
            return SourceResolveOutcome.Fail(SourceFailureKind.NoSource, "没有可用音源");
        }

        var lastFailure = SourceResolveOutcome.Fail(SourceFailureKind.NoUrl, "所有音源均未返回可用链接");
        const int attempts = 2; // enableRetry 对齐参照：重试一次

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (attempt > 0)
            {
                // 对齐 lx：限流类失败等更久，普通失败 1.8s 后重试一次
                var waitMs = lastFailure.Kind == SourceFailureKind.RateLimited ? 3_500 : 1_800;
                try { await Task.Delay(waitMs, ct); } catch (OperationCanceledException) { break; }
            }

            var result = await ResolveOnceAsync(track, sourceOrder, preferredSource, forceRefresh, quality, lxReady, ct);
            if (result.Success) return result;
            lastFailure = result;
            if (lastFailure.Kind == SourceFailureKind.NoSource) return lastFailure;
        }

        return lastFailure;
    }

    private static async Task<SourceResolveOutcome> ResolveOnceAsync(
        Track track,
        List<string> sourceOrder,
        string? preferredSource,
        bool forceRefresh,
        string quality,
        bool lxReady,
        CancellationToken ct)
    {
        var cacheKey = $"{track.SourceId}:{track.OnlineKey}";
        var lxSources = lxReady ? LxJsEngine.Instance.GetSources() : new Dictionary<string, LxJsEngine.LxSourceInfo>();

        // ① 当前/指定平台：只用 LX 脚本音源（getPlayUrl），按音质链逐个解析
        foreach (var source in sourceOrder)
        {
            var lxKey = LxPlatformMap.KeyOf(source) ?? source;
            if (!lxSources.ContainsKey(lxKey)) continue;
            foreach (var q in QualityChain(quality, lxSources[lxKey].Qualitys))
            {
                if (!forceRefresh && OnlineSourceRegistry.TryGetCached($"{cacheKey}:{q}", out var cached))
                {
                    return SourceResolveOutcome.Ok(cached, source, q, fromCache: true, switched: false);
                }
                var url = await ResolveFromLxAsync(lxKey, track, q, ct);
                if (url is not null)
                {
                    OnlineSourceRegistry.PutCached($"{cacheKey}:{q}", url);
                    track.StreamUrl = url;
                    return SourceResolveOutcome.Ok(url, source, q, fromCache: false, switched: false);
                }
            }
        }

        // ② 跨源换源：按歌名/歌手/专辑/时长用脚本在其它平台重新搜索同一首歌
        var exclude = preferredSource?.TakeIfNotBlank() ?? track.SourceId.TakeIfNotBlank();
        var candidates = await FindOtherSourceCandidatesAsync(
            track.Title, track.Artist, track.Album, (long)track.DurationSeconds * 1000, exclude, ct: ct);

        foreach (var candidate in candidates)
        {
            var candidateLx = LxPlatformMap.KeyOf(candidate.SourceId) ?? candidate.SourceId;
            if (!lxSources.ContainsKey(candidateLx)) continue;
            foreach (var q in QualityChain(quality, lxSources[candidateLx].Qualitys))
            {
                var url = await ResolveFromLxAsync(candidateLx, candidate, q, ct);
                if (url is not null)
                {
                    OnlineSourceRegistry.PutCached($"{cacheKey}:{q}", url);
                    track.StreamUrl = url;
                    return SourceResolveOutcome.Ok(url, candidate.SourceId, q, fromCache: false, switched: true);
                }
            }
        }

        return SourceResolveOutcome.Fail(SourceFailureKind.NoUrl, "所有音源均未返回可用链接");
    }

    /// <summary>LX 脚本 getPlayUrl 解析（应用不内置任何平台 API 兜底）。</summary>
    private static async Task<string?> ResolveFromLxAsync(string lxKey, Track song, string quality, CancellationToken ct)
    {
        try
        {
            var lxUrl = await LxJsEngine.Instance.GetPlayUrlAsync(lxKey, LxPlatformMap.ToLxSong(song), quality);
            return StreamUrlValidator.IsUsable(lxKey, lxUrl) ? lxUrl : null;
        }
        catch
        {
            return null;
        }
    }

    private static List<string> BuildSourceOrder(Track track, string? preferredSource, bool lxReady)
    {
        // 只列 LX 脚本注册的音源平台（wy/tx/kg/kw/mg → 内部 id）
        var ids = lxReady
            ? LxJsEngine.Instance.GetSources().Keys
                .Where(k => KnownLxSources.Contains(k))
                .Select(LxPlatformMap.IdOf)
                .Where(id => id is not null)
                .Cast<string>()
                .ToList()
            : new List<string>();

        var head = preferredSource.TakeIfNotBlank() ?? track.SourceId.TakeIfNotBlank();
        var order = head is null
            ? ids
            : (ids.Contains(head)
                ? new[] { head }.Concat(ids.Where(id => id != head))
                : ids).Distinct().ToList();
        return order;
    }

    private static readonly HashSet<string> KnownLxSources = new() { "wy", "tx", "kg", "kw", "mg" };

    /// <summary>音质链：目标音质 + 320k/128k 兜底；LX 音源支持列表内的音质优先（对齐参照 qualityChain）。</summary>
    private static List<string> QualityChain(string target, List<string>? lxQualitys = null)
    {
        var chain = new List<string>();
        if (lxQualitys is { Count: > 0 })
        {
            foreach (var q in lxQualitys) chain.Add(q);
        }
        chain.Add(target);
        chain.Add("320k");
        chain.Add("128k");
        return chain.Where(q => !string.IsNullOrEmpty(q)).Distinct().ToList();
    }

    // ─── 换源：用元信息在其它平台重新搜同一首歌（移植落雪 findMusic）──────────
    //
    // 关键点：换源必须换成目标平台自己的歌曲标识。拿本源的 songmid/hash 硬喂给
    // 别的平台去查是错的。落雪的做法是：拿「歌名 + 歌手 + 专辑 + 时长」到其它
    // 平台重新搜索，再做归一化比对挑出同一首歌，然后用它自己的 id 取链。

    private static readonly Regex MatchStripRegex = new(@"[\s'.,，&""、()（）`~\-<>|/\[\]!！]", RegexOptions.Compiled);
    private static readonly Regex SingerSplitRegex = new(@"[、&;；/,，|]", RegexOptions.Compiled);

    private static string StripForMatch(string? value) =>
        value is null ? "" : MatchStripRegex.Replace(value, "").ToLowerInvariant();

    /// <summary>歌手名归一化：多歌手先排序再比较，避免「A、B」与「B、A」被判成不同。</summary>
    private static string SortSinger(string? value)
    {
        if (value is null) return "";
        var parts = SingerSplitRegex.Split(value).Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        return parts.Count > 1 ? string.Join("、", parts.OrderBy(p => p, StringComparer.Ordinal)) : value;
    }

    /// <summary>
    /// 换源候选：除 excludeSource 外，各平台并行用「歌名 + 歌手」搜索，
    /// 再按落雪 findMusic 的规则挑出最像的同一首歌。
    /// </summary>
    public static async Task<List<Track>> FindOtherSourceCandidatesAsync(
        string name,
        string singer,
        string albumName = "",
        long durationMs = 0L,
        string excludeSource = "",
        int perSourceLimit = 25,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) return new List<Track>();
        var keyword = $"{name} {singer}".Trim();
        if (!LxJsEngine.Instance.IsReady) return new List<Track>();

        // 只用 LX 音源脚本搜索（不内置平台 API）
        var excludeLx = LxPlatformMap.KeyOf(excludeSource) ?? excludeSource;
        var lxKeys = LxJsEngine.Instance.GetSources().Keys
            .Where(k => KnownLxSources.Contains(k) && k != excludeLx)
            .ToList();
        if (lxKeys.Count == 0) return new List<Track>();

        // 各平台并行搜索
        var perSourceLists = await Task.WhenAll(lxKeys.Select(async lxKey =>
        {
            try
            {
                var r = await LxJsEngine.Instance.SearchAsync(keyword, lxKey, 1, perSourceLimit);
                return r.List.Select(LxPlatformMap.ToTrack).ToList();
            }
            catch
            {
                return new List<Track>();
            }
        }));

        var fName = StripForMatch(name);
        var fSinger = StripForMatch(SortSinger(singer));
        var fAlbum = StripForMatch(albumName);

        // 时长校验：±5 秒内才算同一首（防翻唱/Live）。任一侧没有时长则不校验。
        // 必须兼容「秒/毫秒」两种单位：接口来源不同，有的给秒有的给毫秒。
        static bool SameDuration(long durationMs, long ms)
        {
            if (durationMs <= 0L || ms <= 0L) return true;
            var a = durationMs >= 10_000L ? durationMs : durationMs * 1000L;
            var b = ms >= 10_000L ? ms : ms * 1000L;
            return Math.Abs(a - b) < 5_000L;
        }

        bool SingerMatches(string candidateSingers)
        {
            if (fSinger.Length == 0) return true;
            var f = StripForMatch(SortSinger(candidateSingers));
            return f.Length > 0 && (fSinger.Contains(f) || f.Contains(fSinger));
        }

        bool NameMatches(string candidateName)
        {
            var f = StripForMatch(candidateName);
            return f.Length > 0 && (fName.Contains(f) || f.Contains(fName));
        }

        long DurationOf(Track t) => (long)t.DurationSeconds * 1000;

        // 每个平台先各自挑「第一条命中」，三级匹配从严到宽（与落雪一致）
        var perSource = perSourceLists
            .Select(list =>
            {
                var usable = list.Where(t => SameDuration(durationMs, DurationOf(t))).ToList();
                if (usable.Count == 0) return null;
                return usable.FirstOrDefault(t =>
                            StripForMatch(t.Title) == fName && SingerMatches(t.Artist))
                    ?? usable.FirstOrDefault(t =>
                            fSinger.Length > 0 && StripForMatch(SortSinger(t.Artist)) == fSinger &&
                            NameMatches(t.Title))
                    ?? usable.FirstOrDefault(t =>
                            fAlbum.Length > 0 && StripForMatch(t.Album) == fAlbum &&
                            SingerMatches(t.Artist) && NameMatches(t.Title));
            })
            .Where(t => t is not null)
            .Cast<Track>()
            .ToList();

        // 全局 9 档排序：越像的越靠前（对齐落雪 sortMusic 的档位顺序）
        var tiers = new List<Func<Track, bool>>
        {
            t => StripForMatch(SortSinger(t.Artist)) == fSinger && StripForMatch(t.Title) == fName && durationMs > 0L && DurationOf(t) == durationMs,
            t => StripForMatch(t.Title) == fName && StripForMatch(SortSinger(t.Artist)) == fSinger && fAlbum.Length > 0 && StripForMatch(t.Album) == fAlbum,
            t => StripForMatch(SortSinger(t.Artist)) == fSinger && StripForMatch(t.Title) == fName,
            t => StripForMatch(t.Title) == fName && durationMs > 0L && DurationOf(t) == durationMs,
            t => StripForMatch(SortSinger(t.Artist)) == fSinger && durationMs > 0L && DurationOf(t) == durationMs,
            t => durationMs > 0L && DurationOf(t) == durationMs,
            t => StripForMatch(t.Title) == fName,
            t => StripForMatch(SortSinger(t.Artist)) == fSinger,
            t => fAlbum.Length > 0 && StripForMatch(t.Album) == fAlbum,
        };

        var picked = new List<Track>();
        var remaining = perSource.ToList();
        foreach (var predicate in tiers)
        {
            for (var i = remaining.Count - 1; i >= 0; i--)
            {
                if (predicate(remaining[i]))
                {
                    picked.Add(remaining[i]);
                    remaining.RemoveAt(i);
                }
            }
        }
        picked.AddRange(remaining);

        return picked
            .DistinctBy(t => $"{t.SourceId}_{t.OnlineKey}")
            .ToList();
    }

    /// <summary>校验直链真的可播（Range: bytes=0-1，接受 2xx/206），避免把 410 等失效链接交给播放器。</summary>
    public static async Task<bool> IsPlayableAsync(string url, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Range", "bytes=0-1");
            req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Linux; Android 14) AppleWebKit/537.36");
            using var resp = await _rangeHttp.SendAsync(req, ct);
            var code = (int)resp.StatusCode;
            return code is >= 200 and <= 206;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>在线直链可用性校验：只接受 http(s)，并过滤加密容器/跨平台占位流（对齐参照 StreamUrlValidator）。</summary>
public static class StreamUrlValidator
{
    private static readonly string[] EncryptedExtensions =
    {
        ".mflac", ".mflac0", ".mflac2", ".mgg", ".mgg0", ".mgg1", ".mggl", ".mmp4",
    };

    public static bool IsUsable(string source, string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        var trimmed = url.Trim();
        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return false;

        var path = trimmed.Split('?', '#')[0].ToLowerInvariant();
        if (EncryptedExtensions.Any(path.EndsWith)) return false;

        // 非 kw 源不允许酷我域名占位流（试听流带语音提示）
        if (source != "kuwo")
        {
            try
            {
                var host = new Uri(trimmed).Host.ToLowerInvariant();
                if (host == "kuwo.cn" || host.EndsWith(".kuwo.cn") ||
                    host == "kuwo.com" || host.EndsWith(".kuwo.com"))
                {
                    return false;
                }
            }
            catch
            {
                return false;
            }
        }
        return true;
    }
}

internal static class StringExtensions
{
    public static string? TakeIfNotBlank(this string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s;
}
