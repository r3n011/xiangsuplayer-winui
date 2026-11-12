using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PixelPlayer.WinUI.Models;

namespace PixelPlayer.WinUI.Services.Online;

/// <summary>网易云音源（对应参照项目 NeteaseApiService 的公开接口部分）。</summary>
public sealed class NeteaseSource : OnlineSource
{
    public override string Id => "netease";
    public override string Name => "网易云音乐";

    private static readonly HttpClient _http = CreateClient("https://music.163.com/", "");

    public override async Task<List<Track>> SearchAsync(string keyword, int limit, CancellationToken ct = default)
    {
        var form = new Dictionary<string, string>
        {
            ["s"] = keyword,
            ["type"] = "1",
            ["limit"] = limit.ToString(),
            ["offset"] = "0",
        };

        using var resp = await _http.PostAsync(
            "https://music.163.com/api/search/get/web",
            new FormUrlEncodedContent(form), ct);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var songs = doc.RootElement.TryGetProperty("result", out var result)
                && result.TryGetProperty("songs", out var arr)
                && arr.ValueKind == JsonValueKind.Array
            ? arr
            : default;

        var list = new List<Track>();
        if (songs.ValueKind != JsonValueKind.Array) return list;

        foreach (var s in songs.EnumerateArray())
        {
            var id = s.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var iid) ? iid.ToString() : "";
            var name = TrimText(s, "name");

            var artist = "";
            if (s.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array)
            {
                artist = string.Join(" / ", artists.EnumerateArray()
                    .Select(a => TrimText(a, "name"))
                    .Where(x => x.Length > 0));
            }

            var album = "";
            string? pic = null;
            if (s.TryGetProperty("album", out var al))
            {
                album = TrimText(al, "name");
                pic = TrimText(al, "picUrl");
                if (string.IsNullOrEmpty(pic)) pic = null;
            }

            var dur = TrimMs(s, "duration");
            if (string.IsNullOrEmpty(id) || name.Length == 0) continue;

            list.Add(new Track
            {
                Path = $"netease://{id}",
                SourceId = Id,
                OnlineKey = id,
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
        // ids 方括号需 URL 编码
        var url = $"https://music.163.com/api/song/enhance/player/url?ids=%5B{track.OnlineKey}%5D&br=320000";
        using var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array
            || data.GetArrayLength() == 0)
        {
            return null;
        }

        var first = data[0];
        if (!first.TryGetProperty("url", out var urlEl) || urlEl.ValueKind != JsonValueKind.String) return null;
        var stream = urlEl.GetString();
        return string.IsNullOrWhiteSpace(stream) ? null : stream;
    }

    public override async Task<(string Lrc, string Translation)?> GetLyricAsync(Track track, CancellationToken ct = default)
    {
        try
        {
            var url = $"https://music.163.com/api/song/lyric?id={track.OnlineKey}&lv=1&kv=1&tv=-1";
            using var resp = await _http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            var lrc = root.TryGetProperty("lrc", out var l) && l.TryGetProperty("lyric", out var ll)
                ? ll.GetString() ?? "" : "";
            var trans = root.TryGetProperty("tlyric", out var t) && t.TryGetProperty("lyric", out var tl)
                ? tl.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(lrc)) return null;
            return (lrc, trans.Length > 0 ? trans : "");
        }
        catch
        {
            return null;
        }
    }
}
