using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Godot;

namespace DollyForge.Game;

/// <summary>
/// VS Code 통합 브라우저로 게임을 보여 준다 (--stream).
/// Godot 4 는 C# 프로젝트를 웹으로 내보낼 수 없으므로, 게임은 이 컴퓨터의 Godot 에서 그대로(Forward+, Jolt) 돌고
/// 렌더링한 화면을 JPEG 로 브라우저에 보낸다. 브라우저의 키보드·마우스는 Godot 입력 이벤트로 되돌려 넣는다.
///   http://127.0.0.1:7780/    화면 페이지 (godot/web/stream.html)
///   ws://127.0.0.1:7780/ws    ← 화면(JPEG 바이너리)·소리('A' 머리 + 16비트 PCM 바이너리)·상태(JSON 텍스트) / → 입력(JSON 텍스트)
///   ws://127.0.0.1:7780/ws?watch=1  같은 화면·소리를 받기만 하는 관찰 연결 (탭을 밀어내지 않는다, 점검용)
///   GET  /frame.jpg           지금 화면 한 장 (탭의 연결을 빼앗지 않고 같은 화면을 본다: 개발·점검용)
///   POST /input               입력 JSON 한 개 (ws 와 같은 형식, 점검 도구가 게임 안에서만 조작한다)
/// 로컬 주소에서만 연다.
/// </summary>
public partial class BrowserStream : Node
{
    public int Port { get; set; } = 7780;
    public int Fps { get; set; } = 30;
    public float Quality { get; set; } = 0.78f;
    /// <summary>브라우저로 보내는 화면 해상도 (종이 양식들이 설계된 기준 해상도와 같게)</summary>
    public Vector2I Size { get; set; } = new(1600, 900);

    HttpListener? _http;
    WebSocket? _ws;
    readonly SemaphoreSlim _sendLock = new(1, 1);
    readonly ConcurrentQueue<string> _inbox = new();
    int _inFlight;       // 인코딩 중인 화면 수 (여러 장을 동시에 JPEG 로 굽는다)
    long _frameSeq, _lastSent;
    const int MaxInFlight = 3;
    double _frameTimer;
    /// <summary>DOLLY_STREAM_STATS=1 이면 5초마다 렌더·전송 속도를 출력한다 (성능 확인용)</summary>
    readonly bool _stats = OS.GetEnvironment("DOLLY_STREAM_STATS") == "1";
    double _statsTimer;
    long _statsSeq;
    string _page = "";
    string? _lastStatus;
    Vector2 _mouse, _seen;
    byte[]? _lastJpg;
    /// <summary>주 버스에 건 소리 잡기: 게임이 내는 소리(발소리 등)를 브라우저로 보낸다</summary>
    AudioEffectCapture? _capture;
    /// <summary>관찰 연결들: 화면·소리를 받기만 한다</summary>
    readonly List<WebSocket> _watchers = [];
    /// <summary>/frame.jpg 요청이 있으면 탭이 없어도 잠시 화면을 굽는다</summary>
    long _observeUntil;

    /// <summary>브라우저에서 넣은 입력의 장치 번호. 이 번호가 아닌 입력은 OS 의 실제 마우스·키보드다.</summary>
    internal const int StreamDevice = 0x5D;

    /// <summary>
    /// 숨은 Godot 창이 받는 실제 마우스·키보드는 버린다: 게임은 브라우저 탭의 입력으로만 움직이고,
    /// 사용자가 데스크톱에서 마우스를 움직여도 시점이 따라가지 않는다. 게임 노드들이 받은 마우스 위치도 기억한다.
    /// </summary>
    public override void _Input(InputEvent ev)
    {
        if (ev is (InputEventMouse or InputEventKey) && ev.Device != StreamDevice)
        {
            GetViewport().SetInputAsHandled();
            return;
        }
        if (ev is InputEventMouse m) _seen = m.Position;
    }

