using System.Collections.Generic;

namespace PixelPlayer.WinUI.Models;

/// <summary>应用设置 + 播放列表的本地持久化模型。所有字段直接映射 settings.json。</summary>
public sealed class AppSettings
{
    public List<string> MusicFolders { get; set; } = new();
    public List<string> ExcludedFolders { get; set; } = new();
    public string Theme { get; set; } = "Default";     // Default / Light / Dark
    public double Volume { get; set; } = 0.8;
    public bool Shuffle { get; set; }
    public int RepeatMode { get; set; }               // 0 关闭 / 1 列表循环 / 2 单曲循环

    // 播放（参照项目 PLAYBACK 分类）
    public double PlaybackSpeed { get; set; } = 1.0;
    public bool PitchFollowSpeed { get; set; }        // 变调跟随倍速（DSP 管线接入后生效）
    public bool GaplessPlayback { get; set; }         // 无缝播放（占位）
    public bool CrossfadeEnabled { get; set; }        // 交叉淡入淡出（占位）
    public int CrossfadeSeconds { get; set; } = 3;

    // 均衡器（参照项目 10 段 EQ：31Hz…16kHz）
    public bool EqualizerEnabled { get; set; }
    public string EqPresetName { get; set; } = "平坦";
    public List<double> EqGains { get; set; } = new(); // 10 个 -12…+12 dB
    public bool BassBoost { get; set; }
    public bool Virtualizer { get; set; }              // 空间音效

    // AI 集成（参照项目 AI_INTEGRATION 分类）
    public string AiProvider { get; set; } = "OpenAI 兼容";
    public string AiBaseUrl { get; set; } = "https://api.openai.com/v1";
    public string AiApiKey { get; set; } = "";
    public string AiModel { get; set; } = "gpt-4o-mini";

    // 网页遥控（参照项目 WEB_REMOTE 分类）
    public bool WebRemoteEnabled { get; set; }
    public int WebRemotePort { get; set; } = 9849;

    // 在线音源（参照项目各音源模块）
    public string OnlineSourceId { get; set; } = "netease";
    public int StreamProxyPort { get; set; } = 9850;

    // 自建媒体服务器（参照项目 Jellyfin / Navidrome 模块）
    public string JellyfinServer { get; set; } = "";
    public string JellyfinUser { get; set; } = "";
    public string JellyfinPassword { get; set; } = "";
    public string JellyfinToken { get; set; } = "";
    public string NavidromeServer { get; set; } = "";
    public string NavidromeUser { get; set; } = "";
    public string NavidromePassword { get; set; } = "";

    // 下载管理（参照项目 MusicDownloadService）
    public string DownloadFolder { get; set; } = "";   // 空 → 默认 音乐\PixelPlayer
    public int DownloadConcurrent { get; set; } = 3;

    // DSP / 引擎（参照项目 HiFi 引擎）
    public bool ReplayGainEnabled { get; set; }
    public double ReplayGainPreamp { get; set; } = 0;  // dB
    public bool FloatingLyricsEnabled { get; set; }    // 桌面悬浮歌词

    public string? LastTrackPath { get; set; }
    public List<string> RecentTracks { get; set; } = new();
}
