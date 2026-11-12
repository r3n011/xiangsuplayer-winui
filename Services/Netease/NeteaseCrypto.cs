using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PixelPlayer.WinUI.Services.Netease;

/// <summary>
/// 网易云加密工具（对齐参照项目 NeteaseEncryption.kt，支持 weapi / eapi / linuxapi / api）。
/// </summary>
public static class NeteaseCrypto
{
    private const string Base62 = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    private const string PresetKey = "0CoJUm6Qyw8W8jud";
    private const string Iv = "0102030405060708";
    private const string LinuxKey = "rFgB&h#%2?^eDg:Q";
    private const string EapiKey = "e82ckenh8dichen8";
    private const string EapiFormat = "{0}-36cd479b6b5-{1}-36cd479b6b5-{2}";
    private const string EapiSalt = "nobody{0}use{1}md5forencrypt";

    private const string PublicKeyPem =
        "MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQDgtQn2JZ34ZC28NWYpAUd98iZ37BUrX/aKzmFb" +
        "t7clFSs6sXqHauqKWqdtLkF2KexO40H1YTX8z2lSgBBOAxLsvaklV8k4cBFK9snQXE9/DDaFt6Rr7iVZ" +
        "MldczhC0JNgTz+SHXT6CBHuX3e9SdB1Ua44oncaTWz7OBGLbCiK45wIDAQAB";

    private static readonly Random _random = new();

