using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using PixelPlayer.WinUI.Models;
using PixelPlayer.WinUI.Services;
using PixelPlayer.WinUI.Services.Netease;

namespace PixelPlayer.WinUI.Pages;

/// <summary>
/// 主页（微软商店式布局，对齐参照项目 HomeScreen 平板形态）：
/// 精选 Hero（左大卡 + 右侧卡组，4.5s 自动轮换）+ 横滑卡片行（最近播放/每日推荐/最多播放/我的歌单），
/// 宽屏（≥1000px）内容双列，对应参照 tablet_two_column_cards。
/// </summary>
public sealed partial class HomePage : Page
{
    /// <summary>横滑行卡片视图模型（封面异步加载后通知刷新）。</summary>
    public sealed class CardItem : INotifyPropertyChanged
    {
        public Track? Track { get; init; }
        public Playlist? Playlist { get; init; }
        public int Rank { get; init; }

        public string Title => Playlist is not null ? Playlist.Name : Track?.Title ?? "";
        public string Subtitle => Playlist is not null ? $"{Playlist.TrackPaths.Count} 首" : Track?.Artist ?? "";
        public string RankText => $"#{Rank}";
        public string ToolTipText => $"{Title} - {Subtitle}";

        private BitmapImage? _cover;
        public BitmapImage? CoverImage
        {
            get => _cover;
            set { _cover = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CoverImage))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private const int HeroMax = 12;
    private static readonly TimeSpan HeroRotateInterval = TimeSpan.FromMilliseconds(4500); // FEATURED_AUTO_SCROLL_MS

    private List<Track> _recent = new();
    private List<Track> _daily = new();
    private List<(Track Track, int Count)> _mostPlayed = new();
    private List<Track> _hero = new();
    private int _heroOffset;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _heroTimer;

    private bool _narrowLayout = true;

    public HomePage()
    {
        App.LogCrash("trace", new Exception("HP ctor start"));
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
        App.LogCrash("trace", new Exception("HP ctor end"));
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            LibraryService.Instance.LibraryChanged += OnLibraryChanged;
            HistoryService.Instance.HistoryChanged += OnHistoryChanged;
            NeteasePlaylistService.Instance.SyncCompleted += OnSyncCompleted;
            await LibraryService.Instance.LoadCacheAsync();
            await HistoryService.Instance.LoadAsync();
            StartHeroTimer();
            Refresh();
            _ = LoadCoversAsync();
            _ = LoadCloudPlaylistsAsync();
        }
        catch (Exception ex)
        {
            App.LogCrash("HomePage.OnLoaded", ex);
            throw; // 交由上层记录 stowed exception（但日志已落盘）
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        LibraryService.Instance.LibraryChanged -= OnLibraryChanged;
        HistoryService.Instance.HistoryChanged -= OnHistoryChanged;
        NeteasePlaylistService.Instance.SyncCompleted -= OnSyncCompleted;
        _heroTimer?.Stop();
    }

    private void OnLibraryChanged(object? s, EventArgs e) => DispatcherQueue.TryEnqueue(() => { Refresh(); _ = LoadCoversAsync(); });
    private void OnHistoryChanged(object? s, EventArgs e) => DispatcherQueue.TryEnqueue(() => { Refresh(); _ = LoadCoversAsync(); });
    private void OnSyncCompleted(object? s, EventArgs e) => DispatcherQueue.TryEnqueue(() => { Refresh(); _ = LoadCoversAsync(); });

    private void StartHeroTimer()
    {
        _heroTimer?.Stop();
        _heroTimer = DispatcherQueue.CreateTimer();
        _heroTimer.Interval = HeroRotateInterval;
        _heroTimer.Tick += (_, _) => RotateHero();
        _heroTimer.Start();
    }

    // ─── 布局（宽屏双列，对齐参照 ≥600dp 判断，这里换算为像素阈值）────────

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // SizeChanged 处于布局 pass 中，重挂可视化树会触发 re-entrant 布局
        // fail-fast（0xc000027b），必须推迟到 DispatcherQueue 执行。
        var width = e.NewSize.Width;
        DispatcherQueue.TryEnqueue(() => ApplyLayout(width));
    }

