using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using PixelPlayer.WinUI.Models;

namespace PixelPlayer.WinUI.Services;

/// <summary>应用设置 + 播放列表的本地持久化（%LocalAppData%\PixelPlayer.WinUI）。</summary>
public sealed class SettingsService
{
    // 注意：静态字段必须先于 Instance 声明，否则构造时 Root 还是 null。
    private static readonly string Root = CreateRoot();

    private static string CreateRoot()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local)) local = AppContext.BaseDirectory;
        return Path.Combine(local, "PixelPlayer.WinUI");
    }

    private static readonly string SettingsFile = Path.Combine(Root, "settings.json");
    private static readonly string PlaylistsFile = Path.Combine(Root, "playlists.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static SettingsService Instance { get; } = new();

    public AppSettings Settings { get; private set; } = new();
    public List<Playlist> Playlists { get; private set; } = new();

    /// <summary>整体替换设置（导入备份用）。</summary>
    public void SetSettings(AppSettings settings)
    {
        Settings = settings ?? new AppSettings();
        SaveSettings();
    }

    public static string CoverCacheDir => Path.Combine(Root, "covers");

    private SettingsService()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(CoverCacheDir);
        Load();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                Settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile)) ?? new AppSettings();
            }
            if (File.Exists(PlaylistsFile))
            {
                Playlists = JsonSerializer.Deserialize<List<Playlist>>(File.ReadAllText(PlaylistsFile)) ?? new List<Playlist>();
            }
        }
        catch
        {
            Settings = new AppSettings();
            Playlists = new List<Playlist>();
        }
    }

    public void SaveSettings()
    {
        try
        {
            File.WriteAllText(SettingsFile, JsonSerializer.Serialize(Settings, JsonOptions));
        }
        catch { /* 忽略写盘失败 */ }
    }

    public void SavePlaylists()
    {
        try
        {
            File.WriteAllText(PlaylistsFile, JsonSerializer.Serialize(Playlists, JsonOptions));
        }
        catch { /* 忽略写盘失败 */ }
    }
}
