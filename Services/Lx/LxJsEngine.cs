using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jint;
using Jint.Native;

namespace PixelPlayer.WinUI.Services.Lx;

/// <summary>
/// 落雪 userApi JS 引擎（对齐参考项目 LxJsEngine，QuickJS → Jint）：
///  - 每个脚本独立 Jint Engine 上下文（隔离，可同时加载多个）
///  - lx.request 异步非阻塞（脚本发请求 → 原生异步 HTTP → response 事件回传）
///  - setTimeout / clearTimeout 由原生定时器驱动
///  - 完整 utils.crypto（aesEncrypt / rsaEncrypt / md5 / randomBytes）与 utils.buffer
///  - search / getPlayUrl / lyric / pic 按音源路由到注册该音源的脚本实例
/// 对齐 lx-music-mobile 的 user-api-preload.js 协议。
/// </summary>
public sealed class LxJsEngine
{
    public static LxJsEngine Instance { get; } = new();

    public sealed class LxSourceInfo
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public List<string> Actions { get; set; } = new();
        public List<string> Qualitys { get; set; } = new();
    }

    public sealed class LxScriptInfo
    {
        public string FileName { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string Version { get; set; } = "";
        public string Author { get; set; } = "";
        public string Homepage { get; set; } = "";
        public string LastUpdate { get; set; } = "";
        public string Md5 { get; set; } = "";
    }

    public sealed class LxSongInfo
    {
        public string Id { get; set; } = "";
        public string Songmid { get; set; } = "";
        public string Hash { get; set; } = "";
        public string Name { get; set; } = "";
        public string Singer { get; set; } = "";
        public string AlbumName { get; set; } = "";
        public long Duration { get; set; }
        public string Pic { get; set; } = "";
        public string Source { get; set; } = "";
    }

    public sealed class LxSearchResult
    {
        public List<LxSongInfo> List { get; init; } = new();
        public bool IsEnd { get; init; }
        public int Total { get; init; }
    }

    public sealed class LxLyricInfo
    {
        public string Lyric { get; init; } = "";
        public string? Tlyric { get; init; }
        public string? Rlyric { get; init; }
        public string? Lxlyric { get; init; }
        public bool IsUsable() => !string.IsNullOrWhiteSpace(Lyric);
    }

    private sealed class EngineInstance
    {
        public string FileName = "";
        public Engine Ctx = null!;
        public object Lock = new();
        public Dictionary<string, LxSourceInfo> Sources = new();
        public LxScriptInfo ScriptInfo = new();
        public string VersionName = "";
        public ConcurrentDictionary<string, TaskCompletionSource<string>> PendingCalls = new();
        public ConcurrentDictionary<string, CancellationTokenSource> ScriptRequests = new();
        public Dictionary<long, CancellationTokenSource> TimeoutIds = new();
    }

    private static readonly HttpClient _http = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    })
    { Timeout = TimeSpan.FromSeconds(45) };

    private readonly object _engineGate = new();
    private Dictionary<string, EngineInstance> _instances = new();
    private Dictionary<string, List<string>> _sourceIndex = new();
    private Dictionary<string, LxSourceInfo> _sources = new();
    private volatile bool _inited;

    public string? LastError { get; private set; }
    public bool IsReady => _inited && _instances.Count > 0;

    private LxJsEngine() { }

    // ── 生命周期 ─────────────────────────────────────────────────────────

    public bool Ready()
    {
        lock (_engineGate)
        {
            LastError = null;
            if (_inited && _instances.Count > 0) return true;

            var files = LxFileStore.Instance.ListFiles();
            if (files.Count == 0)
            {
                LastError = "没有导入 JS 文件";
                return false;
            }

            var loaded = new Dictionary<string, EngineInstance>();
            var index = new Dictionary<string, List<string>>();
            var errors = new List<string>();
            var anySuccess = false;

            // 并行初始化各脚本实例（互相独立，参考项目为串行；并行把大脚本 eval
            // 的墙钟时间从 sum 降到 max）
            var pairs = files
                .Select(f => (f.Name, js: LxFileStore.Instance.Content(f.Name)))
                .ToList();
            var results = new (string Name, EngineInstance? Inst, string? Error)[pairs.Count];
            Parallel.For(0, pairs.Count, i =>
            {
                var (name, js) = pairs[i];
                if (string.IsNullOrWhiteSpace(js))
                {
                    results[i] = (name, null, $"{name}: 文件为空或读取失败");
                    return;
                }
                try { results[i] = (name, CreateInstance(name, js), null); }
                catch (Exception ex)
                {
                    App.LogCrash("LxJsEngine.load", ex);
                    results[i] = (name, null, $"{name}: JS 解析/执行失败: {ex.Message}");
                }
            });

            foreach (var (name, inst, error) in results)
            {
                if (inst is null)
                {
                    errors.Add(error!);
                    continue;
                }
                if (inst.Sources.Count == 0)
                {
                    errors.Add($"{name}: 初始化了但没有注册任何音源，或执行失败");
                    continue;
                }
                loaded[name] = inst;
                anySuccess = true;
                foreach (var key in inst.Sources.Keys)
                {
                    if (!index.TryGetValue(key, out var list)) index[key] = list = new List<string>();
                    list.Add(name);
                }
            }

            _instances = loaded;
            _sourceIndex = index;
            _sources = new Dictionary<string, LxSourceInfo>();
            foreach (var inst in loaded.Values)
                foreach (var kv in inst.Sources) _sources[kv.Key] = kv.Value;
            _inited = anySuccess;
            LastError = anySuccess ? null : (string.Join("；", errors) is { Length: > 0 } e ? e : "JS 加载失败");
            return anySuccess;
        }
    }

    public async Task<bool> AwaitReadyAsync(int timeoutMs = 15000)
    {
        if (IsReady) return true;
        await Task.Run(() => { try { Ready(); } catch { } });
        if (IsReady) return true;
        if (LxFileStore.Instance.ListFiles().Count == 0) return false;
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!IsReady && Environment.TickCount64 < deadline)
        {
            await Task.Delay(150);
            try { Ready(); } catch { }
        }
        return IsReady;
    }

    public void Close()
    {
        lock (_engineGate)
        {
            _instances.Clear();
            _sourceIndex.Clear();
            _sources.Clear();
            _inited = false;
        }
    }

    public bool Reload() { Close(); return Ready(); }

    public Dictionary<string, LxSourceInfo> GetSources()
    {
        lock (_engineGate) return new Dictionary<string, LxSourceInfo>(_sources);
    }

    /// <summary>脚本文件名 → 该脚本注册的音源 key 列表（供音源管理页展示"提供平台"）。</summary>
    public Dictionary<string, List<string>> GetScriptSources()
    {
        lock (_engineGate)
        {
            var map = new Dictionary<string, List<string>>();
            foreach (var kv in _sourceIndex)
            {
                foreach (var file in kv.Value)
                {
                    if (!map.TryGetValue(file, out var list)) map[file] = list = new List<string>();
                    list.Add(kv.Key);
                }
            }
            return map;
        }
    }

    public List<LxScriptInfo> ScriptInfos()
    {
        var names = LxFileStore.Instance.ListFiles().Select(f => f.Name).ToList();
        lock (_engineGate)
        {
            return names.Select(n =>
                _instances.TryGetValue(n, out var inst) ? inst.ScriptInfo
                : ParseScriptInfo(LxFileStore.Instance.Content(n) ?? "", n)).ToList();
        }
    }

    // ── 对外 API（musicSearch / musicUrl / lyric / pic） ─────────────────

    public async Task<LxSearchResult> SearchAsync(string keyword, string source, int page = 1, int pagesize = 30)
    {
        if (!await AwaitReadyAsync()) return new LxSearchResult();
        var info = new JsonObject
        {
            ["keyword"] = keyword,
            ["page"] = page,
            ["pagesize"] = pagesize,
            ["type"] = "music",
        };
        foreach (var fileName in TargetsFor(source))
        {
            if (!_instances.TryGetValue(fileName, out var inst)) continue;
            var raw = await CallJsAsync(inst, "musicSearch", source, info, 30000);
            var result = ParseSearchResult(raw);
            if (result.List.Count > 0) return result;
        }
        return new LxSearchResult();
    }

    public async Task<string?> GetPlayUrlAsync(string source, LxSongInfo songInfo, string quality = "128k")
    {
        if (!await AwaitReadyAsync()) return null;
        // 对齐参考项目：info = { musicInfo: {...}, type: quality }
        var info = new JsonObject
        {
            ["musicInfo"] = BuildSongInfoJson(songInfo),
            ["type"] = quality,
        };
        foreach (var fileName in TargetsFor(source))
        {
            if (!_instances.TryGetValue(fileName, out var inst)) continue;
            string? raw = null;
            try { raw = await CallJsAsync(inst, "musicUrl", source, info, 20000); }
            catch (Exception ex) { App.LogCrash("LxJsEngine.getPlayUrl", ex); }
            var result = ProcessUrlResult(raw);
            if (result is not null) return result;
        }
        return null;
    }

    public async Task<LxLyricInfo?> GetLyricAsync(string source, LxSongInfo songInfo, string quality = "128k")
    {
        if (!await AwaitReadyAsync()) return null;
        var info = new JsonObject
        {
            ["musicInfo"] = BuildSongInfoJson(songInfo),
            ["type"] = quality,
        };
        foreach (var fileName in TargetsFor(source))
        {
            if (!_instances.TryGetValue(fileName, out var inst)) continue;
            string? raw = null;
            try { raw = await CallJsAsync(inst, "lyric", source, info, 15000); }
            catch { }
            var lyric = ProcessLyricResult(raw);
            if (lyric is not null && lyric.IsUsable()) return lyric;
        }
        return null;
    }

    public async Task<string?> GetPicAsync(string source, LxSongInfo songInfo, string quality = "128k")
    {
        if (!await AwaitReadyAsync()) return null;
        var info = new JsonObject
        {
            ["musicInfo"] = BuildSongInfoJson(songInfo),
            ["type"] = quality,
        };
        foreach (var fileName in TargetsFor(source))
        {
            if (!_instances.TryGetValue(fileName, out var inst)) continue;
            string? raw = null;
            try { raw = await CallJsAsync(inst, "pic", source, info, 15000); }
            catch { }
            var pic = ProcessUrlResult(raw);
            if (!string.IsNullOrWhiteSpace(pic)) return pic;
        }
        return null;
    }

    private static JsonObject BuildSongInfoJson(LxSongInfo s)
    {
        var artists = (s.Singer ?? "").Split(new[] { '、', '&', ';', '/', ',', '|' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(n => new JsonObject { ["name"] = n.Trim() }).ToList();
        return new JsonObject
        {
            ["id"] = s.Id,
            ["vid"] = s.Id,
            ["songmid"] = s.Songmid,
            ["hash"] = s.Hash,
            ["name"] = s.Name,
            ["singer"] = s.Singer,
            ["artists"] = new JsonArray(artists.Select(a => (JsonNode?)a).ToArray()),
            ["album"] = s.AlbumName,
            ["albumName"] = s.AlbumName,
            ["duration"] = s.Duration,
            ["pic"] = s.Pic,
            ["cover"] = s.Pic,
            ["source"] = s.Source,
        };
    }

    // ── 引擎调用 ─────────────────────────────────────────────────────────

    private async Task<string?> CallJsAsync(EngineInstance inst, string action, string source, JsonObject info, int timeoutMs)
    {
        var callId = $"c{Environment.TickCount64}_{Interlocked.Increment(ref _callCounter)}";
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        inst.PendingCalls[callId] = tcs;
        try
        {
            var infoJson = info.ToJsonString();
            lock (inst.Lock)
            {
                inst.Ctx.SetValue("__px_arg_action", action);
                inst.Ctx.SetValue("__px_arg_source", source);
                inst.Ctx.SetValue("__px_arg_info", infoJson);
                Execute(inst, $"__pixelplay_call_js({JsStr(callId)}, __px_arg_action, __px_arg_source, JSON.parse(__px_arg_info));");
            }

            using var cts = new CancellationTokenSource(timeoutMs);
            try
            {
                var raw = await tcs.Task.WaitAsync(cts.Token);
                if (string.IsNullOrEmpty(raw)) return null;
                var obj = TryParseJson(raw);
                if (obj is JsonObject jObj && jObj.ContainsKey("error"))
                {
                    App.LogCrash("LxJs.callJs", new Exception($"{action}/{source} JS error: {jObj["error"]}"));
                    return null;
                }
                if (obj is JsonObject valObj && valObj.ContainsKey("value"))
                    return valObj["value"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : valObj["value"]?.ToJsonString();
                return raw;
            }
            catch (OperationCanceledException)
            {
                App.LogCrash("LxJs.callJs", new Exception($"{action}/{source} timeout after {timeoutMs}ms"));
                return null;
            }
        }
        finally
        {
            inst.PendingCalls.TryRemove(callId, out _);
        }
    }

    private int _callCounter;

    private string? ProcessUrlResult(string? raw)
    {
        if (raw is null) return null;
        var url = raw.Trim();
        var node = TryParseJson(url);
        if (node is JsonObject obj)
        {
            url = StringOf(obj, "url") ?? StringOf(obj, "data") ?? StringOf(obj, "src") ?? url;
        }
        else if (node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (item is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)) { url = s; break; }
            }
        }
        else if (url.Length >= 2 && url.StartsWith('"') && url.EndsWith('"'))
        {
            url = url.Substring(1, url.Length - 2);
        }

        var trimmed = url.Trim();
        var lower = trimmed.ToLowerInvariant();
        if (lower.StartsWith("http://") || lower.StartsWith("https://")) return trimmed;
        if (trimmed.Length > 0) App.LogCrash("LxJs.getPlayUrl", new Exception($"rejecting non-http URL: {trimmed[..Math.Min(80, trimmed.Length)]}"));
        return null;
    }

    private static string? StringOf(JsonObject obj, string key) =>
        obj[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    private static JsonNode? TryParseJson(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.TrimStart();
        if (t.StartsWith("{") || t.StartsWith("[")) { try { return JsonNode.Parse(s); } catch { return null; } }
        return null;
    }

    private LxLyricInfo? ProcessLyricResult(string? raw)
    {
        if (raw is null) return null;
        var s = raw.Trim();
        if (s.StartsWith('{'))
        {
            var obj = TryParseJson(s) as JsonObject;
            var lyric = obj is null ? null : StringOf(obj, "lyric");
            if (string.IsNullOrWhiteSpace(lyric)) return null;
            return new LxLyricInfo
            {
                Lyric = lyric!,
                Tlyric = obj is null ? null : StringOf(obj, "tlyric"),
                Rlyric = obj is null ? null : StringOf(obj, "rlyric"),
                Lxlyric = obj is null ? null : StringOf(obj, "lxlyric"),
            };
        }
        if (s.StartsWith('"') && s.EndsWith('"') && s.Length >= 2)
            return new LxLyricInfo { Lyric = s.Substring(1, s.Length - 2) };
        if (s.Length > 0) return new LxLyricInfo { Lyric = s };
        return null;
    }

    private LxSearchResult ParseSearchResult(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new LxSearchResult();
        var obj = TryParseJson(raw) as JsonObject;
        if (obj is null || obj["list"] is not JsonArray listArr) return new LxSearchResult();
        var total = obj["total"] is JsonValue tv && tv.TryGetValue<int>(out var t) ? t : 0;
        var isEnd = obj["isEnd"] is JsonValue iv && iv.TryGetValue<bool>(out var ie) ? ie : listArr.Count < 30;
        var list = new List<LxSongInfo>();
        foreach (var item in listArr)
        {
            if (item is not JsonObject o) continue;
            list.Add(new LxSongInfo
            {
                Id = StringOf(o, "id") ?? StringOf(o, "vid") ?? StringOf(o, "songmid") ?? "",
                Songmid = StringOf(o, "songmid") ?? "",
                Hash = StringOf(o, "hash") ?? "",
                Name = StringOf(o, "name") ?? "",
                Singer = ParseSinger(o["singer"] ?? o["artists"]),
                AlbumName = (o["album"] as JsonObject) is { } al ? (StringOf(al, "name") ?? "") : (StringOf(o, "albumName") ?? ""),
                Duration = o["duration"] is JsonValue dv && dv.TryGetValue<long>(out var d) ? d : 0,
                Pic = StringOf(o, "pic") ?? ((o["al"] as JsonObject) is { } a ? (StringOf(a, "picUrl") ?? "") : ""),
                Source = StringOf(o, "source") ?? "",
            });
        }
        return new LxSearchResult { List = list, IsEnd = isEnd, Total = total };
    }

    private static string ParseSinger(JsonNode? node)
    {
        try
        {
            if (node is JsonValue v && v.TryGetValue<string>(out var s)) return s;
            if (node is JsonObject obj && obj["name"] is JsonArray names)
                return string.Join("、", names.Select(n => (n as JsonValue)?.TryGetValue<string>(out var ns) == true ? ns : "").Where(x => !string.IsNullOrEmpty(x)));
            if (node is JsonArray arr)
                return string.Join("、", arr.OfType<JsonObject>()
                    .Select(a => StringOf(a, "name") ?? "").Where(x => !string.IsNullOrEmpty(x)));
        }
        catch { }
        return "";
    }

    // ── 脚本实例创建 + shim 注入 ─────────────────────────────────────────

    private EngineInstance CreateInstance(string fileName, string js)
    {
        var inst = new EngineInstance { FileName = fileName };
        var engine = new Engine(options => options
            .TimeoutInterval(TimeSpan.FromSeconds(15))
            .LimitRecursion(128)
            .Strict(false));
        inst.Ctx = engine;

        // 原生钩子
        engine.SetValue("__pixelplay_native_call__", new Action<string, string>((action, dataJson) =>
        {
            if (action == "request") HandleScriptRequest(inst, dataJson);
            else if (action == "cancelRequest") CancelScriptRequest(inst, dataJson);
        }));
        engine.SetValue("__pixelplay_set_timeout", new Action<long, long>((id, ms) =>
        {
            var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(Math.Max(0, ms)));
            inst.TimeoutIds[id] = cts;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(0, ms)), cts.Token);
                    lock (inst.Lock) Execute(inst, $"__pixelplay_fire_timeout({id})");
                }
                catch (OperationCanceledException) { }
            });
        }));
        engine.SetValue("__pixelplay_clear_timeout", new Action<long>(id =>
        {
            lock (inst.TimeoutIds)
            {
                if (inst.TimeoutIds.Remove(id, out var cts)) cts.Cancel();
            }
        }));
        engine.SetValue("__pixelplay_cancel_request", new Action<string>(key => CancelScriptRequest(inst, key)));

        engine.SetValue("__pixelplay_str2b64", new Func<string, string>(s =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(s ?? ""))));
        engine.SetValue("__pixelplay_b642buf", new Func<string, string>(b64 =>
        {
            try { return "[" + string.Join(",", Convert.FromBase64String(b64 ?? "")) + "]"; }
            catch { return "[]"; }
        }));
        engine.SetValue("__pixelplay_md5", new Func<string, string>(encoded =>
        {
            try
            {
                var decoded = Uri.UnescapeDataString(encoded ?? "");
                var digest = MD5.HashData(Encoding.UTF8.GetBytes(decoded));
                return Convert.ToHexString(digest).ToLowerInvariant();
            }
            catch { return ""; }
        }));
        engine.SetValue("__pixelplay_aes_encrypt", new Func<string, string, string, string, string>((dataB64, keyB64, ivB64, mode) =>
        {
            try
            {
                var data = Convert.FromBase64String(dataB64 ?? "");
                var key = Convert.FromBase64String(keyB64 ?? "");
                using var aes = Aes.Create();
                aes.Key = key;
                if (mode == "CBC_128_PKCS7Padding")
                {
                    var ivRaw = Convert.FromBase64String(ivB64 ?? "");
                    var iv = new byte[16];
                    Array.Copy(ivRaw, iv, Math.Min(ivRaw.Length, 16));
                    aes.IV = iv;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;
                }
                else
                {
                    aes.Mode = CipherMode.ECB;
                    aes.Padding = PaddingMode.None;
                }
                return Convert.ToBase64String(aes.CreateEncryptor().TransformFinalBlock(data, 0, data.Length));
            }
            catch (Exception ex)
            {
                App.LogCrash("LxJs.aesEncrypt", ex);
                return "";
            }
        }));
        engine.SetValue("__pixelplay_rsa_encrypt", new Func<string, string, string, string>((dataB64, keyB64, padding) =>
        {
            try
            {
                var data = Convert.FromBase64String(dataB64 ?? "");
                var key = (keyB64 ?? "").Trim();
                using var rsa = RSA.Create();
                rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key), out _);
                var result = padding == "OAEPWithSHA1AndMGF1Padding"
                    ? rsa.Encrypt(data, RSAEncryptionPadding.OaepSHA1)
                    : RawRsaEncrypt(rsa, data);
                return Convert.ToBase64String(result);
            }
            catch (Exception ex)
            {
                App.LogCrash("LxJs.rsaEncrypt", ex);
                return "";
            }
        }));
        engine.SetValue("__pixelplay_native_pending_response", new Action<string, string>((callId, json) =>
        {
            if (callId is not null && inst.PendingCalls.TryRemove(callId, out var tcs))
                tcs.TrySetResult(json ?? "null");
        }));
        engine.SetValue("__pixelplay_console_log", new Action<object?[]>(args =>
            App.LogCrash("LxJs.console", new Exception("log: " + JoinArgs(args)))));
        engine.SetValue("__pixelplay_console_err", new Action<object?[]>(args =>
            App.LogCrash("LxJs.console", new Exception("err: " + JoinArgs(args)))));

        Execute(inst, Shim);
        Execute(inst, js, fileName);

        // 允许 init 阶段用 setTimeout 异步注册音源（最多等 2s，对齐参考项目）
        var deadline = Environment.TickCount64 + 2000;
        while (Environment.TickCount64 < deadline)
        {
            var done = Execute(inst, "__pixelplay_get_done_js()");
            if (done is JsNumber n && n == 1) break;
            Execute(inst, "null");
            Thread.Sleep(10);
        }

        inst.ScriptInfo = ParseScriptInfo(js, fileName);
        inst.VersionName = ExtractVersion(js);
        inst.Sources = ReadSourcesFromJs(inst);
        return inst;
    }

    /// <summary>裸 RSA（NoPadding）：m^e mod n，输出模长定长（对齐 RSA/ECB/NoPadding）。</summary>
    private static byte[] RawRsaEncrypt(RSA rsa, byte[] data)
    {
        try
        {
            var parameters = rsa.ExportParameters(false);
            var n = BytesToBig(parameters.Modulus!);
            var e = BytesToBig(parameters.Exponent!);
            var m = BytesToBig(data);
            var c = System.Numerics.BigInteger.ModPow(m, e, n);
            var bytes = BigToBytes(c, parameters.Modulus!.Length);
            return bytes;
        }
        catch
        {
            return rsa.Encrypt(data, RSAEncryptionPadding.OaepSHA1);
        }
    }

    private static System.Numerics.BigInteger BytesToBig(byte[] bigEndian)
    {
        var reversed = bigEndian.Reverse().ToArray();
        return new System.Numerics.BigInteger(reversed.Concat(new byte[] { 0 }).ToArray());
    }

    private static byte[] BigToBytes(System.Numerics.BigInteger value, int length)
    {
        var bytes = value.ToByteArray(); // little-endian
        var result = new byte[length];
        for (var i = 0; i < Math.Min(bytes.Length, length); i++) result[i] = bytes[i];
        return result.Reverse().ToArray();
    }

    private static string JoinArgs(object?[] args)
    {
        try { return string.Join(" ", args.Select(a => a?.ToString() ?? "null")); }
        catch { return ""; }
    }

    private static void CancelScriptRequest(EngineInstance inst, string? key)
    {
        if (!string.IsNullOrEmpty(key) && inst.ScriptRequests.TryRemove(key, out var cts)) cts.Cancel();
    }

    // ── 脚本 HTTP 请求 ───────────────────────────────────────────────────

    private void HandleScriptRequest(EngineInstance inst, string? dataJson)
    {
        if (string.IsNullOrEmpty(dataJson)) return;
        JsonObject? data;
        try { data = JsonNode.Parse(dataJson) as JsonObject; } catch { return; }
        if (data is null) return;
        var requestKey = StringOf(data, "requestKey");
        if (string.IsNullOrEmpty(requestKey)) return;
        var url = StringOf(data, "url") ?? "";
        var options = data["options"] as JsonObject;
        var method = (StringOf(options, "method") ?? "GET").ToUpperInvariant();
        var timeout = options?["timeout"] is JsonValue tv && tv.TryGetValue<long>(out var t) ? t : 0;
        var binary = options?["binary"] is JsonValue bv && bv.TryGetValue<bool>(out var b) && b;

        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeout > 0 ? timeout : 15000));
        inst.ScriptRequests[requestKey] = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                var (headers, body) = BuildScriptOptions(options, method);
                using var req = new HttpRequestMessage(new HttpMethod(method), url);
                foreach (var kv in headers) req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                if (body is not null && method is "POST" or "PUT" or "PATCH" or "DELETE")
                    req.Content = new StringContent(body, Encoding.UTF8,
                        headers.FirstOrDefault(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)).Value
                        ?? "application/json");

                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token);
                var respBody = await resp.Content.ReadAsStringAsync(cts.Token);
                var respHeaders = resp.Headers.Concat(resp.Content.Headers)
                    .GroupBy(h => h.Key).ToDictionary(g => g.Key, g => string.Join(", ", g.SelectMany(v => v.Value)));
                DeliverScriptResponse(inst, requestKey, (int)resp.StatusCode, resp.ReasonPhrase ?? "",
                    resp.RequestMessage?.RequestUri?.ToString() ?? url, respHeaders, respBody, binary);
            }
            catch (OperationCanceledException)
            {
                DeliverScriptError(inst, requestKey, "request aborted/timeout");
            }
            catch (Exception ex)
            {
                DeliverScriptError(inst, requestKey, ex.Message);
            }
            finally
            {
                inst.ScriptRequests.TryRemove(requestKey, out _);
            }
        });
    }

    private static (Dictionary<string, string> Headers, string? Body) BuildScriptOptions(JsonObject? options, string method)
    {
        var headers = new Dictionary<string, string>();
        string? body = null;
        if (options is not null)
        {
            if (options["headers"] is JsonObject h)
                foreach (var kv in h) headers[kv.Key] = kv.Value?.ToString() ?? "";

            var rawBody = options["body"];
            if (rawBody is not null) body = rawBody.ToString();

            var isPost = method.Equals("POST", StringComparison.OrdinalIgnoreCase) ||
                         method.Equals("PUT", StringComparison.OrdinalIgnoreCase);
            if (isPost && !headers.Keys.Any(k => k.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)))
            {
                if (options["form"] is JsonObject form)
                {
                    headers["Content-Type"] = "application/x-www-form-urlencoded";
                    body = string.Join("&", form.Select(kv =>
                        $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value?.ToString() ?? "")}"));
                }
                else if (options["formData"] is not null)
                {
                    headers["Content-Type"] = "multipart/form-data";
                    body = options["formData"]!.ToString();
                }
                else if (rawBody is not null)
                {
                    headers["Content-Type"] = "application/json";
                }
            }
        }
        return (headers, body);
    }

    private void DeliverScriptResponse(EngineInstance inst, string requestKey, int statusCode, string statusMessage,
        string url, Dictionary<string, string> headers, string body, bool binary)
    {
        var payload = new JsonObject
        {
            ["requestKey"] = requestKey,
            ["error"] = null,
            ["response"] = new JsonObject
            {
                ["statusCode"] = statusCode,
                ["statusMessage"] = statusMessage,
                ["headers"] = JsonObjectFrom(headers),
                ["url"] = url,
                ["ok"] = statusCode is >= 200 and < 300,
                ["body"] = binary ? body : (TryParseJson(body) ?? (body.Length == 0 ? null : JsonValue.Create(body))),
            },
        };
        DeliverToJs(inst, $"__pixelplay_http_response({JsStr(payload.ToJsonString())});");
    }

    private void DeliverScriptError(EngineInstance inst, string requestKey, string message)
    {
        var payload = new JsonObject
        {
            ["requestKey"] = requestKey,
            ["error"] = message,
            ["response"] = null,
        };
        DeliverToJs(inst, $"__pixelplay_http_response({JsStr(payload.ToJsonString())});");
    }

    private static JsonObject JsonObjectFrom(Dictionary<string, string> map)
    {
        var obj = new JsonObject();
        foreach (var kv in map) obj[kv.Key] = kv.Value;
        return obj;
    }

    private void DeliverToJs(EngineInstance inst, string jsCode)
    {
        _ = Task.Run(() =>
        {
            try
            {
                lock (inst.Lock)
                {
                    Execute(inst, jsCode);
                    // 驱动 Promise 微任务队列（对齐参考项目 repeat(20) tick）
                    for (var i = 0; i < 5; i++) Execute(inst, "null");
                }
            }
            catch (Exception ex)
            {
                App.LogCrash("LxJs.deliver", ex);
            }
        });
    }

    private Dictionary<string, LxSourceInfo> ReadSourcesFromJs(EngineInstance inst)
    {
        try
        {
            var raw = Execute(inst, "__pixelplay_get_inited_js()");
            var rawJson = raw?.ToString();
            if (string.IsNullOrWhiteSpace(rawJson)) return new();
            var obj = JsonNode.Parse(rawJson) as JsonObject;
            if (obj?["sources"] is not JsonObject sourcesObj) return new();
            var result = new Dictionary<string, LxSourceInfo>();
            foreach (var kv in sourcesObj)
            {
                if (kv.Value is not JsonObject v) continue;
                result[kv.Key] = new LxSourceInfo
                {
                    Name = StringOf(v, "name") ?? "",
                    Type = StringOf(v, "type") ?? "",
                    Actions = v["actions"] is JsonArray arr
                        ? arr.Select(a => (a as JsonValue)?.TryGetValue<string>(out var s) == true ? s : "").ToList()
                        : new List<string>(),
                    Qualitys = v["qualitys"] is JsonArray qarr
                        ? qarr.Select(a => (a as JsonValue)?.TryGetValue<string>(out var s) == true ? s : "").ToList()
                        : new List<string>(),
                };
            }
            return result;
        }
        catch (Exception ex)
        {
            App.LogCrash("LxJs.readSources", ex);
            return new();
        }
    }

    // ── 路由 ─────────────────────────────────────────────────────────────

    private List<string> TargetsFor(string source)
    {
        lock (_engineGate)
        {
            return _sourceIndex.TryGetValue(source, out var list) ? list.ToList() : new List<string>();
        }
    }

    // ── 元数据解析 ───────────────────────────────────────────────────────

    public static LxScriptInfo ParseScriptInfo(string js, string fileName)
    {
        var info = new LxScriptInfo { FileName = fileName };
        try
        {
            var blockMatch = System.Text.RegularExpressions.Regex.Match(js, @"/\*!([\s\S]*?)\*/")
                ?? System.Text.RegularExpressions.Regex.Match(js, @"/\*([\s\S]*?)\*/");
            var block = blockMatch.Success ? blockMatch.Groups[1].Value : "";
            var fields = new Dictionary<string, string>();
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(block, @"@([\w\-]+)\s+([^@\r\n*]+)"))
            {
                var k = m.Groups[1].Value;
                var v = m.Groups[2].Value.Trim();
                if (v.Length > 0) fields[k] = v;
            }
            string Field(string key) => fields.TryGetValue(key, out var v) ? v : "";
            var fallbackName = System.Text.RegularExpressions.Regex.Match(js, "name\\s*[:=]\\s*['\"]([^'\"]+)['\"]").Groups[1].Value;
            info.Name = string.IsNullOrWhiteSpace(Field("name")) ? fallbackName : Field("name");
            info.Description = Field("description");
            info.Version = Field("version");
            info.Author = Field("author");
            info.Homepage = Field("homepage");
            info.LastUpdate = Field("lastUpdate");
            info.Md5 = Field("md5");
        }
        catch { }
        return info;
    }

    private static string ExtractVersion(string js)
    {
        var m = System.Text.RegularExpressions.Regex.Match(js, "version\\s*[:=]\\s*['\"]?([0-9a-zA-Z._\\-]+)")
            ?? System.Text.RegularExpressions.Regex.Match(js, "@version\\s+([\\d.]+)");
        return m.Success ? m.Groups[1].Value : "custom";
    }

    // ── 工具 ─────────────────────────────────────────────────────────────

    private static JsValue Execute(EngineInstance inst, string code, string? name = null)
    {
        try { return inst.Ctx.Evaluate(code, name ?? "script"); }
        catch (Exception ex)
        {
            App.LogCrash("LxJs.execute", new Exception($"{inst.FileName} [{name}]: {ex.Message}"));
            return JsValue.Undefined;
        }
    }

    private static string JsStr(string s) => JsonSerializer.Serialize(s);

    // ── shim：对齐 lx-music-mobile 的 user-api-preload.js（与参考项目逐字一致） ──

    private const string Shim = """
    (function () {
        var _initedJson = null;
        var _initedDone = false;

        function _pxStr(a) {
            if (a === null) return 'null';
            if (a === undefined) return 'undefined';
            if (typeof a === 'object') {
                try { return JSON.stringify(a); } catch (e) { return String(a); }
            }
            return String(a);
        }
        function _pxLog() {
            try { __pixelplay_console_log(Array.prototype.slice.call(arguments).map(_pxStr).join(' ')); } catch (e) {}
        }
        function _pxErr() {
            try { __pixelplay_console_err(Array.prototype.slice.call(arguments).map(_pxStr).join(' ')); } catch (e) {}
        }
        try {
            if (!globalThis.console) globalThis.console = {};
            console.log = _pxLog;
            console.error = _pxErr;
            console.warn = _pxLog;
            console.info = _pxLog;
            console.debug = _pxLog;
        } catch (e) {}

        if (typeof Promise.allSettled !== 'function') {
            Promise.allSettled = function (iterable) {
                var promises = Array.prototype.slice.call(iterable);
                return new Promise(function (resolve) {
                    if (promises.length === 0) { resolve([]); return; }
                    var results = new Array(promises.length);
                    var remaining = promises.length;
                    for (var i = 0; i < promises.length; i++) {
                        (function (idx, p) {
                            var wrapped = (p && typeof p.then === 'function') ? p : Promise.resolve(p);
                            wrapped.then(function (value) {
                                results[idx] = { status: 'fulfilled', value: value };
                                if (--remaining === 0) resolve(results);
                            }, function (reason) {
                                results[idx] = { status: 'rejected', reason: reason };
                                if (--remaining === 0) resolve(results);
                            });
                        })(i, promises[i]);
                    }
                });
            };
        }

        var _timeoutCallbacks = new Map();
        var _timeoutId = 0;
        function _setTimeout(callback, timeout) {
            if (typeof callback !== 'function') throw new Error('callback required a function');
            var ms = Number(timeout) || 0;
            if (ms < 0) ms = 0;
            var id = _timeoutId++;
            _timeoutCallbacks.set(id, { callback: callback, params: Array.prototype.slice.call(arguments, 2) });
            __pixelplay_set_timeout(id, Math.floor(ms));
            return id;
        }
        function _clearTimeout(id) {
            _timeoutCallbacks.delete(id);
            try { __pixelplay_clear_timeout(id); } catch (e) {}
        }
        globalThis.setTimeout = _setTimeout;
        globalThis.clearTimeout = _clearTimeout;
        globalThis.__pixelplay_fire_timeout = function (id) {
            var target = _timeoutCallbacks.get(id);
            if (!target) return;
            _timeoutCallbacks.delete(id);
            target.callback.apply(null, target.params);
        };

        var _requestQueue = new Map();
        function _sendNativeRequest(url, options, callback) {
            var requestKey = 'r' + Date.now() + '_' + Math.random().toString().substring(2);
            var requestInfo = {
                aborted: false,
                abort: function () {
                    try { __pixelplay_cancel_request(requestKey); } catch (e) {}
                }
            };
            _requestQueue.set(requestKey, { callback: callback, requestInfo: requestInfo });
            try {
                __pixelplay_native_call__('request', JSON.stringify({ requestKey: requestKey, url: url, options: options || {} }));
            } catch (e) {
                _requestQueue.delete(requestKey);
                callback(new Error('native request failed: ' + (e && e.message ? e.message : e)), null);
            }
            return requestInfo;
        }
        globalThis.__pixelplay_http_response = function (jsonStr) {
            var data;
            try { data = JSON.parse(jsonStr); } catch (e) { return; }
            var target = _requestQueue.get(data.requestKey);
            if (!target) return;
            _requestQueue.delete(data.requestKey);
            target.requestInfo.aborted = true;
            if (data.error == null && data.response != null) target.callback(null, data.response);
            else target.callback(new Error(data.error || 'request failed'), null);
        };

        function bytesToString(bytes) {
            var result = '';
            var i = 0;
            while (i < bytes.length) {
                var byte = bytes[i];
                if (byte < 128) {
                    result += String.fromCharCode(byte);
                    i++;
                } else if (byte >= 192 && byte < 224) {
                    result += String.fromCharCode(((byte & 31) << 6) | (bytes[i + 1] & 63));
                    i += 2;
                } else {
                    result += String.fromCharCode(((byte & 15) << 12) | ((bytes[i + 1] & 63) << 6) | (bytes[i + 2] & 63));
                    i += 3;
                }
            }
            return result;
        }
        function stringToBytes(str) {
            var bytes = [];
            for (var i = 0; i < str.length; i++) {
                var charCode = str.charCodeAt(i);
                if (charCode < 128) {
                    bytes.push(charCode);
                } else if (charCode < 2048) {
                    bytes.push((charCode >> 6) | 192);
                    bytes.push((charCode & 63) | 128);
                } else {
                    bytes.push((charCode >> 12) | 224);
                    bytes.push(((charCode >> 6) & 63) | 128);
                    bytes.push((charCode & 63) | 128);
                }
            }
            return bytes;
        }
        function dataToB64(data) {
            if (typeof data === 'string') return __pixelplay_str2b64(data);
            if (Array.isArray(data) || ArrayBuffer.isView(data)) return utils.buffer.bufToString(data, 'base64');
            throw new Error('data type error: ' + typeof data);
        }

        var utils = {
            crypto: {
                aesEncrypt: function (buffer, mode, key, iv) {
                    switch (mode) {
                        case 'aes-128-cbc':
                            return utils.buffer.from(__pixelplay_aes_encrypt(dataToB64(buffer), dataToB64(key), dataToB64(iv), 'CBC_128_PKCS7Padding'), 'base64');
                        case 'aes-128-ecb':
                            return utils.buffer.from(__pixelplay_aes_encrypt(dataToB64(buffer), dataToB64(key), '', 'ECB_128_NoPadding'), 'base64');
                        default:
                            throw new Error('Binary encoding is not supported for input strings');
                    }
                },
                rsaEncrypt: function (buffer, key) {
                    if (typeof key !== 'string') throw new Error('Invalid RSA key');
                    key = key.replace('-----BEGIN PUBLIC KEY-----', '')
                        .replace('-----END PUBLIC KEY-----', '');
                    return utils.buffer.from(__pixelplay_rsa_encrypt(dataToB64(buffer), key, 'NoPadding'), 'base64');
                },
                randomBytes: function (size) {
                    var byteArray = new Uint8Array(size);
                    for (var i = 0; i < size; i++) byteArray[i] = Math.floor(Math.random() * 256);
                    return byteArray;
                },
                md5: function (str) {
                    if (typeof str !== 'string') throw new Error('param required a string');
                    return __pixelplay_md5(encodeURIComponent(str));
                }
            },
            buffer: {
                from: function (input, encoding) {
                    if (typeof input === 'string') {
                        switch (encoding) {
                            case 'binary':
                                throw new Error('Binary encoding is not supported for input strings');
                            case 'base64':
                                return new Uint8Array(JSON.parse(__pixelplay_b642buf(input)));
                            case 'hex':
                                return new Uint8Array(input.match(/.{1,2}/g).map(function (b) { return parseInt(b, 16); }));
                            default:
                                return new Uint8Array(stringToBytes(input));
                        }
                    } else if (Array.isArray(input)) {
                        return new Uint8Array(input);
                    } else {
                        throw new Error('Unsupported input type: ' + input + ' encoding: ' + encoding);
                    }
                },
                bufToString: function (buf, format) {
                    if (Array.isArray(buf) || ArrayBuffer.isView(buf)) {
                        switch (format) {
                            case 'binary':
                                return buf;
                            case 'hex':
                                return new Uint8Array(buf).reduce(function (str, byte) { return str + byte.toString(16).padStart(2, '0'); }, '');
                            case 'base64':
                                return __pixelplay_str2b64(bytesToString(Array.from(buf)));
                            case 'utf8':
                            case 'utf-8':
                            default:
                                return bytesToString(Array.from(buf));
                        }
                    } else {
                        throw new Error('Input is not a valid buffer: ' + buf + ' format: ' + format);
                    }
                }
            }
        };

        var EVENT_NAMES = { request: 'request', inited: 'inited', updateAlert: 'updateAlert' };
        var _requestHandler = null;
        var lx = {
            EVENT_NAMES: EVENT_NAMES,
            request: function (url, options, callback) {
                var opts = options || {};
                var sendOpts = { method: opts.method || 'get', binary: opts.binary === true };
                if (typeof opts.timeout === 'number' && opts.timeout > 0) sendOpts.timeout = Math.min(opts.timeout, 60000);
                if (opts.headers && typeof opts.headers === 'object') sendOpts.headers = opts.headers;
                if (opts.body != null) sendOpts.body = opts.body;
                if (opts.form) sendOpts.form = opts.form;
                if (opts.formData != null) sendOpts.formData = opts.formData;
                var requestInfo = _sendNativeRequest(url, sendOpts, function (err, resp) {
                    if (err) callback(err, null, null);
                    else callback(err, resp, resp && resp.body != null ? resp.body : null);
                });
                return function () {
                    if (!requestInfo.aborted) requestInfo.abort();
                    requestInfo = null;
                };
            },
            send: function (eventName, data) {
                return new Promise(function (resolve, reject) {
                    if (eventName === EVENT_NAMES.inited) {
                        try { _initedJson = JSON.stringify(data || { sources: {} }); }
                        catch (e) { _initedJson = JSON.stringify({ error: String(e && e.message ? e.message : e) }); }
                        _initedDone = true;
                        resolve();
                    } else if (eventName === EVENT_NAMES.updateAlert) {
                        reject(new Error('updateAlert is not supported'));
                    } else {
                        reject(new Error('The event is not supported: ' + eventName));
                    }
                });
            },
            on: function (eventName, handler) {
                if (eventName === EVENT_NAMES.request) {
                    _requestHandler = handler;
                    return Promise.resolve();
                }
                return Promise.reject(new Error('The event is not supported: ' + eventName));
            },
            utils: utils,
            currentScriptInfo: {
                name: '',
                description: '',
                version: '',
                author: '',
                homepage: '',
                rawScript: ''
            },
            version: '2.0.0',
            env: 'mobile'
        };
        globalThis.lx = lx;

        globalThis.__pixelplay_call_js = function (callId, action, source, info) {
            var h = _requestHandler;
            if (!h) {
                globalThis.__pixelplay_native_pending_response(callId, JSON.stringify({ error: 'no request handler registered' }));
                return;
            }
            try {
                var reqData = { action: action, source: source, info: info };
                var res = h(reqData);
                if (res && typeof res.then === 'function') {
                    res.then(function (v) {
                        var json;
                        try { json = JSON.stringify({ value: v }); }
                        catch (e) { json = JSON.stringify({ error: String(e && e.message ? e.message : e) }); }
                        globalThis.__pixelplay_native_pending_response(callId, json);
                    }, function (e) {
                        globalThis.__pixelplay_native_pending_response(callId, JSON.stringify({ error: e ? (e.message || String(e)) : 'rejected' }));
                    });
                    return;
                }
                globalThis.__pixelplay_native_pending_response(callId, JSON.stringify({ value: res }));
            } catch (e) {
                globalThis.__pixelplay_native_pending_response(callId, JSON.stringify({ error: e ? (e.message || String(e)) : 'thrown' }));
            }
        };
        globalThis.__pixelplay_pending_response = function (callId, json) {
            globalThis.__pixelplay_native_pending_response(callId, json);
        };
        globalThis.__pixelplay_get_inited_js = function () { return _initedJson; };
        globalThis.__pixelplay_get_done_js = function () { return _initedDone ? 1 : 0; };

        try {
            (function freezeObject(obj) {
                if (typeof obj !== 'object') return;
                Object.freeze(obj);
                for (var k in obj) {
                    try { freezeObject(obj[k]); } catch (e) {}
                }
            })(lx);
            globalThis.eval = function () { throw new Error('eval is not available'); };
        } catch (e) {}
    })();
    """;
}
