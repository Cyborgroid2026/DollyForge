using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace DollyForge;

/// <summary>
/// Rust 세계 서버(world/, tcp 127.0.0.1:7701)와의 연결: 한 줄 JSON.
///   세계를 바꾸는 일(파기·붓기·줍기·내려놓기)은 서버에 요청하고, 서버가 존중 규칙으로 판정해 허락한 것만 세계에 반영한다.
///   흩어진 물건은 서버의 Rapier 가 움직이고, 엔진은 자세를 받아 그린다.
/// 서버가 없으면 2 초마다 다시 붙고, 그동안 세계는 그대로 걷고 볼 수 있다 (편집만 안 된다).
/// </summary>
sealed class WorldLink : IDisposable
{
    public sealed class Body
    {
        public int Id;
        public string Item = "";
        public Vector3 Half;
        public float R;
        public Vector3 P;
        public Quaternion Q = Quaternion.Identity;
    }

    readonly WorldScene _scene;
    readonly string _player;
    readonly string _host;
    readonly int _port;
    readonly ConcurrentQueue<JsonElement> _in = new();
    readonly Thread _thread;
    volatile bool _stop;
    volatile StreamWriter? _w;
    readonly object _sendLock = new();

    public bool Connected => _w is not null;
    public string? Held { get; private set; }
    public readonly Dictionary<int, Body> Bodies = [];
    /// <summary>서버의 마지막 알림 (허락·거절 이유)</summary>
    public string Note { get; private set; } = "";
    public event Action? Changed;

    public WorldLink(WorldScene scene, string player = "local")
    {
        _scene = scene; _player = player;
        var addr = Environment.GetEnvironmentVariable("DOLLY_WORLD_ADDR") ?? "127.0.0.1:7701";
        _host = addr.Split(':')[0]; _port = int.Parse(addr.Split(':')[1]);
        _thread = new Thread(Run) { IsBackground = true, Name = "world-link" };
        _thread.Start();
    }

    void Run()
    {
        var warned = false;
        while (!_stop)
        {
            try
            {
                using var tcp = new TcpClient { NoDelay = true };
                tcp.Connect(_host, _port);
                using var stream = tcp.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                writer.WriteLine(Hello());
                _w = writer;
                Console.WriteLine($"[link] 세계 서버 {_host}:{_port} 연결");
                warned = false;
                while (!_stop && reader.ReadLine() is { } line)
                {
                    using var doc = JsonDocument.Parse(line);
                    _in.Enqueue(doc.RootElement.Clone());
                }
            }
            catch (Exception ex) when (ex is SocketException or IOException or JsonException)
            {
                if (!warned) Console.WriteLine($"[link] 세계 서버에 붙지 못함 ({_host}:{_port}) — 편집 없이 걷기만, 2 초마다 다시 시도");
                warned = true;
            }
            _w = null;
            if (!_stop) Thread.Sleep(2000);
        }
    }

    /// <summary>처음 인사: 서버의 Rapier 가 쓸 땅 높이장(가까운 ±64 m, 0.5 m)과 나무 줄기</summary>
    string Hello()
    {
        var n = WorldScene.GroundCount; var step = WorldScene.GroundStep; var o = -WorldScene.GroundHalf;
        var h = new float[n * n];
        for (var k = 0; k < n; k++) for (var i = 0; i < n; i++) h[k * n + i] = MathF.Round(_scene.Terrain.Height(o + i * step, o + k * step) * 1000) / 1000;
        var trees = _scene.Flora.Trees
            .Where(t => MathF.Abs(t.Pos.X) < WorldScene.GroundHalf && MathF.Abs(t.Pos.Z) < WorldScene.GroundHalf)
            .Select(t => { var v = _scene.Flora.Variants[t.Variant]; return new[] { t.Pos.X, t.Pos.Y, t.Pos.Z, v.TrunkR * t.Scale, (v.TrunkTop + 1) * t.Scale }; })
            .ToArray();
        return JsonSerializer.Serialize(new { t = "hello", player = _player, ground = new { origin = new[] { o, o }, step, n, h }, trees });
    }