    public static string Md5Hex(string data)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(data));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string RandomKey()
    {
        var sb = new StringBuilder(16);
        for (var i = 0; i < 16; i++) sb.Append(Base62[_random.Next(Base62.Length)]);
        return sb.ToString();
    }

    private static byte[] AesEncrypt(string text, string key, string? iv, CipherMode mode)
    {
        using var aes = Aes.Create();
        aes.Key = Encoding.UTF8.GetBytes(key);
        if (mode == CipherMode.CBC)
        {
            aes.Mode = CipherMode.CBC;
            aes.IV = Encoding.UTF8.GetBytes(iv!);
        }
        else
        {
            aes.Mode = CipherMode.ECB;
        }
        aes.Padding = PaddingMode.PKCS7;
        using var enc = aes.CreateEncryptor();
        return enc.TransformFinalBlock(Encoding.UTF8.GetBytes(text), 0, Encoding.UTF8.GetByteCount(text));
    }

    private static string RsaEncrypt(string text)
    {
        // X.509 SubjectPublicKeyInfo → RSA 公钥（ modulus + exponent）
        var spki = Convert.FromBase64String(PublicKeyPem);
        // SPKI 结构手工解析：跳过 AlgorithmIdentifier，取 BIT STRING 里的 RSAPublicKey
        // 简化：用 AsnReader 太啰嗦，这里按已知结构定位 modulus（RSA 1024 = 128 字节，前置 00）
        // SPKI: SEQUENCE { SEQUENCE{oid...} BIT STRING { SEQUENCE{ modulus INTEGER, exponent INTEGER } } }
        var rsaKey = ParseSpkiRsaKey(spki);
        var message = new System.Numerics.BigInteger(
            text.Select(b => (byte)b).Reverse().Concat(new byte[] { 0 }).ToArray()); // 大端无符号
        var result = System.Numerics.BigInteger.ModPow(message, rsaKey.exponent, rsaKey.modulus);
        var bytes = result.ToByteArray(); // little-endian，含符号位
        Array.Reverse(bytes);
        if (bytes.Length > 0 && bytes[0] == 0) bytes = bytes[1..];
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static (System.Numerics.BigInteger modulus, System.Numerics.BigInteger exponent) ParseSpkiRsaKey(byte[] spki)
    {
        var idx = 0;
        // outer SEQUENCE
        idx++; // 0x30
        idx += ReadLen(spki, idx, out _);
        // algorithm SEQUENCE
        idx++; // 0x30
        idx += ReadLen(spki, idx, out var algLen);
        idx += algLen;
        // BIT STRING
        idx++; // 0x03
        idx += ReadLen(spki, idx, out _);
        idx++; // unused bits = 0
        // RSAPublicKey SEQUENCE
        idx++; // 0x30
        idx += ReadLen(spki, idx, out _);
        // modulus INTEGER
        idx++; // 0x02
        idx += ReadLen(spki, idx, out var modLen);
        var modulusBytes = spki[idx..(idx + modLen)];
        idx += modLen;
        // exponent INTEGER
        idx++; // 0x02
        idx += ReadLen(spki, idx, out var expLen);
        var exponentBytes = spki[idx..(idx + expLen)];

        return (ToBigInteger(modulusBytes), ToBigInteger(exponentBytes));
    }

    private static System.Numerics.BigInteger ToBigInteger(byte[] bigEndian) =>
        new(bigEndian.Reverse().Concat(new byte[] { 0 }).ToArray());

    private static int ReadLen(byte[] data, int pos, out int headerLen)
    {
        var b = data[pos];
        if (b < 0x80)
        {
            headerLen = 1;
            return b;
        }
        var n = b & 0x7F;
        headerLen = 1 + n;
        var len = 0;
        for (var i = 0; i < n; i++) len = (len << 8) | data[pos + 1 + i];
        return len;
    }

    /// <summary>weapi：双重 AES-CBC + RSA（params + encSecKey）。</summary>
    public static (string Params, string EncSecKey) WeApiEncrypt(string json)
    {
        var secretKey = RandomKey();
        var enc1 = Convert.ToBase64String(AesEncrypt(json, PresetKey, Iv, CipherMode.CBC));
        var @params = Convert.ToBase64String(AesEncrypt(enc1, secretKey, Iv, CipherMode.CBC));
        var encSecKey = RsaEncrypt(new string(secretKey.Reverse().ToArray()));
        return (@params, encSecKey);
    }

    /// <summary>eapi：AES-ECB + MD5 摘要（登录 / 歌曲 URL）。</summary>
    public static string EApiEncrypt(string urlPath, string data)
    {
        var apiUrl = urlPath.Replace("/eapi", "/api");
        var message = string.Format(EapiFormat, apiUrl, data, Md5Hex(string.Format(EapiSalt, apiUrl, data)));
        return Convert.ToHexString(AesEncrypt(message, EapiKey, null, CipherMode.ECB));
    }

    /// <summary>linuxapi：AES-ECB 固定 key（eparams hex）。</summary>
    public static string LinuxApiEncrypt(string json) =>
        Convert.ToHexString(AesEncrypt(json, LinuxKey, null, CipherMode.ECB));

    /// <summary>把参数字典序列化为紧凑 JSON（对齐参照项目手写序列化器，保持键序）。</summary>
    public static string ToJson(IEnumerable<KeyValuePair<string, object?>> kv)
    {
        var sb = new StringBuilder("{");
        var first = true;
        foreach (var (k, v) in kv)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append('"').Append(k).Append("\":").Append(ToJsonValue(v));
        }
        sb.Append('}');
        return sb.ToString();
    }

    public static string ToJson(IReadOnlyDictionary<string, object?> map) => ToJson(map.AsEnumerable());

    private static string ToJsonValue(object? v) => v switch
    {
        null => "null",
        string s => JsonQuote(s),
        bool b => b ? "true" : "false",
        int or long or double or float => Convert.ToString(v, CultureInfo.InvariantCulture),
        _ => JsonQuote(v.ToString() ?? ""),
    };

    private static string JsonQuote(string s)
    {
        var sb = new StringBuilder(s.Length + 16);
        sb.Append('"');
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (ch < ' ') sb.Append("\\u").Append(((int)ch).ToString("x4"));
                    else sb.Append(ch);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
}

