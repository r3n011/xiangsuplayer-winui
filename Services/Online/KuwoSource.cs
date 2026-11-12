using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PixelPlayer.WinUI.Models;

namespace PixelPlayer.WinUI.Services.Online;

/// <summary>酷我音源（对应参照项目 BuiltInSourceSearchApi kw 部分）。</summary>
public sealed class KuwoSource : OnlineSource
{
    public override string Id => "kuwo";
    public override string Name => "酷我音乐";

    private static readonly HttpClient _http = CreateClient("http://www.kuwo.cn/", "");
    private static string _csrfToken = Guid.NewGuid().ToString("N").Substring(0, 16);

    public override async Task<List<Track>> SearchAsync(string keyword, int limit, CancellationToken ct = default)
    {
        // www API 需要 kw_token cookie + csrf header（同值）
        var url = $"http://www.kuwo.cn/api/www/search/searchMusicBykeyWord?key={Uri.EscapeDataString(keyword)}" +
                  $"&pn=1&rn={limit}&httpsStatus=1&reqId=0";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("csrf", _csrfToken);
        req.Headers.TryAddWithoutValidation("Cookie", $"kw_token={_csrfToken}");
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var list = new List<Track>();

        if (!doc.RootElement.TryGetProperty("data", out var data)
            || !data.TryGetProperty("list", out var arr)
            || arr.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var s in arr.EnumerateArray())
        {
            var rid = TrimText(s, "rid");
            if (rid.Length == 0) rid = TrimText(s, "musicrid").Replace("MUSIC_", "");
            var name = TrimText(s, "name");
            var artist = TrimText(s, "artist");
            var album = TrimText(s, "album");
            var dur = s.TryGetProperty("duration", out var d) && d.TryGetInt64(out var dv) ? dv : 0;
            string? pic = TrimText(s, "pic");
            pic = string.IsNullOrEmpty(pic) ? null : pic;

            if (rid.Length == 0 || name.Length == 0) continue;

            list.Add(new Track
            {
                Path = $"cloud://kuwo/{rid}",
                SourceId = Id,
                OnlineKey = rid,
                Title = name,
                Artist = artist,
                Album = album,
                DurationSeconds = dur,
                PicUrl = pic,
            });
        }

        return list;
    }

    public override async Task<string?> GetStreamUrlAsync(Track track, CancellationToken ct = default)
    {
        // web 播放接口（免登录 128k/320k）
        var url = $"http://www.kuwo.cn/api/v1/www/music/playUrl?mid={track.OnlineKey}&type=music&br=320kmp3";
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("data", out var d)
                && d.TryGetProperty("url", out var u)
                && u.ValueKind == JsonValueKind.String)
            {
                var s = u.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
        }
        catch
        {
            // 继续尝试移动端接口
        }

        // 兜底：mobi.s（对应参照项目 kwplayer source）
        try
        {
            var mobi = $"http://mobi.kuwo.cn/mobi.s?f=kuwo&q={Uri.EscapeDataString($"{{\"br\":320,\"rid\":\"{track.OnlineKey}\"}}")}";
            using var resp = await _http.GetAsync(mobi, ct);
            resp.EnsureSuccessStatusCode();
            var text = await resp.Content.ReadAsStringAsync(ct);
            // 返回形如 key=value 行，找 url=
            foreach (var line in text.Split('\n'))
            {
                if (line.StartsWith("url=", StringComparison.OrdinalIgnoreCase))
                {
                    var s = line.Substring(4).Trim();
                    if (s.Length > 0) return s;
                }
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }
}
