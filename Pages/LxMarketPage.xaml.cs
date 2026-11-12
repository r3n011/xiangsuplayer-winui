using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PixelPlayer.WinUI.Services.Lx;

namespace PixelPlayer.WinUI.Pages;

/// <summary>音源市场页（1:1 对齐参考项目 SourceMarketScreen）：
/// MarketIntroCard（已安装计数 + 刷新）+ ReleaseCard 手风琴（Idle/Downloading/Inspecting/Inspected/Error），
/// 展开 Release 自动下载 zip 并解析出脚本行（MarketJsEntryRow：安装按钮 / 已安装 chip）。</summary>
public sealed partial class LxMarketPage : Page
{
    /// <summary>Release 卡片（对齐参考 ReleaseState 状态机）。</summary>
    public sealed class ReleaseVm : INotifyPropertyChanged
    {
        public LxSourceMarket.GitHubRelease Release { get; init; } = null!;
        public string TagName => Release.TagName;
        public string Name => string.IsNullOrWhiteSpace(Release.Name) ? "" : Release.Name;
        public string MetaText { get; set; } = "";
        public string Notes { get; set; } = "";

        // 状态：idle / downloading / inspecting / inspected / error
        private string _state = "idle";
        public string StatusText { get; private set; } = "点击展开以下载并查看脚本";
        public Visibility StatusVisibility { get; private set; } = Visibility.Visible;
        public Visibility ProgressVisibility { get; private set; } = Visibility.Collapsed;
        public Visibility NotesVisibility { get; private set; } = Visibility.Collapsed;
        public Visibility JsVisibility { get; private set; } = Visibility.Collapsed;
        public double ProgressPercent { get; private set; }

        private bool _expanded;
        public bool Expanded
        {
            get => _expanded;
            set { _expanded = value; Raise(); }
        }

        public Visibility BodyVisibility => Expanded ? Visibility.Visible : Visibility.Collapsed;
        public string Chevron => Expanded ? "\uE70D" : "\uE76C"; // 展开箭头上下切换

        public List<JsEntryVm> JsEntries { get; } = new();

        public event PropertyChangedEventHandler? PropertyChanged;

        public void SetState(string state, string? status = null)
        {
            _state = state;
            StatusText = status ?? state switch
            {
                "idle" => "点击展开以下载并查看脚本",
                "downloading" => "正在下载音源包…",
                "inspecting" => "正在解析 zip 中的脚本…",
                "inspected" => $"共 {JsEntries.Count} 个音源脚本",
                "error" => "下载失败，请检查网络后重试",
                _ => "",
            };
            StatusVisibility = StatusText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            ProgressVisibility = state == "downloading" ? Visibility.Visible : Visibility.Collapsed;
            NotesVisibility = state == "inspected" && Notes.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            JsVisibility = state == "inspected" && JsEntries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            RaiseAll();
        }

        public void SetProgress(float p)
        {
            ProgressPercent = p * 100;
            Raise(nameof(ProgressPercent));
        }

        private void RaiseAll()
        {
            Raise(nameof(StatusText));
            Raise(nameof(StatusVisibility));
            Raise(nameof(ProgressVisibility));
            Raise(nameof(NotesVisibility));
            Raise(nameof(JsVisibility));
            Raise(nameof(BodyVisibility));
            Raise(nameof(Chevron));
        }

        public void Raise([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));

        public event Action<ReleaseVm>? ExpandedFirstTime;

        private bool _everExpanded;
        public void EnsureExpanded()
        {
            Expanded = !Expanded;
            Raise(nameof(BodyVisibility));
            Raise(nameof(Chevron));
            if (Expanded && !_everExpanded)
            {
                _everExpanded = true;
                ExpandedFirstTime?.Invoke(this);
            }
        }
    }

    /// <summary>zip 内单个脚本行（对齐参考 MarketJsEntryRow）。</summary>
    public sealed class JsEntryVm : INotifyPropertyChanged
    {
        public LxSourceMarket.MarketJsEntry Entry { get; init; } = null!;
        public string FileName => Entry.FileName;
        public string SizeText => Entry.Size > 1024 * 1024
            ? $"{Entry.Size / 1024d / 1024:F1} MB"
            : $"{Entry.Size / 1024d:F0} KB";

