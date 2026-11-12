using System;

namespace PixelPlayer.WinUI.Models;

/// <summary>一条播放历史记录（收听统计 / 媒体库「历史」标签）。</summary>
public sealed class PlayHistoryEntry
{
    public string Path { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public double DurationSeconds { get; set; }
    public DateTime PlayedAt { get; set; }

    public string PlayedAtText => PlayedAt switch
    {
        var t when t.Date == DateTime.Today => $"今天 {t:HH:mm}",
        var t when t.Date == DateTime.Today.AddDays(-1) => $"昨天 {t:HH:mm}",
        var t => $"{t:MM-dd HH:mm}",
    };
}
