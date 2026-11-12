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
using PixelPlayer.WinUI.Services.Lx;
using PixelPlayer.WinUI.Services.Online;

namespace PixelPlayer.WinUI.Pages;

/// <summary>搜索页：本地曲库搜索 + 在线音源搜索，分类展示结果。</summary>
public sealed partial class SearchPage : Page
{
    private List<Track> _songs = new();
    private List<AlbumGroup> _albums = new();
    private List<ArtistGroup> _artists = new();
    private List<TrackRowVm> _songRows = new();

    /// <summary>歌曲行 VM：Track 的 CoverThumb 是普通属性（无 INPC），在线封面异步下载完成后
    /// 通过 VM 的 PropertyChanged 逐行刷新封面，避免整表刷新导致滚动位置丢失。</summary>
    public sealed class TrackRowVm : System.ComponentModel.INotifyPropertyChanged
    {
        public Track Track { get; }
        public TrackRowVm(Track track) => Track = track;
        public string Title => Track.Title;
        public string AlbumArtistText => Track.AlbumArtistText;
        public string DurationText => Track.DurationText;
        public Microsoft.UI.Xaml.Media.ImageSource? CoverThumb => Track.CoverThumb;

        public void RaiseCover() =>
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(CoverThumb)));

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

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

        // 音源列表：本地曲库 + LX 音源脚本提供的平台（音源市场下载的脚本）+ 自建媒体服务器。
        // 应用不内置任何平台 API；脚本可能还在后台预热，就绪后刷新一次下拉框。
        RefreshSourceItems();
        if (!LxJsEngine.Instance.IsReady)
        {
            _ = Task.Run(async () =>
            {
                var ok = await LxJsEngine.Instance.AwaitReadyAsync(60_000);
                if (ok) DispatcherQueue.TryEnqueue(RefreshSourceItems);
            });
        }

        await LibraryService.Instance.LoadCacheAsync();
        RefreshAll();
        SearchBox.Focus(FocusState.Programmatic);
    }

    private void RefreshSourceItems()
    {
        var items = new List<SourceItem> { new("", "本地曲库") };
        if (LxJsEngine.Instance.IsReady)
        {
            foreach (var key in LxJsEngine.Instance.GetSources().Keys.OrderBy(k => k))
            {
                if (LxPlatformMap.IdOf(key) is { } id)
                    items.Add(new SourceItem(id, LxPlatformMap.DisplayName(key)));
            }
        }
        items.AddRange(OnlineSourceRegistry.Sources
            .Where(s => s.IsConfigured)
            .Select(s => new SourceItem(s.Id, s.Name)));

        var saved = SettingsService.Instance.Settings.OnlineSourceId;
        SourceBox.ItemsSource = items;
        SourceBox.SelectedValue = items.Any(i => i.Id == saved) ? saved : "";
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
            List<Track> songs;
            string sourceName;
            var selected = SourceBox.SelectedValue as string ?? "";
            var lxKey = LxPlatformMap.KeyOf(selected);

            if (lxKey is not null)
            {
                // LX 音源脚本平台（音源市场下载的脚本提供搜索/播放/歌词）
                var ready = await LxJsEngine.Instance.AwaitReadyAsync(30_000);
                if (!ready)
                {
                    SummaryText.Text = "音源脚本未加载：请到 设置 → 在线音源 安装音源脚本";
                    return;
                }
                if (ct.IsCancellationRequested) return;
                var result = await LxJsEngine.Instance.SearchAsync(query, lxKey, 1, 30);
                if (ct.IsCancellationRequested) return;
                songs = result.List.Select(LxPlatformMap.ToTrack).ToList();
                sourceName = LxPlatformMap.DisplayName(lxKey);
            }
            else
            {
                var source = OnlineSourceRegistry.Resolve(selected);
                if (source is null)
                {
                    SummaryText.Text = "该音源不可用";
                    return;
                }
                songs = await source.SearchAsync(query, 30, ct);
                sourceName = source.Name;
            }

            _songs = songs;
            _albums.Clear();
            _artists.Clear();
            SummaryText.Text = songs.Count > 0
                ? $"找到 {songs.Count} 首歌曲（{sourceName}）"
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

        _songRows = _songs.Select(t => new TrackRowVm(t)).ToList();
        TrackList.ItemsSource = _songRows;
        SongsSection.Visibility = _songs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        AlbumsList.ItemsSource = _albums;
        AlbumsSection.Visibility = !online && _albums.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        ArtistsList.ItemsSource = _artists;
        ArtistsSection.Visibility = !online && _artists.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        EmptyHint.Visibility = _songs.Count == 0 && _albums.Count == 0 && _artists.Count == 0 && queryNotEmpty
            ? Visibility.Visible
            : Visibility.Collapsed;

        // 在线搜索结果补充加载封面：PicUrl → 下载 → 磁盘缓存 → 逐行刷新
        if (online && _songs.Any(t => t.CoverThumb is null && !string.IsNullOrEmpty(t.PicUrl)))
        {
            _ = LoadOnlineCoversAsync(_onlineCts?.Token ?? CancellationToken.None);
        }
    }

    /// <summary>并发拉取在线封面（限 4 路），完成后通过行 VM 的 INPC 刷新对应行。</summary>
    private async Task LoadOnlineCoversAsync(CancellationToken ct)
    {
        var rows = _songRows.Where(r => r.Track.CoverThumb is null && !string.IsNullOrEmpty(r.Track.PicUrl)).ToList();
        if (rows.Count == 0) return;

        using var gate = new SemaphoreSlim(4);
        var tasks = rows.Select(async row =>
        {
            try
            {
                await gate.WaitAsync(ct);
                var img = await CoverCacheService.Instance.GetAsync(row.Track, ct);
                if (img is not null && !ct.IsCancellationRequested)
                {
                    DispatcherQueue.TryEnqueue(row.RaiseCover);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                App.LogCrash("SearchCover", ex);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();
        await Task.WhenAll(tasks);
    }

    private bool queryNotEmpty => (SearchBox.Text ?? "").Trim().Length > 0;

    private void Track_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not TrackRowVm row) return;
        PlaybackService.Instance.PlayQueue(_songs, _songs.IndexOf(row.Track));
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
