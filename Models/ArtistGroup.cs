using System.Collections.Generic;
using Microsoft.UI.Xaml.Media.Imaging;

namespace PixelPlayer.WinUI.Models;

/// <summary>艺术家分组（媒体库「艺术家」标签）。</summary>
public sealed class ArtistGroup
{
    public string Name { get; set; } = "";
    public string AlbumCount { get; set; } = "";
    public List<Track> Tracks { get; set; } = new();
    public BitmapImage? CoverThumb { get; set; }

    public string DetailText => $"{Tracks.Count} 首曲目 · {AlbumCount}";
}
