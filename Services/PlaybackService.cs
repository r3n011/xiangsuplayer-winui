using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;
using PixelPlayer.WinUI.Models;
using PixelPlayer.WinUI.Services.Online;

namespace PixelPlayer.WinUI.Services;

public enum RepeatMode
{
    Off = 0,
    All = 1,
    One = 2,
}

/// <summary>
/// 播放引擎：双 MediaPlayer（A/B 交替）实现交叉淡入淡出与无缝播放，
/// 队列 + 随机/循环 + SMTC + 在线音源解析 + ReplayGain。
/// 对应参照项目 DualPlayerEngine + TransitionController。
/// </summary>
public sealed class PlaybackService
{
    public static PlaybackService Instance { get; } = new();

    private readonly MediaPlayer _playerA = new();
    private readonly MediaPlayer _playerB = new();
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();

    private bool _useB;                     // 当前活动的播放器
    private MediaPlayer Player => _useB ? _playerB : _playerA;
    private MediaPlayer Inactive => _useB ? _playerA : _playerB;

    public event EventHandler? TrackChanged;
    public event EventHandler? StateChanged;
    public event EventHandler<TimeSpan>? PositionChanged;

    public List<Track> Queue { get; private set; } = new();
    public int Index { get; private set; } = -1;
    public Track? Current => Index >= 0 && Index < Queue.Count ? Queue[Index] : null;

    public bool IsPlaying => Player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
    public TimeSpan Position => Player.PlaybackSession.Position;
    public TimeSpan Duration => Player.PlaybackSession.NaturalDuration;

    public bool Shuffle { get; set; }
    public RepeatMode Repeat { get; set; } = RepeatMode.All;

    /// <summary>睡眠定时器到点事件（UI 提示用）。</summary>
    public event EventHandler? SleepTimerFired;

    private DispatcherQueueTimer? _sleepTimer;
    private DateTime? _sleepTimerEnd;

    /// <summary>睡眠定时器结束时间（未启用为 null）。</summary>
    public DateTime? SleepTimerEnd => _sleepTimerEnd;

    /// <summary>播放倍速 0.25–4.0。</summary>
    public double PlaybackSpeed
    {
        get => SettingsService.Instance.Settings.PlaybackSpeed;
        set
        {
            var v = Math.Clamp(value, 0.25, 4.0);
            SettingsService.Instance.Settings.PlaybackSpeed = v;
            try
            {
                _playerA.PlaybackSession.PlaybackRate = v;
                _playerB.PlaybackSession.PlaybackRate = v;
            }
            catch (Exception ex)
            {
                App.LogCrash("PlaybackRate", ex);
            }
            SettingsService.Instance.SaveSettings();
        }
    }

    public void StartSleepTimer(int minutes)
    {
        CancelSleepTimer();
        if (minutes <= 0) return;

        _sleepTimerEnd = DateTime.Now.AddMinutes(minutes);
        _sleepTimer = _dispatcher.CreateTimer();
        _sleepTimer.Interval = TimeSpan.FromMinutes(minutes);
        _sleepTimer.IsRepeating = false;
        _sleepTimer.Tick += (_, _) =>
        {
            Pause();
            _sleepTimerEnd = null;
            SleepTimerFired?.Invoke(this, EventArgs.Empty);
        };
        _sleepTimer.Start();
    }

    public void CancelSleepTimer()
    {
        _sleepTimer?.Stop();
        _sleepTimer = null;
        _sleepTimerEnd = null;
    }

    /* ---------------- 音量 / ReplayGain ---------------- */

    private double _baseVolume;           // 用户音量 0..1
    private double _rgFactor = 1.0;       // ReplayGain 系数

    public double Volume
    {
        get => _baseVolume;
        set
        {
            _baseVolume = Math.Clamp(value, 0, 1);
            SettingsService.Instance.Settings.Volume = _baseVolume;
            if (!_transitioning) ApplyVolume();
        }
    }

    private void ApplyVolume()
    {
        var v = Math.Clamp(_baseVolume * _rgFactor, 0, 1);
        Player.Volume = v;
        Inactive.Volume = 0;
    }

