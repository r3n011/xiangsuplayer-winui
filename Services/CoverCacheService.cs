using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Imaging;
using PixelPlayer.WinUI.Models;

namespace PixelPlayer.WinUI.Services;

/// <summary>
/// 封面加载服务（对齐参照项目 SmartImage + Coil 缓存思路）：
/// - 本地曲目：直接用曲库扫描出的缩略图
/// - 在线曲目：PicUrl 下载到磁盘缓存 covers/{sha256}.jpg + 内存缓存，飞行中去重
/// </summary>
public sealed class CoverCacheService
{
    // 注意：静态字段初始化器按声明顺序执行，Instance 必须放在依赖的字段之后。
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PixelPlayer.WinUI", "covers");

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(12) };

    public static CoverCacheService Instance { get; } = new();

    private readonly ConcurrentDictionary<string, BitmapImage> _memory = new();
    private readonly ConcurrentDictionary<string, Task<byte[]?>> _inFlight = new();

    private CoverCacheService()
    {
        try { Directory.CreateDirectory(CacheDir); } catch { }
    }

    /// <summary>取曲目封面 BitmapImage（UI 线程调用；内部异步补图后再回调刷新）。</summary>
    public BitmapImage? GetImmediate(Track track) =>
        track.CoverThumb ?? LoadLocal(track);

    /// <summary>异步获取封面：在线曲目下载缓存后返回，失败返回 null。</summary>
    public async Task<BitmapImage?> GetAsync(Track track, CancellationToken ct = default)
    {
        if (track.CoverThumb is not null) return track.CoverThumb;
        if (track.CoverPath is { Length: > 0 } && File.Exists(track.CoverPath))
        {
            return LoadLocal(track);
        }
        if (string.IsNullOrEmpty(track.PicUrl)) return null;

        var bytes = await LoadOnlineBytesAsync(track.PicUrl!, ct);
        if (bytes is null || bytes.Length == 0) return null;
        try
        {
            var img = new BitmapImage();
            using var ms = new MemoryStream(bytes);
            await img.SetSourceAsync(ms.AsRandomAccessStream());
            track.CoverThumb = img;
            return img;
        }
        catch
        {
            return null;
        }
    }

    private BitmapImage? LoadLocal(Track track)
    {
        if (track.CoverThumb is not null) return track.CoverThumb;
        track.CoverThumb = LibraryService.CreateThumb(track.CoverPath, 160);
        return track.CoverThumb;
    }

    private async Task<byte[]?> LoadOnlineBytesAsync(string url, CancellationToken ct)
    {
        var key = url;
        if (_memory.ContainsKey(key) && _memory[key] is not null)
        {
            return _memory.TryGetValue(key, out var _) ? CachedBytes(key) : null;
        }

        var task = _inFlight.GetOrAdd(key, DownloadAndCacheAsync);
        try
        {
            return await task.WaitAsync(ct);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (task.IsCompleted) _inFlight.TryRemove(key, out _);
        }
    }

    /// <summary>下载封面字节，写磁盘缓存并放入内存字典（内存里只存路径标记）。</summary>
    private async Task<byte[]?> DownloadAndCacheAsync(string url)
    {
        try
        {
            var path = Path.Combine(CacheDir, Sha256(url) + ".img");
            if (File.Exists(path) && new FileInfo(path).Length > 0)
            {
                var cached = await File.ReadAllBytesAsync(path);
                Remember(url, cached);
                return cached;
            }
            var bytes = await _http.GetByteArrayAsync(url);
            if (bytes.Length == 0) return null;
            await File.WriteAllBytesAsync(path, bytes);
            Remember(url, bytes);
            return bytes;
        }
        catch
        {
            return null;
        }
    }

    private static readonly ConcurrentDictionary<string, byte[]> _bytes = new();
    private static void Remember(string url, byte[] bytes) => _bytes[url] = bytes;
    private static byte[]? CachedBytes(string url) => _bytes.TryGetValue(url, out var b) ? b : null;

    private static string Sha256(string text)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes)[..24];
    }

    /// <summary>清空封面缓存（设置页入口）。</summary>
    public void ClearCache()
    {
        _bytes.Clear();
        try { foreach (var f in Directory.GetFiles(CacheDir)) File.Delete(f); } catch { }
    }
}
