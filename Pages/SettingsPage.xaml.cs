using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PixelPlayer.WinUI.Pages;

/// <summary>设置中心：分类卡片入口（对应参照项目 SettingsScreen 分类首页）。</summary>
public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
    }

    private void Category_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;
        Frame.Navigate(typeof(SettingsDetailPage), tag);
    }
}
