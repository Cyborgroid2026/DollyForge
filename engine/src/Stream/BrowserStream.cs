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
            *, *::before, *::after { margin:0; padding:0; box-sizing:border-box; }
            html, body { width:100%; height:100%; overflow:hidden; background:#000; }
            /* Street View 배경 레이어 */
            #bg { position:fixed; inset:0; z-index:0; }
            #bg iframe { width:100%; height:100%; border:none; display:block; }
            body.sv-mode  #bg iframe { pointer-events:auto; }
            body.game-mode #bg iframe { pointer-events:none; }
            /* WebGL 캐릭터 레이어: 검정=투명, 캐릭터만 보임 */
            #c { position:fixed; inset:0; z-index:1; width:100%; height:100%;
                 pointer-events:none; }
            body.game-mode #c { cursor:crosshair; }
            /* 상단 URL 바 */
            #bar { position:fixed; top:0; left:0; right:0; z-index:20;
                   background:rgba(0,0,0,0.82); display:flex; align-items:center;
                   gap:6px; padding:4px 8px; font:12px 'Consolas','Courier New',monospace; }
            #bar label { color:#666; flex-shrink:0; }
            #url-in { flex:1; min-width:0; background:#0a0a0a; color:#0f0;
                      border:1px solid #333; padding:2px 6px; font:inherit; outline:none; }
            #url-in:focus { border-color:#0f0; }
            .btn { background:#111; color:#888; border:1px solid #333;
                   padding:2px 10px; font:inherit; cursor:pointer; flex-shrink:0; }
            .btn:hover  { border-color:#0f0; color:#0f0; }
            .btn.active { border-color:#0f0; color:#0f0; background:#001800; }
            #fps { color:#0f0; flex-shrink:0; min-width:50px; text-align:right; }
            /* 하단 힌트 */
            #hint { position:fixed; bottom:6px; left:8px; z-index:20;
                    color:#444; font:11px 'Consolas','Courier New',monospace; pointer-events:none; }
          </style>
        </head>
        <body class="sv-mode">

        <div id="bg"><iframe id="bg-frame" src="about:blank"></iframe></div>
        <canvas id="c"></canvas>

        <div id="bar">
          <label>배경 URL:</label>
          <input id="url-in" type="text"
            placeholder="Google Maps → 공유 → 지도 퍼가기 → src 안의 URL 붙여넣기 (Enter)">
          <button class="btn active" id="btn-sv">거리뷰 모드</button>
          <button class="btn"        id="btn-gm">게임 모드</button>
          <span id="fps">0 fps</span>
        </div>
        <div id="hint">거리뷰 모드: 마우스로 거리뷰 조작 | 게임 모드: WASD 이동 · 마우스 시점 · Esc 해제</div>

        <script>
        'use strict';
        const bgFrame = document.getElementById('bg-frame');
        const urlIn   = document.getElementById('url-in');
        const btnSv   = document.getElementById('btn-sv');
        const btnGm   = document.getElementById('btn-gm');
        const fpsEl   = document.getElementById('fps');
        const canvas  = document.getElementById('c');
        let locked = false;

        // ── 모드 전환 ─────────────────────────────────────────────
        function setMode(m) {
          document.body.className = m;
          btnSv.classList.toggle('active', m === 'sv-mode');
          btnGm.classList.toggle('active', m === 'game-mode');
          if (m === 'game-mode') canvas.requestPointerLock?.();
          else if (locked) document.exitPointerLock?.();
        }
        btnSv.addEventListener('click', () => setMode('sv-mode'));
        btnGm.addEventListener('click', () => setMode('game-mode'));
        document.addEventListener('pointerlockchange', () => {
          locked = !!document.pointerLockElement;
          if (!locked) setMode('sv-mode');
        });
        document.addEventListener('pointerlockerror', () => setMode('sv-mode'));

        // ── URL 입력 → iframe ────────────────────────────────────
        function applyUrl() {
          const u = urlIn.value.trim();
          bgFrame.src = u || 'about:blank';
        }
        urlIn.addEventListener('keydown', e => { if (e.key==='Enter') { applyUrl(); urlIn.blur(); }});
        urlIn.addEventListener('blur', applyUrl);

        // ── WebGL 초기화 (검정 픽셀 discard → iframe 비침) ──────
        const gl = canvas.getContext('webgl2', {alpha:true, premultipliedAlpha:false})
                || canvas.getContext('webgl',  {alpha:true, premultipliedAlpha:false});

        function mkSh(type, src) {
          const s = gl.createShader(type);
          gl.shaderSource(s, src); gl.compileShader(s); return s;
        }
        const prog = gl.createProgram();
        gl.attachShader(prog, mkSh(gl.VERTEX_SHADER, `
          attribute vec2 a_pos;
          varying   vec2 v_uv;
          void main(){
            v_uv = vec2(a_pos.x*0.5+0.5, 0.5-a_pos.y*0.5);
            gl_Position = vec4(a_pos, 0.0, 1.0);
          }
        `));
        gl.attachShader(prog, mkSh(gl.FRAGMENT_SHADER, `
          precision highp float;
          uniform sampler2D u_tex;
          varying vec2 v_uv;
          void main(){
            vec4 c = texture2D(u_tex, v_uv);
            float lum = dot(c.rgb, vec3(0.299, 0.587, 0.114));
            if (lum < 0.03) discard;
            gl_FragColor = c;
          }
        `));
        gl.linkProgram(prog);
        gl.useProgram(prog);

        const quadBuf = gl.createBuffer();
        gl.bindBuffer(gl.ARRAY_BUFFER, quadBuf);
        gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1,-1, 1,-1, -1,1, 1,1]), gl.STATIC_DRAW);
        const aPos = gl.getAttribLocation(prog, 'a_pos');
        gl.enableVertexAttribArray(aPos);
        gl.vertexAttribPointer(aPos, 2, gl.FLOAT, false, 0, 0);
        gl.uniform1i(gl.getUniformLocation(prog, 'u_tex'), 0);

        const tex = gl.createTexture();
        gl.bindTexture(gl.TEXTURE_2D, tex);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);

        gl.enable(gl.BLEND);
        gl.blendFunc(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA);

        // ── WebSocket 프레임 수신 ────────────────────────────────
        const ws = new WebSocket('ws://' + location.host + '/ws');
        ws.binaryType = 'arraybuffer';
        let fps = 0, frames = 0, last = performance.now();

        ws.onmessage = e => {
          createImageBitmap(new Blob([e.data], {type:'image/jpeg'})).then(bmp => {
            if (canvas.width !== bmp.width || canvas.height !== bmp.height) {
              canvas.width = bmp.width; canvas.height = bmp.height;
              gl.viewport(0, 0, bmp.width, bmp.height);
            }
            gl.clearColor(0,0,0,0);
            gl.clear(gl.COLOR_BUFFER_BIT);
            gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGB, gl.RGB, gl.UNSIGNED_BYTE, bmp);
            gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
            frames++;
            const now = performance.now();
            if (now - last >= 1000) { fps = frames; frames = 0; last = now; fpsEl.textContent = fps + ' fps'; }
          });
        };

        // ── 입력 이벤트 ──────────────────────────────────────────
        function send(obj){ if(ws.readyState===1) ws.send(JSON.stringify(obj)); }
        const keys = new Set();
        document.addEventListener('mousemove', e => { if(locked) send({type:'mousemove',dx:e.movementX,dy:e.movementY}); });
        document.addEventListener('mousedown', e => { if(locked) send({type:'mousedown',button:e.button,down:true}); });
        document.addEventListener('mouseup',   e => { if(locked) send({type:'mouseup',  button:e.button,down:false}); });
        document.addEventListener('keydown',   e => {
          if (e.target===urlIn) return;
          if (e.code==='Escape') { if(locked) document.exitPointerLock(); return; }
          if (['Space','ArrowUp','ArrowDown','ArrowLeft','ArrowRight'].includes(e.code)) e.preventDefault();
          if (!keys.has(e.code)) { keys.add(e.code); send({type:'keydown',key:e.code}); }
        });
        document.addEventListener('keyup', e => {
          if (e.target===urlIn) return;
          keys.delete(e.code); send({type:'keyup',key:e.code});
        });
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
