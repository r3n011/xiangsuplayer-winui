using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using PixelPlayer.WinUI.Models;
using PixelPlayer.WinUI.Services.Online;

namespace PixelPlayer.WinUI.Services;

/// <summary>
/// 下载管理（对应参照项目 MusicDownloadService）：
/// 队列 + 并发上限 + 断点续传 + 进度通知；完成后写标签、内嵌封面与 .lrc 歌词侧车。
/// </summary>
public sealed class DownloadService
{
    public static DownloadService Instance { get; } = new();

    public sealed class DownloadTask
    {
        public Track Track { get; init; } = null!;
        public DownloadStatus Status { get; set; } = DownloadStatus.Pending;
        public double Progress { get; set; }         // 0..100
        public string FilePath { get; set; } = "";
        public string Error { get; set; } = "";
        internal long Received { get; set; }
        internal long Total { get; set; }
        internal CancellationTokenSource Cts { get; } = new();
    }

    public enum DownloadStatus { Pending, Downloading, Done, Failed, Cancelled }

    public ObservableCollection<DownloadTask> Tasks { get; } = new();

    public event EventHandler? Changed;

    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly Queue<DownloadTask> _queue = new();
    private readonly object _lock = new();
    private int _activeCount;
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };

    static DownloadService()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/120 PixelPlayer/1.0");
    }

    private int MaxConcurrent => Math.Clamp(SettingsService.Instance.Settings.DownloadConcurrent, 1, 6);

    public string DownloadFolder
    {
        get
        {
            var folder = SettingsService.Instance.Settings.DownloadFolder;
            if (string.IsNullOrWhiteSpace(folder))
            {
                folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
                    "PixelPlayer");
            }
            return folder;
        }
    }

    /// <summary>入队下载一首曲目（在线曲目自动解析直链；本地曲目复制）。</summary>
    public void Enqueue(Track track)
    {
        var task = new DownloadTask { Track = track };
        lock (_lock)
        {
            _queue.Enqueue(task);
            Tasks.Insert(0, task);
        }
        Changed?.Invoke(this, EventArgs.Empty);
        Pump();
    }

    public void Cancel(DownloadTask task)
    {
        task.Cts.Cancel();
        SetStatus(task, DownloadStatus.Cancelled);
    }

    public void ClearFinished()
    {
        foreach (var t in Tasks.Where(t => t.Status is DownloadStatus.Done or DownloadStatus.Failed or DownloadStatus.Cancelled).ToList())
        {
            Tasks.Remove(t);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Pump()
    {
        lock (_lock)
        {
            while (_activeCount < MaxConcurrent && _queue.Count > 0)
            {
                var task = _queue.Dequeue();
                if (task.Status == DownloadStatus.Cancelled) continue;
                _activeCount++;
                _ = Task.Run(() => RunAsync(task));
            }
        }
    }

    private async Task RunAsync(DownloadTask task)
    {
        try
        {
            SetStatus(task, DownloadStatus.Downloading);

            var track = task.Track;
            string sourceUrl;
            if (track.IsOnline)
            {
                sourceUrl = await OnlineSourceRegistry.ResolveStreamUrlAsync(track, task.Cts.Token) ?? "";
                if (sourceUrl.Length == 0) throw new InvalidOperationException("无法解析音源直链");
            }
            else
            {
                sourceUrl = track.Path;
            }

            var fileName = Sanitize($"{(track.Artist.Length > 0 ? track.Artist + " - " : "")}{track.Title}");
            var ext = DecideExtension(sourceUrl);
            var folder = DownloadFolder;
            Directory.CreateDirectory(folder);
            var finalPath = UniquePath(Path.Combine(folder, fileName + ext));
            var tempPath = finalPath + ".part";
            task.FilePath = finalPath;

            // 断点续传
            long resumeFrom = 0;
            if (File.Exists(tempPath))
            {
                resumeFrom = new FileInfo(tempPath).Length;
                task.Received = resumeFrom;
            }

            using var req = new HttpRequestMessage(HttpMethod.Get, sourceUrl);
            if (resumeFrom > 0 && track.IsOnline)
            {
                req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(resumeFrom, null);
            }

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, task.Cts.Token);
            resp.EnsureSuccessStatusCode();

            task.Total = resp.Content.Headers.ContentLength ?? 0;
            if (resumeFrom > 0 && !track.IsOnline)
            {
                File.Copy(track.Path, tempPath, overwrite: true);
            }
            else
            {
                await using (var output = new FileStream(tempPath, resumeFrom > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write))
                await using (var input = await resp.Content.ReadAsStreamAsync(task.Cts.Token))
                {
                    var buffer = new byte[64 * 1024];
                    int read;
                    while ((read = await input.ReadAsync(buffer, task.Cts.Token)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), task.Cts.Token);
                        task.Received += read;
                        SetProgress(task, task.Total > 0 ? 100.0 * task.Received / task.Total : 0);
                    }
                }
            }

            // 移动到最终位置
            File.Move(tempPath, finalPath, overwrite: true);
            task.FilePath = finalPath;

            // 补标签 + 内嵌封面 + .lrc 侧车（对应 writeTags / writeLrcFile）
            _ = TagEditorService.ApplyDownloadedTagsAsync(track, finalPath);
            try
            {
                var lyrics = await LyricsFetchService.FetchAsync(track);
                if (lyrics is { Lrc.Length: > 0 })
                {
                    await File.WriteAllTextAsync(Path.ChangeExtension(finalPath, ".lrc"), lyrics.Lrc, Encoding.UTF8);
                }
            }
            catch { /* 歌词获取失败忽略 */ }

            SetProgress(task, 100);
            SetStatus(task, DownloadStatus.Done);
        }
        catch (OperationCanceledException)
        {
            SetStatus(task, DownloadStatus.Cancelled);
        }
        catch (Exception ex)
        {
            task.Error = ex.Message;
            SetStatus(task, DownloadStatus.Failed);
            App.LogCrash("Download", ex);
        }
        finally
        {
            lock (_lock)
            {
                _activeCount--;
            }
            Pump();
        }
    }

    private void SetStatus(DownloadTask task, DownloadStatus status)
    {
        _dispatcher.TryEnqueue(() =>
        {
            task.Status = status;
            Changed?.Invoke(this, EventArgs.Empty);
        });
    }

    private void SetProgress(DownloadTask task, double progress)
    {
        _dispatcher.TryEnqueue(() =>
        {
            task.Progress = progress;
            Changed?.Invoke(this, EventArgs.Empty);
        });
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        name = name.Trim();
        return name.Length > 150 ? name[..150] : name;
    }

    private static string DecideExtension(string url)
    {
        var clean = url.Split('?')[0].ToLowerInvariant();
        foreach (var ext in new[] { ".mp3", ".flac", ".m4a", ".aac", ".ogg", ".wav", ".opus" })
        {
            if (clean.EndsWith(ext)) return ext;
        }
        if (clean.EndsWith(".m4s")) return ".m4a";
        return ".mp3";
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate) && !File.Exists(candidate + ".part")) return candidate;
        }
        return path;
    }
}
