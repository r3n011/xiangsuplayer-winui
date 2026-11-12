using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Imaging;
using PixelPlayer.WinUI.Models;

namespace PixelPlayer.WinUI.Services;

/// <summary>本地曲库：扫描目录 → 读标签/封面 → 缓存 JSON。支持 mp3/flac/m4a/aac/wav/ogg/wma。</summary>
public sealed class LibraryService
{
    private static readonly string[] Extensions =
    {
        ".mp3", ".flac", ".m4a", ".aac", ".wav", ".ogg", ".opus", ".wma", ".alac", ".aiff", ".ape",
    };

    private static readonly string CacheFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PixelPlayer.WinUI", "library.json");

    public static LibraryService Instance { get; } = new();

    public ObservableCollection<Track> Tracks { get; } = new();
    public ObservableCollection<AlbumGroup> Albums { get; } = new();
    public ObservableCollection<ArtistGroup> Artists { get; } = new();

    /// <summary>曲库内容发生变化（扫描完成 / 缓存载入）时触发，页面据此刷新统计。</summary>
    public event EventHandler? LibraryChanged;

    public bool IsLoaded { get; private set; }

    private Microsoft.UI.Dispatching.DispatcherQueue? _dispatcher;
    private Task? _loadTask;

    private LibraryService() { }

    /// <summary>在 UI 线程上调用一次：ObservableCollection / BitmapImage 只能在 UI 线程操作。</summary>
    public void SetDispatcher(Microsoft.UI.Dispatching.DispatcherQueue dispatcher) => _dispatcher = dispatcher;

    private void RunOnUi(Action action)
    {
        if (_dispatcher is null || _dispatcher.HasThreadAccess) action();
        else _dispatcher.TryEnqueue(() => action());
    }

    public Task LoadCacheAsync() => _loadTask ??= LoadCacheCoreAsync();

    private async Task LoadCacheCoreAsync()
    {
        if (IsLoaded) return;
        try
        {
            if (File.Exists(CacheFile))
            {
                var list = JsonSerializer.Deserialize<List<Track>>(await File.ReadAllTextAsync(CacheFile)) ?? new();
                RunOnUi(() =>
                {
                    foreach (var track in list.Where(t => File.Exists(t.Path)))
                    {
                        Tracks.Add(track);
                    }
                });
            }
        }
        catch { /* 缓存坏了就当空库 */ }

        await RebuildAlbumsAsync();
        IsLoaded = true;
        RunOnUi(() => LibraryChanged?.Invoke(this, EventArgs.Empty));
    }

