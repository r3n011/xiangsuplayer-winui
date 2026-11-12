using System;
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

namespace PixelPlayer.WinUI.Services.Online;

/// <summary>Bilibili 音源（音频区搜索 + dash 直链，对应参照项目 BilibiliSearchApi；含 wbi 签名）。</summary>
public sealed class BilibiliSource : OnlineSource
{
    public override string Id => "bili";
    public override string Name => "哔哩哔哩";

    private static readonly HttpClient _http = CreateClient("https://www.bilibili.com/", "");

    private const string MixinTable = "46,47,18,2,53,8,23,32,15,50,10,31,58,3,45,27,11,25,37,40,29,33,16,6,35,38,9,57,56,21,44,30";

    private string? _mixinKey;
    private string? _buvid3;
    private DateTime _wbiFetchedAt = DateTime.MinValue;

    private void EnsureCookies()
    {
        if (_buvid3 is null)
        {
            try
            {
                var json = _http.GetStringAsync("https://api.bilibili.com/x/frontend/finger/spi").Result;
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("data", out var d))
                {
                    _buvid3 = TrimText(d, "b_3");
                }
            }
            catch
            {
                _buvid3 = "";
            }
            _buvid3 ??= "";
        }
    }

    private async Task EnsureWbiKeyAsync(CancellationToken ct)
    {
        if (_mixinKey is not null && DateTime.Now - _wbiFetchedAt < TimeSpan.FromHours(2)) return;
        EnsureCookies();

        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.bilibili.com/x/web-interface/nav");
        if (_buvid3.Length > 0) req.Headers.TryAddWithoutValidation("Cookie", $"buvid3={_buvid3}");
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("data", out var d)
            || !d.TryGetProperty("wbi_img", out var wbi))
        {
            throw new InvalidOperationException("无法获取 Bilibili wbi key");
        }

        var imgKey = Path.GetFileNameWithoutExtension(TrimText(wbi, "img_url"));
        var subKey = Path.GetFileNameWithoutExtension(TrimText(wbi, "sub_url"));
        var raw = imgKey + subKey;

        var indices = MixinTable.Split(',').Select(int.Parse).ToArray();
        _mixinKey = new string(indices.Take(32).Where(i => i < raw.Length).Select(i => raw[i]).ToArray());
        _wbiFetchedAt = DateTime.Now;
    }

    private static (string W_Rid, long Wts) Sign(IDictionary<string, string> query, string mixinKey)
    {
        var wts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        query["wts"] = wts.ToString();
        // 按 key ASCII 排序，过滤 !'()*
        var encoded = string.Join("&", query
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv =>
            {
                var v = Uri.EscapeDataString(kv.Value).Replace("!", "%21").Replace("'", "%27")
                    .Replace("(", "%28").Replace(")", "%29").Replace("*", "%2A");
                return $"{Uri.EscapeDataString(kv.Key)}={v}";
            }));
        var md5 = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(encoded + mixinKey))).ToLowerInvariant();
        return (md5, wts);
    }

    private async Task<JsonElement> GetSignedJsonAsync(string endpoint, Dictionary<string, string> query, CancellationToken ct)
    {
        await EnsureWbiKeyAsync(ct);
        var (rid, _) = Sign(query, _mixinKey!);
        query["w_rid"] = rid;
        var qs = string.Join("&", query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.bilibili.com{endpoint}?{qs}");
        req.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
        if (_buvid3?.Length > 0) req.Headers.TryAddWithoutValidation("Cookie", $"buvid3={_buvid3}");
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.Clone().GetProperty("data");
    }

    public override async Task<List<Track>> SearchAsync(string keyword, int limit, CancellationToken ct = default)
    {
        var data = await GetSignedJsonAsync("/x/web-interface/wbi/search/type", new Dictionary<string, string>
        {
            ["search_type"] = "video",
            ["keyword"] = keyword,
            ["page"] = "1",
            ["page_size"] = Math.Min(limit, 50).ToString(),
        }, ct);

        var list = new List<Track>();
        if (!data.TryGetProperty("result", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;

        foreach (var s in arr.EnumerateArray())
        {
            if (TrimText(s, "type") is { } t && t.Length > 0 && t != "video") continue;

            var bvid = TrimText(s, "bvid");
            var title = StripTags(TrimText(s, "title"));
            var author = TrimText(s, "author");
            var dur = s.TryGetProperty("duration", out var d) && d.TryGetInt64(out var dv) ? dv : 0;
            var pic = TrimText(s, "pic");
            if (pic.StartsWith("//")) pic = "https:" + pic;

            if (bvid.Length == 0 || title.Length == 0) continue;

            list.Add(new Track
            {
                Path = $"bilibili://{bvid}",
                SourceId = Id,
                OnlineKey = bvid,
                Title = title,
                Artist = author,
                Album = "哔哩哔哩",
                DurationSeconds = dur,
                PicUrl = string.IsNullOrEmpty(pic) ? null : pic,
            });
        }

        return list;
    }

    public override async Task<string?> GetStreamUrlAsync(Track track, CancellationToken ct = default)
    {
        // 1. 取 cid
        using var viewReq = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.bilibili.com/x/web-interface/view?bvid={Uri.EscapeDataString(track.OnlineKey)}");
        viewReq.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
        if (_buvid3?.Length > 0) viewReq.Headers.TryAddWithoutValidation("Cookie", $"buvid3={_buvid3}");
        using var viewResp = await _http.SendAsync(viewReq, ct);
        viewResp.EnsureSuccessStatusCode();

        long cid = 0;
        using (var viewDoc = JsonDocument.Parse(await viewResp.Content.ReadAsStringAsync(ct)))
        {
            if (viewDoc.RootElement.TryGetProperty("data", out var vd))
            {
                if (vd.TryGetProperty("cid", out var c) && c.TryGetInt64(out var cv)) cid = cv;
            }
        }
        if (cid == 0) return null;

        // 2. playurl（dash 音频流）
        var data = await GetSignedJsonAsync("/x/player/wbi/playurl", new Dictionary<string, string>
        {
            ["bvid"] = track.OnlineKey,
            ["cid"] = cid.ToString(),
            ["qn"] = "64",
            ["fnval"] = "16",
            ["fnver"] = "0",
            ["platform"] = "web",
        }, ct);

        if (!data.TryGetProperty("dash", out var dash)
            || !dash.TryGetProperty("audio", out var audio)
            || audio.ValueKind != JsonValueKind.Array
            || audio.GetArrayLength() == 0)
        {
            return null;
        }

        var baseUrl = TrimText(audio[0], "baseUrl");
        if (string.IsNullOrEmpty(baseUrl)) baseUrl = TrimText(audio[0], "base_url");
        if (string.IsNullOrEmpty(baseUrl)) return null;

        // B 站 CDN 校验 Referer → 走本地流代理
        var cookie = _buvid3?.Length > 0 ? $"buvid3={_buvid3}" : "";
        return StreamProxyService.ProxyUrl(baseUrl, "https://www.bilibili.com/", cookie);
    }
}
