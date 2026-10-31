using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace DollyForge;

/// <summary>
/// Rust 세계 서버(world/, tcp 127.0.0.1:7701)와의 연결: 한 줄 JSON.
///   세계를 바꾸는 일(파기·붓기·줍기·내려놓기·잇기·풀기)은 서버에 요청하고, 서버가 존중 규칙으로 판정해 허락한 것만 세계에 반영한다.
///   흩어진 물건(부품 조립체)은 서버의 Rapier 가 움직이고, 엔진은 자세를 받아 그린다.
/// 서버가 없으면 2 초마다 다시 붙고, 그동안 세계는 그대로 걷고 볼 수 있다 (편집만 안 된다).
/// </summary>
sealed class WorldLink : IDisposable
{
    public sealed class Body
    {
        public int Id;
        public string Owner = "";
        public AssemblyDef Shape = new();
        public float Mass;
        public Vector3 P;
        public Quaternion Q = Quaternion.Identity;
        public Matrix4x4 Model => Matrix4x4.CreateFromQuaternion(Q) * Matrix4x4.CreateTranslation(P);
        // 렌더가 물건 자리마다 구한 빛 (해·하늘·전등) 과 그때의 자리
        public Vector3 Light = Vector3.One, LightAt = new(float.NaN);
    }

    /// <summary>충돌·겨냥용 부품 상자 하나 (세계 좌표 자세의 역행렬, 반 치수, 둥근 모서리)</summary>
    public readonly record struct Solid(int Body, int Part, Matrix4x4 Inv, Vector3 Center, Vector3 Half, float R, float Bound);

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
    /// <summary>들고 있는 조립체 (없으면 null)</summary>
    public AssemblyDef? Held { get; private set; }
    public string HeldName { get; private set; } = "";
    public float HeldMass { get; private set; }
    public readonly Dictionary<int, Body> Bodies = [];
    /// <summary>놓인 물건의 부품 상자들 (Pump 가 바꿔 끼우는 사본: 걷기 물리·광선이 다른 스레드에서 읽어도 된다)</summary>
    public Solid[] Solids { get; private set; } = [];
    bool _solidsDirty;
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

