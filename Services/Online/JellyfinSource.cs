using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PixelPlayer.WinUI.Models;
using PixelPlayer.WinUI.Services.Online;

namespace PixelPlayer.WinUI.Services.Online;

/// <summary>Jellyfin 自建媒体服务器音源（对应参照项目 JellyfinApiService）。</summary>
public sealed class JellyfinSource : OnlineSource
{
    public override string Id => "jellyfin";
    public override string Name => "Jellyfin";

    private static readonly HttpClient _http = CreateClient("", "PixelPlayer/1.0");
    private readonly object _authLock = new();

    public override bool IsConfigured =>
        !string.IsNullOrEmpty(SettingsService.Instance.Settings.JellyfinServer)
        && (!string.IsNullOrEmpty(SettingsService.Instance.Settings.JellyfinToken)
            || (!string.IsNullOrEmpty(SettingsService.Instance.Settings.JellyfinUser)
                && SettingsService.Instance.Settings.JellyfinPassword is not null));

    private string Base => SettingsService.Instance.Settings.JellyfinServer.TrimEnd('/');

    public async Task<bool> AuthenticateAsync(CancellationToken ct = default)
    {
        var s = SettingsService.Instance.Settings;
        if (string.IsNullOrEmpty(s.JellyfinServer)) return false;

        if (!string.IsNullOrEmpty(s.JellyfinToken)) return true;

        var body = JsonSerializer.Serialize(new { Username = s.JellyfinUser, Pw = s.JellyfinPassword });
        using var resp = await _http.PostAsync($"{Base}/Users/AuthenticateByName",
            new StringContent(body, Encoding.UTF8, "application/json"), ct);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var token = doc.RootElement.TryGetProperty("AccessToken", out var t) ? t.GetString() : null;
        var userId = doc.RootElement.TryGetProperty("User", out var u)
            && u.TryGetProperty("Id", out var uid) ? uid.GetString() : null;
        if (string.IsNullOrEmpty(token)) return false;

        s.JellyfinToken = token;
        SettingsService.Instance.SaveSettings();
        return true;
    }

    private string AuthQuery => $"api_key={Uri.EscapeDataString(SettingsService.Instance.Settings.JellyfinToken ?? "")}";

    public override async Task<List<Track>> SearchAsync(string keyword, int limit, CancellationToken ct = default)
    {
        if (!await AuthenticateAsync(ct)) return new();

        var url = $"{Base}/Users/{SettingsService.Instance.Settings.JellyfinToken}/Items?" +
                  $"searchTerm={Uri.EscapeDataString(keyword)}&IncludeItemTypes=Audio&Recursive=true" +
                  $"&Limit={limit}&{AuthQuery}&SortBy=SortName&MediaTypes=Audio";
        // Jellyfin 新版用 /Items 端点
        var itemsUrl = $"{Base}/Items?searchTerm={Uri.EscapeDataString(keyword)}&IncludeItemTypes=Audio&Recursive=true" +
                       $"&Limit={limit}&{AuthQuery}&MediaTypes=Audio";

        using var resp = await _http.GetAsync(itemsUrl, ct);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var list = new List<Track>();
        if (!doc.RootElement.TryGetProperty("Items", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;

        foreach (var s in arr.EnumerateArray())
        {
            var id = TrimText(s, "Id");
            var name = TrimText(s, "Name");
            var album = TrimText(s, "Album");
            var dur = s.TryGetProperty("RunTimeTicks", out var ticks) && ticks.TryGetInt64(out var tv)
                ? tv / 10_000_000.0 : 0;
            var artist = "";
            if (s.TryGetProperty("Artists", out var arts) && arts.ValueKind == JsonValueKind.Array)
            {
                artist = string.Join(" / ", arts.EnumerateArray().Select(a => a.GetString() ?? "").Where(x => x.Length > 0));
            }

            if (id.Length == 0 || name.Length == 0) continue;

            list.Add(new Track
            {
                Path = $"jellyfin://{id}",
                SourceId = Id,
                OnlineKey = id,
                Title = name,
                Artist = artist,
                Album = album,
                DurationSeconds = dur,
            });
        }

        return list;
    }

    public override async Task<string?> GetStreamUrlAsync(Track track, CancellationToken ct = default)
    {
        if (!await AuthenticateAsync(ct)) return null;

        // universal audio 端点直接出流
        return $"{Base}/Audio/{track.OnlineKey}/universal?UserId=&DeviceId=PixelPlayer&" +
               $"MaxStreamingBitrate=320000&Container=opus,mp3,aac,m4a,flac,webma,webm,wav,ogg&" +
               $"TranscodingProtocol=http&TranscodingContainer=ts&AudioCodec=aac&{AuthQuery}";
    }

    /// <summary>封面直链（UI 可选使用）。</summary>
    public string? GetImageUrl(Track track) =>
        IsConfigured ? $"{Base}/Items/{track.OnlineKey}/Images/Primary?{AuthQuery}" : null;
}
