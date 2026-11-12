using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PixelPlayer.WinUI.Models;
using PixelPlayer.WinUI.Services;

namespace PixelPlayer.WinUI.Pages;

/// <summary>收听统计（对应参照项目 StatsScreen）：概览 / 最近7天 / 最常播放 / 最常听艺术家。</summary>
public sealed partial class StatsPage : Page
{
    public sealed class DayBar
    {
        public string Day { get; init; } = "";
        public int Count { get; init; }
        public double BarHeight { get; init; }
    }

    public sealed class ArtistStat
    {
        public string Artist { get; init; } = "";
        public int Count { get; init; }
        public double Percent { get; init; }
    }

    public sealed class TopTrackItem
    {
        public Track Track { get; init; } = null!;
        public int Count { get; init; }
    }

    public StatsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        HistoryService.Instance.HistoryChanged += OnHistoryChanged;
        await HistoryService.Instance.LoadAsync();
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        HistoryService.Instance.HistoryChanged -= OnHistoryChanged;
    }

    private void OnHistoryChanged(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(Refresh);

    private void Refresh()
    {
        var history = HistoryService.Instance;

        TotalPlays.Text = history.TotalPlays.ToString();
        TotalHours.Text = history.TotalListened.TotalHours.ToString("F1");
        TodayCount.Text = history.TodayPlays.ToString();
        DistinctCount.Text = history.DistinctTracks.ToString();

        // 最近 7 天柱状图
        var days = history.Last7Days();
        var max = Math.Max(1, days.Max(d => d.Count));
        WeekList.ItemsSource = days.Select(d => new DayBar
        {
            Day = d.Day,
            Count = d.Count,
            BarHeight = Math.Max(4, 100.0 * d.Count / max),
        }).ToList();

        // 最常播放
        var top = history.TopTracks(10)
            .Select(t => new TopTrackItem { Track = t.Track, Count = t.Count })
            .ToList();
        foreach (var item in top.Where(t => t.Track.CoverThumb is null))
        {
            item.Track.CoverThumb = LibraryService.CreateThumb(item.Track.CoverPath, 80);
        }
        TopTracksList.ItemsSource = top;
        TopTracksEmpty.Visibility = top.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // 最常听艺术家（相对第一名的百分比）
        var artists = history.TopArtists(10);
        var maxArtist = Math.Max(1, artists.Max(a => a.Count));
        TopArtistsList.ItemsSource = artists.Select(a => new ArtistStat
        {
            Artist = a.Artist,
            Count = a.Count,
            Percent = 100.0 * a.Count / maxArtist,
        }).ToList();
        TopArtistsEmpty.Visibility = artists.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        StatsNote.Text = $"统计来自 {history.Entries.Count} 条播放记录";
    }

    private void TopTrack_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not TopTrackItem item) return;
        PlaybackService.Instance.PlayTrack(item.Track);
    }

    private async void ClearStats_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "清空播放历史？",
            Content = "收听统计与媒体库「历史」都会被清空，且无法恢复。",
            PrimaryButtonText = "清空",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        HistoryService.Instance.Clear();
    }
}
