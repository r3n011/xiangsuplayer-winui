using System;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml.Media.Imaging;

namespace PixelPlayer.WinUI.Models;

/// <summary>一首曲目。曲库扫描时从标签读入，播放队列里直接复用该对象。</summary>
public sealed class Track
{
    public string Path { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public string Genre { get; set; } = "";
    public uint TrackNumber { get; set; }
    public uint Year { get; set; }
    public double DurationSeconds { get; set; }

    // 在线音源（网易云/QQ/酷狗/酷我/B站等）：本地曲目这些字段为默认值
    public string SourceId { get; set; } = "";      // netease / qq / kugou / kuwo / bili / jellyfin / navidrome
    public string OnlineKey { get; set; } = "";     // 各源的 id/mid/hash/bvid
    public string? PicUrl { get; set; }             // 在线封面
    [JsonIgnore] public bool IsOnline => !string.IsNullOrEmpty(SourceId) && !string.IsNullOrEmpty(OnlineKey);
    [JsonIgnore] public string StreamUrl { get; set; } = "";

    /// <summary>封面缩略图缓存文件（PNG），无封面时为 null。</summary>
    public string? CoverPath { get; set; }

    [JsonIgnore] public BitmapImage? CoverThumb { get; set; }

    [JsonIgnore] public TimeSpan Duration => TimeSpan.FromSeconds(DurationSeconds);

    [JsonIgnore] public string DurationText => Duration.TotalHours >= 1
        ? $"{(int)Duration.TotalHours}:{Duration.Minutes:D2}:{Duration.Seconds:D2}"
        : $"{Duration.Minutes}:{Duration.Seconds:D2}";

    [JsonIgnore] public string Subtitle => string.IsNullOrWhiteSpace(Artist) ? Album : Artist;

    [JsonIgnore] public string AlbumArtistText => string.IsNullOrWhiteSpace(Album) ? Artist : $"{Artist} · {Album}";
}
