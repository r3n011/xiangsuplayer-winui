using System;
using System.IO;
using Microsoft.UI.Xaml;

namespace PixelPlayer.WinUI;

public partial class App : Application
{
    public static Window? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();

        UnhandledException += (_, e) =>
        {
            LogCrash("UnhandledException", e.Exception);
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new MainWindow();
        MainWindow.Activate();
    }

    /// <summary>崩溃/异常日志，写在 exe 同目录 crash.log。</summary>
    public static void LogCrash(string tag, Exception? ex)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "crash.log");
            File.AppendAllText(path, $"[{DateTime.Now:O}] {tag}: {ex}\n\n");
        }
        catch
        {
            // 忽略日志写入失败
        }
    }
}