    private void ApplyLayout(double width)
    {
        var wide = width >= 1000; // 参照 isWideTabletLayout：>=600dp ≈ 1000px
        // _narrowLayout=true 表示当前窄布局；目标态与当前态一致则无需切换
        if (wide == !_narrowLayout || RecentSection.Parent is null) return;
        _narrowLayout = !wide;

        if (wide)
        {
            NarrowStack.Visibility = Visibility.Collapsed;
            WideGrid.Visibility = Visibility.Visible;
            Detach(RecentSection); Detach(DailySection); Detach(MostSection); Detach(PlaylistsSection);
            WideColA.Children.Add(RecentSection);
            WideColA.Children.Add(MostSection);
            WideColB.Children.Add(DailySection);
            WideColB.Children.Add(PlaylistsSection);
        }
        else
        {
            WideGrid.Visibility = Visibility.Collapsed;
            NarrowStack.Visibility = Visibility.Visible;
            Detach(RecentSection); Detach(DailySection); Detach(MostSection); Detach(PlaylistsSection);
            NarrowStack.Children.Insert(0, RecentSection);
            NarrowStack.Children.Insert(1, DailySection);
            NarrowStack.Children.Insert(2, MostSection);
            NarrowStack.Children.Insert(3, PlaylistsSection);
        }
    }

    private static void Detach(FrameworkElement el)
    {
        if (el.Parent is Panel p) p.Children.Remove(el);
    }

    // ─── 数据刷新 ───────────────────────────────────────────────────

    private void Refresh_Click(object sender, RoutedEventArgs e) { Refresh(); _ = LoadCoversAsync(); }

    private void Refresh()
    {
        var library = LibraryService.Instance;

        // 最近播放（去重，最多 20 张卡）
        _recent = HistoryService.Instance.Entries
            .Select(h => library.Tracks.FirstOrDefault(t => t.Path == h.Path))
            .Where(t => t is not null)
            .Cast<Track>()
            .Distinct()
            .Take(20)
            .ToList();

        // 每日推荐（按日期播种）
        _daily = library.DailyRecommend(20);

        // 最多播放
        _mostPlayed = library.MostPlayed(10).ToList();

        // Hero 精选池：每日推荐优先，随后补最近播放/最常播放（对齐 FEATURED_CAROUSEL_MAX=12）
        _hero = _daily.Take(HeroMax).ToList();
        foreach (var t in _mostPlayed.Select(m => m.Track))
        {
            if (_hero.Count >= HeroMax) break;
            if (!_hero.Contains(t)) _hero.Add(t);
        }
        foreach (var t in _recent)
        {
            if (_hero.Count >= HeroMax) break;
            if (!_hero.Contains(t)) _hero.Add(t);
        }
        RenderHero();

        // 横滑行
        RecentCards.ItemsSource = _recent.Select(t => new CardItem { Track = t }).ToList();
        DailyCards.ItemsSource = _daily.Select(t => new CardItem { Track = t }).ToList();
        MostCards.ItemsSource = _mostPlayed.Select((m, i) => new CardItem { Track = m.Track, Rank = i + 1 }).ToList();

        DailySection.Visibility = _daily.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        MostSection.Visibility = _mostPlayed.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        EmptyHint.Visibility = _recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // 我的歌单（云端 + 本地）
        var cloud = NeteasePlaylistService.Instance.Playlists
            .Select(p => new Playlist
            {
                Name = p.Name,
                CloudId = p.Id.ToString(),
                CoverUrl = p.CoverUrl,
                TrackPaths = p.Songs.Select(s => s.Path).ToList(),
            })
            .Cast<Playlist>()
            .Concat(SettingsService.Instance.Playlists);
        var plList = cloud.ToList();
        PlaylistCards.ItemsSource = plList.Select(p => new CardItem
        {
            Playlist = p,
        }).ToList();
        PlaylistsSection.Visibility = plList.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        ApplyLayout(ActualWidth);
    }

