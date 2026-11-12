using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PixelPlayer.WinUI.Models;
using PixelPlayer.WinUI.Services;
using PixelPlayer.WinUI.Services.Netease;
using Windows.Storage.Pickers;

namespace PixelPlayer.WinUI.Pages;

/// <summary>设置分类详情页（对应参照项目 SettingsCategoryScreen）。</summary>
public sealed partial class SettingsDetailPage : Page
{
    private readonly ObservableCollection<string> _folders = new();
    private CancellationTokenSource? _scanCts;
    private bool _loading = true;
    private string _tag = "playback";

    public SettingsDetailPage()
    {
        InitializeComponent();
        FolderList.ItemsSource = _folders;
        Loaded += OnLoaded;
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        if (e.Parameter is string tag) _tag = tag;
        base.OnNavigatedTo(e);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        foreach (var section in new[] { SectionAccount, SectionPlayback, SectionAppearance, SectionLibrary, SectionEqualizer, SectionAi, SectionSources, SectionFloating, SectionWebRemote, SectionBackup, SectionAbout })
        {
            section.Visibility = Visibility.Collapsed;
        }

        var (title, desc) = _tag switch
        {
            "account" => ("账号", "网易云登录与云歌单同步。"),
            "playback" => ("播放", "倍速、睡眠定时器与播放行为。"),
            "appearance" => ("外观", "主题与配色。"),
            "library" => ("音乐管理", "音乐文件夹、扫描与曲库维护。"),
            "equalizer" => ("均衡器", "EQ 与音效增强。"),
            "ai" => ("AI 集成（β）", "AI 服务商与接口配置。"),
            "sources" => ("在线音源", "自建媒体服务器与下载目录；音源脚本与市场在「音源管理与市场」入口。"),
            "floating" => ("悬浮歌词", "桌面悬浮歌词窗口。"),
            "webremote" => ("网页遥控", "局域网遥控服务。"),
            "backup" => ("备份与恢复", "导出/导入配置、歌单与统计。"),
            _ => ("关于", "版本与说明。"),
        };

        TitleText.Text = title;
        DescText.Text = desc;
        if (_tag == "account") SectionAccount.Visibility = Visibility.Visible;
        else if (_tag == "playback") SectionPlayback.Visibility = Visibility.Visible;
        else if (_tag == "appearance") SectionAppearance.Visibility = Visibility.Visible;
        else if (_tag == "library") SectionLibrary.Visibility = Visibility.Visible;
        else if (_tag == "equalizer") SectionEqualizer.Visibility = Visibility.Visible;
        else if (_tag == "ai") SectionAi.Visibility = Visibility.Visible;
        else if (_tag == "sources") SectionSources.Visibility = Visibility.Visible;
        else if (_tag == "floating") SectionFloating.Visibility = Visibility.Visible;
        else if (_tag == "webremote") SectionWebRemote.Visibility = Visibility.Visible;
        else if (_tag == "backup") SectionBackup.Visibility = Visibility.Visible;
        else SectionAbout.Visibility = Visibility.Visible;

        switch (_tag)
        {
            case "account": InitAccount(); break;
            case "playback": InitPlayback(); break;
            case "appearance": InitAppearance(); break;
            case "library": InitLibrary(); break;
            case "ai": InitAi(); break;
            case "sources": InitSources(); break;
            case "floating": InitFloating(); break;
            case "webremote": InitWebRemote(); break;
            case "backup": InitBackup(); break;
            case "about": InitAbout(); break;
        }

        _loading = false;
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack) Frame.GoBack();
        else (App.MainWindow as MainWindow)?.NavigateToTag("settings");
    }

    /* ---------------- 账号（网易云） ---------------- */

    private void InitAccount()
    {
        NeteaseAccountService.Instance.AccountChanged -= OnAccountChanged;
        NeteaseAccountService.Instance.AccountChanged += OnAccountChanged;
        RefreshAccountUi();
    }

    private void OnAccountChanged(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(RefreshAccountUi);

    private void RefreshAccountUi()
    {
        var account = NeteaseAccountService.Instance;
        if (account.IsLoggedIn && account.Profile is not null)
        {
            AccountName.Text = account.Profile.Nickname;
            AccountStatus.Text = $"已登录 · UID {account.Profile.UserId}";
            BtnLogout.Visibility = Visibility.Visible;
            BtnLogin.Visibility = Visibility.Collapsed;
            BtnSendCaptcha.IsEnabled = false;
            PhoneBox.IsEnabled = false;
            CaptchaBox.IsEnabled = false;
            AccountAvatar.Visibility = Visibility.Visible;
            _ = LoadAvatarAsync(account.Profile.AvatarUrl);
        }
        else
        {
            AccountName.Text = "未登录网易云";
            AccountStatus.Text = "登录后可同步云歌单、提升在线播放音质";
            BtnLogout.Visibility = Visibility.Collapsed;
            BtnLogin.Visibility = Visibility.Visible;
            BtnSendCaptcha.IsEnabled = true;
            PhoneBox.IsEnabled = true;
            CaptchaBox.IsEnabled = true;
            AccountAvatar.Visibility = Visibility.Collapsed;
            AccountAvatar.Source = null;
        }
    }

    private async Task LoadAvatarAsync(string? url)
    {
        if (string.IsNullOrEmpty(url)) return;
        try
        {
            var bytes = await new System.Net.Http.HttpClient().GetByteArrayAsync(url);
            var img = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            using var ms = new MemoryStream(bytes);
            await img.SetSourceAsync(ms.AsRandomAccessStream());
            AccountAvatar.Source = img;
        }
        catch { }
    }

    private async void SendCaptcha_Click(object sender, RoutedEventArgs e)
    {
        var phone = PhoneBox.Text.Trim();
        if (phone.Length == 0)
        {
            SyncStatus.Text = "请先输入手机号";
            return;
        }
        try
        {
            BtnSendCaptcha.IsEnabled = false;
            SyncStatus.Text = "验证码发送中…";
            await NeteaseAccountService.Instance.SendCaptchaAsync(phone);
            SyncStatus.Text = "验证码已发送，请查收短信";
        }
        catch (Exception ex)
        {
            SyncStatus.Text = $"发送失败：{ex.Message}";
        }
        finally
        {
            BtnSendCaptcha.IsEnabled = true;
        }
    }

    private async void AccountLogin_Click(object sender, RoutedEventArgs e)
    {
        var phone = PhoneBox.Text.Trim();
        var captcha = CaptchaBox.Text.Trim();
        if (phone.Length == 0 || captcha.Length == 0)
        {
            SyncStatus.Text = "请输入手机号和验证码";
            return;
        }
        try
        {
            BtnLogin.IsEnabled = false;
            SyncStatus.Text = "登录中…";
            var profile = await NeteaseAccountService.Instance.LoginByCaptchaAsync(phone, captcha);
            SyncStatus.Text = $"登录成功：{profile.Nickname}";
        }
        catch (Exception ex)
        {
            SyncStatus.Text = $"登录失败：{ex.Message}";
        }
        finally
        {
            BtnLogin.IsEnabled = true;
        }
    }

    private async void SyncPlaylists_Click(object sender, RoutedEventArgs e)
    {
        if (!NeteaseAccountService.Instance.IsLoggedIn)
        {
            SyncStatus.Text = "请先登录网易云账号";
            return;
        }
        try
        {
            BtnSyncPlaylists.IsEnabled = false;
            SyncStatus.Text = "同步中…";
            var (plCount, songCount) = await NeteasePlaylistService.Instance.SyncAllAsync();
            SyncStatus.Text = $"同步完成：{plCount} 个歌单 / {songCount} 首歌";
        }
        catch (Exception ex)
        {
            SyncStatus.Text = $"同步失败：{ex.Message}";
        }
        finally
        {
            BtnSyncPlaylists.IsEnabled = true;
        }
    }

    private void AccountLogout_Click(object sender, RoutedEventArgs e)
    {
        NeteaseAccountService.Instance.Logout();
        SyncStatus.Text = "已退出登录";
    }

    private void CookieImport_Click(object sender, RoutedEventArgs e)
    {
        var cookie = CookieBox.Text.Trim();
        if (cookie.Length == 0)
        {
            SyncStatus.Text = "请粘贴 Cookie";
            return;
        }
        try
        {
            NeteaseAccountService.Instance.ImportCookies(cookie);
            SyncStatus.Text = "Cookie 已导入";
        }
        catch (Exception ex)
        {
            SyncStatus.Text = $"导入失败：{ex.Message}";
        }
    }

    /* ---------------- 播放 ---------------- */

    private void InitPlayback()
    {
        var settings = SettingsService.Instance.Settings;
        var speed = settings.PlaybackSpeed;

        for (var i = 0; i < SpeedBox.Items.Count; i++)
        {
            if (SpeedBox.Items[i] is ComboBoxItem item
                && double.TryParse(item.Tag as string, out var v)
                && Math.Abs(v - speed) < 0.01)
            {
                SpeedBox.SelectedIndex = i;
                break;
            }
        }
        if (SpeedBox.SelectedIndex < 0) SpeedBox.SelectedIndex = 2;

        PitchFollow.IsOn = settings.PitchFollowSpeed;
        Gapless.IsOn = settings.GaplessPlayback;
        Crossfade.IsOn = settings.CrossfadeEnabled;
        CrossfadeSeconds.Text = settings.CrossfadeSeconds.ToString();

        UpdateSleepStatus();
        if (PlaybackService.Instance.SleepTimerEnd is null) SleepBox.SelectedIndex = 0;
    }

    private void Speed_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (SpeedBox.SelectedItem is ComboBoxItem { Tag: string tag } && double.TryParse(tag, out var v))
        {
            PlaybackService.Instance.PlaybackSpeed = v;
        }
    }

    private void PitchFollow_Toggled(object sender, RoutedEventArgs e)
    {
        SettingsService.Instance.Settings.PitchFollowSpeed = PitchFollow.IsOn;
        SettingsService.Instance.SaveSettings();
    }

    private void Gapless_Toggled(object sender, RoutedEventArgs e)
    {
        SettingsService.Instance.Settings.GaplessPlayback = Gapless.IsOn;
        SettingsService.Instance.SaveSettings();
    }

    private void Crossfade_Toggled(object sender, RoutedEventArgs e)
    {
        SettingsService.Instance.Settings.CrossfadeEnabled = Crossfade.IsOn;
        SettingsService.Instance.SaveSettings();
    }

    private void CrossfadeSeconds_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        if (int.TryParse(CrossfadeSeconds.Text, out var v) && v is >= 0 and <= 12)
        {
            SettingsService.Instance.Settings.CrossfadeSeconds = v;
            SettingsService.Instance.SaveSettings();
        }
    }

    private void UpdateSleepStatus()
    {
        var end = PlaybackService.Instance.SleepTimerEnd;
        SleepStatus.Text = end is null ? "未启用" : $"将在 {end:HH:mm} 暂停播放";
    }

    private void Sleep_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (SleepBox.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out var minutes))
        {
            if (minutes > 0) PlaybackService.Instance.StartSleepTimer(minutes);
            else PlaybackService.Instance.CancelSleepTimer();
            UpdateSleepStatus();
        }
    }

    /* ---------------- 在线音源 ---------------- */

    private void InitSources()
    {
        var s = SettingsService.Instance.Settings;
        _loading = true;
        JellyfinServer.Text = s.JellyfinServer;
        JellyfinUser.Text = s.JellyfinUser;
        JellyfinPassword.Password = s.JellyfinPassword;
        NavidromeServer.Text = s.NavidromeServer;
        NavidromeUser.Text = s.NavidromeUser;
        NavidromePassword.Password = s.NavidromePassword;
        DownloadFolderBox.Text = s.DownloadFolder;
        _loading = false;
    }

    private void LxSourcesEntry_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) =>
        Frame.Navigate(typeof(LxSourcesPage), null);

    private void Sources_TextChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = SettingsService.Instance.Settings;
        s.JellyfinServer = JellyfinServer.Text.Trim();
        s.JellyfinUser = JellyfinUser.Text.Trim();
        s.JellyfinPassword = JellyfinPassword.Password;
        s.JellyfinToken = ""; // 凭据变化后重新登录
        s.NavidromeServer = NavidromeServer.Text.Trim();
        s.NavidromeUser = NavidromeUser.Text.Trim();
        s.NavidromePassword = NavidromePassword.Password;
        s.DownloadFolder = DownloadFolderBox.Text.Trim();
        SettingsService.Instance.SaveSettings();
    }

    /* ---------------- 悬浮歌词 ---------------- */

    private void InitFloating()
    {
        FloatingSwitch.IsOn = SettingsService.Instance.Settings.FloatingLyricsEnabled;
    }

    private void Floating_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SettingsService.Instance.Settings.FloatingLyricsEnabled = FloatingSwitch.IsOn;
        SettingsService.Instance.SaveSettings();
        MainWindow.SyncFloatingLyrics();
    }

    private async void AiTest_Click(object sender, RoutedEventArgs e)
    {
        AiStatus.Text = "正在测试…";
        var (ok, message) = await AiChatService.TestConnectionAsync();
        AiStatus.Text = message;
    }

    /* ---------------- 外观 ---------------- */

    private void InitAppearance()
    {
        var theme = SettingsService.Instance.Settings.Theme;
        ThemeChoice.SelectedIndex = theme switch
        {
            "Light" => 1,
            "Dark" => 2,
            _ => 0,
        };
    }

    private void Theme_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (ThemeChoice.SelectedItem is not RadioButton { Tag: string tag }) return;
        (App.MainWindow as MainWindow)?.ApplyTheme(tag);
    }

    /* ---------------- 音乐管理 ---------------- */

    private void InitLibrary()
    {
        foreach (var folder in SettingsService.Instance.Settings.MusicFolders)
        {
            _folders.Add(folder);
        }
        UpdateFolderHint();
        UpdateStorageText();
    }

    private void UpdateFolderHint() =>
        EmptyFolderHint.Visibility = _folders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.MusicLibrary };
            picker.FileTypeFilter.Add("*");

            if (App.MainWindow is not null)
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            }

            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;
            var path = folder.Path;
            if (string.IsNullOrEmpty(path)) return;
            if (_folders.Any(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase))) return;

            _folders.Add(path);
            SettingsService.Instance.Settings.MusicFolders = _folders.ToList();
            SettingsService.Instance.SaveSettings();
            UpdateFolderHint();
            UpdateStorageText();
            ScanStatus.Text = "已添加文件夹，点「扫描媒体库」开始导入。";
        }
        catch (Exception ex)
        {
            App.LogCrash("AddFolder", ex);
            ScanStatus.Text = "添加文件夹失败：" + ex.Message;
        }
    }

    private void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string path }) return;
        _folders.Remove(path);
        SettingsService.Instance.Settings.MusicFolders = _folders.ToList();
        SettingsService.Instance.SaveSettings();
        UpdateFolderHint();
        UpdateStorageText();
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_folders.Count == 0)
        {
            ScanStatus.Text = "请先添加至少一个音乐文件夹。";
            return;
        }

        if (_scanCts is not null)
        {
            _scanCts.Cancel();
            return;
        }

        _scanCts = new CancellationTokenSource();
        ScanButton.Content = "取消扫描";
        ScanProgress.Visibility = Visibility.Visible;
        ScanProgress.IsIndeterminate = true;
        ScanStatus.Text = "正在扫描…";

        var progress = new Progress<(int Scanned, int Added)>(p =>
            ScanStatus.Text = $"已扫描 {p.Scanned} 个文件，新增 {p.Added} 首…");

        try
        {
            var (added, skipped) = await LibraryService.Instance.ScanAsync(_folders, progress, _scanCts.Token);
            ScanStatus.Text = $"扫描完成：新增 {added} 首，跳过 {skipped} 首。曲库共 {LibraryService.Instance.Tracks.Count} 首。";
        }
        catch (OperationCanceledException)
        {
            ScanStatus.Text = $"扫描已取消，当前曲库 {LibraryService.Instance.Tracks.Count} 首。";
        }
        catch (Exception ex)
        {
            App.LogCrash("Scan", ex);
            ScanStatus.Text = "扫描失败：" + ex.Message;
        }
        finally
        {
            _scanCts.Dispose();
            _scanCts = null;
            ScanButton.Content = "扫描媒体库";
            ScanProgress.Visibility = Visibility.Collapsed;
            ScanProgress.IsIndeterminate = false;
            UpdateStorageText();
        }
    }

    private async void ClearLibrary_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "清空曲库？",
            Content = "只会移除曲库索引与封面缓存，不会删除磁盘上的音乐文件。",
            PrimaryButtonText = "清空",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        LibraryService.Instance.Tracks.Clear();
        LibraryService.Instance.Albums.Clear();
        LibraryService.Instance.Artists.Clear();
        await LibraryService.Instance.SaveCacheAsync();
        ScanStatus.Text = "曲库已清空。";
        UpdateStorageText();
    }

    private void UpdateStorageText()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PixelPlayer.WinUI");

        StorageInfoText();
    }

    private void StorageInfoText()
    {
        // 状态行合并到扫描状态，避免额外控件
        ScanStatus.Text = $"曲库 {LibraryService.Instance.Tracks.Count} 首 · 专辑 {LibraryService.Instance.Albums.Count} 张 · 播放列表 {SettingsService.Instance.Playlists.Count} 个";
    }

    /* ---------------- 均衡器 ---------------- */

    private void OpenEqualizer_Click(object sender, RoutedEventArgs e) =>
        (App.MainWindow as MainWindow)?.NavigateToTag("equalizer");

    /* ---------------- AI ---------------- */

    private void InitAi()
    {
        var settings = SettingsService.Instance.Settings;
        for (var i = 0; i < AiProviderBox.Items.Count; i++)
        {
            if (AiProviderBox.Items[i] is ComboBoxItem item && (item.Content as string) == settings.AiProvider)
            {
                AiProviderBox.SelectedIndex = i;
                break;
            }
        }
        if (AiProviderBox.SelectedIndex < 0) AiProviderBox.SelectedIndex = 0;
        AiBaseUrl.Text = settings.AiBaseUrl;
        AiApiKey.Password = settings.AiApiKey;
        AiModel.Text = settings.AiModel;
        AiStatus.Text = "已保存";
    }

    private void Ai_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var settings = SettingsService.Instance.Settings;
        settings.AiProvider = (AiProviderBox.SelectedItem as ComboBoxItem)?.Content as string ?? "OpenAI 兼容";
        settings.AiBaseUrl = AiBaseUrl.Text;
        settings.AiApiKey = AiApiKey.Password;
        settings.AiModel = AiModel.Text;
        SettingsService.Instance.SaveSettings();
    }

    /* ---------------- 网页遥控 ---------------- */

    private void InitWebRemote()
    {
        var settings = SettingsService.Instance.Settings;
        RemoteSwitch.IsOn = settings.WebRemoteEnabled;
        RemotePort.Text = settings.WebRemotePort.ToString();
        UpdateRemoteStatus();
    }

    private void Remote_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SettingsService.Instance.Settings.WebRemoteEnabled = RemoteSwitch.IsOn;
        SettingsService.Instance.SaveSettings();
        var remote = WebRemoteService.Instance;
        if (RemoteSwitch.IsOn) remote.Start(); else remote.Stop();
        UpdateRemoteStatus();
    }

    private void RemotePort_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        if (int.TryParse(RemotePort.Text, out var port) && port is >= 1024 and <= 65535)
        {
            SettingsService.Instance.Settings.WebRemotePort = port;
            SettingsService.Instance.SaveSettings();
        }
    }

    private void UpdateRemoteStatus()
    {
        var remote = WebRemoteService.Instance;
        RemoteStatus.Text = remote.IsRunning
            ? $"服务运行中：{remote.ListenUrl}（手机需与电脑同一局域网）"
            : "服务未运行。";
    }

    private void OpenRemote_Click(object sender, RoutedEventArgs e)
    {
        var remote = WebRemoteService.Instance;
        if (!remote.IsRunning) remote.Start();
        UpdateRemoteStatus();
        try
        {
            Process.Start(new ProcessStartInfo(remote.ListenUrl ?? "http://localhost:9849/") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.LogCrash("OpenRemote", ex);
        }
    }

    private void CopyRemote_Click(object sender, RoutedEventArgs e)
    {
        var remote = WebRemoteService.Instance;
        if (!remote.IsRunning) remote.Start();
        UpdateRemoteStatus();
        var url = remote.ListenUrl ?? "";
        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(url);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            RemoteStatus.Text = $"已复制：{url}";
        }
        catch { /* 忽略 */ }
    }

    /* ---------------- 备份与恢复 ---------------- */

    private void InitBackup()
    {
        BackupStatus.Text = $"当前：设置/歌单/历史存放于 %LocalAppData%\\PixelPlayer.WinUI，曲库 {LibraryService.Instance.Tracks.Count} 首。";
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileSavePicker { SuggestedFileName = "pixelplayer-backup" };
            picker.FileTypeChoices.Add("备份 JSON", new List<string> { ".json" });
            if (App.MainWindow is not null)
            {
                WinRT.Interop.InitializeWithWindow.Initialize(picker,
                    WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
            }
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;

            var payload = new
            {
                version = 1,
                exportedAt = DateTime.Now,
                settings = SettingsService.Instance.Settings,
                playlists = SettingsService.Instance.Playlists,
                history = HistoryService.Instance.Entries.ToList(),
            };

            await File.WriteAllTextAsync(file.Path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            BackupStatus.Text = $"已导出到 {file.Path}";
        }
        catch (Exception ex)
        {
            App.LogCrash("Export", ex);
            BackupStatus.Text = "导出失败：" + ex.Message;
        }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".json");
            if (App.MainWindow is not null)
            {
                WinRT.Interop.InitializeWithWindow.Initialize(picker,
                    WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
            }
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;

            var json = await File.ReadAllTextAsync(file.Path);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("settings", out var settingsEl))
            {
                var settings = settingsEl.Deserialize<AppSettings>();
                if (settings is not null)
                {
                    SettingsService.Instance.SetSettings(settings);
                    (App.MainWindow as MainWindow)?.ApplyTheme(settings.Theme);
                }
            }

            if (doc.RootElement.TryGetProperty("playlists", out var playlistsEl))
            {
                var playlists = playlistsEl.Deserialize<List<Playlist>>();
                if (playlists is not null)
                {
                    SettingsService.Instance.Playlists.Clear();
                    foreach (var p in playlists) SettingsService.Instance.Playlists.Add(p);
                    SettingsService.Instance.SavePlaylists();
                }
            }

            if (doc.RootElement.TryGetProperty("history", out var historyEl))
            {
                var history = historyEl.Deserialize<List<PlayHistoryEntry>>();
                if (history is not null)
                {
                    HistoryService.Instance.Clear();
                    foreach (var h in history.OrderByDescending(h => h.PlayedAt))
                    {
                        HistoryService.Instance.Entries.Add(h);
                    }
                    await HistoryService.Instance.SaveAsync();
                }
            }

            BackupStatus.Text = $"已从 {file.Path} 恢复。重启应用后全部生效。";
        }
        catch (Exception ex)
        {
            App.LogCrash("Import", ex);
            BackupStatus.Text = "导入失败：" + ex.Message;
        }
    }

    /* ---------------- 关于 ---------------- */

    private void InitAbout()
    {
        AboutInfo.Text =
            $"数据目录：%LocalAppData%\\PixelPlayer.WinUI\n" +
            $"曲库：{LibraryService.Instance.Tracks.Count} 首 · 专辑 {LibraryService.Instance.Albums.Count} 张 · 艺术家 {LibraryService.Instance.Artists.Count} 位\n" +
            $"播放历史：{HistoryService.Instance.Entries.Count} 条";
    }
}
