using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PixelPlayer.WinUI.Models;

namespace PixelPlayer.WinUI.Services.Online;

/// <summary>QQ 音乐音源（对应参照项目 QqMusicApiService 的免签名链路）。</summary>
public sealed class QqSource : OnlineSource
{
    public override string Id => "qq";
    public override string Name => "QQ 音乐";

    private static readonly HttpClient _http = CreateClient("https://y.qq.com/", "");

    public override async Task<List<Track>> SearchAsync(string keyword, int limit, CancellationToken ct = default)
    {
        var url = $"https://c.y.qq.com/soso/fcgi-bin/client_search_cp?ct=24&qqmusic_ver=1238&remoteplace=txt.yqq.top" +
                  $"&t=0&searchid=0&aggr=1&cr=1&catZhida=1&p=1&n={limit}&w={Uri.EscapeDataString(keyword)}&format=json";
        using var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var list = new List<Track>();

        if (!doc.RootElement.TryGetProperty("data", out var data)
            || !data.TryGetProperty("song", out var song)
            || !song.TryGetProperty("list", out var arr)
            || arr.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var s in arr.EnumerateArray())
        {
            // 兼容 old/new 两种字段命名
            var mid = TrimText(s, "songmid");
            if (mid.Length == 0) mid = TrimText(s, "mid");
            if (mid.Length == 0 && s.TryGetProperty("id", out var idEl)) mid = idEl.ToString();

            var name = TrimText(s, "songname");
            if (name.Length == 0) name = TrimText(s, "name");

            var artist = "";
            if (s.TryGetProperty("singer", out var singers) && singers.ValueKind == JsonValueKind.Array)
            {
                artist = string.Join(" / ", singers.EnumerateArray()
                    .Select(a => TrimText(a, "name"))
                    .Where(x => x.Length > 0));
            }

            var album = TrimText(s, "albumname");
            if (album.Length == 0 && s.TryGetProperty("album", out var al) && al.ValueKind == JsonValueKind.Object)
            {
                album = TrimText(al, "name");
                album = string.IsNullOrEmpty(album) ? TrimText(al, "title") : album;
            }

            var dur = s.TryGetProperty("interval", out var itv) && itv.TryGetInt64(out var d) ? d : 0;
            string? pic = null;
            if (s.TryGetProperty("albummid", out var am) && am.ValueKind == JsonValueKind.String && am.GetString() is { Length: > 0 } albumMid)
            {
                pic = $"https://y.gtimg.cn/music/photo_new/T002R500x500M000{albumMid}.jpg";
            }

            if (mid.Length == 0 || name.Length == 0) continue;

            list.Add(new Track
            {
                Path = $"qq://{mid}",
                SourceId = Id,
                OnlineKey = mid,
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
        // vkey.GetVkeyServer 免签名 GET 链路
        var data = JsonSerializer.Serialize(new
        {
            req = new
            {
                module = "vkey.GetVkeyServer",
                method = "CgiGetVkey",
                param = new
                {
                    guid = "10000",
                    songmid = new[] { track.OnlineKey },
                    songtype = new[] { 0 },
                    uin = "0",
                    loginflag = 1,
                    platform = "20",
                },
            }
        });

        var url = $"https://u.y.qq.com/cgi-bin/musicu.fcg?format=json&data={Uri.EscapeDataString(data)}";
        using var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("req", out var req)
            || !req.TryGetProperty("data", out var d)
            || !d.TryGetProperty("midurlinfo", out var info)
            || info.GetArrayLength() == 0)
        {
            return null;
        }

        var purl = TrimText(info[0], "purl");
        if (string.IsNullOrEmpty(purl)) return null;
        return $"https://ws.stream.qqmusic.qq.com/{purl}";
    }
}
