using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PixelPlayer.WinUI.Models;
using PixelPlayer.WinUI.Services;
using PixelPlayer.WinUI.Services.Online;

namespace PixelPlayer.WinUI.Pages;

/// <summary>搜索页：本地曲库搜索 + 在线音源（网易云）搜索，分类展示结果。</summary>
public sealed partial class SearchPage : Page
{
    private List<Track> _songs = new();
    private List<AlbumGroup> _albums = new();
    private List<ArtistGroup> _artists = new();

    private DispatcherQueueTimer? _onlineDebounce;
    private CancellationTokenSource? _onlineCts;

    private sealed record SourceItem(string Id, string Name);

    private bool IsOnlineSource => (SourceBox.SelectedValue as string)?.Length > 0;

    public SearchPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        LibraryService.Instance.LibraryChanged += OnLibraryChanged;

        // 音源列表：本地曲库 + 注册中心所有源
        if (SourceBox.ItemsSource is null)
        {
            var items = new List<SourceItem> { new("", "本地曲库") };
            items.AddRange(OnlineSourceRegistry.Sources.Select(s => new SourceItem(s.Id, s.Name)));
            SourceBox.ItemsSource = items;
            var saved = SettingsService.Instance.Settings.OnlineSourceId;
            SourceBox.SelectedValue = items.Any(i => i.Id == saved) ? saved : "";
        }

        await LibraryService.Instance.LoadCacheAsync();
        RefreshAll();
        SearchBox.Focus(FocusState.Programmatic);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        LibraryService.Instance.LibraryChanged -= OnLibraryChanged;
    }

    private void OnLibraryChanged(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(RefreshAll);

    private void Search_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;

        if (IsOnlineSource)
        {
            // 在线搜索加 400ms 防抖，避免每个字符都发请求
            _onlineDebounce ??= DispatcherQueue.CreateTimer();
            _onlineDebounce.Stop();
            _onlineDebounce.Interval = TimeSpan.FromMilliseconds(400);
            _onlineDebounce.IsRepeating = false;
            _onlineDebounce.Tick += (_, _) => _ = RunOnlineSearchAsync();
            _onlineDebounce.Start();
        }
        else
        {
            RefreshAll();
        }
    }

    private void Source_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SearchBox is null || SummaryText is null) return; // XAML 初始化阶段控件未全部就绪

        // 持久化音源选择
        if (SourceBox.SelectedValue is string id && SourceBox.ItemsSource is List<SourceItem> items
            && items.Any(i => i.Id == id))
        {
            SettingsService.Instance.Settings.OnlineSourceId = id;
            SettingsService.Instance.SaveSettings();
        }

        if (IsOnlineSource)
        {
            _ = RunOnlineSearchAsync();
        }
        else
        {
            RefreshAll();
        }
    }

    private void RefreshAll()
    {
        if (SearchBox is null || SummaryText is null) return; // XAML 初始化阶段控件未全部就绪
        var library = LibraryService.Instance;
        var query = (SearchBox.Text ?? "").Trim();

        if (query.Length == 0)
        {
            _songs.Clear();
            _albums.Clear();
            _artists.Clear();
            SummaryText.Text = $"曲库共 {library.Tracks.Count} 首";
            ApplyResults();
            return;
        }

        _songs = library.Search(query, 100).ToList();
        _albums = library.Albums
            .Where(a => a.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                     || a.Artist.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(12)
            .ToList();
        _artists = library.Artists
            .Where(a => a.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(12)
            .ToList();

        SummaryText.Text = $"找到 {_songs.Count} 首歌曲 · {_albums.Count} 张专辑 · {_artists.Count} 位艺术家";
        ApplyResults();
    }

    private async Task RunOnlineSearchAsync()
    {
        var query = (SearchBox.Text ?? "").Trim();
        _onlineCts?.Cancel();
        _onlineCts = new CancellationTokenSource();
        var ct = _onlineCts.Token;

        if (query.Length == 0)
        {
            _songs.Clear();
            _albums.Clear();
            _artists.Clear();
            SummaryText.Text = "";
            ApplyResults();
            return;
        }

        try
        {
            var source = OnlineSourceRegistry.Resolve(SourceBox.SelectedValue as string)
                         ?? (OnlineSourceRegistry.Active);
            var songs = await source.SearchAsync(query, 30, ct);
            if (ct.IsCancellationRequested) return;

            _songs = songs;
            _albums.Clear();
            _artists.Clear();
            SummaryText.Text = songs.Count > 0
                ? $"找到 {songs.Count} 首歌曲（{source.Name}）"
                : "未找到相关结果";
            ApplyResults();
        }
        catch (OperationCanceledException)
        {
            // 被新的搜索取代
        }
        catch (Exception ex)
        {
            App.LogCrash("OnlineSearch", ex);
            if (!ct.IsCancellationRequested)
            {
                SummaryText.Text = "在线搜索失败，请检查网络";
            }
        }
    }

    private void ApplyResults()
    {
        var online = IsOnlineSource;

        foreach (var track in _songs.Where(t => t.CoverThumb is null && t.CoverPath is not null))
        {
            track.CoverThumb = LibraryService.CreateThumb(track.CoverPath, 80);
        }

        TrackList.ItemsSource = _songs;
        SongsSection.Visibility = _songs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        AlbumsList.ItemsSource = _albums;
        AlbumsSection.Visibility = !online && _albums.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        ArtistsList.ItemsSource = _artists;
        ArtistsSection.Visibility = !online && _artists.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        EmptyHint.Visibility = _songs.Count == 0 && _albums.Count == 0 && _artists.Count == 0 && queryNotEmpty
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private bool queryNotEmpty => (SearchBox.Text ?? "").Trim().Length > 0;

    private void Track_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not Track track) return;
        PlaybackService.Instance.PlayQueue(_songs, _songs.IndexOf(track));
    }

    private void AlbumResult_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AlbumGroup album) return;
        PlaybackService.Instance.PlayQueue(album.Tracks);
    }

    private void ArtistResult_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ArtistGroup artist) return;
        PlaybackService.Instance.PlayQueue(artist.Tracks);
    }
}