    private async Task LoadCoversAsync()
    {
        // 在线封面异步补图（对应 Coil 异步加载 + 渐显）
        var items = new List<CardItem>();
        CollectCards(RecentCards, items);
        CollectCards(DailyCards, items);
        CollectCards(MostCards, items);
        CollectCards(PlaylistCards, items);

        foreach (var item in items)
        {
            if (item.CoverImage is not null) continue;
            if (item.Track is not null)
            {
                var img = await CoverCacheService.Instance.GetAsync(item.Track);
                if (img is not null) item.CoverImage = img;
            }
            else if (item.Playlist is not null)
            {
                if (item.Playlist.CloudId is not null && long.TryParse(item.Playlist.CloudId, out var id))
                {
                    var pl = NeteasePlaylistService.Instance.Find(id);
                    var first = pl?.Songs.FirstOrDefault(s => !string.IsNullOrEmpty(s.PicUrl));
                    if (first?.PicUrl is not null)
                    {
                        var img = await CoverCacheService.Instance.GetAsync(first);
                        if (img is not null) item.CoverImage = img;
                    }
                }
                else
                {
                    var first = item.Playlist.TrackPaths
                        .Select(p => LibraryService.Instance.Tracks.FirstOrDefault(t => t.Path == p))
                        .FirstOrDefault(t => t?.CoverThumb is not null);
                    item.CoverImage = first?.CoverThumb;
                }
            }
        }
    }

    private static void CollectCards(ItemsControl control, List<CardItem> into)
    {
        if (control.ItemsSource is IEnumerable<CardItem> list) into.AddRange(list);
    }

    private async Task LoadCloudPlaylistsAsync()
    {
        var account = NeteaseAccountService.Instance;
        if (account.IsLoggedIn && NeteasePlaylistService.Instance.Playlists.Count == 0)
        {
            try { await NeteasePlaylistService.Instance.SyncAllAsync(); }
            catch { /* 静默失败，设置页可手动同步 */ }
        }
    }

    // ─── Hero ───────────────────────────────────────────────────────

    private void RenderHero()
    {
        if (_hero.Count == 0)
        {
            HeroGrid.Visibility = Visibility.Collapsed;
            return;
        }
        HeroGrid.Visibility = Visibility.Visible;
        SetHeroSlot(_hero[_heroOffset % _hero.Count], true);
        SetHeroSide(1, _hero.Count > 1 ? _hero[(_heroOffset + 1) % _hero.Count] : null);
        SetHeroSide(2, _hero.Count > 2 ? _hero[(_heroOffset + 2) % _hero.Count] : null);
        SetHeroSide(3, _hero.Count > 3 ? _hero[(_heroOffset + 3) % _hero.Count] : null);
    }

    private void RotateHero()
    {
        if (_hero.Count <= 1) return;
        _heroOffset = (_heroOffset + 1) % _hero.Count;
        RenderHero();
        _ = LoadHeroCoversAsync();
    }

    private void SetHeroSlot(Track t, bool main)
    {
        HeroMainTitle.Text = t.Title;
        HeroMainArtist.Text = string.IsNullOrWhiteSpace(t.Artist) ? t.Album : t.Artist;
        _mainTrack = t;
        _ = SetCoverAsync(HeroMainCover, t);
    }

    private void SetHeroSide(int slot, Track? t)
    {
        TextBlock? title = slot switch { 1 => HeroSide1Title, 2 => HeroSide2Title, 3 => HeroSide3Title, _ => null };
        TextBlock? artist = slot switch { 1 => HeroSide1Artist, 2 => HeroSide2Artist, 3 => HeroSide3Artist, _ => null };
        Image? cover = slot switch { 1 => HeroSide1Cover, 2 => HeroSide2Cover, 3 => HeroSide3Cover, _ => null };
        Border? host = slot switch { 1 => HeroSide1, 2 => HeroSide2, 3 => HeroSide3, _ => null };
        if (title is null || artist is null || cover is null || host is null) return;
        if (t is null)
        {
            host.Visibility = Visibility.Collapsed;
            return;
        }
        host.Visibility = Visibility.Visible;
        title.Text = t.Title;
        artist.Text = t.Artist;
        switch (slot) { case 1: _side1Track = t; break; case 2: _side2Track = t; break; case 3: _side3Track = t; break; }
        _ = SetCoverAsync(cover, t);
    }

