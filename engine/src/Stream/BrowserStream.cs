using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace DollyForge;

/// <summary>
/// HTTP + WebSocket으로 FBO 프레임을 스트리밍한다.
/// 포트 7780, 경로 / (HTML 페이지), /ws (WebSocket 프레임 스트림), /input (키보드·마우스).
/// </summary>
sealed class BrowserStream : IDisposable
{
    const int    Port    = 7780;
    const int    JpegQ   = 72;

    readonly HttpListener _http = new();
    readonly List<WebSocket> _ws = new();
    readonly object _wsLock = new();

    // 입력 큐 (게임 루프가 드레인)
    public readonly Queue<InputEvent> InputQueue = new();
    readonly object _inputLock = new();

    volatile byte[]? _lastFrame;
    volatile bool _running;

    public void Start()
    {
        _http.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _http.Start();
        _running = true;
        Task.Run(AcceptLoop);
        Console.WriteLine($"[stream] http://127.0.0.1:{Port}/ — VS Code 통합 브라우저로 여세요");
    }

    // 게임 루프에서 새 RGBA 프레임 (Y 뒤집기 없음 → 아래에서 처리)을 넘긴다
    public void PushFrame(byte[] rgba, int w, int h)
    {
        // OpenGL 은 Y=0 이 아래: 상하 반전
        var flipped = FlipY(rgba, w, h);
        var jpeg = EncodeJpeg(flipped, w, h);
        _lastFrame = jpeg;
        BroadcastFrame(jpeg);
    }

    static byte[] FlipY(byte[] src, int w, int h)
    {
        var dst = new byte[src.Length];
        int row = w * 4;
        for (var y = 0; y < h; y++)
            Buffer.BlockCopy(src, (h-1-y)*row, dst, y*row, row);
        return dst;
    }

    static readonly JpegEncoder JpegEnc = new() { Quality = JpegQ };

    static byte[] EncodeJpeg(byte[] rgba, int w, int h)
    {
        using var img = Image.LoadPixelData<Rgba32>(rgba, w, h);
        using var ms  = new MemoryStream();
        img.SaveAsJpeg(ms, JpegEnc);
        return ms.ToArray();
    }

    void BroadcastFrame(byte[] jpeg)
    {
        List<WebSocket> dead = [];
        lock (_wsLock)
        {
            foreach (var ws in _ws)
            {
                if (ws.State != WebSocketState.Open) { dead.Add(ws); continue; }
                try { ws.SendAsync(jpeg, WebSocketMessageType.Binary, true, default).GetAwaiter().GetResult(); }
                catch { dead.Add(ws); }
            }
            foreach (var ws in dead) _ws.Remove(ws);
        }
    }

    async Task AcceptLoop()
    {
        while (_running)
        {
            HttpListenerContext ctx;
            try { ctx = await _http.GetContextAsync(); } catch { break; }
            _ = Task.Run(() => HandleRequest(ctx));
        }
    }