    public void Send(object msg)
    {
        var w = _w;
        if (w is null) { SetNote("세계 서버에 연결되어 있지 않다 (world/ 를 실행하면 편집할 수 있다)"); return; }
        try { lock (_sendLock) w.WriteLine(JsonSerializer.Serialize(msg)); }
        catch (IOException) { _w = null; }
    }

    static float[] A(Vector3 v) => [v.X, v.Y, v.Z];
    public void Dig(Vector3 p, Vector3 n, Vector3 from) => Send(new { t = "dig", p = A(p), n = A(n), from = A(from) });
    public void Pour(Vector3 p, Vector3 from) => Send(new { t = "pour", p = A(p), from = A(from) });
    public void Pickup(Vector3 p) => Send(new { t = "pickup", p = A(p) });
    public void Drop(Vector3 p, Vector3 v) => Send(new { t = "drop", p = A(p), v = A(v) });

    void SetNote(string s) { Note = s; Changed?.Invoke(); }

    static Vector3 V3(JsonElement e) => new(e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle());

    void AddBody(JsonElement b)
    {
        var q = b.GetProperty("q");
        Bodies[b.GetProperty("id").GetInt32()] = new Body
        {
            Id = b.GetProperty("id").GetInt32(), Item = b.GetProperty("item").GetString()!, Half = V3(b.GetProperty("half")), R = b.GetProperty("r").GetSingle(),
            P = V3(b.GetProperty("p")), Q = new Quaternion(q[0].GetSingle(), q[1].GetSingle(), q[2].GetSingle(), q[3].GetSingle()),
        };
    }

    void ApplyEdit(JsonElement e) =>
        _scene.ApplyEdit(new Terrain.Edit(e.GetProperty("id").GetInt64(), e.GetProperty("op").GetString() == "add", V3(e.GetProperty("p")), e.GetProperty("r").GetSingle()));

    /// <summary>받은 메시지를 세계에 반영한다 (주 스레드에서 프레임마다)</summary>
    public void Pump()
    {
        while (_in.TryDequeue(out var m))
        {
            switch (m.GetProperty("t").GetString())
            {
                case "welcome":
                    foreach (var e in m.GetProperty("edits").EnumerateArray()) ApplyEdit(e);
                    Bodies.Clear();
                    foreach (var b in m.GetProperty("bodies").EnumerateArray()) AddBody(b);
                    Held = m.GetProperty("held").ValueKind == JsonValueKind.String ? m.GetProperty("held").GetString() : null;
                    SetNote($"세계 서버 연결: 편집 {m.GetProperty("edits").GetArrayLength()}개, 물건 {Bodies.Count}개");
                    break;
                case "edit":
                    ApplyEdit(m.GetProperty("edit"));
                    if (m.TryGetProperty("msg", out var em)) SetNote(em.GetString()!);
                    break;
                case "deny":
                    SetNote("✕ " + m.GetProperty("msg").GetString());
                    break;
                case "body_add": AddBody(m.GetProperty("body")); break;
                case "body_del": Bodies.Remove(m.GetProperty("id").GetInt32()); break;
                case "bodies":
                    foreach (var a in m.GetProperty("list").EnumerateArray())
                        if (Bodies.TryGetValue(a[0].GetInt32(), out var b))
                        {
                            b.P = new(a[1].GetSingle(), a[2].GetSingle(), a[3].GetSingle());
                            b.Q = new(a[4].GetSingle(), a[5].GetSingle(), a[6].GetSingle(), a[7].GetSingle());
                        }
                    break;
                case "held":
                    Held = m.GetProperty("item").ValueKind == JsonValueKind.String ? m.GetProperty("item").GetString() : null;
                    if (m.TryGetProperty("msg", out var hm)) SetNote(hm.GetString()!);
                    else Changed?.Invoke();
                    break;
            }
        }
    }

    public void Dispose() { _stop = true; _w = null; }
}
