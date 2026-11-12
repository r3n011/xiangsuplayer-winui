using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PixelPlayer.WinUI.Services.Lx;
using Windows.Storage.Pickers;
using Windows.UI;

namespace PixelPlayer.WinUI.Pages;

/// <summary>自定义音源页（1:1 对齐参考项目 CloudMusicSettingsScreen 的音源管理部分）：
/// HeroSourceCard（引擎状态）+ MarketEntryCard（市场入口）+ PlatformRoutingCard + ScriptSourceCard 列表 + UsageInfoCard。</summary>
public sealed partial class LxSourcesPage : Page
{
    /// <summary>音源脚本行（对齐参考 ScriptSourceCard：48dp 字母头像 + 名称 + meta + 平台 chips + 简介）。</summary>
    public sealed class ScriptRowVm
    {
        public string FileName { get; init; } = "";
        public string Name { get; init; } = "";
        public string MetaText { get; init; } = "";
        public string Letter { get; init; } = "?";
        public Brush AvatarBrush { get; init; } = new SolidColorBrush(Colors.Gray);
        public List<string> PlatformChips { get; init; } = new();
        public string Description { get; init; } = "";
        public Visibility HasDescription { get; init; } = Visibility.Collapsed;
    }

    public sealed class RoutingRowVm
    {
        public string Platform { get; init; } = "";
        public string ScriptName { get; init; } = "";
        public string State { get; init; } = "";
    }

    private static readonly Color[] AvatarPalette =
    {
        Color.FromArgb(255, 124, 77, 255), // 紫
        Color.FromArgb(255, 68, 138, 255), // 蓝
        Color.FromArgb(255, 0, 170, 150),  // 青
        Color.FromArgb(255, 240, 100, 80), // 红
        Color.FromArgb(255, 240, 160, 30), // 橙
        Color.FromArgb(255, 90, 160, 70),  // 绿
    };