    /// <summary>本地曲目读取 ReplayGain 标签并应用（对应 ReplayGainManager）。</summary>
    private void ApplyReplayGain(Track track)
    {
        _rgFactor = 1.0;
        if (!SettingsService.Instance.Settings.ReplayGainEnabled || track.IsOnline) return;

        try
        {
            var gain = TagEditorService.ReadTrackGain(track.Path);
            if (gain is not null)
            {
                var preamp = SettingsService.Instance.Settings.ReplayGainPreamp;
                _rgFactor = Math.Clamp(Math.Pow(10, (gain.Value + preamp) / 20.0), 0.1, 2.5);
            }
        }
        catch (Exception ex)
        {
            App.LogCrash("ReplayGain", ex);
        }
    }

    /* ---------------- 构造 ---------------- */

    private int _loadGeneration;
    private int _consecutiveFailures;
    private bool _transitioning;          // 交叉淡入淡出进行中
    private DispatcherQueueTimer? _fadeTimer;

    private PlaybackService()
    {
        _baseVolume = SettingsService.Instance.Settings.Volume;
        Shuffle = SettingsService.Instance.Settings.Shuffle;
        Repeat = (RepeatMode)SettingsService.Instance.Settings.RepeatMode;
        try
        {
            var rate = SettingsService.Instance.Settings.PlaybackSpeed;
            _playerA.PlaybackSession.PlaybackRate = rate;
            _playerB.PlaybackSession.PlaybackRate = rate;
        }
        catch { /* 忽略 */ }

        foreach (var p in new[] { _playerA, _playerB })
        {
            var pl = p;
            p.AutoPlay = false;
            p.MediaEnded += (_, _) => _dispatcher.TryEnqueue(() =>
            {
                if (!ReferenceEquals(pl, Player)) return; // 旧播放器的 Ended 忽略
                OnMediaEnded();
            });
            p.MediaFailed += (_, args) => _dispatcher.TryEnqueue(() =>
            {
                if (!ReferenceEquals(pl, Player)) return;
                App.LogCrash("MediaFailed", new InvalidOperationException($"{args.Error}: {args.ErrorMessage}"));
                SkipUnavailable();
            });
            p.PlaybackSession.PlaybackStateChanged += (_, _) => _dispatcher.TryEnqueue(() =>
            {
                if (!ReferenceEquals(pl, Player)) return;
                if (Player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
                {
                    _consecutiveFailures = 0;
                }
                UpdateSmtcStatus();
                StateChanged?.Invoke(this, EventArgs.Empty);
            });
            p.PlaybackSession.PositionChanged += (session, _) => _dispatcher.TryEnqueue(() =>
            {
                if (!ReferenceEquals(pl, Player)) return;
                var pos = session.Position;
                PositionChanged?.Invoke(this, pos);
                CheckTransition(pos);
            });

            // SMTC：两个播放器只挂一次，切换活动者时只改 IsEnabled
            try
            {
                var smtc = p.SystemMediaTransportControls;
                smtc.IsEnabled = true;
                smtc.IsPlayEnabled = true;
                smtc.IsPauseEnabled = true;
                smtc.IsNextEnabled = true;
                smtc.IsPreviousEnabled = true;
                smtc.ButtonPressed += (_, args) =>
                {
                    _dispatcher.TryEnqueue(() =>
                    {
                        if (!ReferenceEquals(pl, Player)) return;
                        switch (args.Button)
                        {
                            case SystemMediaTransportControlsButton.Play: Resume(); break;
                            case SystemMediaTransportControlsButton.Pause: Pause(); break;
                            case SystemMediaTransportControlsButton.Next: Next(); break;
                            case SystemMediaTransportControlsButton.Previous: Previous(); break;
                        }
                    });
                };
            }
            catch (Exception ex)
            {
                App.LogCrash("Smtc", ex);
            }
        }

        ApplyVolume();
    }

    /* ---------------- 队列 ---------------- */

    public void PlayQueue(IEnumerable<Track> tracks, int startIndex = 0)
    {
        var list = tracks.ToList();
        if (list.Count == 0) return;

        Queue = list;
        Index = Math.Clamp(startIndex, 0, list.Count - 1);
        _ = LoadCurrentAsync(autoPlay: true);
    }

    public void PlayTrack(Track track, IEnumerable<Track>? queue = null)
    {
        if (queue is not null)
        {
            PlayQueue(queue, queue.ToList().IndexOf(track));
            return;
        }

        var list = Queue.Count > 0 ? Queue : new List<Track> { track };
        if (!list.Contains(track)) list = new List<Track> { track };
        Queue = list;
        Index = list.IndexOf(track);
        _ = LoadCurrentAsync(autoPlay: true);
    }

    /// <summary>解析直链；在线曲目走统一解析链路（SourceResolver：源顺序 × 音质链 → 跨源换源）。返回 null 表示不可用。</summary>
    private async Task<string?> ResolveSourceAsync(Track track, int gen)
    {
        if (!track.IsOnline) return track.Path;

        string url;
        try
        {
            var outcome = await SourceResolver.ResolveAsync(track);
            url = outcome.Success ? outcome.Url ?? "" : "";
            App.LogCrash("Resolve", new Exception(
                $"{track.Title} [{track.SourceId}/{track.OnlineKey}] -> success={outcome.Success} " +
                $"kind={outcome.Kind} src={outcome.SourceId} switched={outcome.Switched} " +
                $"url={(string.IsNullOrEmpty(url) ? "-" : url[..Math.Min(90, url.Length)])} msg={outcome.Message}"));
            if (!outcome.Success && !string.IsNullOrEmpty(outcome.Message))
            {
                System.Diagnostics.Debug.WriteLine($"[SourceResolver] {track.Title}: {outcome.Kind} {outcome.Message}");
            }
        }
        catch (Exception ex)
        {
            App.LogCrash("ResolveStreamUrl", ex);
            url = "";
        }

        return gen != _loadGeneration ? "" : (string.IsNullOrEmpty(url) ? null : url);
    }

    private async Task LoadCurrentAsync(bool autoPlay)
    {
        var track = Current;
        if (track is null) return;

        var gen = ++_loadGeneration;
        ResetInactivePlayer();

        // 先通知 UI（立即显示曲名），再异步解析在线直链
        ApplyReplayGain(track);
        if (!_transitioning) ApplyVolume();
        UpdateSmtcMetadata(track);
        TrackChanged?.Invoke(this, EventArgs.Empty);
        StateChanged?.Invoke(this, EventArgs.Empty);

        var sourceUri = await ResolveSourceAsync(track, gen);
        if (gen != _loadGeneration) return;
        if (sourceUri is null)
        {
            SkipUnavailable();
            return;
        }
        track.StreamUrl = sourceUri;

        try
        {
            Player.Source = MediaSource.CreateFromUri(new Uri(sourceUri));
        }
        catch (Exception ex)
        {
            App.LogCrash("LoadSource", ex);
            if (gen == _loadGeneration) SkipUnavailable();
            return;
        }

        if (gen != _loadGeneration) return;
        if (autoPlay) Player.Play();

        if (!track.IsOnline)
        {
            var settings = SettingsService.Instance.Settings;
            settings.LastTrackPath = track.Path;
            settings.RecentTracks.Remove(track.Path);
            settings.RecentTracks.Insert(0, track.Path);
            if (settings.RecentTracks.Count > 30) settings.RecentTracks.RemoveRange(30, settings.RecentTracks.Count - 30);
            SettingsService.Instance.SaveSettings();

            _ = HistoryService.Instance.LogAsync(track);
        }
        else if (Queue.Count > 0)
        {
            // Navidrome scrobble 上报
            var src = OnlineSourceRegistry.Resolve(track.SourceId);
            if (src is NavidromeSource ns) _ = ns.ScrobbleAsync(track);
        }
    }

    /// <summary>重置非活动播放器，防止残留音频。</summary>
    private void ResetInactivePlayer()
    {
        try
        {
            if (Inactive.Source is not null)
            {
                Inactive.Pause();
                Inactive.Source = null;
            }
            Inactive.Volume = 0;
        }
        catch { /* 忽略 */ }
    }

    /// <summary>曲目不可用（无版权/文件缺失）时自动跳下一首；连跳上限防死循环。</summary>
    private void SkipUnavailable()
    {
        App.LogCrash("SkipUnavailable", new Exception($"跳过 {Current?.Title}（不可用）failures={_consecutiveFailures + 1}"));
        if (Queue.Count <= 1 || ++_consecutiveFailures > Queue.Count)
        {
            Pause();
            return;
        }

        Next();
    }

    /* ---------------- 交叉淡入淡出 / 无缝播放 ---------------- */

    /// <summary>过渡时长（秒）：交叉淡入淡出启用时为其秒数，否则 -1（曲尾自然切换）。</summary>
    private double TransitionSpan()
    {
        var s = SettingsService.Instance.Settings;
        if (s.CrossfadeEnabled && s.CrossfadeSeconds > 0.2) return Math.Clamp(s.CrossfadeSeconds, 0.5, 12);
        return -1;
    }

    private int NextTransitionIndex()
    {
        if (Queue.Count <= 1) return -1;
        if (Repeat == RepeatMode.Off && Index >= Queue.Count - 1) return -1;
        if (Shuffle) return PickRandomIndex();
        return (Index + 1) % Queue.Count;
    }

    private void CheckTransition(TimeSpan position)
    {
        var span = TransitionSpan();
        if (span < 0 || _transitioning) return;
        var dur = Duration;
        if (dur <= TimeSpan.Zero) return;
        if (position.TotalSeconds < dur.TotalSeconds - span) return;

        var nextIndex = NextTransitionIndex();
        if (nextIndex < 0) return;

        _ = StartCrossfadeAsync(nextIndex, span);
    }

    /// <summary>交叉淡入淡出到下一曲（对应 TransitionController 的双播放器切换）。</summary>
    private async Task StartCrossfadeAsync(int nextIndex, double spanSeconds)
    {
        _transitioning = true;
        Index = nextIndex;
        var track = Current;
        if (track is null)
        {
            _transitioning = false;
            return;
        }

        var gen = ++_loadGeneration;
        var outgoing = Player;
        var incoming = Inactive;
        ApplyReplayGain(track);
        UpdateSmtcMetadata(track);
        TrackChanged?.Invoke(this, EventArgs.Empty);
        StateChanged?.Invoke(this, EventArgs.Empty);

        var sourceUri = await ResolveSourceAsync(track, gen);
        if (gen != _loadGeneration || sourceUri is null)
        {
            _transitioning = false;
            if (sourceUri is null && gen == _loadGeneration)
            {
                _ = LoadCurrentAsync(true); // 走普通加载+自动跳过
            }
            return;
        }
        track.StreamUrl = sourceUri;

        try
        {
            incoming.Source = MediaSource.CreateFromUri(new Uri(sourceUri));
            incoming.Play();
        }
        catch (Exception ex)
        {
            App.LogCrash("CrossfadeLoad", ex);
            _transitioning = false;
            _ = LoadCurrentAsync(true);
            return;
        }

        if (!track.IsOnline)
        {
            _ = HistoryService.Instance.LogAsync(track);
        }

        // 音量斜坡
        var totalTicks = Math.Max(1, (int)(spanSeconds * 1000 / 30));
        var tick = 0;
        _fadeTimer?.Stop();
        _fadeTimer = _dispatcher.CreateTimer();
        _fadeTimer.Interval = TimeSpan.FromMilliseconds(30);
        _fadeTimer.IsRepeating = true;
        _fadeTimer.Tick += (_, _) =>
        {
            tick++;
            var p = Math.Min(1.0, (double)tick / totalTicks);
            try
            {
                outgoing.Volume = Math.Clamp(_baseVolume * _rgFactor * (1 - p), 0, 1);
                incoming.Volume = Math.Clamp(_baseVolume * _rgFactor * p, 0, 1);
            }
            catch { /* 播放器可能已释放 */ }

            if (p >= 1.0)
            {
                _fadeTimer.Stop();
                _useB = !_useB;
                try
                {
                    outgoing.Pause();
                }
                catch { /* 忽略 */ }
                ApplyVolume();
                try
                {
                    Inactive.SystemMediaTransportControls.IsEnabled = false;
                    Player.SystemMediaTransportControls.IsEnabled = true;
                }
                catch { /* 忽略 */ }
                _transitioning = false;
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
        };
        _fadeTimer.Start();
    }

    private void OnMediaEnded()
    {
        if (_transitioning) return;

        switch (Repeat)
        {
            case RepeatMode.One:
                Player.PlaybackSession.Position = TimeSpan.Zero;
                Player.Play();
                break;
            case RepeatMode.Off when Index >= Queue.Count - 1:
                Player.Pause();
                break;
            default:
                Next();
                break;
        }
    }

    /* ---------------- 控制 ---------------- */

    public void Next()
    {
        if (Queue.Count == 0) return;
        if (_transitioning)
        {
            _fadeTimer?.Stop();
            _transitioning = false;
            ApplyVolume();
        }

        Index = Shuffle && Queue.Count > 1
            ? PickRandomIndex()
            : (Index + 1) % Queue.Count;
        _ = LoadCurrentAsync(autoPlay: true);
    }

    public void Previous()
    {
        if (Queue.Count == 0) return;
        if (_transitioning)
        {
            _fadeTimer?.Stop();
            _transitioning = false;
            ApplyVolume();
        }

        if (Position.TotalSeconds > 3)
        {
            Player.PlaybackSession.Position = TimeSpan.Zero;
            return;
        }

        Index = Index <= 0 ? Queue.Count - 1 : Index - 1;
        _ = LoadCurrentAsync(autoPlay: true);
    }

    private int PickRandomIndex()
    {
        if (Queue.Count <= 1) return Index;
        var next = Index;
        var random = new Random();
        while (next == Index) next = random.Next(Queue.Count);
        return next;
    }

    public void TogglePlayPause()
    {
        if (Player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
        {
            Player.Pause();
        }
        else
        {
            if (Player.Source is null && Current is not null) _ = LoadCurrentAsync(autoPlay: true);
            else Player.Play();
        }
    }

    public void Pause() => Player.Pause();
    public void Resume() => Player.Play();

    public void Seek(TimeSpan position)
    {
        var max = Player.PlaybackSession.NaturalDuration;
        if (max <= TimeSpan.Zero) return;
        Player.PlaybackSession.Position = position > max ? max : position < TimeSpan.Zero ? TimeSpan.Zero : position;
    }

    public void ToggleShuffle()
    {
        Shuffle = !Shuffle;
        SettingsService.Instance.Settings.Shuffle = Shuffle;
        SettingsService.Instance.SaveSettings();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void CycleRepeat()
    {
        Repeat = Repeat switch
        {
            RepeatMode.Off => RepeatMode.All,
            RepeatMode.All => RepeatMode.One,
            _ => RepeatMode.Off,
        };
        SettingsService.Instance.Settings.RepeatMode = (int)Repeat;
        SettingsService.Instance.SaveSettings();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /* ---------------- 系统媒体控件 ---------------- */

    private void EnsureSmtc()
    {
        // SMTC 已在构造时统一启用；切歌时只更新元数据
    }

    private void UpdateSmtcMetadata(Track track)
    {
        EnsureSmtc();
        var updater = Player.SystemMediaTransportControls.DisplayUpdater;
        updater.Type = MediaPlaybackType.Music;
        updater.MusicProperties.Title = track.Title;
        updater.MusicProperties.Artist = track.Artist;
        updater.MusicProperties.AlbumTitle = track.Album;
        updater.Thumbnail = string.IsNullOrEmpty(track.CoverPath) || !System.IO.File.Exists(track.CoverPath)
            ? null
            : RandomAccessStreamReference.CreateFromUri(new Uri(track.CoverPath));
        updater.Update();
        UpdateSmtcStatus();
    }

    private void UpdateSmtcStatus()
    {
        EnsureSmtc();
        Player.SystemMediaTransportControls.PlaybackStatus =
            Player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing
                ? MediaPlaybackStatus.Playing
                : Player.PlaybackSession.PlaybackState == MediaPlaybackState.Paused
                    ? MediaPlaybackStatus.Paused
                    : MediaPlaybackStatus.Stopped;
    }
}