/// <summary>网易云 API 请求层（weapi/eapi/api 三模式 + Cookie 持久化，对齐 NeteaseApiService.kt）。</summary>
public sealed class NeteaseApi
{
    public const string WeapiUserAgent =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36 Edg/124.0.0.0";
    public const string EapiUserAgent = "NeteaseMusic 9.0.90/5038 (iPhone; iOS 16.2; zh_CN)";

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private string? _deviceId;

    public Dictionary<string, string> Cookies { get; private set; } = new();

    public void SetCookies(Dictionary<string, string> cookies) => Cookies = cookies;
    public bool HasLogin => Cookies.ContainsKey("MUSIC_U");
    public string? CookieValue(string name) => Cookies.TryGetValue(name, out var v) ? v : null;

    private string GetOrCreateDeviceId()
    {
        if (_deviceId is not null) return _deviceId;
        if (Cookies.TryGetValue("deviceId", out var d) && !string.IsNullOrEmpty(d)) { _deviceId = d; return d; }
        const string hexChars = "0123456789ABCDEF";
        var sb = new StringBuilder(52);
        for (var i = 0; i < 52; i++) sb.Append(hexChars[Random.Shared.Next(hexChars.Length)]);
        _deviceId = sb.ToString();
        Cookies["deviceId"] = _deviceId;
        return _deviceId;
    }

    public enum Mode { WeApi, EApi, Api, LinuxApi }

    public async Task<string> RequestAsync(
        string url, Dictionary<string, object?> @params, Mode mode, bool useCookies = true,
        string method = "POST", CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(new HttpMethod(method), url);

        Dictionary<string, string> bodyParams;
        var uri = new Uri(url);
        if (mode == Mode.WeApi)
        {
            req.Headers.TryAddWithoutValidation("Referer", "https://music.163.com");
            req.Headers.TryAddWithoutValidation("User-Agent", WeapiUserAgent);
            var p = new Dictionary<string, object?>(@params)
            {
                ["csrf_token"] = CookieValue("__csrf") ?? "",
            };
            if (useCookies) AddCookieHeader(req);
            var (encParams, encSecKey) = NeteaseCrypto.WeApiEncrypt(NeteaseCrypto.ToJson(p));
            bodyParams = new Dictionary<string, string> { ["params"] = encParams, ["encSecKey"] = encSecKey };
        }
        else if (mode == Mode.EApi)
        {
            req.Headers.TryAddWithoutValidation("User-Agent", EapiUserAgent);
            if (useCookies) AddCookieHeader(req, headerCookie: true);
            var p = new Dictionary<string, object?>(@params)
            {
                ["header"] = BuildEapiHeader(),
                ["e_r"] = false,
            };
            var enc = NeteaseCrypto.EApiEncrypt(uri.AbsolutePath, NeteaseCrypto.ToJson(p));
            bodyParams = new Dictionary<string, string> { ["params"] = enc };
        }
        else if (mode == Mode.LinuxApi)
        {
            req.Headers.TryAddWithoutValidation("Referer", "https://music.163.com");
            req.Headers.TryAddWithoutValidation("User-Agent", WeapiUserAgent);
            if (useCookies) AddCookieHeader(req);
            bodyParams = new Dictionary<string, string>
            {
                ["eparams"] = NeteaseCrypto.LinuxApiEncrypt(NeteaseCrypto.ToJson(@params)),
            };
        }
        else
        {
            req.Headers.TryAddWithoutValidation("Referer", "https://music.163.com");
            req.Headers.TryAddWithoutValidation("User-Agent", WeapiUserAgent);
            if (useCookies) AddCookieHeader(req);
            bodyParams = @params.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? "");
        }

