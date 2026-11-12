using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PixelPlayer.WinUI.Models;
using PixelPlayer.WinUI.Services;

namespace PixelPlayer.WinUI.Pages;

/// <summary>主页：统计卡片 + 快速开始 + 每日推荐 + 最近播放 + 最多播放（对应参照项目 HomeScreen 卡片流）。</summary>
    public sealed partial class HomePage : Page
    {
        public sealed class TrackCountItem
        {
            public Track Track { get; init; } = null!;
            public int Count { get; init; }
        }

        private List<Track> _recent = new();
        private List<Track> _daily = new();
        private List<TrackCountItem> _mostPlayed = new();

    public HomePage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        LibraryService.Instance.LibraryChanged += OnLibraryChanged;
        HistoryService.Instance.HistoryChanged += OnHistoryChanged;
        await LibraryService.Instance.LoadCacheAsync();
        await HistoryService.Instance.LoadAsync();
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        LibraryService.Instance.LibraryChanged -= OnLibraryChanged;
        HistoryService.Instance.HistoryChanged -= OnHistoryChanged;
    }

    private void OnLibraryChanged(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(Refresh);

    private void OnHistoryChanged(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(Refresh);

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void Refresh()
    {
        var library = LibraryService.Instance;
        TrackCount.Text = library.Tracks.Count.ToString();
        AlbumCount.Text = library.Albums.Count.ToString();
        ArtistCount.Text = library.Artists.Count.ToString();
        TodayPlays.Text = HistoryService.Instance.TodayPlays.ToString();

        // 最近播放（来自播放历史，去重）
        _recent = HistoryService.Instance.Entries
            .Select(h => library.Tracks.FirstOrDefault(t => t.Path == h.Path))
            .Where(t => t is not null)
            .Cast<Track>()
            .Distinct()
            .Take(12)
            .ToList();

        foreach (var track in _recent.Where(t => t.CoverThumb is null))
        {
            track.CoverThumb = LibraryService.CreateThumb(track.CoverPath, 80);
        }

        RecentList.ItemsSource = _recent;
        EmptyHint.Visibility = _recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // 每日推荐（按日期播种）
        _daily = library.DailyRecommend(12);
        foreach (var track in _daily.Where(t => t.CoverThumb is null))
        {
            track.CoverThumb = LibraryService.CreateThumb(track.CoverPath, 160);
        }
        DailyList.ItemsSource = _daily;
        DailyCard.Visibility = _daily.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        // 最多播放
        _mostPlayed = library.MostPlayed(10)
            .Select(m => new TrackCountItem { Track = m.Track, Count = m.Count })
            .ToList();
        foreach (var item in _mostPlayed.Where(m => m.Track.CoverThumb is null))
        {
            item.Track.CoverThumb = LibraryService.CreateThumb(item.Track.CoverPath, 80);
        }
        MostPlayedList.ItemsSource = _mostPlayed;
        MostPlayedCard.Visibility = _mostPlayed.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void PlayAll_Click(object sender, RoutedEventArgs e)
    {
        var tracks = LibraryService.Instance.Tracks;
        if (tracks.Count == 0) return;
        PlaybackService.Instance.PlayQueue(tracks);
    }

    private void ShuffleAll_Click(object sender, RoutedEventArgs e)
    {
        var tracks = LibraryService.Instance.Tracks.ToList();
        if (tracks.Count == 0) return;
        PlaybackService.Instance.PlayQueue(tracks.OrderBy(_ => Random.Shared.Next()));
    }

    private void DailyPlay_Click(object sender, RoutedEventArgs e)
    {
        if (_daily.Count == 0) return;
        PlaybackService.Instance.PlayQueue(_daily.OrderBy(_ => Random.Shared.Next()));
    }

    private void DailyItem_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Track track) return;
        PlaybackService.Instance.PlayQueue(_daily, _daily.IndexOf(track));
    }

    private void DailyItem_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Track track) return;
        PlaybackService.Instance.PlayQueue(_daily, _daily.IndexOf(track));
    }

    private void Recent_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not Track track) return;
        if (_recent.Count > 0) PlaybackService.Instance.PlayQueue(_recent, _recent.IndexOf(track));
    }

    private void MostPlayed_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not TrackCountItem item) return;
        PlaybackService.Instance.PlayQueue(_mostPlayed.Select(m => m.Track), _mostPlayed.FindIndex(m => m.Track == item.Track));
    }
}