    /// <summary>처음 인사: 서버의 Rapier 가 쓸 땅 높이장(가까운 ±64 m, 0.5 m)과 나무 줄기, 집 충돌 곡면</summary>
    string Hello()
    {
        var (ownShell, otherShell, ownBoxes, otherBoxes) = _scene.CollisionShapes();
        static object M(StaticMeshData m) => new
        {
            v = m.Pos.SelectMany(p => new[] { MathF.Round(p.X, 3), MathF.Round(p.Y, 3), MathF.Round(p.Z, 3) }).ToArray(),
            i = m.Idx.ToArray(),
        };
        var n = WorldScene.GroundCount; var step = WorldScene.GroundStep; var o = -WorldScene.GroundHalf;
        var h = new float[n * n];
        var size = _scene.House.Size;
        bool UnderHouse(float x, float z) => _scene.HouseOrigins.Any(c => x > c.X - 0.05f && x < c.X + size.X + 0.05f && z > c.Z - 0.05f && z < c.Z + size.Y + 0.05f);
        for (var k = 0; k < n; k++) for (var i = 0; i < n; i++)
        {
            float x = o + i * step, z = o + k * step;
            var y = _scene.Terrain.Height(x, z);
            if (UnderHouse(x, z)) y = MathF.Min(y, -0.1f);    // 집 바닥 슬래브 아래: 땅이 마루를 뚫고 올라오지 않게
            h[k * n + i] = MathF.Round(y * 1000) / 1000;
        }
        var trees = _scene.Flora.Trees
            .Where(t => MathF.Abs(t.Pos.X) < WorldScene.GroundHalf && MathF.Abs(t.Pos.Z) < WorldScene.GroundHalf)
            .Select(t => { var v = _scene.Flora.Variants[t.Variant]; return new[] { t.Pos.X, t.Pos.Y, t.Pos.Z, v.TrunkR * t.Scale, (v.TrunkTop + 1) * t.Scale }; })
            .ToArray();
        var houses = new { origins = _scene.HouseOrigins.Select(A).ToArray(), shell = M(ownShell), shell_other = M(otherShell), own = ownBoxes, other = otherBoxes };
        return JsonSerializer.Serialize(new { t = "hello", player = _player, ground = new { origin = new[] { o, o }, step, n, h }, trees, houses });
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
    public void Pickup(int? id, Vector3 p, Vector3 from) => Send(new { t = "pickup", id, p = A(p), from = A(from) });
    public void Drop(Vector3 p, Quaternion q, Vector3 v) => Send(new { t = "drop", p = A(p), q = new[] { q.X, q.Y, q.Z, q.W }, v = A(v) });
    public void Join(int id, Vector3 p, Vector3 from) => Send(new { t = "join", id, p = A(p), from = A(from) });
    public void Split(int id, Vector3 p, Vector3 from) => Send(new { t = "split", id, p = A(p), from = A(from) });

    void SetNote(string s) { Note = s; Changed?.Invoke(); }

    static Vector3 V3(JsonElement e) => new(e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle());

    void AddBody(JsonElement b)
    {
        var q = b.GetProperty("q");
        Bodies[b.GetProperty("id").GetInt32()] = new Body
        {
            Id = b.GetProperty("id").GetInt32(), Owner = b.GetProperty("owner").GetString()!, Shape = AssemblyDef.Parse(b.GetProperty("shape")),
            Mass = b.GetProperty("mass").GetSingle(),
            P = V3(b.GetProperty("p")), Q = new Quaternion(q[0].GetSingle(), q[1].GetSingle(), q[2].GetSingle(), q[3].GetSingle()),
        };
        _solidsDirty = true;
    }

    void SetHeld(JsonElement m)
    {
        var sh = m.TryGetProperty("shape", out var s) ? s : default;
        Held = sh.ValueKind == JsonValueKind.Object ? AssemblyDef.Parse(sh) : null;
        HeldName = m.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String ? nm.GetString()! : Held is null ? "" : string.Join("+", Held.Parts.Select(p => _scene.Pack.Items.TryGetValue(p.Item, out var it) ? it.Name : p.Item));
        HeldMass = m.TryGetProperty("mass", out var ms) && ms.ValueKind == JsonValueKind.Number ? ms.GetSingle() : 0;
        HeldChanged?.Invoke();
    }

    /// <summary>들고 있는 것이 바뀌었다 (손에 든 자세를 처음으로)</summary>
    public event Action? HeldChanged;

    void RebuildSolids()
    {
        var list = new List<Solid>();
        foreach (var b in Bodies.Values)
        {
            var model = b.Model;
            for (var i = 0; i < b.Shape.Parts.Length; i++)
            {
                var part = b.Shape.Parts[i];
                var m = Matrix4x4.CreateFromQuaternion(part.Q) * Matrix4x4.CreateTranslation(part.P) * model;
                Matrix4x4.Invert(m, out var inv);
                var half = Parts.Half(_scene.Pack, part.Item);
                list.Add(new Solid(b.Id, i, inv, m.Translation, half, Parts.Round(_scene.Pack, part.Item), half.Length()));
            }
        }
        Solids = list.ToArray();
    }

    /// <summary>놓인 물건까지의 거리 (둥근 상자 근사): 걷기 물리가 지형·집과 함께 쓴다</summary>
    public static float SolidSdf(Solid[] solids, Vector3 p)
    {
        var d = 1e9f;
        foreach (var s in solids)
        {
            if (Vector3.Distance(p, s.Center) - s.Bound > d) continue;
            var q = Vector3.Abs(Vector3.Transform(p, s.Inv)) - (s.Half - new Vector3(s.R));
            d = MathF.Min(d, Vector3.Max(q, Vector3.Zero).Length() + MathF.Min(MathF.Max(q.X, MathF.Max(q.Y, q.Z)), 0) - s.R);
        }
        return d;
    }

    /// <summary>시선이 처음 닿는 물건 (부품 상자와 광선의 교차): (물건 id, 거리)</summary>
    public (int Id, float T)? Raycast(Vector3 o, Vector3 dir, float max)
    {
        (int, float)? best = null;
        foreach (var s in Solids)
        {
            var lo = Vector3.Transform(o, s.Inv); var ld = Vector3.TransformNormal(dir, s.Inv);
            float t0 = 0, t1 = max;
            var hit = true;
            for (var k = 0; k < 3 && hit; k++)
            {
                float oo = k == 0 ? lo.X : k == 1 ? lo.Y : lo.Z, dd = k == 0 ? ld.X : k == 1 ? ld.Y : ld.Z, hh = k == 0 ? s.Half.X : k == 1 ? s.Half.Y : s.Half.Z;
                if (MathF.Abs(dd) < 1e-8f) { if (MathF.Abs(oo) > hh) hit = false; continue; }
                var a = (-hh - oo) / dd; var b = (hh - oo) / dd;
                if (a > b) (a, b) = (b, a);
                t0 = MathF.Max(t0, a); t1 = MathF.Min(t1, b);
                if (t0 > t1) hit = false;
            }
            if (hit && (best is null || t0 < best.Value.Item2)) best = (s.Body, t0);
        }
        return best;
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
                    Bodies.Clear(); _solidsDirty = true;
                    foreach (var b in m.GetProperty("bodies").EnumerateArray()) AddBody(b);
                    SetHeld(JsonDocument.Parse(JsonSerializer.Serialize(new { shape = m.GetProperty("held") })).RootElement);
                    SetNote($"세계 서버 연결: 편집 {m.GetProperty("edits").GetArrayLength()}개, 물건 {Bodies.Count}개");
                    break;
                case "edit":
                    ApplyEdit(m.GetProperty("edit"));
                    if (m.TryGetProperty("msg", out var em)) SetNote(em.GetString()!);
                    break;
                case "deny":
                    SetNote("✕ " + m.GetProperty("msg").GetString());
                    break;
                case "note":
                    SetNote(m.GetProperty("msg").GetString()!);
                    break;
                case "body_add": AddBody(m.GetProperty("body")); break;
                case "body_del": Bodies.Remove(m.GetProperty("id").GetInt32()); _solidsDirty = true; break;
                case "bodies":
                    foreach (var a in m.GetProperty("list").EnumerateArray())
                        if (Bodies.TryGetValue(a[0].GetInt32(), out var b))
                        {
                            b.P = new(a[1].GetSingle(), a[2].GetSingle(), a[3].GetSingle());
                            b.Q = new(a[4].GetSingle(), a[5].GetSingle(), a[6].GetSingle(), a[7].GetSingle());
                            _solidsDirty = true;
                        }
                    break;
                case "held":
                    SetHeld(m);
                    if (m.TryGetProperty("msg", out var hm) && hm.ValueKind == JsonValueKind.String) SetNote(hm.GetString()!);
                    else Changed?.Invoke();
                    break;
            }
        }
        if (_solidsDirty) { RebuildSolids(); _solidsDirty = false; }
    }

    public void Dispose() { _stop = true; _w = null; }
}
