using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using PixelPlayer.WinUI.Models;

namespace PixelPlayer.WinUI.Services;

/// <summary>局域网网页遥控（对应参照项目 WebRemoteServerService）。TcpListener 裸 HTTP，无需管理员权限。</summary>
public sealed class WebRemoteService
{
    public static WebRemoteService Instance { get; } = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();

    public bool IsRunning { get; private set; }
    public string? ListenUrl { get; private set; }

    public event EventHandler? StateChanged;

    private WebRemoteService() { }

    public string LocalAddress
    {
        get
        {
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
                socket.Connect("223.5.5.5", 53);
                if (socket.LocalEndPoint is IPEndPoint ep) return $"{ep.Address}:{ListenPort}";
            }
            catch { /* 回退 */ }
            return $"127.0.0.1:{ListenPort}";
        }
    }

    private int ListenPort => SettingsService.Instance.Settings.WebRemotePort;

    public void Start()
    {
        if (IsRunning) return;
        try
        {
            _listener = new TcpListener(IPAddress.Any, ListenPort);
            _listener.Start();
        }
        catch (Exception ex)
        {
            App.LogCrash("WebRemote.Start", ex);
            _listener = null;
            return;
        }

        IsRunning = true;
        ListenUrl = $"http://{LocalAddress}/";
        _cts = new CancellationTokenSource();
        _ = AcceptLoopAsync(_cts.Token);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;
        try
        {
            _cts?.Cancel();
            _listener?.Stop();
        }
        catch { /* 忽略 */ }
        _listener = null;
        ListenUrl = null;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        var listener = _listener;
        while (listener is not null && !token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(token);
            }
            catch
            {
                break;
            }

            _ = Task.Run(() => HandleClient(client), CancellationToken.None);
        }
    }

    private async Task HandleClient(TcpClient client)
    {
        try
        {
            using var _ = client;
            client.ReceiveTimeout = 5000;
            client.SendTimeout = 5000;

            var stream = client.GetStream();
            var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);

            // 请求行: GET /api/status HTTP/1.1
            var requestLine = await reader.ReadLineAsync();
            if (string.IsNullOrEmpty(requestLine)) return;
            var parts = requestLine.Split(' ');
            if (parts.Length < 2) return;

            var url = parts[1];
            var pathEnd = url.IndexOf('?');
            var path = pathEnd >= 0 ? url[..pathEnd] : url;
            var query = pathEnd >= 0
                ? System.Web.HttpUtility.ParseQueryString(url[(pathEnd + 1)..])
                : null;

            // 读完剩余请求头（到空行）
            string? line;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync())) { }

            switch (path)
            {
                case "/":
                    await WriteAsync(stream, 200, "text/html; charset=utf-8", RemoteHtml);
                    return;
                case "/api/status":
                    await WriteAsync(stream, 200, "application/json; charset=utf-8", StatusJson());
                    return;
                case "/api/toggle":
                    _dispatcher.TryEnqueue(() => PlaybackService.Instance.TogglePlayPause());
                    await WriteAsync(stream, 200, "application/json", "{\"ok\":true}");
                    return;
                case "/api/next":
                    _dispatcher.TryEnqueue(() => PlaybackService.Instance.Next());
                    await WriteAsync(stream, 200, "application/json", "{\"ok\":true}");
                    return;
                case "/api/prev":
                    _dispatcher.TryEnqueue(() => PlaybackService.Instance.Previous());
                    await WriteAsync(stream, 200, "application/json", "{\"ok\":true}");
                    return;
                case "/api/volume" when query is not null:
                    if (double.TryParse(query["v"], out var vol))
                    {
                        var v = Math.Clamp(vol, 0, 100) / 100.0;
                        _dispatcher.TryEnqueue(() => PlaybackService.Instance.Volume = v);
                    }
                    await WriteAsync(stream, 200, "application/json", "{\"ok\":true}");
                    return;
                case "/api/seek" when query is not null:
                    if (double.TryParse(query["s"], out var sec))
                    {
                        _dispatcher.TryEnqueue(() => PlaybackService.Instance.Seek(TimeSpan.FromSeconds(sec)));
                    }
                    await WriteAsync(stream, 200, "application/json", "{\"ok\":true}");
                    return;
                default:
                    await WriteAsync(stream, 404, "text/plain", "not found");
                    return;
            }
        }
        catch (Exception ex)
        {
            App.LogCrash("WebRemote.Handle", ex);
        }
    }

    private static async Task WriteAsync(NetworkStream stream, int code, string contentType, string body)
    {
        var payload = Encoding.UTF8.GetBytes(body);
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {code} {(code == 200 ? "OK" : "Not Found")}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {payload.Length}\r\n" +
            "Connection: close\r\n" +
            "\r\n");
        await stream.WriteAsync(head, 0, head.Length);
        await stream.WriteAsync(payload, 0, payload.Length);
        await stream.FlushAsync();
    }

    private string StatusJson()
    {
        var player = PlaybackService.Instance;
        var track = player.Current;
        var payload = new
        {
            playing = player.IsPlaying,
            title = track?.Title ?? "",
            artist = track?.Artist ?? "",
            album = track?.Album ?? "",
            position = player.Position.TotalSeconds,
            duration = track?.DurationSeconds ?? 0,
            volume = (int)Math.Round(player.Volume * 100),
        };
        return JsonSerializer.Serialize(payload);
    }

    private const string RemoteHtml = """
        <!DOCTYPE html>
        <html lang="zh">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <title>像素播放器 · 网页遥控</title>
        <style>
          :root { color-scheme: dark; }
          body { font-family: "Segoe UI",system-ui,sans-serif; background:#0f1827; color:#e8edf5;
                 display:flex; flex-direction:column; align-items:center; justify-content:center;
                 min-height:100vh; margin:0; gap:18px; }
          .card { background:#1a2333; border-radius:18px; padding:26px 30px; width:min(420px,92vw);
                  box-shadow:0 8px 30px rgba(0,0,0,.4); }
          h1 { font-size:16px; margin:0 0 14px; color:#8ab4ff; }
          .title { font-size:20px; font-weight:600; text-align:center; margin:4px 0 2px; }
          .artist { font-size:14px; color:#9aa7bd; text-align:center; margin-bottom:14px; }
          .time { display:flex; justify-content:space-between; font-size:12px; color:#9aa7bd; margin-top:4px; }
          .bar { height:6px; background:#2a3550; border-radius:3px; overflow:hidden; cursor:pointer; }
          .bar > i { display:block; height:100%; background:#5c9cff; }
          .controls { display:flex; justify-content:center; gap:14px; margin-top:18px; }
          button { border:0; background:#2a3550; color:#e8edf5; width:52px; height:52px; border-radius:50%;
                   font-size:18px; cursor:pointer; transition:.15s; }
          button:hover { background:#38466a; }
          button.main { width:66px; height:66px; background:#5c9cff; font-size:24px; }
          .vol { display:flex; align-items:center; gap:10px; margin-top:16px; font-size:13px; }
          input[type=range] { flex:1; accent-color:#5c9cff; }
          .foot { font-size:11px; color:#6b7891; text-align:center; }
        </style>
        </head>
        <body>
        <div class="card">
          <h1>像素播放器 · 网页遥控</h1>
          <div class="title" id="t">—</div>
          <div class="artist" id="a"></div>
          <div class="bar"><i id="p" style="width:0%"></i></div>
          <div class="time"><span id="pos">0:00</span><span id="dur">0:00</span></div>
          <div class="controls">
            <button onclick="api('prev')">⏮</button>
            <button class="main" id="pp" onclick="api('toggle')">▶</button>
            <button onclick="api('next')">⏭</button>
          </div>
          <div class="vol">🔈 <input type="range" id="v" min="0" max="100" oninput="setVol(this.value)"> <span id="vv">80</span></div>
        </div>
        <div class="foot">与电脑在同一局域网内即可遥控</div>
        <script>
        async function refresh(){
          try{
            const s = await (await fetch('/api/status')).json();
            t.textContent=s.title||'未在播放'; a.textContent=s.artist+(s.album?' · '+s.album:'');
            pp.textContent=s.playing?'⏸':'▶';
            p.style.width=(s.duration? s.position/s.duration*100:0)+'%';
            pos.textContent=fmt(s.position); dur.textContent=fmt(s.duration);
            v.value=s.volume; vv.textContent=s.volume;
            window._dur=s.duration;
          }catch(e){}
        }
        function fmt(x){x=Math.max(0,x|0);const m=x/60|0;return m+':'+String(x%60).padStart(2,'0');}
        async function api(k){await fetch('/api/'+k);refresh();}
        let volT=null;
        function setVol(v){vv.textContent=v;clearTimeout(volT);volT=setTimeout(()=>fetch('/api/volume?v='+v),150);}
        function seek(e){
          const r=e.currentTarget.getBoundingClientRect();
          const pct=(e.clientX-r.left)/r.width;
          if(window._dur) fetch('/api/seek?s='+pct*window._dur);
        }
        document.querySelector('.bar').addEventListener('click',seek);
        refresh(); setInterval(refresh,1000);
        </script>
        </body>
        </html>
        """;
}
