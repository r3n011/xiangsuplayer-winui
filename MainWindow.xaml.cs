using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PixelPlayer.WinUI.Models;
using PixelPlayer.WinUI.Pages;
using PixelPlayer.WinUI.Services;
using Windows.Graphics;

namespace PixelPlayer.WinUI;

public sealed partial class MainWindow : Window
{
    private bool _suppressSeek;
    private bool _suppressVolume;

    private IReadOnlyList<LyricLine> _lyrics = Array.Empty<LyricLine>();
    private int _lyricIndex = -1;
    private bool _lyricsVisible = true;

    public MainWindow()
    {
        InitializeComponent();

        LibraryService.Instance.SetDispatcher(DispatcherQueue);

        Title = "像素播放器";
        ExtendsContentIntoTitleBar = true;
        AppWindow.Resize(new SizeInt32(1280, 860));
        SystemBackdrop = new MicaBackdrop();

        // 应用图标复用参照项目（Android 像素播放器）的启动图标
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "app.ico");
            if (File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
        }
        catch { /* 图标失败不影响运行 */ }

        ApplyTheme(SettingsService.Instance.Settings.Theme);
        Nav.SelectedItem = Nav.MenuItems[0];

        // 自适应导航（对齐参照项目 isTabletShell = isLandscape || forceTabletLayout）：
        // 宽 ≥1260px（840dp）→ 常开侧栏；≥900px（600dp）→ 紧凑图标栏；窄 → 汉堡折叠
        Nav.SizeChanged += Nav_SizeChanged;
        ApplyNavLayout(Nav.ActualWidth);

        var player = PlaybackService.Instance;
        player.TrackChanged += (_, _) => { UpdatePlayerBar(); SyncFloatingLyrics(); };
        player.StateChanged += (_, _) => UpdateTransportState();
        player.PositionChanged += (_, position) => UpdateProgress(position);
        player.SleepTimerFired += (_, _) => UpdateSleepTimerUi();

        if (SettingsService.Instance.Settings.FloatingLyricsEnabled)
        {
            FloatingLyricsWindow.Show();
        }

        // 本地流代理（B 站等需要 Referer 的 CDN 转发）
        StreamProxyService.Instance.Start(SettingsService.Instance.Settings.StreamProxyPort);

        // 事件必须在 InitializeComponent 之后挂：否则 XAML 里赋 Value 时会提前触发，
        // 此时 PlaybackService/SettingsService 尚未就绪，会抛 XamlParseException 直接闪退。
        BarSeek.ValueChanged += Seek_ValueChanged;
        SheetSeek.ValueChanged += Seek_ValueChanged;
        VolumeSlider.ValueChanged += Volume_Changed;

        _suppressVolume = true;
        VolumeSlider.Value = player.Volume * 100;
        _suppressVolume = false;

        UpdatePlayerBar();
        UpdateTransportState();
        UpdateSleepTimerUi();

        // 启动：后台载入缓存与历史；若配置了音乐文件夹且曲库为空，则自动扫描一次。
        _ = AutoScanAsync();
        _ = HistoryService.Instance.LoadAsync();