    static void Inject(InputEvent ev)
    {
        ev.Device = StreamDevice;
        Input.ParseInputEvent(ev);
    }

    public override void _Ready()
    {
        Pointer.Streaming = true;
        // 화면은 브라우저에 보이므로 Godot 창은 내려 둔다. 숨은 창은 수직 동기를 기다리지 않게 한다.
        DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
        Engine.MaxFps = 60;
        PutWindowAway();
        _page = System.IO.File.ReadAllText(ProjectSettings.GlobalizePath("res://web/stream.html"));
        _capture = new AudioEffectCapture { BufferLength = 0.5f };
        AudioServer.AddBusEffect(0, _capture);
        _http = new HttpListener();
        _http.Prefixes.Add($"http://127.0.0.1:{Port}/");
        try { _http.Start(); }
        catch (HttpListenerException e)
        {
            GD.PrintErr($"[stream] 포트 {Port} 를 열 수 없습니다 ({e.Message}). 이미 실행 중인 Dolly Forge 를 종료하세요.");
            GetTree().Quit(1);
            return;
        }
        _ = Task.Run(AcceptLoop);
        GD.Print($"[stream] http://127.0.0.1:{Port}/ — VS Code 통합 브라우저로 여세요");
    }

    /// <summary>
    /// 최소화하면 Godot 가 그리기를 멈추고, 창을 화면 밖으로 옮기면 창 관리자가 되돌려 놓는다.
    /// 그래서 게임은 고정 해상도(Size)로 렌더링하고(content scale = viewport),
    /// 실제 창은 테두리 없는 작은 창으로 만들어 어느 모니터에도 보이지 않는 자리(없으면 화면 구석)에 둔다.
    /// </summary>
    void PutWindowAway()
    {
        var root = GetTree().Root;
        root.ContentScaleMode = Window.ContentScaleModeEnum.Viewport;
        root.ContentScaleAspect = Window.ContentScaleAspectEnum.Keep;
        root.ContentScaleSize = Size;
        // tools/run-game.sh 가 모니터 없는 가상 화면(cage)에서 띄웠으면 숨길 창이 없다
        if (OS.GetEnvironment("DOLLY_OFFSCREEN") == "1") return;
        DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.Borderless, true);
        DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.NoFocus, true);
        var tiny = new Vector2I(16, 9);
        DisplayServer.WindowSetSize(tiny);
        DisplayServer.WindowSetPosition(HiddenSpot(tiny));
    }

    /// <summary>모니터 배치의 빈틈(어느 모니터에도 속하지 않는 점)을 찾는다</summary>
    static Vector2I HiddenSpot(Vector2I size)
    {
        var screens = Enumerable.Range(0, DisplayServer.GetScreenCount())
            .Select(i => new Rect2I(DisplayServer.ScreenGetPosition(i), DisplayServer.ScreenGetSize(i))).ToList();
        var all = screens.Aggregate((a, b) => a.Merge(b));
        for (var y = all.End.Y - size.Y; y >= all.Position.Y; y -= 8)
        for (var x = all.End.X - size.X; x >= all.Position.X; x -= 8)
        {
            var r = new Rect2I(new Vector2I(x, y), size);
            if (!screens.Any(s => s.Intersects(r))) return r.Position;
        }
        return all.End - size; // 빈틈이 없으면 오른쪽 아래 구석
    }

    public override void _ExitTree()
    {
        _http?.Close();
        _ws?.Abort();
    }

    async Task AcceptLoop()
    {
        while (_http is { IsListening: true })
        {
            HttpListenerContext ctx;
            try { ctx = await _http.GetContextAsync(); }
            catch { return; }
            if (ctx.Request.IsWebSocketRequest && ctx.Request.Url?.AbsolutePath == "/ws" && ctx.Request.QueryString["watch"] == "1")
            {
                var watcher = (await ctx.AcceptWebSocketAsync(null)).WebSocket;
                lock (_watchers) _watchers.Add(watcher);
                continue;
            }
            if (ctx.Request.IsWebSocketRequest && ctx.Request.Url?.AbsolutePath == "/ws")
            {
                var ws = (await ctx.AcceptWebSocketAsync(null)).WebSocket;
                var old = _ws;
                _ws = ws; // 새 탭이 열리면 그쪽으로 넘긴다
                _lastStatus = null;
                if (old is { State: WebSocketState.Open })
                {
                    // 예전 탭이 바로 다시 붙어 서로 빼앗지 않게 알린다
                    await Send(old, Encoding.UTF8.GetBytes("{\"t\":\"replaced\"}"), WebSocketMessageType.Text);
                    try { await old.CloseAsync(WebSocketCloseStatus.NormalClosure, "replaced", CancellationToken.None); }
                    catch { old.Abort(); }
                }
                _ = Task.Run(() => ReceiveLoop(ws));
                continue;
            }
            var path = ctx.Request.Url?.AbsolutePath;
            if (path == "/frame.jpg")
            {
                // 탭이 없으면 화면을 굽지 않고 있었을 수 있다: 이 요청 뒤에 구운 새 화면을 기다린다
                var since = Interlocked.Read(ref _lastSent);
                Interlocked.Exchange(ref _observeUntil, System.Environment.TickCount64 + 3000);
                for (var i = 0; i < 60 && (Volatile.Read(ref _lastJpg) is null || Interlocked.Read(ref _lastSent) <= since); i++) await Task.Delay(50);
                var jpg = Volatile.Read(ref _lastJpg);
                ctx.Response.StatusCode = jpg is null ? 503 : 200;
                ctx.Response.ContentType = "image/jpeg";
                ctx.Response.Headers["Cache-Control"] = "no-store";
                if (jpg is not null) await ctx.Response.OutputStream.WriteAsync(jpg);
                ctx.Response.Close();
                continue;
            }
            if (path == "/input" && ctx.Request.HttpMethod == "POST")
            {
                using var reader = new System.IO.StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                _inbox.Enqueue(await reader.ReadToEndAsync());
                ctx.Response.StatusCode = 204;
                ctx.Response.Close();
                continue;
            }
            var body = path is "/" or "/index.html" ? Encoding.UTF8.GetBytes(_page) : null;
            ctx.Response.StatusCode = body is null ? 404 : 200;
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.Headers["Cache-Control"] = "no-store";
            if (body is not null) await ctx.Response.OutputStream.WriteAsync(body);
            ctx.Response.Close();
        }
    }

    async Task ReceiveLoop(WebSocket ws)
    {
        var buf = new byte[8192];
        var text = new StringBuilder();
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var r = await ws.ReceiveAsync(buf, CancellationToken.None);
                if (r.MessageType == WebSocketMessageType.Close) break;
                text.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
                if (!r.EndOfMessage) continue;
                _inbox.Enqueue(text.ToString());
                text.Clear();
            }
        }
        catch (WebSocketException) { }
    }

    async Task Send(WebSocket ws, ReadOnlyMemory<byte> data, WebSocketMessageType type)
    {
        await _sendLock.WaitAsync();
        try { if (ws.State == WebSocketState.Open) await ws.SendAsync(data, type, true, CancellationToken.None); }
        catch (Exception) { }
        finally { _sendLock.Release(); }
    }

    public override void _Process(double delta)
    {
        while (_inbox.TryDequeue(out var msg)) HandleInput(msg);
        var ws = _ws is { State: WebSocketState.Open } open ? open : null;
        WebSocket[] watchers;
        lock (_watchers)
        {
            _watchers.RemoveAll(w => w.State != WebSocketState.Open);
            watchers = _watchers.ToArray();
        }
        if (ws is null && watchers.Length == 0)
        {
            if (System.Environment.TickCount64 > Interlocked.Read(ref _observeUntil)) return;
        }
        if (ws is not null) SendStatus(ws);
        SendAudio(ws, watchers);
        Stats(delta);

        _frameTimer += delta;
        if (Volatile.Read(ref _inFlight) >= MaxInFlight || _frameTimer < 1.0 / Fps) return;
        _frameTimer = 0;
        var seq = ++_frameSeq;
        Interlocked.Increment(ref _inFlight);
        // GPU 를 멈춰 세우지 않고 화면을 받는다: 렌더링은 계속되고, 데이터가 준비되면 콜백이 온다.
        // 읽을 수 없는 형식이면 예전처럼 동기 읽기(GetImage)로 돌아간다.
        if (!RequestFrameAsync(seq, ws))
            Encode(GetViewport().GetTexture().GetImage(), seq, ws);
    }

    /// <summary>잡힌 소리를 모노 16비트로 줄여 보낸다: 'A' 0 0 0 + 표본율(uint32) + 표본들</summary>
    void SendAudio(WebSocket? ws, WebSocket[] watchers)
    {
        if (_capture is null) return;
        if (ws is null && watchers.Length == 0) { _capture.ClearBuffer(); return; }
        var frames = _capture.GetFramesAvailable();
        if (frames < 256) return;
        var buffer = _capture.GetBuffer(frames);
        var packet = new byte[8 + buffer.Length * 2];
        packet[0] = (byte)'A';
        BitConverter.TryWriteBytes(packet.AsSpan(4), (uint)AudioServer.GetMixRate());
        for (var i = 0; i < buffer.Length; i++)
        {
            var mono = Mathf.Clamp((buffer[i].X + buffer[i].Y) * 0.5f, -1f, 1f);
            BitConverter.TryWriteBytes(packet.AsSpan(8 + i * 2), (short)(mono * 32767));
        }
        if (ws is not null) _ = Send(ws, packet, WebSocketMessageType.Binary);
        foreach (var watcher in watchers) _ = Send(watcher, packet, WebSocketMessageType.Binary);
    }

    void SendStatus(WebSocket ws)
    {
        var status = Pointer.Captured ? "{\"t\":\"mode\",\"captured\":true}" : "{\"t\":\"mode\",\"captured\":false}";
        if (status != _lastStatus)
        {
            _lastStatus = status;
            _ = Send(ws, Encoding.UTF8.GetBytes(status), WebSocketMessageType.Text);
        }
    }

    void Stats(double delta)
    {
        if (_stats)
        {
            RenderingServer.ViewportSetMeasureRenderTime(GetViewport().GetViewportRid(), true);
            _statsTimer += delta;
            if (_statsTimer >= 5)
            {
                GD.Print($"[stream] 렌더 {Engine.GetFramesPerSecond():0} fps · 전송 {(_frameSeq - _statsSeq) / _statsTimer:0.0} 장/초 · " +
                         $"처리 {Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000:0} ms · 물리 {Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000:0} ms · " +
                         $"렌더 CPU {RenderingServer.ViewportGetMeasuredRenderTimeCpu(GetViewport().GetViewportRid()) + RenderingServer.GetFrameSetupTimeCpu():0} ms · " +
                         $"GPU {RenderingServer.ViewportGetMeasuredRenderTimeGpu(GetViewport().GetViewportRid()):0} ms · 그리기 호출 {Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame):0} · " +
                         $"물리 활성 {Performance.GetMonitor(Performance.Monitor.Physics3DActiveObjects):0} · 충돌쌍 {Performance.GetMonitor(Performance.Monitor.Physics3DCollisionPairs):0} · 노드 {Performance.GetMonitor(Performance.Monitor.ObjectNodeCount):0}");
                _statsTimer = 0;
                _statsSeq = _frameSeq;
            }
        }
    }

    Rid _rdTexture;
    Vector2I _rdSize;

    bool RequestFrameAsync(long seq, WebSocket? ws)
    {
        var rd = RenderingServer.GetRenderingDevice();
        if (rd is null) return false;
        var tex = GetViewport().GetTexture();
        var size = new Vector2I(tex.GetWidth(), tex.GetHeight());
        if (!_rdTexture.IsValid || size != _rdSize)
        {
            _rdTexture = RenderingServer.TextureGetRdTexture(tex.GetRid());
            _rdSize = size;
            if (!_rdTexture.IsValid) return false;
            var format = rd.TextureGetFormat(_rdTexture).Format;
            if (format is not (RenderingDevice.DataFormat.R8G8B8A8Unorm or RenderingDevice.DataFormat.R8G8B8A8Srgb))
            {
                GD.Print($"[stream] 화면 형식 {format} — 동기 읽기로 보냅니다");
                _rdTexture = default;
                _asyncUnsupported = true;
            }
        }
        if (_asyncUnsupported || !_rdTexture.IsValid) return false;
        var err = rd.TextureGetDataAsync(_rdTexture, 0, Callable.From<byte[]>(data =>
            Encode(Image.CreateFromData(size.X, size.Y, false, Image.Format.Rgba8, data), seq, ws)));
        return err == Error.Ok;
    }
    bool _asyncUnsupported;

    void Encode(Image image, long seq, WebSocket? ws)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var jpg = image.SaveJpgToBuffer(Quality);
                // 더 새 화면이 먼저 나갔으면 이 화면은 버린다 (순서가 뒤바뀌지 않게)
                if (Interlocked.Read(ref _lastSent) > seq) return;
                Interlocked.Exchange(ref _lastSent, seq);
                Volatile.Write(ref _lastJpg, jpg);
                if (ws is not null) await Send(ws, jpg, WebSocketMessageType.Binary);
                WebSocket[] watchers;
                lock (_watchers) watchers = _watchers.ToArray();
                foreach (var watcher in watchers) await Send(watcher, jpg, WebSocketMessageType.Binary);
            }
            finally { Interlocked.Decrement(ref _inFlight); }
        });
    }

    // ------------------------------------------------------------------ 브라우저 입력 → Godot 입력

    void HandleInput(string json)
    {
        JsonElement e;
        try { e = JsonDocument.Parse(json).RootElement; }
        catch (JsonException) { return; }
        // 브라우저 좌표는 게임 화면(Size) 기준 0..1. Godot 는 창 좌표로 받은 이벤트를 content scale 로 되돌려
        // 게임 화면 좌표로 바꾸므로, 여기서는 게임 화면 좌표 → (작은) 창 좌표로 바꿔 넣는다.
        var size = (Vector2)Size;
        var toWindow = GetTree().Root.GetFinalTransform();
        Vector2 At() => e.TryGetProperty("x", out var x) ? new Vector2(x.GetSingle() * size.X, e.GetProperty("y").GetSingle() * size.Y) : _mouse;
        switch (e.GetProperty("t").GetString())
        {
            case "k":
                if (KeyOf(e.GetProperty("code").GetString() ?? "") is not { } key) return;
                var ev = new InputEventKey
                {
                    Keycode = key, PhysicalKeycode = key,
                    Pressed = e.GetProperty("down").GetBoolean(), Echo = e.GetProperty("rep").GetBoolean(),
                    ShiftPressed = e.GetProperty("shift").GetBoolean(), CtrlPressed = e.GetProperty("ctrl").GetBoolean(),
                    AltPressed = e.GetProperty("alt").GetBoolean(),
                };
                // 글자 입력(전화번호·이름 칸)용: 한 글자짜리 key 는 유니코드로
                if (e.GetProperty("key").GetString() is { Length: 1 } ch && !ev.CtrlPressed) ev.Unicode = ch[0];
                Inject(ev);
                break;
            case "mm":
                var to = At();
                var rel = new Vector2(e.GetProperty("dx").GetSingle(), e.GetProperty("dy").GetSingle());
                if (!Pointer.Captured) rel = to - _mouse;
                _mouse = to;
                var w = toWindow * to;
                var wr = toWindow.BasisXform(rel);
                Inject(new InputEventMouseMotion { Position = w, GlobalPosition = w, Relative = wr, ScreenRelative = wr });
                break;
            case "mb":
                var at = Pointer.Captured ? size / 2 : At();
                _mouse = at;
                var button = e.GetProperty("b").GetInt32() switch
                {
                    0 => MouseButton.Left, 1 => MouseButton.Middle, 2 => MouseButton.Right,
                    3 => MouseButton.WheelUp, _ => MouseButton.WheelDown,
                };
                var down = e.GetProperty("down").GetBoolean();
                var wa = toWindow * at;
                Inject(new InputEventMouseButton { ButtonIndex = button, Pressed = down, Position = wa, GlobalPosition = wa });
                if (button is MouseButton.WheelUp or MouseButton.WheelDown && down)
                    Inject(new InputEventMouseButton { ButtonIndex = button, Pressed = false, Position = wa, GlobalPosition = wa });
                break;
            case "where": // 시험용: 게임이 받은 마우스 위치를 돌려준다
                var p = _seen;
                if (_ws is { } sock) _ = Send(sock, Encoding.UTF8.GetBytes($"{{\"t\":\"where\",\"x\":{p.X:0.0},\"y\":{p.Y:0.0}}}"), WebSocketMessageType.Text);
                break;
            case "unlock":
                // 브라우저가 Esc 로 포인터 잠금을 풀었다 (그 Esc 는 페이지에 오지 않는다)
                if (!Pointer.Captured) return;
                Inject(new InputEventKey { Keycode = Key.Escape, PhysicalKeycode = Key.Escape, Pressed = true });
                Inject(new InputEventKey { Keycode = Key.Escape, PhysicalKeycode = Key.Escape, Pressed = false });
                if (Pointer.Captured) Pointer.Mode = Input.MouseModeEnum.Visible;
                break;
        }
    }

    /// <summary>KeyboardEvent.code (물리 키 위치) → Godot Key</summary>
    static Key? KeyOf(string code)
    {
        if (code.StartsWith("Key") && code.Length == 4) return Key.A + (code[3] - 'A');
        if (code.StartsWith("Digit") && code.Length == 6) return Key.Key0 + (code[5] - '0');
        if (code.StartsWith("Numpad") && code.Length == 7 && char.IsDigit(code[6])) return Key.Kp0 + (code[6] - '0');
        if (code.Length is 2 or 3 && code[0] == 'F' && int.TryParse(code[1..], out var f) && f is >= 1 and <= 12) return Key.F1 + (f - 1);
        return code switch
        {
            "Space" => Key.Space, "Enter" or "NumpadEnter" => Key.Enter, "Escape" => Key.Escape, "Backspace" => Key.Backspace,
            "Tab" => Key.Tab, "Delete" => Key.Delete, "Home" => Key.Home, "End" => Key.End,
            "ShiftLeft" or "ShiftRight" => Key.Shift, "ControlLeft" or "ControlRight" => Key.Ctrl, "AltLeft" or "AltRight" => Key.Alt,
            "ArrowLeft" => Key.Left, "ArrowRight" => Key.Right, "ArrowUp" => Key.Up, "ArrowDown" => Key.Down,
            "BracketLeft" => Key.Bracketleft, "BracketRight" => Key.Bracketright, "Minus" => Key.Minus, "Equal" => Key.Equal,
            "Comma" => Key.Comma, "Period" => Key.Period, "Slash" => Key.Slash, "Semicolon" => Key.Semicolon,
            "Quote" => Key.Apostrophe, "Backquote" => Key.Quoteleft, "Backslash" => Key.Backslash,
            _ => null,
        };
    }
}
