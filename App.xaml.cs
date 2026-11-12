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
            // 已记录即视为已处理，避免 stowed exception fail-fast 闪退
            e.Handled = true;
        };

        // WinUI stowed exception 有时不走 UnhandledException，补两条兜底通道
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogCrash("AppDomain.UnhandledException", e.ExceptionObject as Exception);

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogCrash("UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            MainWindow = new MainWindow();
            MainWindow.Activate();
        }
        catch (Exception ex)
        {
            LogCrash("OnLaunched", ex);
            throw;
        }
    }

    private static readonly object _logLock = new();

    /// <summary>崩溃/异常日志，写在 exe 同目录 crash.log。</summary>
    public static void LogCrash(string tag, Exception? ex)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "crash.log");
            lock (_logLock)
            {
                File.AppendAllText(path, $"[{DateTime.Now:O}] {tag}: {ex}\n\n");
            }
        }
        catch
        {
            // 忽略日志写入失败
        }
    }
}
