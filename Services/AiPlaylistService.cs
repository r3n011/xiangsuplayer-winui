using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PixelPlayer.WinUI.Models;

namespace PixelPlayer.WinUI.Services;

/// <summary>
/// AI 歌单生成（对应参照项目 AiPlaylistGenerator）：
/// 从曲库构建候选池 + 用户听歌画像 → 模型返回 JSON 数组 → 映射回本地曲目。
/// </summary>
public static class AiPlaylistService
{
    /// <summary>生成 AI 歌单；返回映射到本地曲库的曲目列表。</summary>
    public static async Task<List<Track>> GenerateAsync(int targetCount = 20, CancellationToken ct = default)
    {
        var library = LibraryService.Instance;
        if (library.Tracks.Count == 0) throw new InvalidOperationException("曲库为空");

        var candidates = BuildCandidatePool(100);
        var digest = BuildUserProfileDigest();
        var prompt = BuildPrompt(digest, targetCount, candidates);

        var reply = await AiChatService.GenerateAsync(SystemPrompt, prompt, ct);
        var titles = ExtractSongTitles(reply);

        // 按标题/艺术家模糊映射回本地曲库
        var result = new List<Track>();
        foreach (var (title, artist) in titles)
        {
            var match = MatchTrack(library.Tracks, title, artist);
            if (match is not null && !result.Contains(match)) result.Add(match);
            if (result.Count >= targetCount) break;
        }

        // 不足时用候选池补齐
        foreach (var c in candidates)
        {
            if (result.Count >= targetCount) break;
            if (!result.Contains(c)) result.Add(c);
        }

        return result;
    }

    private const string SystemPrompt =
        "你是音乐播放器里的 AI 歌单 DJ。你必须只输出一个纯 JSON 数组，不要输出任何其他文字、解释或 Markdown 代码块。" +
        "数组每个元素形如 {\"t\": \"歌曲名\", \"a\": \"艺术家\"}。" +
        "歌曲名和艺术家必须来自候选池，不得编造候选池之外的歌曲。";

    /// <summary>候选池：播放历史加权 + 曲库随机（对应 DailyMixManager.getTopCandidatesForAi）。</summary>
    private static List<Track> BuildCandidatePool(int limit)
    {
        var library = LibraryService.Instance;
        var history = HistoryService.Instance;

        var scored = new Dictionary<string, int>();
        foreach (var entry in history.Entries.Take(500))
        {
            scored[entry.Path] = scored.GetValueOrDefault(entry.Path) + 1;
        }

        var pool = new List<Track>();
        var random = new Random($"{DateTime.Today:yyyyMMdd}".GetHashCode());

        // 历史加权高的优先 + 少量随机补充
        var byPath = library.Tracks.ToDictionary(t => t.Path, t => t);
        foreach (var kv in scored.OrderByDescending(kv => kv.Value).Take(limit / 2))
        {
            if (byPath.TryGetValue(kv.Key, out var t)) pool.Add(t);
        }

        var rest = library.Tracks.Where(t => !pool.Contains(t)).OrderBy(_ => random.Next()).ToList();
        pool.AddRange(rest.Take(limit - pool.Count));
        return pool;
    }

    /// <summary>用户听歌画像（对应 UserProfileDigestGenerator）。</summary>
    private static string BuildUserProfileDigest()
    {
        var history = HistoryService.Instance;
        var topArtists = history.TopArtists(10).Select(a => a.Artist).Where(x => x.Length > 0);
        var topGenres = LibraryService.Instance.Tracks
            .Where(t => !string.IsNullOrEmpty(t.Genre))
            .GroupBy(t => t.Genre)
            .OrderByDescending(g => g.Count())
            .Take(5)
            .Select(g => g.Key);

        var parts = new List<string>();
        var artists = string.Join("、", topArtists);
        if (artists.Length > 0) parts.Add($"常听艺术家：{artists}");
        var genres = string.Join("、", topGenres);
        if (genres.Length > 0) parts.Add("偏好流派：" + genres);
        parts.Add($"总播放 {history.TotalPlays} 次");
        return parts.Count > 0 ? string.Join("；", parts) : "暂无收听历史，从候选池随机推荐。";
    }

    private static string BuildPrompt(string digest, int targetCount, List<Track> candidates)
    {
        var pool = string.Join("\n", candidates.Take(100).Select(t =>
            $"{{\"t\": \"{Escape(t.Title)}\", \"a\": \"{Escape(string.IsNullOrEmpty(t.Artist) ? "未知艺术家" : t.Artist)}\", \"g\": \"{Escape(t.Genre)}\"}}"));

        return $"""
            <request>
              <query>根据我的听歌偏好生成 {targetCount} 首歌的歌单，兼顾我常听的和我可能喜欢的</query>
              <user_profile>{digest}</user_profile>
              <target_length>{targetCount}</target_length>
              <candidate_pool>
            {pool}
              </candidate_pool>
            </request>
            只输出 JSON 数组。
            """;
    }

    /// <summary>从模型回复清洗出 (title, artist) 列表（对应 AiResponseCleaner）。</summary>
    private static List<(string Title, string Artist)> ExtractSongTitles(string reply)
    {
        var result = new List<(string, string)>();
        var start = reply.IndexOf('[');
        var end = reply.LastIndexOf(']');
        if (start < 0 || end <= start) return result;

        var json = reply.Substring(start, end - start + 1);
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var t = item.TryGetProperty("t", out var te) && te.ValueKind == JsonValueKind.String ? te.GetString() ?? "" : "";
                var a = item.TryGetProperty("a", out var ae) && ae.ValueKind == JsonValueKind.String ? ae.GetString() ?? "" : "";
                if (t.Length > 0) result.Add((t, a));
            }
        }
        catch
        {
            // JSON 不合法则忽略
        }
        return result;
    }

    private static Track? MatchTrack(IReadOnlyList<Track> tracks, string title, string artist)
    {
        title = title.Trim();
        artist = artist.Trim();
        return tracks.FirstOrDefault(t => t.Title.Equals(title, StringComparison.OrdinalIgnoreCase)
                        && (artist.Length == 0 || t.Artist.Contains(artist, StringComparison.OrdinalIgnoreCase)))
            ?? tracks.FirstOrDefault(t => t.Title.Equals(title, StringComparison.OrdinalIgnoreCase))
            ?? tracks.FirstOrDefault(t => t.Title.Contains(title, StringComparison.OrdinalIgnoreCase)
                        && artist.Length > 0 && t.Artist.Contains(artist, StringComparison.OrdinalIgnoreCase));
    }

    private static string Escape(string s) => s.Replace("\"", "\\\"").Replace("\n", " ");
}
