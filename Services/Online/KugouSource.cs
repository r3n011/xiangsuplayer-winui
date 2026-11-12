using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PixelPlayer.WinUI.Models;

namespace PixelPlayer.WinUI.Services.Online;

/// <summary>酷狗音源（对应参照项目 BuiltInSourceSearchApi kg 部分）。</summary>
public sealed class KugouSource : OnlineSource
{
    public override string Id => "kugou";
    public override string Name => "酷狗音乐";

    private static readonly HttpClient _http = CreateClient("http://kugou.com/", "");

    public override async Task<List<Track>> SearchAsync(string keyword, int limit, CancellationToken ct = default)
    {
        var url = $"https://songsearch.kugou.com/song_search_v2?keyword={Uri.EscapeDataString(keyword)}" +
                  $"&page=1&pagesize={limit}&album_id=&music_type=0&duration=&singer=&correction=1";
        using var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var list = new List<Track>();

        if (!doc.RootElement.TryGetProperty("data", out var data)
            || !data.TryGetProperty("lists", out var arr)
            || arr.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var s in arr.EnumerateArray())
        {
            var hash = TrimText(s, "FileHash");
            var name = StripTags(TrimText(s, "SongName"));
            var artist = StripTags(TrimText(s, "SingerName"));
            var album = StripTags(TrimText(s, "AlbumName"));
            var dur = s.TryGetProperty("Duration", out var d) && d.TryGetInt64(out var dv) ? dv : 0;
            var mixSongId = TrimText(s, "MixSongID");
            string? pic = TrimText(s, "Image");
            pic = string.IsNullOrEmpty(pic) ? null : pic.Replace("/{size}", "/500");

            if (hash.Length == 0 || name.Length == 0) continue;

            list.Add(new Track
            {
                Path = $"kgaudio://{hash}",
                SourceId = Id,
                OnlineKey = hash,
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
        var url = $"http://m.kugou.com/app/i/getSongInfo.php?cmd=playInfo&hash={track.OnlineKey}";
        using var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("data", out var d)) return null;
        var stream = TrimText(d, "url");
        return string.IsNullOrWhiteSpace(stream) ? null : stream;
    }
}
