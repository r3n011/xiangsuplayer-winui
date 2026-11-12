using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using TagLib;
using PixelPlayer.WinUI.Models;
using File = TagLib.File;

namespace PixelPlayer.WinUI.Services;

/// <summary>
/// 标签编辑服务：读/写本地曲目元数据与封面（对应参照项目 SongMetadataEditor，Jaudiotagger → TagLibSharp）。
/// </summary>
public static class TagEditorService
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public sealed class TagData
    {
        public string Title { get; set; } = "";
        public string Artist { get; set; } = "";
        public string Album { get; set; } = "";
        public string Genre { get; set; } = "";
        public uint Year { get; set; }
        public uint Track { get; set; }
        public string Comment { get; set; } = "";
    }

    /// <summary>读取标签（只读，不写回）。</summary>
    public static TagData? ReadTags(string path)
    {
        try
        {
            using var file = File.Create(path);
            var t = file.Tag;
            return new TagData
            {
                Title = t.Title ?? "",
                Artist = t.FirstPerformer ?? t.FirstAlbumArtist ?? "",
                Album = t.Album ?? "",
                Genre = t.FirstGenre ?? "",
                Year = t.Year,
                Track = t.Track,
                Comment = t.Comment ?? "",
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 写标签。字段为 null 表示不修改；coverBytes 非空时替换封面（PNG/JPEG 原始字节）。
    /// </summary>
    public static bool WriteTags(string path, TagData? data, byte[]? coverBytes = null)
    {
        try
        {
            using var file = File.Create(path);
            if (data is not null)
            {
                var t = file.Tag;
                if (data.Title.Length > 0) t.Title = data.Title;
                if (data.Artist.Length > 0)
                {
                    t.Performers = data.Artist.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    t.AlbumArtists = t.Performers;
                }
                if (data.Album.Length > 0) t.Album = data.Album;
                if (data.Genre.Length > 0) t.Genres = new[] { data.Genre };
                if (data.Year > 0) t.Year = data.Year;
                if (data.Track > 0) t.Track = data.Track;
                if (data.Comment.Length > 0) t.Comment = data.Comment;
            }

            if (coverBytes is not null && coverBytes.Length > 0)
            {
                var ext = DetectImageType(coverBytes);
                file.Tag.Pictures = new[]
                {
                    new Picture(new ByteVector(coverBytes))
                    {
                        Type = PictureType.FrontCover,
                        MimeType = ext == "png" ? "image/png" : "image/jpeg",
                    }
                };
            }

            file.Save();
            return true;
        }
        catch (Exception ex)
        {
            App.LogCrash("TagEditor.Write", ex);
            return false;
        }
    }

    /// <summary>为下载完成的文件补全标签（对应 writeDownloadedFileTags）。</summary>
    public static async Task<bool> ApplyDownloadedTagsAsync(Track track, string path, bool embedCover = true)
    {
        var data = new TagData
        {
            Title = track.Title,
            Artist = track.Artist,
            Album = track.Album,
        };

        byte[]? cover = null;
        if (embedCover && !string.IsNullOrEmpty(track.PicUrl))
        {
            try
            {
                cover = await _http.GetByteArrayAsync(track.PicUrl);
            }
            catch
            {
                // 封面获取失败不影响标签写入
            }
        }

        return WriteTags(path, data, cover);
    }

    /// <summary>读取 ReplayGain 曲目增益（dB）标签；无则 null。</summary>
    public static double? ReadTrackGain(string path)
    {
        try
        {
            using var file = File.Create(path);
            var tag = file.Tag;

            // FLAC/Ogg/Opus 等 Vorbis comment 风格
            if (tag is TagLib.Ogg.XiphComment xiph)
            {
                foreach (var c in xiph.GetField("REPLAYGAIN_TRACK_GAIN"))
                {
                    var v = ParseGain(c);
                    if (v is not null) return v;
                }
            }

            // ID3v2 TXXX 帧
            if (tag is TagLib.Id3v2.Tag id3)
            {
                foreach (var frame in id3.GetFrames<TagLib.Id3v2.UserTextInformationFrame>())
                {
                    if (frame.Text is { Length: > 0 }
                        && string.Equals(frame.Description, "replaygain_track_gain", StringComparison.OrdinalIgnoreCase))
                    {
                        var v = ParseGain(frame.Text[0]);
                        if (v is not null) return v;
                    }
                }
            }
        }
        catch
        {
            // 无法解析则视为无 RG
        }
        return null;
    }

    private static double? ParseGain(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim().TrimEnd('d', 'B', 'b', 'D').Trim();
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return v;
        return null;
    }

    private static string DetectImageType(byte[] bytes)
    {
        if (bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50) return "png";
        return "jpg";
    }
}