        // 网页遥控：按设置自动启停
        ApplyWebRemoteSetting();
    }

    private static async Task AutoScanAsync()
    {
        try
        {
            await LibraryService.Instance.LoadCacheAsync();

            var folders = SettingsService.Instance.Settings.MusicFolders;
            if (folders.Count > 0 && LibraryService.Instance.Tracks.Count == 0)
            {
                await LibraryService.Instance.ScanAsync(folders);
            }
        }
        catch (Exception ex)
        {
            App.LogCrash("AutoScan", ex);
        }
    }

    private void ApplyWebRemoteSetting()
    {
        var remote = WebRemoteService.Instance;
        if (SettingsService.Instance.Settings.WebRemoteEnabled && !remote.IsRunning) remote.Start();
        if (!SettingsService.Instance.Settings.WebRemoteEnabled && remote.IsRunning) remote.Stop();
    }

    /// <summary>宽度驱动的导航形态切换（对应参照 isTabletShell：紧凑=底部栏 / 展开=Rail）。
    /// 注意：SizeChanged 处于布局 pass 中，直接改 PaneDisplayMode 会造成 re-entrant 布局
    /// 崩溃（0xc000027b stowed exception），必须推迟到 DispatcherQueue 执行。</summary>
    private void Nav_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = e.NewSize.Width;
        DispatcherQueue.TryEnqueue(() => ApplyNavLayout(width));
    }

    private void ApplyNavLayout(double width)
    {
        // ≥1260px（≈840dp）→ 常开侧栏；≥900px（≈600dp）→ 紧凑图标栏；窄 → 汉堡折叠
        Nav.PaneDisplayMode = width >= 1260 ? NavigationViewPaneDisplayMode.Left
            : width >= 900 ? NavigationViewPaneDisplayMode.LeftCompact
            : NavigationViewPaneDisplayMode.LeftMinimal;
    }

    /// <summary>供设置页等跨页跳转：按 Tag 选中导航项。</summary>
    public void NavigateToTag(string tag)
    {
        var items = Nav.MenuItems.Concat(Nav.FooterMenuItems)
            .OfType<NavigationViewItem>()
            .FirstOrDefault(i => (i.Tag as string) == tag);
        if (items is not null) Nav.SelectedItem = items;
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item || item.Tag is not string tag) return;

        var pageType = tag switch
        {
            "home" => typeof(HomePage),
            "search" => typeof(SearchPage),
            "library" => typeof(LibraryPage),
            "stats" => typeof(StatsPage),
            "equalizer" => typeof(EqualizerPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(HomePage),
        };
        ContentFrame.Navigate(pageType, null, args.RecommendedNavigationTransitionInfo);
    }

    /* ---------------- 主题 ---------------- */

    public void ApplyTheme(string theme)
    {
        RootGrid.RequestedTheme = theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

        SettingsService.Instance.Settings.Theme = theme;
        SettingsService.Instance.SaveSettings();
    }

    /* ---------------- 播放抽屉 ---------------- */

    private void PlayerBar_PointerPressed(object sender, PointerRoutedEventArgs e) => ShowSheet(true);

    private void ShowSheet(bool show)
    {
        PlayerSheet.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
        {
            UpdatePlayerBar();
            UpdateTransportState();
            UpdateProgress(PlaybackService.Instance.Position);
        }
    }

    private void SheetClose_Click(object sender, RoutedEventArgs e) => ShowSheet(false);

    private void LyricsToggle_Click(object sender, RoutedEventArgs e)
    {
        _lyricsVisible = !_lyricsVisible;
        LyricsCard.Visibility = _lyricsVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    private int _fetchGeneration;

    private async void LoadLyrics()
    {
        var track = PlaybackService.Instance.Current;
        if (track is null)
        {
            _lyrics = Array.Empty<LyricLine>();
            LyricsList.ItemsSource = new List<string> { "" };
            LyricsHint.Text = "";
            UpdateFloatingLyrics("");
            return;
        }

        var gen = ++_fetchGeneration;

        // 1. 本地 .lrc
        _lyrics = LyricsService.LoadFor(track.Path);
        _lyricIndex = -1;

        if (_lyrics.Count == 0)
        {
            LyricsList.ItemsSource = new List<string> { "" };
            LyricsHint.Text = "歌词获取中…";

            // 2. 在线获取链（源自带 → LRCLIB），对应参照项目 LyricsRepositoryImpl
            try
            {
                var fetched = await LyricsFetchService.FetchAsync(track);
                if (gen != _fetchGeneration) return; // 已切歌

                if (fetched is { Lrc.Length: > 0 })
                {
                    var lines = LyricsService.Parse(fetched.Lrc.Split('\n'));
                    if (fetched.Translation is { Length: > 0 })
                    {
                        lines = MergeTranslation(lines, LyricsService.Parse(fetched.Translation.Split('\n')));
                    }

                    _lyrics = lines;
                    LyricsHint.Text = $"{lines.Count} 行 · {fetched.Origin}";
                }
                else
                {
                    LyricsHint.Text = "";
                }
            }
            catch
            {
                if (gen == _fetchGeneration) LyricsHint.Text = "";
            }

            if (gen != _fetchGeneration) return;
            if (_lyrics.Count == 0)
            {
                LyricsList.ItemsSource = new List<string> { "暂无歌词" };
            }
            else
            {
                LyricsList.ItemsSource = _lyrics.Select(l => l.Text).ToList();
            }
        }
        else
        {
            LyricsList.ItemsSource = _lyrics.Select(l => l.Text).ToList();
            LyricsHint.Text = $"{_lyrics.Count} 行 · 同步歌词";
        }
    }

    /// <summary>翻译行合并到原文行（±500ms 最近邻，对应参照项目 pairTranslationLines）。</summary>
    private static IReadOnlyList<LyricLine> MergeTranslation(IReadOnlyList<LyricLine> original, IReadOnlyList<LyricLine> translation)
    {
        if (original.Count == 0 || translation.Count == 0) return original;
        var result = new List<LyricLine>(original.Count);
        foreach (var line in original)
        {
            LyricLine? best = null;
            var bestDelta = TimeSpan.FromMilliseconds(500);
            foreach (var t in translation)
            {
                var delta = t.Time > line.Time ? t.Time - line.Time : line.Time - t.Time;
                if (delta < bestDelta)
                {
                    bestDelta = delta;
                    best = t;
                }
            }
            result.Add(best is null ? line : line with { Text = $"{line.Text} / {best.Text}" });
        }
        return result;
    }

    private void HighlightLyric(TimeSpan position)
    {
        if (!_lyricsVisible || _lyrics.Count == 0 || PlayerSheet.Visibility != Visibility.Visible) return;

        var index = -1;
        for (var i = 0; i < _lyrics.Count; i++)
        {
            if (_lyrics[i].Time <= position + TimeSpan.FromMilliseconds(120)) index = i;
            else break;
        }

        if (index == _lyricIndex || index < 0)
        {
            return;
        }
        _lyricIndex = index;
        LyricsList.SelectedIndex = index;
        LyricsList.ScrollIntoView(LyricsList.Items[index]);
        UpdateFloatingLyrics(_lyrics[index].Text);
    }

    private void UpdateFloatingLyrics(string line)
    {
        if (SettingsService.Instance.Settings.FloatingLyricsEnabled)
        {
            FloatingLyricsWindow.UpdateText(line);
        }
    }

    /// <summary>按设置开关显示/隐藏悬浮歌词（设置页调用）。</summary>
    public static void SyncFloatingLyrics()
    {
        if (SettingsService.Instance.Settings.FloatingLyricsEnabled)
        {
            FloatingLyricsWindow.Show();
        }
        else if (FloatingLyricsWindow.IsVisible)
        {
            FloatingLyricsWindow.Hide();
        }
    }

    private void Lyrics_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (_lyrics.Count == 0) return;
        var index = LyricsList.Items.IndexOf(e.ClickedItem);
        if (index >= 0 && index < _lyrics.Count)
        {
            PlaybackService.Instance.Seek(_lyrics[index].Time);
        }
    }

    /* ---------------- 睡眠定时器（标题栏提示） ---------------- */

    private void UpdateSleepTimerUi()
    {
        Title = PlaybackService.Instance.SleepTimerEnd is { } end
            ? $"像素播放器 · 睡眠定时 {end:HH:mm}"
            : "像素播放器";
    }

    /* ---------------- 播放条 + 抽屉 同步更新 ---------------- */

    private void UpdatePlayerBar()
    {
        var track = PlaybackService.Instance.Current;
        if (track is null) return;

        BarTitle.Text = track.Title;
        BarArtist.Text = track.Artist;
        BarCover.Source = LibraryService.CreateThumb(track.CoverPath, 120);

        SheetTitle.Text = track.Title;
        SheetArtist.Text = track.Artist;
        SheetAlbum.Text = track.Album;
        SheetCover.Source = LibraryService.CreateThumb(track.CoverPath, 400);

        var max = Math.Max(1, track.DurationSeconds);
        BarSeek.Maximum = max;
        SheetSeek.Maximum = max;

        LoadLyrics();
        UpdateProgress(PlaybackService.Instance.Position);
    }

    private void UpdateTransportState()
    {
        var player = PlaybackService.Instance;
        var playing = player.IsPlaying;
        BarPlayIcon.Glyph = playing ? "\uE769" : "\uE768";
        SheetPlayIcon.Glyph = playing ? "\uE769" : "\uE768";
        ShuffleIcon.Opacity = player.Shuffle ? 1 : 0.4;
        SheetShuffleIcon.Opacity = player.Shuffle ? 1 : 0.4;
        RepeatIcon.Glyph = player.Repeat switch
        {
            Services.RepeatMode.One => "\uE8ED",
            _ => "\uE8EE",
        };
        SheetRepeatIcon.Glyph = RepeatIcon.Glyph;
        RepeatIcon.Opacity = player.Repeat == Services.RepeatMode.Off ? 0.4 : 1;
        SheetRepeatIcon.Opacity = RepeatIcon.Opacity;
        BarTotal.Text = Format(player.Current?.DurationSeconds ?? 0);
        SheetTotal.Text = BarTotal.Text;
    }

    private void UpdateProgress(TimeSpan position)
    {
        if (_suppressSeek) return;

        _suppressSeek = true;
        BarSeek.Value = position.TotalSeconds;
        SheetSeek.Value = position.TotalSeconds;
        _suppressSeek = false;
        BarElapsed.Text = Format(position.TotalSeconds);
        SheetElapsed.Text = BarElapsed.Text;
        HighlightLyric(position);
    }

    private static string Format(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:D2}:{span.Seconds:D2}"
            : $"{span.Minutes}:{span.Seconds:D2}";
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e) => PlaybackService.Instance.TogglePlayPause();
    private void Next_Click(object sender, RoutedEventArgs e) => PlaybackService.Instance.Next();
    private void Prev_Click(object sender, RoutedEventArgs e) => PlaybackService.Instance.Previous();
    private void Shuffle_Click(object sender, RoutedEventArgs e) => PlaybackService.Instance.ToggleShuffle();
    private void Repeat_Click(object sender, RoutedEventArgs e) => PlaybackService.Instance.CycleRepeat();

    private void Seek_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressSeek) return;
        PlaybackService.Instance.Seek(TimeSpan.FromSeconds(e.NewValue));
        BarElapsed.Text = Format(e.NewValue);
        SheetElapsed.Text = BarElapsed.Text;
    }

    private void Volume_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressVolume) return;
        PlaybackService.Instance.Volume = e.NewValue / 100.0;
        SettingsService.Instance.SaveSettings();
    }
}