        if (method == "POST")
        {
            req.Content = new FormUrlEncodedContent(bodyParams);
        }
        else
        {
            var query = string.Join("&", bodyParams.Select(kv =>
                $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
            req.RequestUri = new Uri(url + (url.Contains('?') ? "&" : "?") + query);
        }

        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    private void AddCookieHeader(HttpRequestMessage req, bool headerCookie = false)
    {
        string cookie;
        if (headerCookie)
        {
            var header = BuildEapiHeader();
            cookie = "os=pc; appver=8.10.35; " + string.Join("; ",
                header.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        }
        else
        {
            cookie = string.Join("; ", Cookies.Select(kv => $"{kv.Key}={kv.Value}"));
        }
        if (cookie.Length > 0) req.Headers.TryAddWithoutValidation("Cookie", cookie);
    }

    private Dictionary<string, string> BuildEapiHeader()
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        var header = new Dictionary<string, string>
        {
            ["osver"] = "Microsoft-Windows-10-Professional-build-19045-64bit",
            ["deviceId"] = GetOrCreateDeviceId(),
            ["os"] = "pc",
            ["appver"] = "8.10.35",
            ["versioncode"] = "140",
            ["mobilename"] = "",
            ["buildver"] = now.ToString().Substring(0, 10),
            ["resolution"] = "1920x1080",
            ["__csrf"] = CookieValue("__csrf") ?? "",
            ["channel"] = "netease",
            ["requestId"] = $"{now}_{Random.Shared.Next(1000):D4}",
        };
        if (CookieValue("MUSIC_U") is { Length: > 0 } u) header["MUSIC_U"] = u;
        if (CookieValue("MUSIC_A") is { Length: > 0 } a) header["MUSIC_A"] = a;
        return header;
    }

    // ─── 便捷方法（端点对齐参照项目）──────────────────────────────────

    public Task<string> SendCaptchaAsync(string phone, int ctcode = 86, CancellationToken ct = default) =>
        RequestAsync("https://interface.music.163.com/weapi/sms/captcha/sent",
            new Dictionary<string, object?> { ["cellphone"] = phone, ["ctcode"] = ctcode.ToString() },
            Mode.WeApi, useCookies: false, ct: ct);

    public Task<string> LoginByCaptchaAsync(string phone, string captcha, int ctcode = 86, CancellationToken ct = default) =>
        RequestAsync("https://interface.music.163.com/eapi/w/login/cellphone",
            new Dictionary<string, object?>
            {
                ["phone"] = phone,
                ["countrycode"] = ctcode.ToString(),
                ["remember"] = "true",
                ["type"] = "1",
                ["captcha"] = captcha,
            },
            Mode.EApi, useCookies: false, ct: ct);

    public Task<string> GetCurrentUserAccountAsync(CancellationToken ct = default) =>
        RequestAsync("https://music.163.com/weapi/w/nuser/account/get", new(), Mode.WeApi, ct: ct);

    public Task<string> GetUserPlaylistsAsync(long userId, int offset, int limit = 50, CancellationToken ct = default) =>
        RequestAsync("https://music.163.com/weapi/user/playlist",
            new Dictionary<string, object?>
            {
                ["uid"] = userId.ToString(),
                ["offset"] = offset.ToString(),
                ["limit"] = limit.ToString(),
                ["includeVideo"] = "true",
            },
            Mode.WeApi, ct: ct);

    public Task<string> GetPlaylistDetailAsync(long playlistId, CancellationToken ct = default) =>
        RequestAsync("https://music.163.com/api/v6/playlist/detail",
            new Dictionary<string, object?> { ["id"] = playlistId.ToString(), ["n"] = "100000", ["s"] = "8" },
            Mode.Api, ct: ct);

    public Task<string> GetSongDetailsAsync(IReadOnlyList<long> songIds, CancellationToken ct = default)
    {
        if (songIds.Count == 0)
        {
            return Task.FromResult("""{"code":200,"songs":[]}""");
        }
        var ids = "[" + string.Join(",", songIds) + "]";
        var c = "[" + string.Join(",", songIds.Select(id => """{"id":""" + id + """}""")) + "]";
        return RequestAsync("https://music.163.com/api/v3/song/detail",
            new Dictionary<string, object?> { ["ids"] = ids, ["c"] = c },
            Mode.Api, ct: ct);
    }
}
