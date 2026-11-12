using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PixelPlayer.WinUI.Services;

/// <summary>
/// 本地流代理：为需要特殊请求头（Referer/Cookie，如 Bilibili CDN）的远程音频做转发，
/// 提供形如 http://127.0.0.1:{port}/p/?u=&amp;h= 的本机直链，支持 Range（播放器拖动进度）。
/// 对应参照项目 CloudStreamProxy 系列的职责。
/// </summary>
public sealed class StreamProxyService
{
    public static StreamProxyService Instance { get; } = new();

    private TcpListener? _listener;
    private int _port;
    private static readonly HttpClient _http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        });
        c.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120 PixelPlayer/1.0");
        c.Timeout = Timeout.InfiniteTimeSpan;
        return c;
    }

    public int Port => _port;

    public void Start(int port)
    {
        if (_listener is not null && _port == port) return;
        Stop();
        _port = port;
        _listener = new TcpListener(IPAddress.Loopback, port);
        try
        {
            _listener.Start();
        }
        catch (Exception)
        {
            // 端口被占用则换一个
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        }

        var thread = new Thread(() => AcceptLoop()) { IsBackground = true };
        thread.Start();
    }

    public void Stop()
    {
        _listener?.Stop();
        _listener = null;
    }

    private async void AcceptLoop()
    {
        var listener = _listener;
        if (listener is null) return;

        while (_listener is not null)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync();
                _ = Task.Run(() => HandleAsync(client));
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                App.LogCrash("StreamProxy.Accept", ex);
            }
        }
    }

    private sealed record ProxyTarget(string Url, string Referer, string Cookie, string UserAgent);

    private async Task HandleAsync(TcpClient client)
    {
        try
        {
            using var _ = client;
            var stream = client.GetStream();
            var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync();
            if (requestLine is null) return;

            // 读掉其余 header（我们不解析，直接按请求处理 Range 由请求行后的 Range 头提供）
            string range = "";
            while (true)
            {
                var line = await reader.ReadLineAsync();
                if (string.IsNullOrEmpty(line)) break;
                if (line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
                    range = line.Substring(6).Trim();
            }

            // GET /p/?u=..&h=.. HTTP/1.1
            var parts = requestLine.Split(' ');
            if (parts.Length < 2 || !parts[0].StartsWith("GET", StringComparison.OrdinalIgnoreCase))
            {
                await WriteRawAsync(stream, "HTTP/1.1 405 Method Not Allowed\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                return;
            }

            var uri = new Uri("http://localhost" + Uri.UnescapeDataString(parts[1]));
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var u = query["u"] ?? "";
            var h = query["h"] ?? "";

            ProxyTarget target;
            try
            {
                var json = JsonSerializer.Deserialize<ProxyTarget>(DecodeB64(h)) ?? new ProxyTarget(DecodeB64(u), "", "", "");
                target = json.Url.Length == 0 ? new ProxyTarget(DecodeB64(u), json.Referer, json.Cookie, json.UserAgent) : json;
            }
            catch
            {
                target = new ProxyTarget(DecodeB64(u), "", "", "");
            }

            await ForwardAsync(stream, target, range);
        }
        catch (Exception ex)
        {
            App.LogCrash("StreamProxy.Handle", ex);
        }
    }

    private static string DecodeB64(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        try
        {
            var padded = s.Replace('-', '+').Replace('_', '/').PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
            return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
        }
        catch { return ""; }
    }

    /// <summary>生成代理直链。headers: Referer/Cookie/UserAgent（可空）。</summary>
    public static string ProxyUrl(string url, string referer = "", string cookie = "", string userAgent = "")
    {
        string b64(string s)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        }

        var port = Instance._port == 0 ? 9850 : Instance._port;
        var headers = new ProxyTarget("", referer, cookie, userAgent);
        var hJson = JsonSerializer.Serialize(headers);
        return $"http://127.0.0.1:{port}/p/?u={b64(url)}&h={b64(hJson)}";
    }

    private async Task ForwardAsync(NetworkStream stream, ProxyTarget target, string range)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, target.Url);
        if (!string.IsNullOrEmpty(range))
        {
            request.Headers.TryAddWithoutValidation("Range", range);
        }
        if (!string.IsNullOrEmpty(target.Referer))
        {
            request.Headers.TryAddWithoutValidation("Referer", target.Referer);
        }
        if (!string.IsNullOrEmpty(target.Cookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", target.Cookie);
        }
        if (!string.IsNullOrEmpty(target.UserAgent))
        {
            request.Headers.TryAddWithoutValidation("User-Agent", target.UserAgent);
        }

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append((int)response.StatusCode).Append(' ').Append(response.ReasonPhrase).Append("\r\n");

        foreach (var header in response.Headers)
        {
            foreach (var v in header.Value)
            {
                sb.Append(header.Key).Append(": ").Append(v).Append("\r\n");
            }
        }
        foreach (var header in response.Content.Headers)
        {
            foreach (var v in header.Value)
            {
                sb.Append(header.Key).Append(": ").Append(v).Append("\r\n");
            }
        }
        sb.Append("Connection: close\r\n\r\n");

        var headerBytes = Encoding.ASCII.GetBytes(sb.ToString());
        await stream.WriteAsync(headerBytes);

        await using var content = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await content.ReadAsync(buffer)) > 0)
        {
            await stream.WriteAsync(buffer.AsMemory(0, read));
        }
    }

    private static async Task WriteRawAsync(NetworkStream stream, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        await stream.WriteAsync(bytes);
    }
}