    private Track? _mainTrack;
    private Track? _side1Track;
    private Track? _side2Track;
    private Track? _side3Track;

    private static readonly Dictionary<Image, Track> _bindMap = new();

    /// <summary>异步设置封面；只有绑定的曲目未变时才落图，避免慢响应覆盖新内容。</summary>
    private async Task SetCoverAsync(Image target, Track t)
    {
        _bindMap[target] = t;
        var img = await CoverCacheService.Instance.GetAsync(t);
        if (img is not null && _bindMap.TryGetValue(target, out var cur) && ReferenceEquals(cur, t))
        {
            target.Source = img;
        }
    }

    private async Task LoadHeroCoversAsync()
    {
        foreach (var t in _hero.Take(4))
        {
            await CoverCacheService.Instance.GetAsync(t);
        }
    }

    // ─── 点击行为 ───────────────────────────────────────────────────

    private void PlayTracks(List<Track> tracks, int index)
    {
        if (tracks.Count == 0) return;
        if (index < 0) index = 0;
        PlaybackService.Instance.PlayQueue(tracks, index);
    }

    private void HeroPlay_Click(object sender, RoutedEventArgs e)
    {
        if (_mainTrack is null) return;
        PlayTracks(_hero, _hero.FindIndex(t => t == _mainTrack));
    }

    private void HeroQueue_Click(object sender, RoutedEventArgs e)
    {
        if (_mainTrack is null) return;
        PlaybackService.Instance.PlayQueue(new[] { _mainTrack }, 0);
    }

    private void Hero_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_mainTrack is null) return;
        PlayTracks(_hero, _hero.FindIndex(t => t == _mainTrack));
    }

    private void SideCard_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var tag = (sender as FrameworkElement)?.Tag as string;
        var t = tag switch { "1" => _side1Track, "2" => _side2Track, "3" => _side3Track, _ => null };
        if (t is null) return;
        PlayTracks(_hero, _hero.FindIndex(x => x == t));
    }

    private void Card_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CardItem item || item.Track is null) return;
        var list = ItemsOf(item.Track);
        PlayTracks(list, list.FindIndex(t => t == item.Track));
    }

    private void Card_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CardItem item || item.Track is null) return;
        var list = ItemsOf(item.Track);
        PlayTracks(list, list.FindIndex(t => t == item.Track));
    }

    private List<Track> ItemsOf(Track track)
    {
        if (_recent.Contains(track)) return _recent;
        if (_daily.Contains(track)) return _daily;
        var most = _mostPlayed.Select(m => m.Track).ToList();
        if (most.Contains(track)) return most;
        return new List<Track> { track };
    }

    private async void PlaylistCard_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CardItem item || item.Playlist is null) return;
        await PlayPlaylistAsync(item);
    }

    private async void PlaylistCard_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CardItem item || item.Playlist is null) return;
        await PlayPlaylistAsync(item);
    }

    private async Task PlayPlaylistAsync(CardItem item)
    {
        var pl = item.Playlist!;
        List<Track> tracks;
        if (pl.CloudId is not null && long.TryParse(pl.CloudId, out var id))
        {
            tracks = NeteasePlaylistService.Instance.Find(id)?.Songs ?? new List<Track>();
        }
        else
        {
            tracks = pl.TrackPaths
                .Select(p => LibraryService.Instance.Tracks.FirstOrDefault(t => t.Path == p))
                .Where(t => t is not null)
                .Cast<Track>()
                .ToList();
        }
        if (tracks.Count == 0)
        {
            if (pl.IsCloud)
            {
                try
                {
                    await NeteasePlaylistService.Instance.SyncAllAsync();
                    tracks = NeteasePlaylistService.Instance.Find(id2(pl))?.Songs ?? new List<Track>();
                }
                catch { }
            }
        }
        PlayTracks(tracks, 0);

        static long id2(Playlist p) => long.TryParse(p.CloudId, out var i) ? i : 0;
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
}
