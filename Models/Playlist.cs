using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml.Media.Imaging;

namespace PixelPlayer.WinUI.Models;

public sealed class Playlist
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新建播放列表";
    public List<string> TrackPaths { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>云端歌单（网易云等）的远程 Id，本地歌单为空。</summary>
    public string? CloudId { get; set; }
    /// <summary>云端歌单封面 URL。</summary>
    public string? CoverUrl { get; set; }

    [JsonIgnore] public bool IsCloud => !string.IsNullOrEmpty(CloudId);
    [JsonIgnore] public string CountText => $"{TrackPaths.Count} 首";
    [JsonIgnore] public BitmapImage? CoverThumb { get; set; }
}

/// <summary>专辑聚合（按 专辑名 + 专辑艺术家 分组）。</summary>
public sealed class AlbumGroup
{
    public string Name { get; init; } = "";
    public string Artist { get; init; } = "";
    public List<Track> Tracks { get; init; } = new();
    public BitmapImage? CoverThumb { get; init; }

    public string DetailText => $"{Artist} · {Tracks.Count} 首";
}
