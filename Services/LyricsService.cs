using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace PixelPlayer.WinUI.Services;

public sealed record LyricLine(TimeSpan Time, string Text);

/// <summary>歌词：优先读取同目录同名 .lrc，解析 [mm:ss.xx] 时间轴。</summary>
public static class LyricsService
{
    private static readonly Regex TimeTag = new(@"\[(\d{1,2}):(\d{2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled);

    public static IReadOnlyList<LyricLine> LoadFor(string audioPath)
    {
        try
        {
            var lrc = Path.ChangeExtension(audioPath, ".lrc");
            if (!File.Exists(lrc))
            {
                var alt = audioPath + ".lrc";
                lrc = File.Exists(alt) ? alt : lrc;
            }
            return File.Exists(lrc) ? Parse(File.ReadAllLines(lrc)) : Array.Empty<LyricLine>();
        }
        catch
        {
            return Array.Empty<LyricLine>();
        }
    }

    public static IReadOnlyList<LyricLine> Parse(IEnumerable<string> lines)
    {
        var result = new List<LyricLine>();
        foreach (var raw in lines)
        {
            var matches = TimeTag.Matches(raw);
            if (matches.Count == 0) continue;

            var text = TimeTag.Replace(raw, "").Trim();
            if (string.IsNullOrEmpty(text)) continue;

            foreach (Match match in matches)
            {
                var minutes = int.Parse(match.Groups[1].Value);
                var seconds = int.Parse(match.Groups[2].Value);
                var fraction = match.Groups[3].Success
                    ? int.Parse(match.Groups[3].Value.PadRight(3, '0')[..3])
                    : 0;

                result.Add(new LyricLine(
                    new TimeSpan(0, 0, minutes, seconds, fraction),
                    text));
            }
        }

        result.Sort((a, b) => a.Time.CompareTo(b.Time));
        return result;
    }
}
