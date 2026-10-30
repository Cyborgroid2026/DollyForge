using System.Net;
using System.Net.Http;
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

    // Google Maps 프록시용 HttpClient (X-Frame-Options 제거)
    static readonly HttpClient _proxy = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
        UseCookies        = false,
    }) { Timeout = TimeSpan.FromSeconds(15) };

    readonly HttpListener _http = new();
    readonly List<WebSocket> _ws = new();
    readonly object _wsLock = new();

    // 입력 큐 (게임 루프가 드레인)
    public readonly Queue<InputEvent> InputQueue = new();

    volatile byte[]? _lastFrame;
    volatile string  _status = "{}";
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

    // 게임 상태(선택 화면/조종 중·시점)를 JSON 텍스트로 탭에 알린다
    public void SetStatus(string json)
    {
        _status = json;
        var bytes = Encoding.UTF8.GetBytes(json);
        lock (_wsLock)
            foreach (var ws in _ws)
                if (ws.State == WebSocketState.Open)
                    try { ws.SendAsync(bytes, WebSocketMessageType.Text, true, default).GetAwaiter().GetResult(); } catch { }
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
            // 접속할 때마다 캐릭터 선택부터 (게임 루프가 받아 선택 화면으로 되돌린다)
            lock (InputQueue) InputQueue.Enqueue(new InputEvent { Type = "hello", Key = "" });
            lock (_wsLock) _ws.Add(wsc.WebSocket);
            lock (_wsLock)
            {
                wsc.WebSocket.SendAsync(Encoding.UTF8.GetBytes(_status = """{"state":"select"}"""), WebSocketMessageType.Text, true, default).GetAwaiter().GetResult();
                if (_lastFrame is { } f) wsc.WebSocket.SendAsync(f, WebSocketMessageType.Binary, true, default).GetAwaiter().GetResult();
            }
            await ReadInputLoop(wsc.WebSocket);
            return;
        }
        if (path == "/frame.jpg")
        {
            if (_lastFrame is not { } jpg) { ctx.Response.StatusCode = 503; ctx.Response.Close(); return; }
            ctx.Response.ContentType = "image/jpeg";
            ctx.Response.ContentLength64 = jpg.Length;
            await ctx.Response.OutputStream.WriteAsync(jpg);
            ctx.Response.Close();
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
        // Google Maps 프록시: X-Frame-Options 제거하여 iframe 삽입 허용
        if (path == "/sv-proxy")
        {
            var targetUrl = ctx.Request.QueryString["url"] ?? "https://www.google.com/maps/@40.93439,69.5706499,17z";
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, targetUrl);
                req.Headers.TryAddWithoutValidation("User-Agent",      "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");
                req.Headers.TryAddWithoutValidation("Accept",          "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
                req.Headers.TryAddWithoutValidation("Accept-Language", "ko-KR,ko;q=0.9,en;q=0.8");
                req.Headers.TryAddWithoutValidation("Referer",         "https://www.google.com/");
                var resp = await _proxy.SendAsync(req);
                var body = await resp.Content.ReadAsByteArrayAsync();
                var ct   = resp.Content.Headers.ContentType?.ToString() ?? "text/html;charset=utf-8";
                ctx.Response.ContentType     = ct;
                ctx.Response.ContentLength64 = body.Length;
                // X-Frame-Options 의도적으로 미전달 → iframe 허용
                await ctx.Response.OutputStream.WriteAsync(body);
            }
            catch (Exception ex)
            {
                var err = Encoding.UTF8.GetBytes($"[sv-proxy error] {ex.Message}");
                ctx.Response.StatusCode      = 502;
                ctx.Response.ContentLength64 = err.Length;
                await ctx.Response.OutputStream.WriteAsync(err);
            }
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
            lock (InputQueue) InputQueue.Enqueue(ev);
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
            *{margin:0;padding:0;box-sizing:border-box}
            body{background:#000;display:flex;justify-content:center;align-items:center;height:100vh;overflow:hidden}
            canvas{image-rendering:pixelated;cursor:crosshair;display:block;max-width:100vw;max-height:100vh}
            #info{position:fixed;top:6px;left:8px;color:#0f0;font:11px monospace;pointer-events:none}
            #hint{position:fixed;bottom:6px;left:8px;color:#777;font:12px monospace;pointer-events:none}
            #view{position:fixed;top:6px;right:10px;color:#cfd6e4;font:13px sans-serif;pointer-events:none;text-shadow:0 0 4px #000}
            #pick{position:fixed;left:0;right:0;bottom:9vh;display:none;justify-content:center;gap:28vw}
            #pick button{font:600 17px sans-serif;color:#e8edf5;background:rgba(20,24,32,.72);border:1px solid #8a94a8;
              border-radius:10px;padding:14px 26px;cursor:pointer;backdrop-filter:blur(4px)}
            #pick button:hover{background:rgba(70,80,100,.85);border-color:#dfe6f2}
            #title{position:fixed;top:5vh;left:0;right:0;text-align:center;color:#dfe6f2;font:600 22px sans-serif;
              letter-spacing:.06em;display:none;text-shadow:0 0 8px #000}
          </style>
        </head>
        <body>
        <canvas id="c"></canvas>
        <div id="info">0 fps</div>
        <div id="hint"></div>
        <div id="view"></div>
        <div id="title">조종할 사이보그를 고르세요</div>
        <div id="pick"><button data-i="0">여성형 [1]</button><button data-i="1">남성형 [2]</button></div>
        <script>
        'use strict';
        const c   = document.getElementById('c');
        const ctx = c.getContext('2d');
        const info= document.getElementById('info');
        let ws=null,selecting=false;
        let fps=0,frames=0,last=performance.now(),locked=false;
        const keys=new Set();

        const hint=document.getElementById('hint'),view=document.getElementById('view'),
              pick=document.getElementById('pick'),title=document.getElementById('title');
        function status(st){
          const sel=st.state==='select'; selecting=sel;
          pick.style.display=sel?'flex':'none'; title.style.display=sel?'block':'none';
          view.textContent=sel?'':st.who+' · '+st.view;
          hint.textContent=sel?'1·2 또는 카드로 선택 | 휠 거리 | N 스튜디오/파노라마'
            :'클릭→마우스 캡처 | WASD 이동 · Shift 달리기 | 마우스 시점 | V 1인칭/3인칭 뒤/3인칭 앞 | 휠 거리 | C 캐릭터 선택 | N 배경 | Esc 해제';
        }
        pick.querySelectorAll('button').forEach(b=>b.addEventListener('click',e=>{
          e.stopPropagation(); send({type:'select',button:+b.dataset.i}); c.requestPointerLock();
        }));
        // 엔진이 다시 켜지면 저절로 다시 붙는다 (새 페이지가 필요하면 새로고침)
        function connect(){
          ws=new WebSocket('ws://'+location.host+'/ws');
          ws.binaryType='arraybuffer';
          ws.onmessage=onMsg;
          ws.onclose=()=>{info.textContent='엔진 연결 끊김 — 다시 연결 중…';setTimeout(connect,1000);};
        }
        const onMsg=e=>{
          if(typeof e.data==='string'){try{status(JSON.parse(e.data));}catch(_){}return;}
          createImageBitmap(new Blob([e.data],{type:'image/jpeg'})).then(bmp=>{
            if(c.width!==bmp.width||c.height!==bmp.height){c.width=bmp.width;c.height=bmp.height;}
            ctx.drawImage(bmp,0,0);
            frames++;
            const now=performance.now();
            if(now-last>=1000){fps=frames;frames=0;last=now;info.textContent=fps+' fps';}
          });
        };

        function send(o){if(ws&&ws.readyState===1)ws.send(JSON.stringify(o));}
        connect();

        // 선택 화면에서는 화면의 왼쪽(여성형)·오른쪽(남성형) 인형을 눌러도 고른다
        c.addEventListener('click',e=>{
          if(selecting){const r=c.getBoundingClientRect();send({type:'select',button:(e.clientX-r.left)<r.width/2?0:1});}
          if(!locked)c.requestPointerLock();
        });
        document.addEventListener('pointerlockchange',()=>{locked=!!document.pointerLockElement;});
        document.addEventListener('mousemove',e=>{if(locked)send({type:'mousemove',dx:e.movementX,dy:e.movementY});});
        document.addEventListener('mousedown',e=>{if(locked)send({type:'mousedown',button:e.button,down:true});});
        document.addEventListener('mouseup',  e=>{if(locked)send({type:'mouseup',  button:e.button,down:false});});
        document.addEventListener('keydown',  e=>{
          if(!keys.has(e.code)){keys.add(e.code);send({type:'keydown',key:e.code});}
          if(e.code==='Escape'&&locked)document.exitPointerLock();
          if(['Space','ArrowUp','ArrowDown','ArrowLeft','ArrowRight'].includes(e.code))e.preventDefault();
        });
        document.addEventListener('keyup',e=>{keys.delete(e.code);send({type:'keyup',key:e.code});});
        c.addEventListener('wheel',e=>{e.preventDefault();send({type:'wheel',dy:e.deltaY});},{passive:false});
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
