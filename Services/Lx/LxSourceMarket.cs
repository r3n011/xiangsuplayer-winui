using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace PixelPlayer.WinUI.Services.Lx;

/// <summary>
/// 像素音源市场（对齐参考项目 SourceMarketRepository）：
/// 从 GitHub 仓库 guoyue2010/lxmusic- 拉取 releases，下载 zip 音源包并提取 .js 安装。
/// GitHub 直链走镜像加速，末尾官方地址兜底。
/// </summary>
public sealed class LxSourceMarket
{
    public static LxSourceMarket Instance { get; } = new();

    private const string RepoOwner = "guoyue2010";
    private const string RepoName = "lxmusic-";
    private const int MaxEntryBytes = 4 * 1024 * 1024;

    private static readonly string[] MirrorPrefixes =
    {
        "https://ghproxy.net/",
        "https://mirror.ghproxy.com/",
        "https://gh-proxy.com/",
        "https://ghproxy.homeboyc.cn/",
        "https://github.akams.cn/",
    };

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    private LxSourceMarket() { }

    public sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("published_at")] public string? PublishedAt { get; set; }
        [JsonPropertyName("assets")] public List<GitHubAsset> Assets { get; set; } = new();
    }

    public sealed class GitHubAsset
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("browser_download_url")] public string BrowserDownloadUrl { get; set; } = "";
    }

    /// <summary>拉取音源仓库的全部 Release（最新在前）。</summary>
    public async Task<List<GitHubRelease>?> FetchReleasesAsync()
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases?per_page=100");
            req.Headers.Add("Accept", "application/vnd.github.v3+json");
            req.Headers.UserAgent.ParseAdd("PixelPlayer.WinUI/1.0");
            using var resp = await _http.SendAsync(req);
            if (resp.StatusCode != System.Net.HttpStatusCode.OK) return null;
            var body = await resp.Content.ReadAsStringAsync();
            var releases = JsonSerializer.Deserialize<List<GitHubRelease>>(body, _json);
            return releases;
        }
        catch (Exception ex)
        {
            App.LogCrash("SourceMarket.fetchReleases", ex);
            return null;
        }
    }

    public GitHubAsset? ZipAssetOf(GitHubRelease release) =>
        release.Assets.FirstOrDefault(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

    /// <summary>下载 release 的 zip（镜像优先 + 官方直链兜底），校验 ZIP 魔数，返回本地缓存路径。</summary>
    public async Task<(bool Ok, string? Path, string? Error)> DownloadZipAsync(GitHubAsset asset, string tag, Action<float>? onProgress = null)
    {
        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PixelPlayer.WinUI", "source_market");
        Directory.CreateDirectory(cacheDir);
        var target = Path.Combine(cacheDir, $"{Sanitize(tag)}.zip");

        var candidates = asset.BrowserDownloadUrl.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)
            ? MirrorPrefixes.Select(p => p + asset.BrowserDownloadUrl).Concat(new[] { asset.BrowserDownloadUrl })
            : new[] { asset.BrowserDownloadUrl };

        string? lastError = null;
        foreach (var (candidateUrl, idx) in candidates.Select((c, i) => (c, i)))
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, candidateUrl);
                req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/120.0.0.0");
                req.Headers.Add("Accept", "application/octet-stream,*/*");
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                if ((int)resp.StatusCode is < 200 or > 299)
                    throw new Exception($"下载失败: HTTP {(int)resp.StatusCode}");

                var total = resp.Content.Headers.ContentLength ?? 0;
                await using var input = await resp.Content.ReadAsStreamAsync();
                await using var output = new FileStream(target, FileMode.Create, FileAccess.Write);
                var buf = new byte[16 * 1024];
                long done = 0;
                int read;
                while ((read = await input.ReadAsync(buf)) > 0)
                {
                    await output.WriteAsync(buf, 0, read);
                    done += read;
                    if (total > 0) onProgress?.Invoke((float)done / total);
                }

                // 校验 ZIP 魔数 PK\x03\x04
                await using var check = new FileStream(target, FileMode.Open, FileAccess.Read);
                var header = new byte[4];
                var ok = await check.ReadAsync(header) == 4 && header[0] == 'P' && header[1] == 'K' && header[2] == 3 && header[3] == 4;
                if (!ok) throw new Exception("下载内容不是有效 zip");
                return (true, target, null);
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                try { if (File.Exists(target)) File.Delete(target); } catch { }
            }
        }
        return (false, null, lastError ?? "下载失败");
    }

    /// <summary>解析 zip 内所有 .js 条目。只取 basename 防目录穿越；单条目上限 4MB；同名去重。</summary>
    public Task<List<MarketJsEntry>> InspectZipAsync(string zipFile) => Task.Run(() =>
    {
        var result = new Dictionary<string, MarketJsEntry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var archive = ZipFile.OpenRead(zipFile);
            foreach (var entry in archive.Entries)
            {
                if (!entry.Name.EndsWith(".js", StringComparison.OrdinalIgnoreCase) || entry.Length == 0) continue;
                if (entry.Length > MaxEntryBytes) continue;
                var baseName = entry.Name.Replace('\\', '/').Split('/').Last();
                if (string.IsNullOrWhiteSpace(baseName) || baseName is "." or "..") continue;
                if (result.ContainsKey(baseName)) continue;

                using var stream = entry.Open();
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                var bytes = ms.ToArray();
                if (bytes.Length > 0)
                    result[baseName] = new MarketJsEntry(baseName, bytes.Length, bytes);
            }
        }
        catch (Exception ex)
        {
            App.LogCrash("SourceMarket.inspectZip", ex);
        }
        return result.Values.ToList();
    });

    private static string Sanitize(string tag)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) tag = tag.Replace(c, '_');
        return tag;
    }

    public sealed record MarketJsEntry(string FileName, long Size, byte[] Bytes);
}
