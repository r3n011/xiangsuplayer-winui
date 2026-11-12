using System;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace PixelPlayer.WinUI.Services;

/// <summary>
/// 桌面悬浮歌词（对应参照项目 FloatingLyricsController/FloatingLyricsWindow）：
/// 无边框置顶小窗，屏幕底部居中，显示当前歌词行。
/// </summary>
public sealed class FloatingLyricsWindow : Window
{
    private static FloatingLyricsWindow? _instance;

    public static bool IsVisible => _instance is { Visible: true };

    private readonly TextBlock _text = new()
    {
        FontSize = 20,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Foreground = new SolidColorBrush(Colors.White),
        TextTrimming = TextTrimming.CharacterEllipsis,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        TextAlignment = TextAlignment.Center,
    };

    private readonly Border _root;

    private FloatingLyricsWindow()
    {
        _root = new Border
        {
            Background = new SolidColorBrush(new Windows.UI.Color { A = 200, R = 16, G = 16, B = 20 }),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(24, 10, 24, 10),
            Child = _text,
        };
        Content = _root;

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
        }

        Title = "悬浮歌词";
        AppWindow.IsShownInSwitchers = false;
    }

    public static void Show()
    {
        _instance ??= new FloatingLyricsWindow();
        _instance.PositionAtBottom();
        _instance.Activate();
    }

    public static void Hide()
    {
        _instance?.Close();
        _instance = null;
    }

    /// <summary>更新当前歌词行（空字符串显示曲名）。</summary>
    public static void UpdateText(string line)
    {
        if (_instance is not { } win) return;
        var text = string.IsNullOrWhiteSpace(line) ? PlaybackService.Instance.Current?.Title ?? "" : line;
        win._text.Text = text;
    }

    private void PositionAtBottom()
    {
        var workArea = DisplayArea.Primary.WorkArea;
        const int width = 720;
        const int height = 64;
        AppWindow.Resize(new SizeInt32(width, height));
        AppWindow.Move(new PointInt32(
            workArea.X + (workArea.Width - width) / 2,
            workArea.Y + workArea.Height - height - 72));
    }
}
