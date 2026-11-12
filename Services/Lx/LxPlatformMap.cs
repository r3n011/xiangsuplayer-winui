using System.Collections.Generic;
using PixelPlayer.WinUI.Models;

namespace PixelPlayer.WinUI.Services.Lx;

/// <summary>
/// LX 音源平台映射（对齐参考项目 wy/tx/kg/kw/mg）：
/// 在线音源完全来自像素音源市场下载的脚本，应用不内置任何平台 API。
/// LX key ↔ 内部 SourceId ↔ 显示名。
/// </summary>
public static class LxPlatformMap
{
    public static readonly Dictionary<string, string> ToSourceId = new()
    {
        ["wy"] = "netease", ["tx"] = "qq", ["kg"] = "kugou", ["kw"] = "kuwo", ["mg"] = "migu",
    };

    public static readonly Dictionary<string, string> ToLxKey = new()
    {
        ["netease"] = "wy", ["qq"] = "tx", ["kugou"] = "kg", ["kuwo"] = "kw", ["migu"] = "mg",
    };

    public static readonly Dictionary<string, string> DisplayNames = new()
    {
        ["wy"] = "网易云", ["tx"] = "QQ音乐", ["kg"] = "酷狗", ["kw"] = "酷我", ["mg"] = "咪咕",
    };

    public static string? IdOf(string lxKey) => ToSourceId.GetValueOrDefault(lxKey);

    public static string? KeyOf(string sourceId) => ToLxKey.GetValueOrDefault(sourceId);

    public static string DisplayName(string lxKey) => DisplayNames.GetValueOrDefault(lxKey, lxKey);

    /// <summary>LX 搜索结果 → 播放用 Track。</summary>
    public static Track ToTrack(LxJsEngine.LxSongInfo s)
    {
        var id = !string.IsNullOrEmpty(s.Songmid) ? s.Songmid
            : !string.IsNullOrEmpty(s.Hash) ? s.Hash
            : s.Id;
        var sourceId = IdOf(s.Source) ?? s.Source;
        return new Track
        {
            Path = $"{sourceId}://{id}",
            SourceId = sourceId,
            OnlineKey = id,
            Title = s.Name,
            Artist = s.Singer,
            Album = s.AlbumName,
            DurationSeconds = s.Duration / 1000.0,
            PicUrl = string.IsNullOrEmpty(s.Pic) ? null : s.Pic,
        };
    }

    /// <summary>Track → LX 脚本 songInfo（getPlayUrl/lyric/pic 的 info.musicInfo）。</summary>
    public static LxJsEngine.LxSongInfo ToLxSong(Track t) => new()
    {
        Id = t.OnlineKey,
        Songmid = t.OnlineKey,
        Hash = t.OnlineKey,
        Name = t.Title ?? "",
        Singer = t.Artist ?? "",
        AlbumName = t.Album ?? "",
        Duration = (long)t.DurationSeconds * 1000,
        Source = KeyOf(t.SourceId) ?? t.SourceId,
    };
}