    public async Task SaveCacheAsync()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CacheFile)!);
            await File.WriteAllTextAsync(CacheFile, JsonSerializer.Serialize(Tracks.ToList()));
        }
        catch { /* 忽略 */ }
    }

    /// <summary>扫描若干目录，progress 回调 (已扫描数, 新增数)。</summary>
    public async Task<(int Added, int Skipped)> ScanAsync(
        IEnumerable<string> folders,
        IProgress<(int Scanned, int Added)>? progress = null,
        CancellationToken token = default)
    {
        var existing = new HashSet<string>(Tracks.Select(t => t.Path), StringComparer.OrdinalIgnoreCase);
        var added = 0;
        var scanned = 0;
        var batch = new List<Track>();

        foreach (var folder in folders.Where(Directory.Exists))
        {
            foreach (var file in EnumerateAudio(folder))
            {
                token.ThrowIfCancellationRequested();
                scanned++;
                if (existing.Contains(file)) continue;

                var track = await Task.Run(() => ReadTrack(file), token);
                if (track is null) continue;

                batch.Add(track);
                existing.Add(file);
                added++;
                if (progress is not null && scanned % 20 == 0) progress.Report((scanned, added));
            }
        }

        foreach (var track in batch)
        {
            var t = track;
            RunOnUi(() => Tracks.Add(t));
        }

        await RebuildAlbumsAsync();
        await SaveCacheAsync();
        progress?.Report((scanned, added));
        RunOnUi(() => LibraryChanged?.Invoke(this, EventArgs.Empty));
        return (added, scanned - added);
    }

    private static IEnumerable<string> EnumerateAudio(string folder)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories);
        }
        catch
        {
            yield break;
        }

        foreach (var file in files)
        {
            if (Extensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
            {
                yield return file;
            }
        }
    }

    private static Track? ReadTrack(string path)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            var tags = file.Tag;
            var title = string.IsNullOrWhiteSpace(tags.Title) ? Path.GetFileNameWithoutExtension(path) : tags.Title;
            var artist = tags.FirstPerformer ?? tags.FirstAlbumArtist ?? "";
            var track = new Track
            {
                Path = path,
                Title = title,
                Artist = artist,
                Album = tags.Album ?? "",
                Genre = tags.FirstGenre ?? "",
                TrackNumber = tags.Track,
                Year = tags.Year,
                DurationSeconds = file.Properties.Duration.TotalSeconds,
                CoverPath = ExtractCover(tags, path),
            };
            return track;
        }
        catch
        {
            return null;   // 无法解析的文件直接跳过
        }
    }

    private static string? ExtractCover(TagLib.Tag tags, string audioPath)
    {
        var picture = tags.Pictures?.FirstOrDefault();
        if (picture is null || picture.Data is null || picture.Data.Count == 0) return null;

        var hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(audioPath)))[..16];
        var file = Path.Combine(SettingsService.CoverCacheDir, hash + ".png");
        if (File.Exists(file)) return file;

        try
        {
            File.WriteAllBytes(file, picture.Data.Data);
            return file;
        }
        catch
        {
            return null;
        }
    }

    public Task RebuildAlbumsAsync() => RebuildAlbumsCoreAsync();

    private async Task RebuildAlbumsCoreAsync()
    {
        // 分组计算放后台，集合与缩略图（BitmapImage）必须在 UI 线程生成
        var snapshot = Tracks.ToList();
        var groups = snapshot
            .GroupBy(t => (Name: string.IsNullOrWhiteSpace(t.Album) ? "未知专辑" : t.Album,
                           Artist: string.IsNullOrWhiteSpace(t.Artist) ? "" : t.Artist))
            .Select(g => new
            {
                Name = g.Key.Name,
                Artist = g.First().Artist,
                Tracks = g.OrderBy(t => t.TrackNumber).ToList(),
            })
            .OrderBy(a => a.Name)
            .ToList();

        var artistGroups = snapshot
            .GroupBy(t => string.IsNullOrWhiteSpace(t.Artist) ? "未知艺术家" : t.Artist)
            .Select(g => new
            {
                Name = g.Key,
                Tracks = g.OrderBy(t => t.Album).ThenBy(t => t.TrackNumber).ToList(),
            })
            .OrderBy(a => a.Name)
            .ToList();

        var tcs = new TaskCompletionSource();

        RunOnUi(() =>
        {
            try
            {
                Albums.Clear();
                foreach (var g in groups)
                {
                    Albums.Add(new AlbumGroup
                    {
                        Name = g.Name,
                        Artist = g.Artist,
                        Tracks = g.Tracks,
                        CoverThumb = CreateThumb(g.Tracks.FirstOrDefault()?.CoverPath, 180),
                    });
                }

                Artists.Clear();
                foreach (var a in artistGroups)
                {
                    var albums = a.Tracks.Select(t => t.Album).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();
                    Artists.Add(new ArtistGroup
                    {
                        Name = a.Name,
                        AlbumCount = $"{albums.Count} 张专辑",
                        Tracks = a.Tracks,
                        CoverThumb = CreateThumb(a.Tracks.FirstOrDefault()?.CoverPath, 96),
                    });
                }

                tcs.SetResult();
            }
            catch (Exception ex)
            {
                App.LogCrash("RebuildAlbums", ex);
                tcs.SetException(ex);
            }
        });

        await tcs.Task;
    }

    /// <summary>全部流派（搜索页分类用）。</summary>
    public IReadOnlyList<string> GetGenres()
    {
        return Tracks
            .Select(t => t.Genre)
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Distinct()
            .OrderBy(g => g)
            .ToList();
    }

    /// <summary>每日推荐：按日期播种的确定性随机选曲（对应参照项目 DailyRecommend）。</summary>
    public List<Track> DailyRecommend(int count = 20)
    {
        var random = new Random($"{DateTime.Today:yyyyMMdd}".GetHashCode());
        return Tracks.OrderBy(_ => random.Next()).Take(count).ToList();
    }

    /// <summary>最多播放（来自播放历史）。</summary>
    public List<(Track Track, int Count)> MostPlayed(int limit = 10) =>
        HistoryService.Instance.TopTracks(limit);

    /// <summary>音乐文件夹下的一级子文件夹（媒体库「文件夹」标签）。</summary>
    public List<(string Path, string Name, int TrackCount)> GetFolderNodes()
    {
        var result = new List<(string, string, int)>();
        foreach (var root in SettingsService.Instance.Settings.MusicFolders.Where(Directory.Exists))
        {
            result.Add((root, Path.GetFileName(root.TrimEnd('\\', '/')), CountTracks(root)));
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(root))
                {
                    result.Add((dir, Path.GetFileName(dir), CountTracks(dir)));
                }
            }
            catch { /* 无权限时跳过 */ }
        }
        return result;
    }

    public List<Track> GetTracksInFolder(string folder) =>
        Tracks.Where(t => t.Path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)).ToList();

    private static int CountTracks(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                .Count(f => Extensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
        }
        catch
        {
            return 0;
        }
    }

    public static BitmapImage? CreateThumb(string? coverPath, int decodeWidth)
    {
        if (string.IsNullOrEmpty(coverPath) || !File.Exists(coverPath)) return null;
        try
        {
            var bitmap = new BitmapImage { DecodePixelWidth = decodeWidth };
            bitmap.UriSource = new Uri(coverPath);
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    public IReadOnlyList<Track> Search(string query, int limit = 200)
    {
        if (string.IsNullOrWhiteSpace(query)) return Tracks.Take(limit).ToList();
        var q = query.Trim();
        return Tracks
            .Where(t => t.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                     || t.Artist.Contains(q, StringComparison.OrdinalIgnoreCase)
                     || t.Album.Contains(q, StringComparison.OrdinalIgnoreCase)
                     || t.Genre.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Take(limit)
            .ToList();
    }
}
