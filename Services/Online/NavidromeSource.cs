using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PixelPlayer.WinUI.Models;

namespace PixelPlayer.WinUI.Services.Online;

/// <summary>Navidrome（Subsonic 协议）音源（对应参照项目 NavidromeApiService）。</summary>
public sealed class NavidromeSource : OnlineSource
{
    public override string Id => "navidrome";
    public override string Name => "Navidrome";

    private static readonly HttpClient _http = CreateClient("", "PixelPlayer/1.0");
    private string _salt = NewSalt();

    private static string NewSalt() => Guid.NewGuid().ToString("N").Substring(0, 12);

    public override bool IsConfigured =>
        !string.IsNullOrEmpty(SettingsService.Instance.Settings.NavidromeServer)
        && !string.IsNullOrEmpty(SettingsService.Instance.Settings.NavidromeUser)
        && SettingsService.Instance.Settings.NavidromePassword is not null;

    private (string Base, string User, string Password) Conf
    {
        get
        {
            var s = SettingsService.Instance.Settings;
            return (s.NavidromeServer.TrimEnd('/'), s.NavidromeUser, s.NavidromePassword ?? "");
        }
    }

    /// <summary>Subsonic 认证参数（md5(password+salt)）。</summary>
    private string AuthParams()
    {
        var (_, user, pass) = Conf;
        _salt = NewSalt();
        var token = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(pass + _salt))).ToLowerInvariant();
        return $"u={Uri.EscapeDataString(user)}&t={token}&s={_salt}&v=1.16.1&c=PixelPlayer&f=json";
    }

    public override async Task<List<Track>> SearchAsync(string keyword, int limit, CancellationToken ct = default)
    {
        var (base_, _, _) = Conf;
        var url = $"{base_}/rest/search3.view?{AuthParams()}&query={Uri.EscapeDataString(keyword)}&songCount={limit}&artistCount=0&albumCount=0";
        using var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var list = new List<Track>();

        if (!doc.RootElement.TryGetProperty("subsonic-response", out var root)
            || !root.TryGetProperty("searchResult3", out var sr)
            || !sr.TryGetProperty("song", out var arr)
            || arr.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var s in arr.EnumerateArray())
        {
            var id = TrimText(s, "id");
            var name = TrimText(s, "title");
            var artist = TrimText(s, "artist");
            var album = TrimText(s, "album");
            var dur = s.TryGetProperty("duration", out var d) && d.TryGetInt64(out var dv) ? dv : 0;
            string? pic = TrimText(s, "coverArt");
            pic = string.IsNullOrEmpty(pic)
                ? null
                : $"{base_}/rest/getCoverArt.view?{AuthParams()}&id={Uri.EscapeDataString(pic)}";

            if (id.Length == 0 || name.Length == 0) continue;

            list.Add(new Track
            {
                Path = $"navidrome://{id}",
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
        var (base_, _, _) = Conf;
        var url = $"{base_}/rest/stream.view?{AuthParams()}&id={Uri.EscapeDataString(track.OnlineKey)}";

        // 校验服务可达（直链本身免头即可播）
        try
        {
            using var resp = await _http.GetAsync($"{base_}/rest/ping.view?{AuthParams()}", ct);
            resp.EnsureSuccessStatusCode();
        }
        catch
        {
            return null;
        }

        return url;
    }

    /// <summary>scrobble 上报（对应参照项目 Navidrome scrobble）。</summary>
    public async Task ScrobbleAsync(Track track, CancellationToken ct = default)
    {
        if (!IsConfigured) return;
        try
        {
            var (base_, _, _) = Conf;
            await _http.GetAsync(
                $"{base_}/rest/scrobble.view?{AuthParams()}&id={Uri.EscapeDataString(track.OnlineKey)}&submission=true", ct);
        }
        catch
        {
            // 忽略上报失败
        }
    }
}