    async Task HandleRequest(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url?.AbsolutePath ?? "/";
        if (ctx.Request.IsWebSocketRequest && path == "/ws")
        {
            var wsc = await ctx.AcceptWebSocketAsync(null);
            lock (_wsLock) _ws.Add(wsc.WebSocket);
            if (_lastFrame is { } f) await wsc.WebSocket.SendAsync(f, WebSocketMessageType.Binary, true, default);
            await ReadInputLoop(wsc.WebSocket);
            return;
        }
        if (path == "/input" && ctx.Request.HttpMethod == "POST")
        {
            using var sr = new StreamReader(ctx.Request.InputStream);
            var json = await sr.ReadToEndAsync();
            try { ProcessInput(json); } catch { }
            ctx.Response.StatusCode = 204;
            ctx.Response.Close();
            return;
        }
        // HTML 페이지
        var html = BuildHtml();
        var bytes = Encoding.UTF8.GetBytes(html);
        ctx.Response.ContentType = "text/html;charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    async Task ReadInputLoop(WebSocket ws)
    {
        var buf = new byte[4096];
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var res = await ws.ReceiveAsync(buf, default);
                if (res.MessageType == WebSocketMessageType.Close) break;
                if (res.MessageType == WebSocketMessageType.Text)
                    ProcessInput(Encoding.UTF8.GetString(buf, 0, res.Count));
            }
        }
        catch { }
    }

    void ProcessInput(string json)
    {
        try
        {
            var doc = JsonDocument.Parse(json);
            var r   = doc.RootElement;
            var t   = r.GetProperty("type").GetString();
            var ev  = new InputEvent { Type = t ?? "" };
            if (r.TryGetProperty("key",    out var k)) ev.Key    = k.GetString() ?? "";
            if (r.TryGetProperty("dx",     out var dx)) ev.Dx    = dx.GetSingle();
            if (r.TryGetProperty("dy",     out var dy)) ev.Dy    = dy.GetSingle();
            if (r.TryGetProperty("button", out var bt)) ev.Button = bt.GetInt32();
            if (r.TryGetProperty("down",   out var dn)) ev.Down  = dn.GetBoolean();
            lock (_inputLock) InputQueue.Enqueue(ev);
        }
        catch { }
    }

    static string BuildHtml() => """
        <!DOCTYPE html>
        <html lang="ko">
        <head>
          <meta charset="utf-8">
          <title>Dolly Forge</title>
          <style>
            * { margin:0; padding:0; box-sizing:border-box; }
            body { background:#000; display:flex; justify-content:center; align-items:center; height:100vh; overflow:hidden; }
            canvas { image-rendering:pixelated; cursor:crosshair; display:block; max-width:100vw; max-height:100vh; }
            #info { position:fixed; top:8px; left:8px; color:#0f0; font:12px monospace; pointer-events:none; }
          </style>
        </head>
        <body>
        <canvas id="c"></canvas>
        <div id="info">Dolly Forge</div>
        <script>
        const c = document.getElementById('c');
        const ctx = c.getContext('2d');
        const info = document.getElementById('info');
        const ws = new WebSocket('ws://' + location.host + '/ws');
        ws.binaryType = 'arraybuffer';
        let fps = 0, frames = 0, last = performance.now();
        let locked = false;
        const keys = new Set();

        ws.onmessage = e => {
          const blob = new Blob([e.data], {type:'image/jpeg'});
          const url  = URL.createObjectURL(blob);
          const img  = new Image();
          img.onload = () => {
            if (c.width !== img.naturalWidth || c.height !== img.naturalHeight) {
              c.width = img.naturalWidth; c.height = img.naturalHeight;
            }
            ctx.drawImage(img, 0, 0);
            URL.revokeObjectURL(url);
            frames++;
            const now = performance.now();
            if (now - last >= 1000) { fps = frames; frames = 0; last = now; info.textContent = fps + ' fps'; }
          };
          img.src = url;
        };

        function send(obj) { if (ws.readyState === 1) ws.send(JSON.stringify(obj)); }

        c.addEventListener('click', () => { if (!locked) c.requestPointerLock(); });
        document.addEventListener('pointerlockchange', () => { locked = !!document.pointerLockElement; });
        document.addEventListener('mousemove', e => { if (locked) send({type:'mousemove', dx:e.movementX, dy:e.movementY}); });
        document.addEventListener('mousedown', e => send({type:'mousedown', button:e.button, down:true}));
        document.addEventListener('mouseup',   e => send({type:'mouseup',   button:e.button, down:false}));
        document.addEventListener('keydown',   e => { if (!keys.has(e.code)) { keys.add(e.code); send({type:'keydown', key:e.code}); }
          if (e.code === 'Escape' && locked) document.exitPointerLock();
          if (['Space','ArrowUp','ArrowDown','ArrowLeft','ArrowRight'].includes(e.code)) e.preventDefault();
        });
        document.addEventListener('keyup', e => { keys.delete(e.code); send({type:'keyup', key:e.code}); });
        </script>
        </body>
        </html>
        """;

    public void Dispose()
    {
        _running = false;
        try { _http.Stop(); } catch { }
    }
}

struct InputEvent
{
    public string Type;
    public string Key;
    public float  Dx, Dy;
    public int    Button;
    public bool   Down;
}
