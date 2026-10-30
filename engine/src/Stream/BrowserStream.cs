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
            *,*::before,*::after{margin:0;padding:0;box-sizing:border-box}
            html,body{width:100%;height:100%;overflow:hidden}
            /* 거리뷰 배경: API 키 없으면 CSS 그라디언트 */
            #sv{position:fixed;inset:0;z-index:0;
                background:linear-gradient(to bottom,
                  #1560a0 0%,#4a9ad4 28%,#9dd0f0 44%,
                  #d0e8f8 49%,#9a9a9c 54%,#606062 70%,#3a3a3c 100%)}
            /* 캐릭터 레이어: WebGL chroma-key (검정→투명) */
            #c{position:fixed;inset:0;z-index:1;width:100%;height:100%;pointer-events:none}
            body.game #c{cursor:crosshair}
            /* 상단 바 */
            #bar{position:fixed;top:0;left:0;right:0;z-index:20;
                 background:rgba(0,0,0,.82);display:flex;align-items:center;
                 gap:5px;padding:3px 8px;font:12px Consolas,monospace}
            #addr{flex:1;min-width:0;background:#080808;color:#0f0;
                  border:1px solid #333;padding:2px 6px;font:inherit;outline:none}
            #addr:focus{border-color:#0f0}
            .btn{background:#111;color:#777;border:1px solid #333;
                 padding:2px 9px;font:inherit;cursor:pointer;white-space:nowrap}
            .btn:hover{border-color:#0f0;color:#0f0}
            .btn.on{border-color:#0f0;color:#0f0;background:#001800}
            #fps{color:#0f0;min-width:48px;text-align:right}
            /* API 키 입력 오버레이 */
            #kover{position:fixed;inset:0;z-index:30;background:rgba(0,0,0,.92);
                   display:flex;flex-direction:column;align-items:center;
                   justify-content:center;gap:14px;font:13px Consolas,monospace;color:#ccc}
            #kover h2{color:#0f0;font-size:17px;letter-spacing:.05em}
            #kover p{max-width:460px;text-align:center;line-height:1.7;color:#888;font-size:12px}
            #kover a{color:#5af}
            #kin{width:340px;background:#080808;color:#0f0;border:1px solid #0f0;
                 padding:6px 10px;font:13px Consolas,monospace;outline:none}
            #kapply{background:#001800;color:#0f0;border:1px solid #0f0;
                    padding:5px 20px;font:13px Consolas,monospace;cursor:pointer}
            #kskip{background:none;border:none;color:#444;cursor:pointer;font:11px Consolas,monospace;margin-top:4px}
            #hint{position:fixed;bottom:6px;left:8px;z-index:20;color:#444;
                  font:11px monospace;pointer-events:none}
          </style>
        </head>
        <body>
        <div id="sv"></div>
        <canvas id="c"></canvas>

        <div id="bar">
          <input id="addr" type="text" placeholder="주소·장소 검색 (Enter)…">
          <button class="btn on" id="sv-btn">거리뷰 모드</button>
          <button class="btn"    id="gm-btn">게임 모드</button>
          <span id="fps">0 fps</span>
        </div>
        <div id="hint">거리뷰 모드: 마우스로 거리뷰 조작 | 게임 모드: WASD+마우스 | Esc 해제</div>

        <div id="kover">
          <h2>Google Maps API 키</h2>
          <p>
            전 세계 Google Street View를 배경으로 사용하려면 API 키가 필요합니다.<br>
            <a href="https://console.cloud.google.com/google/maps-apis/credentials"
               target="_blank">console.cloud.google.com</a>에서 무료 키를 발급받으세요.<br>
            (Maps JavaScript API · Street View Static API 활성화 필요)
          </p>
          <input id="kin" type="text" placeholder="AIzaSy…" autocomplete="off" spellcheck="false">
          <button id="kapply">적용 및 저장</button>
          <button id="kskip">키 없이 계속 (그라디언트 배경 사용)</button>
        </div>

        <script>
        'use strict';
        let panorama=null, geocoder=null, locked=false, svMode=true;
        const addrIn=document.getElementById('addr');
        const svBtn=document.getElementById('sv-btn');
        const gmBtn=document.getElementById('gm-btn');
        const fpsEl=document.getElementById('fps');
        const canvas=document.getElementById('c');

        /* ── 모드 ──────────────────────────────────────────────── */
        function setMode(m){
          svMode=(m==='sv');
          document.body.className=svMode?'':'game';
          svBtn.classList.toggle('on',svMode);
          gmBtn.classList.toggle('on',!svMode);
          if(!svMode)canvas.requestPointerLock?.();
          else if(locked)document.exitPointerLock?.();
        }
        svBtn.addEventListener('click',()=>setMode('sv'));
        gmBtn.addEventListener('click',()=>setMode('game'));
        document.addEventListener('pointerlockchange',()=>{
          locked=!!document.pointerLockElement;
          if(!locked&&!svMode)setMode('sv');
        });

        /* ── 주소 검색 ──────────────────────────────────────────── */
        addrIn.addEventListener('keydown',e=>{
          if(e.key!=='Enter'||!geocoder||!panorama)return;
          geocoder.geocode({address:addrIn.value},(res,st)=>{
            if(st==='OK')panorama.setPosition(res[0].geometry.location);
          });
          e.preventDefault();
        });

        /* ── Google Maps 초기화 ─────────────────────────────────── */
        window.initMaps=function(){
          geocoder=new google.maps.Geocoder();
          panorama=new google.maps.StreetViewPanorama(
            document.getElementById('sv'),{
              position:{lat:37.5655,lng:126.9780},
              pov:{heading:270,pitch:0},zoom:1,
              motionTracking:false,motionTrackingControl:false,
              fullscreenControl:false,linksControl:true,
              addressControl:true,showRoadLabels:true
            }
          );
          document.getElementById('kover').style.display='none';
        };

        /* ── API 키 관리 ─────────────────────────────────────────── */
        function loadMaps(key){
          const s=document.createElement('script');
          s.src='https://maps.googleapis.com/maps/api/js?key='
               +encodeURIComponent(key)+'&callback=initMaps';
          s.async=true;
          s.onerror=()=>alert('Maps API 로드 실패. 키를 확인해주세요.');
          document.head.appendChild(s);
        }
        function applyKey(){
          const k=document.getElementById('kin').value.trim();
          if(!k)return;
          localStorage.setItem('gmk',k);
          loadMaps(k);
        }
        document.getElementById('kapply').addEventListener('click',applyKey);
        document.getElementById('kin').addEventListener('keydown',e=>{if(e.key==='Enter')applyKey();});
        document.getElementById('kskip').addEventListener('click',()=>{
          document.getElementById('kover').style.display='none';
        });
        const sk=localStorage.getItem('gmk');
        if(sk)loadMaps(sk);

        /* ── WebGL: 캐릭터 chroma-key 합성 (검정→투명) ─────────── */
        const gl=canvas.getContext('webgl2',{alpha:true,premultipliedAlpha:false})
               ||canvas.getContext('webgl', {alpha:true,premultipliedAlpha:false});
        function mkSh(t,s){const h=gl.createShader(t);gl.shaderSource(h,s);gl.compileShader(h);return h;}
        const prog=gl.createProgram();
        gl.attachShader(prog,mkSh(gl.VERTEX_SHADER,
          'attribute vec2 a;varying vec2 v;void main(){v=vec2(a.x*.5+.5,.5-a.y*.5);gl_Position=vec4(a,0,1);}'));
        gl.attachShader(prog,mkSh(gl.FRAGMENT_SHADER,
          'precision highp float;uniform sampler2D u;varying vec2 v;void main(){vec4 c=texture2D(u,v);float l=dot(c.rgb,vec3(.299,.587,.114));if(l<.04)discard;gl_FragColor=c;}'));
        gl.linkProgram(prog);gl.useProgram(prog);
        const qb=gl.createBuffer();
        gl.bindBuffer(gl.ARRAY_BUFFER,qb);
        gl.bufferData(gl.ARRAY_BUFFER,new Float32Array([-1,-1,1,-1,-1,1,1,1]),gl.STATIC_DRAW);
        const al=gl.getAttribLocation(prog,'a');
        gl.enableVertexAttribArray(al);gl.vertexAttribPointer(al,2,gl.FLOAT,false,0,0);
        gl.uniform1i(gl.getUniformLocation(prog,'u'),0);
        const tex=gl.createTexture();
        gl.bindTexture(gl.TEXTURE_2D,tex);
        [gl.TEXTURE_WRAP_S,gl.TEXTURE_WRAP_T].forEach(p=>gl.texParameteri(gl.TEXTURE_2D,p,gl.CLAMP_TO_EDGE));
        [gl.TEXTURE_MIN_FILTER,gl.TEXTURE_MAG_FILTER].forEach(p=>gl.texParameteri(gl.TEXTURE_2D,p,gl.LINEAR));
        gl.enable(gl.BLEND);gl.blendFunc(gl.SRC_ALPHA,gl.ONE_MINUS_SRC_ALPHA);

        /* ── WebSocket 프레임 수신 ──────────────────────────────── */
        const ws=new WebSocket('ws://'+location.host+'/ws');
        ws.binaryType='arraybuffer';
        let fps=0,frames=0,last=performance.now();
        ws.onmessage=e=>{
          createImageBitmap(new Blob([e.data],{type:'image/jpeg'})).then(bmp=>{
            if(canvas.width!==bmp.width||canvas.height!==bmp.height){
              canvas.width=bmp.width;canvas.height=bmp.height;
              gl.viewport(0,0,bmp.width,bmp.height);
            }
            gl.clearColor(0,0,0,0);gl.clear(gl.COLOR_BUFFER_BIT);
            gl.texImage2D(gl.TEXTURE_2D,0,gl.RGB,gl.RGB,gl.UNSIGNED_BYTE,bmp);
            gl.drawArrays(gl.TRIANGLE_STRIP,0,4);
            frames++;
            const now=performance.now();
            if(now-last>=1000){fps=frames;frames=0;last=now;fpsEl.textContent=fps+' fps';}
          });
        };

        /* ── 입력 ───────────────────────────────────────────────── */
        function send(o){if(ws.readyState===1)ws.send(JSON.stringify(o));}
        const keys=new Set();
        document.addEventListener('mousemove',e=>{if(locked)send({type:'mousemove',dx:e.movementX,dy:e.movementY});});
        document.addEventListener('mousedown',e=>{if(locked)send({type:'mousedown',button:e.button,down:true});});
        document.addEventListener('mouseup',  e=>{if(locked)send({type:'mouseup',  button:e.button,down:false});});
        document.addEventListener('keydown',  e=>{
          if(e.target===addrIn)return;
          if(e.code==='Escape'){if(locked)document.exitPointerLock();return;}
          if(['Space','ArrowUp','ArrowDown','ArrowLeft','ArrowRight'].includes(e.code))e.preventDefault();
          if(!keys.has(e.code)){keys.add(e.code);send({type:'keydown',key:e.code});}
        });
        document.addEventListener('keyup',e=>{
          if(e.target===addrIn)return;
          keys.delete(e.code);send({type:'keyup',key:e.code});
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