        private bool _installed;
        public bool Installed
        {
            get => _installed;
            set
            {
                _installed = value;
                InstallVisibility = value ? Visibility.Collapsed : Visibility.Visible;
                InstalledVisibility = value ? Visibility.Visible : Visibility.Collapsed;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InstallVisibility)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InstalledVisibility)));
            }
        }

        public Visibility InstallVisibility { get; private set; } = Visibility.Visible;
        public Visibility InstalledVisibility { get; private set; } = Visibility.Collapsed;

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public LxMarketPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await LoadReleasesAsync();
    }

    private async Task LoadReleasesAsync()
    {
        LoadingRing.IsActive = true;
        LoadingRing.Visibility = Visibility.Visible;
        EmptyHint.Visibility = Visibility.Collapsed;
        var installed = LxFileStore.Instance.ListFiles().Count;
        InstalledText.Text = $"已安装 {installed} 个音源";

        List<LxSourceMarket.GitHubRelease>? releases;
        try
        {
            releases = await LxSourceMarket.Instance.FetchReleasesAsync();
        }
        catch (Exception ex)
        {
            App.LogCrash("LxMarket.Fetch", ex);
            releases = null;
        }

        LoadingRing.IsActive = false;
        LoadingRing.Visibility = Visibility.Collapsed;

        if (releases is null || releases.Count == 0)
        {
            ReleaseList.ItemsSource = Array.Empty<ReleaseVm>();
            EmptyHint.Visibility = Visibility.Visible;
            return;
        }

        var installedNames = LxFileStore.Instance.ListFiles().Select(f => f.Name).ToHashSet();
        var vms = new List<ReleaseVm>();
        foreach (var r in releases)
        {
            var zip = LxSourceMarket.Instance.ZipAssetOf(r);
            var date = r.PublishedAt is { Length: > 9 } d ? d[..10] : "";
            var meta = date;
            if (zip is not null) meta += $" · {zip.Size / 1024d / 1024:F1} MB";
            meta += $" · {r.Assets.Count} 个附件";

            var vm = new ReleaseVm { Release = r, MetaText = meta, Notes = (r.Body ?? "").Trim() };
            vm.ExpandedFirstTime += Release_ExpandFirstTime;
            vms.Add(vm);
        }
        ReleaseList.ItemsSource = vms;
    }

    private async void Release_ExpandFirstTime(ReleaseVm vm)
    {
        try
        {
            var zipAsset = LxSourceMarket.Instance.ZipAssetOf(vm.Release);
            if (zipAsset is null)
            {
                vm.SetState("error", $"{vm.Release.TagName} 没有 zip 附件，无法安装");
                return;
            }

            vm.SetState("downloading");
            var (ok, zipPath, error) = await LxSourceMarket.Instance.DownloadZipAsync(
                zipAsset, vm.Release.TagName, p => DispatcherQueue.TryEnqueue(() => vm.SetProgress(p)));
            if (!ok || zipPath is null)
            {
                vm.SetState("error", $"下载失败：{error ?? "未知错误"}");
                return;
            }

            vm.SetState("inspecting");
            var entries = await LxSourceMarket.Instance.InspectZipAsync(zipPath);
            if (entries.Count == 0)
            {
                vm.SetState("error", "zip 内没有 .js 音源脚本");
                return;
            }

            var installedNames = LxFileStore.Instance.ListFiles().Select(f => f.Name).ToHashSet();
            vm.JsEntries.Clear();
            foreach (var entry in entries)
            {
                var row = new JsEntryVm { Entry = entry };
                row.Installed = installedNames.Contains(Path.GetFileName(entry.FileName));
                vm.JsEntries.Add(row);
            }
            vm.SetState("inspected", $"共 {entries.Count} 个音源脚本");
        }
        catch (Exception ex)
        {
            App.LogCrash("LxMarket.Expand", ex);
            vm.SetState("error", $"处理失败：{ex.Message}");
        }
    }

    private void ReleaseHeader_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ReleaseVm vm) vm.EnsureExpanded();
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: JsEntryVm row }) return;
        var vm = FindOwner(row);
        try
        {
            if (LxFileStore.Instance.Import(Path.GetFileName(row.FileName), row.Entry.Bytes))
            {
                row.Installed = true;
                LxJsEngine.Instance.Reload();
                var installed = LxFileStore.Instance.ListFiles().Count;
                InstalledText.Text = $"已安装 {installed} 个音源";
            }
        }
        catch (Exception ex)
        {
            App.LogCrash("LxMarket.Install", ex);
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "安装失败",
                Content = ex.Message,
                CloseButtonText = "确定",
                DefaultButton = ContentDialogButton.Close,
            };
            await dialog.ShowAsync();
        }
    }

    private ReleaseVm? FindOwner(JsEntryVm row)
    {
        if (ReleaseList.ItemsSource is not IEnumerable<ReleaseVm> list) return null;
        return list.FirstOrDefault(v => v.JsEntries.Contains(row));
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadReleasesAsync();

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack) Frame.GoBack();
        else (App.MainWindow as MainWindow)?.NavigateToTag("settings");
    }
}