    public LxSourcesPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await InitAsync();
    }

    private async Task InitAsync()
    {
        EngineBusy.IsActive = true;
        EngineBusy.Visibility = Visibility.Visible;
        EngineStatusText.Text = "正在加载音源脚本…";
        BtnReload.IsEnabled = false;

        // 引擎已在后台预热，这里最多等 15s
        var ready = false;
        try { ready = await LxJsEngine.Instance.AwaitReadyAsync(15_000); }
        catch (Exception ex) { App.LogCrash("LxSources.Init", ex); }

        EngineBusy.IsActive = false;
        EngineBusy.Visibility = Visibility.Collapsed;
        BtnReload.IsEnabled = true;
        RefreshUi(ready);
    }

    private void RefreshUi(bool engineReady)
    {
        var engine = LxJsEngine.Instance;
        var files = LxFileStore.Instance.ListFiles();
        var infos = engine.ScriptInfos().ToDictionary(i => i.FileName, i => i);
        var scriptSources = engine.GetScriptSources();
        var sourceNames = engine.GetSources(); // key → LxSourceInfo（含展示名）

        EngineStatusText.Text = engineReady
            ? $"已加载 {files.Count} 个音源脚本 · 提供 {sourceNames.Count} 个平台"
            : files.Count == 0
                ? "未安装音源脚本，请导入 .js 或前往音源市场下载"
                : $"脚本加载失败：{engine.LastError ?? "未知错误"}";

        MarketEntryText.Text = $"已安装 {files.Count} 个音源 · 从像素音源市场下载更多";

        // 平台路由概览：key → 第一个注册该平台的脚本
        var routing = new List<RoutingRowVm>();
        var order = new[] { ("wy", "网易云"), ("tx", "QQ音乐"), ("kg", "酷狗"), ("kw", "酷我"), ("mg", "咪咕") };
        foreach (var (key, label) in order)
        {
            scriptSources.TryGetValue(key, out var scripts);
            var script = scripts?.FirstOrDefault();
            var display = script is not null && infos.TryGetValue(script, out var si) && !string.IsNullOrWhiteSpace(si.Name)
                ? si.Name
                : script ?? "";
            routing.Add(new RoutingRowVm
            {
                Platform = label,
                ScriptName = script is null ? "使用内置接口" : display,
                State = script is null ? "内置" : "脚本",
            });
        }
        foreach (var kv in scriptSources.Where(k => order.All(o => o.Item1 != k.Key)).OrderBy(k => k.Key))
        {
            var script = kv.Value.FirstOrDefault() ?? "";
            routing.Add(new RoutingRowVm
            {
                Platform = kv.Key,
                ScriptName = script,
                State = "脚本",
            });
        }
        RoutingList.ItemsSource = routing;

        // 脚本卡片列表
        var rows = new List<ScriptRowVm>();
        foreach (var f in files)
        {
            var info = infos.TryGetValue(f.Name, out var si) ? si : null;
            var name = string.IsNullOrWhiteSpace(info?.Name) ? Path.GetFileNameWithoutExtension(f.Name) : info!.Name;
            var meta = f.Name;
            if (!string.IsNullOrWhiteSpace(info?.Version)) meta += $" · v{info.Version}";
            if (!string.IsNullOrWhiteSpace(info?.Author)) meta += $" · {info.Author}";

            var platforms = scriptSources.TryGetValue(f.Name, out var ss)
                ? ss.Select(k => sourceNames.TryGetValue(k, out var s) && !string.IsNullOrWhiteSpace(s.Name) ? $"{s.Name} ({k})" : k)
                    .ToList()
                : new List<string>();

            var desc = (info?.Description ?? "").Trim();
            rows.Add(new ScriptRowVm
            {
                FileName = f.Name,
                Name = name,
                MetaText = meta,
                Letter = name.Length > 0 ? name[..1].ToUpperInvariant() : "?",
                AvatarBrush = new SolidColorBrush(AvatarPalette[Math.Abs(name.GetHashCode()) % AvatarPalette.Length]),
                PlatformChips = platforms,
                Description = desc,
                HasDescription = desc.Length > 0 ? Visibility.Visible : Visibility.Collapsed,
            });
        }
        ScriptList.ItemsSource = rows;
        ScriptCountText.Text = rows.Count > 0 ? $"{rows.Count} 个" : "";
        EmptyScriptsHint.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack) Frame.GoBack();
        else (App.MainWindow as MainWindow)?.NavigateToTag("settings");
    }

    private void MarketEntry_PointerPressed(object sender, PointerRoutedEventArgs e) =>
        Frame.Navigate(typeof(LxMarketPage));

    private void EntryCard_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border b) b.Opacity = 0.85;
    }

    private void EntryCard_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border b) b.Opacity = 1;
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        BtnReload.IsEnabled = false;
        EngineBusy.IsActive = true;
        EngineBusy.Visibility = Visibility.Visible;
        _ = Task.Run(() => LxJsEngine.Instance.Reload())
            .ContinueWith(t =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    EngineBusy.IsActive = false;
                    EngineBusy.Visibility = Visibility.Collapsed;
                    BtnReload.IsEnabled = true;
                    RefreshUi(LxJsEngine.Instance.IsReady);
                });
            });
    }

    private async void ImportScript_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".js");
            if (App.MainWindow is MainWindow mw)
            {
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(mw));
            }
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;

            var bytes = await File.ReadAllBytesAsync(file.Path);
            if (!LxFileStore.Instance.Import(file.Name, bytes))
            {
                EngineStatusText.Text = $"导入失败：{file.Name}";
                return;
            }
            EngineBusy.IsActive = true;
            EngineBusy.Visibility = Visibility.Visible;
            var ready = await Task.Run(() => LxJsEngine.Instance.Reload());
            EngineBusy.IsActive = false;
            EngineBusy.Visibility = Visibility.Collapsed;
            RefreshUi(ready);
        }
        catch (Exception ex)
        {
            App.LogCrash("LxSources.Import", ex);
            EngineStatusText.Text = $"导入失败：{ex.Message}";
        }
    }

    private async void DeleteScript_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string fileName }) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "删除音源脚本",
            Content = $"确定删除 {fileName}？删除后需要重载音源才会生效。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        if (LxFileStore.Instance.Delete(fileName))
        {
            EngineBusy.IsActive = true;
            EngineBusy.Visibility = Visibility.Visible;
            var ready = await Task.Run(() => LxJsEngine.Instance.Reload());
            EngineBusy.IsActive = false;
            EngineBusy.Visibility = Visibility.Collapsed;
            RefreshUi(ready);
        }
    }
}
