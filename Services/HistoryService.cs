using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using PixelPlayer.WinUI.Models;

namespace PixelPlayer.WinUI.Services;

/// <summary>播放历史 + 收听统计（对应参照项目的 PlaybackStatsRepository / LibraryHistoryTab）。</summary>
public sealed class HistoryService
{
    public static HistoryService Instance { get; } = new();

    private static readonly string StoreFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PixelPlayer.WinUI", "history.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    /// <summary>按时间倒序的全部历史（最近在前）。</summary>
    public ObservableCollection<PlayHistoryEntry> Entries { get; } = new();

    /// <summary>历史变化时触发（UI 刷新用）。</summary>
    public event EventHandler? HistoryChanged;

    public bool IsLoaded { get; private set; }
    private Task? _loadTask;
    private readonly object _lock = new();

    private HistoryService() { }

    public Task LoadAsync() => _loadTask ??= LoadCoreAsync();

    private async Task LoadCoreAsync()
    {
        try
        {
            if (File.Exists(StoreFile))
            {
                var list = JsonSerializer.Deserialize<List<PlayHistoryEntry>>(await File.ReadAllTextAsync(StoreFile)) ?? new();
                lock (_lock)
                {
                    foreach (var entry in list.OrderByDescending(e => e.PlayedAt).Take(5000))
                    {
                        Entries.Add(entry);
                    }
                }
            }
        }
        catch { /* 坏了就当空历史 */ }
        IsLoaded = true;
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SaveAsync()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StoreFile)!);
            List<PlayHistoryEntry> snapshot;
            lock (_lock) snapshot = Entries.ToList();
            await File.WriteAllTextAsync(StoreFile, JsonSerializer.Serialize(snapshot, JsonOptions));
        }
        catch { /* 忽略写盘失败 */ }
    }

    /// <summary>记录一次播放（开始播放某曲目时调用）。</summary>
    public async Task LogAsync(Track track)
    {
        if (track is null) return;

        var entry = new PlayHistoryEntry
        {
            Path = track.Path,
            Title = track.Title,
            Artist = track.Artist,
            Album = track.Album,
            DurationSeconds = track.DurationSeconds,
            PlayedAt = DateTime.Now,
        };

        lock (_lock)
        {
            Entries.Insert(0, entry);
            if (Entries.Count > 5000) Entries.RemoveAt(Entries.Count - 1);
        }

        HistoryChanged?.Invoke(this, EventArgs.Empty);
        await SaveAsync();
    }

    public void Clear()
    {
        lock (_lock) Entries.Clear();
        HistoryChanged?.Invoke(this, EventArgs.Empty);
        _ = SaveAsync();
    }

    /* ---------------- 统计（对应 StatsScreen / 收听统计） ---------------- */

    public int TotalPlays { get { lock (_lock) return Entries.Count; } }

    public TimeSpan TotalListened
    {
        get
        {
            lock (_lock) return TimeSpan.FromSeconds(Entries.Sum(e => e.DurationSeconds));
        }
    }

    public int TodayPlays { get { lock (_lock) return Entries.Count(e => e.PlayedAt.Date == DateTime.Today); } }

    public TimeSpan TodayListened
    {
        get
        {
            lock (_lock) return TimeSpan.FromSeconds(
                Entries.Where(e => e.PlayedAt.Date == DateTime.Today).Sum(e => e.DurationSeconds));
        }
    }

    public int DistinctTracks { get { lock (_lock) return Entries.Select(e => e.Path).Distinct().Count(); } }

    /// <summary>最常播放的曲目（按播放次数）。</summary>
    public List<(Track Track, int Count)> TopTracks(int limit = 10)
    {
        var library = LibraryService.Instance;
        var result = new List<(Track Track, int Count)>();
        lock (_lock)
        {
            var groups = Entries
                .GroupBy(e => e.Path)
                .OrderByDescending(g => g.Count())
                .Take(limit);

            foreach (var g in groups)
            {
                var track = library.Tracks.FirstOrDefault(t => t.Path == g.Key);
                if (track is not null) result.Add((track, g.Count()));
            }
        }
        return result;
    }

    /// <summary>最常听的艺术家。</summary>
    public List<(string Artist, int Count)> TopArtists(int limit = 10)
    {
        lock (_lock)
        {
            return Entries
                .GroupBy(e => string.IsNullOrWhiteSpace(e.Artist) ? "未知艺术家" : e.Artist)
                .OrderByDescending(g => g.Count())
                .Take(limit)
                .Select(g => (g.Key, g.Count()))
                .ToList();
        }
    }

    /// <summary>最近 7 天每天的播放次数（今日在前）。</summary>
    public List<(string Day, int Count)> Last7Days()
    {
        lock (_lock)
        {
            var days = new List<(string, int)>();
            for (var i = 0; i < 7; i++)
            {
                var date = DateTime.Today.AddDays(-i);
                days.Add(($"{date:MM-dd}", Entries.Count(e => e.PlayedAt.Date == date)));
            }
            return days;
        }
    }
}
