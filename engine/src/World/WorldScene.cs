using System.Diagnostics;
using System.Numerics;

namespace DollyForge;

/// <summary>
/// M1 세계: 지형 + 길가의 집 다섯 채(가운데가 내 집) + 내 집 가구. 하나의 거리장으로 충돌·빛 굽기를 한다 (화면과 물리가 같은 곡면).
///   지형 섹션: 가까운 곳(원점 ±64 m) 0.25 m 표본 · 8 m 섹션, 먼 곳(±512 m) 1 m 표본 · 32 m 섹션. 먼 섹션은 가까운 영역을 비워 그린다.
///   빛 굽기 (정점마다): 오목한 곳 그늘(AO), 해 그림자(부드러운 그림자 광선), 하늘이 보이는 정도(실내는 창으로만), 침실 전등.
/// 해는 아침 동남쪽 높이 약 38° (집 앞쪽에서 비쳐 침실 창으로 들어온다).
/// </summary>
sealed class WorldScene
{
    public static readonly Vector3 SunDir = Vector3.Normalize(new Vector3(0.55f, 0.62f, -0.56f));
    public static int OwnHouse => WorldLayout.Default.OwnHouse;      // Terrain.HouseX 에서 내 집 번호
    public const float FineHalf = 64, CoarseHalf = 512;

    public readonly ContentPack Pack;
    public readonly Terrain Terrain;
    public readonly HouseModel House;              // 내 집 (가구 포함)
    public readonly Flora Flora;                   // 나무 (인스턴스)
    // 풀 지도 (GPU 풀잎이 읽는다): 원점 ±GroundHalf, GroundStep 간격, GroundCount² 표본
    public const float GroundStep = 0.5f, GroundHalf = 64;
    public static readonly int GroundCount = (int)(2 * GroundHalf / GroundStep) + 1;
    public float[] GroundA = [], GroundB = [];     // A: 풀색 rgb + 풀 양,  B: 해 보임 · 하늘 보임 · 풀 키(0…1) · 꽃 확률
    public StaticMeshData TerrainFine = new(), TerrainCoarse = new(), Shell = new(), Furniture = new(), Glass = new();
    public readonly Vector3[] HouseOrigins = Terrain.HouseX.Select(x => new Vector3(x + WorldLayout.Default.OriginDx, 0, 0)).ToArray();
    public Vector3 Spawn => House.Spawn + HouseOrigins[OwnHouse];
    public float SpawnYaw => House.SpawnYaw;

    readonly SdfScene _neighbourCollide, _ownOptical, _neighbourOptical;
    SdfGrid? _ownGrid, _neighbourGrid;      // 빛 굽기용 미리 계산한 집 거리장 (집 기준 좌표)

    public WorldScene(ContentPack pack)
    {
        var sw = Stopwatch.StartNew();
        Pack = pack;
        Terrain = new Terrain(pack);
        Flora = new Flora(Terrain);
        Flora.BuildMeshes();
        Console.WriteLine($"[world] 나무 {Flora.Trees.Count:N0} 그루 (변형 {Flora.Variants.Count}개, 정점 {Flora.Variants.Sum(v => v.Near.VertexCount):N0}) {sw.Elapsed.TotalSeconds:0.0} 초");
        var def = pack.Houses.Values.First();
        House = HouseModel.Build(pack, def, furniture: true);
        _neighbourCollide = new SdfScene { Blend = 0.01f };
        foreach (var p in House.Shell.Adds) _neighbourCollide.Add(p);
        _neighbourCollide.Subs.AddRange(House.Shell.Subs);
        foreach (var g in House.Glass) _neighbourCollide.AddSolid(g);
        _neighbourOptical = new SdfScene { Blend = House.Shell.Blend }; foreach (var p in House.Shell.Adds) _neighbourOptical.Add(p); _neighbourOptical.Subs.AddRange(House.Shell.Subs);
        _ownOptical = new SdfScene { Blend = 0.01f }; foreach (var p in House.Shell.Adds) _ownOptical.Add(p); _ownOptical.Subs.AddRange(House.Shell.Subs);
        foreach (var (item, sc) in House.Furniture) if (item != "electric_lamp_home") foreach (var p in sc.Adds) _ownOptical.AddSolid(p);

        // 빛 굽기는 정점마다 수백 번 거리장을 묻는다 → 집 거리장을 6 cm 격자에 한 번 계산해 두고 보간해 읽는다
        _ownGrid = SdfGrid.Build(_ownOptical, 0.06f, 0.6f);
        _neighbourGrid = SdfGrid.Build(_neighbourOptical, 0.06f, 0.6f);
        Console.WriteLine($"[world] 집 거리장 격자 {sw.Elapsed.TotalSeconds:0.0} 초");
        BuildTerrain();
        Console.WriteLine($"[world] 지형 메시·빛 {sw.Elapsed.TotalSeconds:0.0} 초");
        BuildHouse();
        BuildGround();
        Console.WriteLine($"[world] 지형 정점 {TerrainFine.VertexCount + TerrainCoarse.VertexCount:N0} · 집 {Shell.VertexCount:N0} · 가구 {Furniture.VertexCount:N0} · {sw.Elapsed.TotalSeconds:0.0} 초");
    }

    // ─── 거리장 ───────────────────────────────────────────────────────────

    float Houses(Vector3 p, float d, bool optical)
    {
        for (var i = 0; i < HouseOrigins.Length; i++)
        {
            if (optical && _ownGrid is not null)
            {
                var grid = i == OwnHouse ? _ownGrid : _neighbourGrid!;
                d = MathF.Min(d, grid.Sample(p - HouseOrigins[i]));
                continue;
            }
            var sc = optical ? (i == OwnHouse ? _ownOptical : _neighbourOptical) : (i == OwnHouse ? House.Collision : _neighbourCollide);
            var l = p - HouseOrigins[i];
            var o = Vector3.Max(Vector3.Max(sc.Min - l, l - sc.Max), Vector3.Zero).Length();
            if (o > d) continue;
            d = MathF.Min(d, sc.Eval(l));
        }
        return d;
    }

    /// <summary>어느 집 안인가 (눈 적응: 실내는 노출을 연다)</summary>
    public bool Indoors(Vector3 p)
    {
        foreach (var o in HouseOrigins)
        {
            var l = p - o;
            if (l.X > 0 && l.X < House.Size.X && l.Z > 0 && l.Z < House.Size.Y && l.Y < House.WallHeight) return true;
        }
        return false;
    }

    /// <summary>충돌용 (유리·가구 포함)</summary>
    public float Sdf(Vector3 p) => Houses(p, MathF.Min(Terrain.Sdf(p), Flora.Sdf(p, false)), false);

    /// <summary>빛용 (유리는 빛을 막지 않는다)</summary>
    public float OpticalSdf(Vector3 p) => Houses(p, MathF.Min(Terrain.Sdf(p), Flora.Sdf(p, true)), true);

    /// <summary>집 바닥 아래인가 (풀잎이 마루를 뚫고 나오지 않게)</summary>
    bool UnderHouse(float x, float z, float margin)
    {
        foreach (var o in HouseOrigins)
            if (x > o.X - margin && x < o.X + House.Size.X + margin && z > o.Z - margin && z < o.Z + House.Size.Y + margin) return true;
        return false;
    }

    // ─── 풀 지도 ─────────────────────────────────────────────────────────

    void BuildGround()
    {
        var n = GroundCount;
        GroundA = new float[n * n * 4]; GroundB = new float[n * n * 4];
        var lengthNoise = new Noise2(53);
        Parallel.For(0, n, k =>
        {
            for (var i = 0; i < n; i++)
            {
                float x = -GroundHalf + i * GroundStep, z = -GroundHalf + k * GroundStep;
                var h = Terrain.Height(x, z);
                var p = new Vector3(x, h, z);
                var amount = UnderHouse(x, z, 0.3f) ? 0 : Terrain.Color(p).W;
                var g = Terrain.GrassColor(x, z);
                // 해 그림자 (집·나무·언덕), 수관 아래는 하늘빛이 줄어든다
                var sun = 1f; var t = 0.05f; var o = p + new Vector3(0, 0.15f, 0);
                for (var s = 0; s < 40 && t < 120; s++)
                {
                    var hh = OpticalSdf(o + SunDir * t);
                    if (hh < 0.001f) { sun = 0; break; }
                    sun = MathF.Min(sun, 8 * hh / t);
                    t += Math.Clamp(hh, 0.03f, 2.5f);
                }
                var sky = 1 - 0.65f * Flora.Cover(p);
                // 풀 키: 집 앞뒤 마당은 깎은 잔디, 길가·들판은 무릎 아래 풀, 야생은 더 길다
                var lawn = (1 - Smooth(42, 50, MathF.Abs(x))) * Smooth(-15, -12.5f, z) * (1 - Smooth(17, 21, z));
                var wild = Terrain.Wildness(x, z);
                var len = MathF.Max(0.1f, (1 - lawn) * (0.45f + 0.35f * wild) * (0.75f + 0.5f * (lengthNoise.Fbm(x / 7f, z / 7f, 2) + 0.5f)));
                var flower = lawn > 0.5f ? 0.012f : 0.03f + 0.04f * wild;
                var a = (k * n + i) * 4;
                GroundA[a] = g.X; GroundA[a + 1] = g.Y; GroundA[a + 2] = g.Z; GroundA[a + 3] = amount;
                GroundB[a] = Math.Clamp(sun, 0, 1); GroundB[a + 1] = sky; GroundB[a + 2] = Math.Clamp(len, 0, 1); GroundB[a + 3] = flower;
            }
        });
    }

    static float Smooth(float a, float b, float x) { var t = Math.Clamp((x - a) / (b - a), 0, 1); return t * t * (3 - 2 * t); }

    public Vector3 Gradient(Vector3 p, float e = 0.01f)
    {
        var g = new Vector3(Sdf(p + new Vector3(e, 0, 0)) - Sdf(p - new Vector3(e, 0, 0)),
                            Sdf(p + new Vector3(0, e, 0)) - Sdf(p - new Vector3(0, e, 0)),
                            Sdf(p + new Vector3(0, 0, e)) - Sdf(p - new Vector3(0, 0, e)));
        return g.LengthSquared() > 1e-20f ? Vector3.Normalize(g) : Vector3.UnitY;
    }

    /// <summary>a 에서 dir 로 쏜 광선이 처음 닿는 거리 (못 닿으면 max)</summary>
    public float Raycast(Vector3 a, Vector3 dir, float max, float radius = 0)
    {
        var t = 0f;
        for (var i = 0; i < 64 && t < max; i++)
        {
            var h = Sdf(a + dir * t) - radius;
            if (h < 0.002f) return t;
            t += MathF.Max(h, 0.01f);
        }
        return max;
    }

    // ─── 지형 메시 ───────────────────────────────────────────────────────

    void BuildTerrain()
    {
        TerrainFine = Sections(0.25f, 32, FineHalf, 0);
        TerrainCoarse = Sections(1f, 32, CoarseHalf, FineHalf - 4);
        Console.WriteLine($"[world]   지형 메시 정점 {TerrainFine.VertexCount:N0} + {TerrainCoarse.VertexCount:N0}");
        Bake(TerrainFine, fullSky: false, shadowSteps: 40, shadowMax: 120);
        Bake(TerrainCoarse, fullSky: false, shadowSteps: 20, shadowMax: 400, terrainOnly: true);
    }

    /// <summary>half 범위의 섹션들을 메시화해 하나로 합친다. skipInner 안쪽의 섹션은 건너뛴다 (가까운 섹션이 그린다)</summary>
    StaticMeshData Sections(float step, int n, float half, float skipInner)
    {
        var size = step * n;
        var count = (int)(2 * half / size);
        var cols = new List<(int X, int Z)>();
        for (var x = 0; x < count; x++)
            for (var z = 0; z < count; z++)
            {
                float x0 = -half + x * size, z0 = -half + z * size;
                if (skipInner > 0 && x0 >= -skipInner && x0 + size <= skipInner && z0 >= -skipInner && z0 + size <= skipInner) continue;
                cols.Add((x, z));
            }
        var parts = new StaticMeshData[cols.Count][];
        Parallel.For(0, cols.Count, ci =>
        {
            var (x, z) = cols[ci];
            float x0 = -half + x * size, z0 = -half + z * size;
            // 기둥의 높이 범위 → 표면이 지나는 섹션만
            float lo = float.MaxValue, hi = float.MinValue;
            for (var a = 0; a <= 8; a++) for (var b = 0; b <= 8; b++)
            {
                var hgt = Terrain.Height(x0 + size * a / 8, z0 + size * b / 8);
                lo = MathF.Min(lo, hgt); hi = MathF.Max(hi, hgt);
            }
            var y0 = (int)MathF.Floor((lo - 1.5f) / size); var y1 = (int)MathF.Floor((hi + 1.5f) / size);
            // 이 기둥의 높이 표 (−2 … n+2): 표본마다 노이즈를 다시 부르지 않게
            var m = n + 5;
            var hs = new float[m * m];
            for (var k = 0; k < m; k++) for (var i = 0; i < m; i++) hs[k * m + i] = Terrain.Height(x0 + (i - 2) * step, z0 + (k - 2) * step);
            float SdfAt(Vector3 p)
            {
                var i = (int)MathF.Round((p.X - x0) / step) + 2; var k = (int)MathF.Round((p.Z - z0) / step) + 2;
                i = Math.Clamp(i, 1, m - 2); k = Math.Clamp(k, 1, m - 2);
                var gx = (hs[k * m + i + 1] - hs[k * m + i - 1]) / (2 * step); var gz = (hs[(k + 1) * m + i] - hs[(k - 1) * m + i]) / (2 * step);
                return (p.Y - hs[k * m + i]) / MathF.Sqrt(1 + gx * gx + gz * gz);
            }
            var list = new List<StaticMeshData>();
            for (var y = y0; y <= y1; y++)
            {
                var mesh = SurfaceNets.Mesh(SdfAt, new Vector3(x0, y * size, z0), step, (n, n, n), Terrain.Normal, Terrain.Color, parallel: false);
                if (mesh.VertexCount > 0) list.Add(mesh);
            }
            parts[ci] = list.ToArray();
        });
        return Merge(parts.SelectMany(p => p));
    }

    static StaticMeshData Merge(IEnumerable<StaticMeshData> meshes)
    {
        var all = new StaticMeshData();
        foreach (var m in meshes)
        {
            var off = all.Pos.Count;
            all.Pos.AddRange(m.Pos); all.Nrm.AddRange(m.Nrm); all.Col.AddRange(m.Col);
            all.Idx.AddRange(m.Idx.Select(i => i + off));
        }
        return all;
    }

    // ─── 집 메시 ─────────────────────────────────────────────────────────

    void BuildHouse()
    {
        var brick = Pack.Color("clay_brick", new(0.62f, 0.30f, 0.22f));
        var floor = Pack.Color("oak", new(0.55f, 0.40f, 0.25f));
        var concrete = Pack.Color("concrete", new(0.66f, 0.65f, 0.62f));
        Shell = MeshScene(House.Shell, 0.05f, p => House.ShellColor(p, brick, floor, concrete));
        Furniture = Merge(House.Furniture.Select(f => MeshScene(f.Scene, Math.Clamp(Thinnest(f.Scene) / 2.5f, 0.008f, 0.02f), null)));
        Glass = Merge(House.Glass.Select(g => MeshScene(new SdfScene { Blend = 0.001f }.Add(g), 0.004f, null)));
        Console.WriteLine($"[world]   집·가구 메시 정점 {Shell.VertexCount:N0} + {Furniture.VertexCount:N0}");
        // 빛 굽기는 내 집 자리 기준 (다섯 채가 같은 방향이라 이웃 집도 같은 빛을 쓴다)
        var origin = HouseOrigins[OwnHouse];
        Offset(Shell, origin); Offset(Furniture, origin);
        Bake(Shell, fullSky: true, shadowSteps: 48, shadowMax: 60);
        Bake(Furniture, fullSky: true, shadowSteps: 48, shadowMax: 60);
        Offset(Shell, -origin); Offset(Furniture, -origin);
        foreach (var _ in Glass.Pos) Glass.Light.Add(new Vector4(1, 1, 1, 0));
    }

    static float Thinnest(SdfScene s) => s.Adds.Min(p => MathF.Min(p.Half.X, MathF.Min(p.Half.Y, p.Half.Z)) * 2);

    static void Offset(StaticMeshData m, Vector3 o) { for (var i = 0; i < m.Pos.Count; i++) m.Pos[i] += o; }

    static StaticMeshData MeshScene(SdfScene s, float step, Func<Vector3, Vector4>? color)
    {
        var lo = s.Min - new Vector3(step * 3); var hi = s.Max + new Vector3(step * 3);
        var n = ((int)MathF.Ceiling((hi.X - lo.X) / step), (int)MathF.Ceiling((hi.Y - lo.Y) / step), (int)MathF.Ceiling((hi.Z - lo.Z) / step));
        return SurfaceNets.Mesh(s.Eval, lo, step, n, p => s.Normal(p, step * 0.5f),
            color ?? (p => { s.EvalColor(p, out var c, out var e); return new Vector4(c, e > 0.5f ? 2 : 0); }));
    }

    // ─── 빛 굽기 ─────────────────────────────────────────────────────────

    static readonly Vector3[] SkyDirs = MakeSkyDirs();
    static Vector3[] MakeSkyDirs()
    {
        var d = new List<Vector3>();
        foreach (var el in new[] { 0.35f, 0.9f })
            for (var a = 0; a < 6; a++)
            {
                var az = a * MathF.Tau / 6 + el;
                d.Add(new Vector3(MathF.Cos(az) * MathF.Cos(el), MathF.Sin(el), MathF.Sin(az) * MathF.Cos(el)));
            }
        d.Add(Vector3.UnitY);
        return d.ToArray();
    }

    void Bake(StaticMeshData m, bool fullSky, int shadowSteps, float shadowMax, bool terrainOnly = false)
    {
        var light = new Vector4[m.VertexCount];
        Func<Vector3, float> sdf = terrainOnly ? Terrain.Sdf : OpticalSdf;
        Parallel.For(0, m.VertexCount, v =>
        {
            var p = m.Pos[v]; var n = m.Nrm[v];
            // 오목한 곳 그늘
            var ao = 1f;
            if (!terrainOnly)
            {
                var occ = 0f; var w = 1f;
                foreach (var h in new[] { 0.04f, 0.1f, 0.22f, 0.45f }) { occ += w * MathF.Max(0, h - sdf(p + n * h)) / h; w *= 0.6f; }
                ao = Math.Clamp(1 - occ * 0.55f, 0.15f, 1);
            }
            // 해 그림자 (부드러운 그림자: 광선이 가장 가까이 스친 정도)
            var sun = 0f;
            if (Vector3.Dot(n, SunDir) > -0.1f)
            {
                var res = 1f; var t = 0.03f;
                var o = p + n * 0.02f;
                for (var i = 0; i < shadowSteps && t < shadowMax; i++)
                {
                    var hh = sdf(o + SunDir * t);
                    if (hh < 0.001f) { res = 0; break; }
                    res = MathF.Min(res, 10 * hh / t);
                    t += Math.Clamp(hh, 0.02f, terrainOnly ? 8f : 2.5f);
                }
                sun = Math.Clamp(res, 0, 1);
            }
            // 하늘이 보이는 정도 (실내는 창·문으로만)
            var sky = 1f;
            if (fullSky)
            {
                float seen = 0, total = 0;
                var t1 = Vector3.Normalize(MathF.Abs(n.Y) < 0.9f ? Vector3.Cross(n, Vector3.UnitY) : Vector3.Cross(n, Vector3.UnitX));
                foreach (var sd in SkyDirs)
                {
                    var dir = Vector3.Normalize(n * 0.6f + sd);
                    if (dir.Y < -0.05f) continue;
                    var wgt = MathF.Max(0.1f, Vector3.Dot(dir, n));
                    total += wgt;
                    var tt = 0.05f; var hit = false;
                    for (var i = 0; i < 24 && tt < 14; i++)
                    {
                        var hh = sdf(p + n * 0.02f + dir * tt);
                        if (hh < 0.004f) { hit = true; break; }
                        tt += MathF.Max(hh, 0.03f);
                    }
                    if (!hit) seen += wgt;
                }
                sky = total > 0 ? seen / total : 0;
            }
            // 전등 (집 안 정점만)
            var lamp = 0f;
            foreach (var lp in House.Lamps)
            {
                var lw = lp + HouseOrigins[OwnHouse];
                var to = lw - p; var dist = to.Length();
                if (dist > 7 || dist < 1e-3f) continue;
                var dir = to / dist; var ndl = Vector3.Dot(n, dir);
                if (ndl <= 0) continue;
                var vis = 1f; var t = 0.03f;
                for (var i = 0; i < 32 && t < dist - 0.25f; i++)
                {
                    var hh = sdf(p + n * 0.02f + dir * t);
                    if (hh < 0.002f) { vis = 0; break; }
                    vis = MathF.Min(vis, 8 * hh / t);
                    t += MathF.Max(hh, 0.02f);
                }
                lamp += Math.Clamp(vis, 0, 1) * ndl * 0.8f / (dist * dist + 0.6f);
            }
            light[v] = new Vector4(ao, sun, sky, lamp);
        });
        m.Light.Clear(); m.Light.AddRange(light);
    }
}

/// <summary>
/// 거리장을 격자에 미리 계산해 둔 것 (삼선형 보간으로 읽는다). 격자 밖은 격자 상자까지의 거리 + 가장자리 값으로 근사한다:
/// 집은 격자 안에 다 들어 있으므로 밖에서는 이 값이 실제 거리보다 작거나 같다 (광선 추적에 안전).
/// </summary>
sealed class SdfGrid
{
    readonly Vector3 _origin; readonly float _step; readonly int _nx, _ny, _nz; readonly float[] _v;

    SdfGrid(Vector3 o, float s, int nx, int ny, int nz) { _origin = o; _step = s; _nx = nx; _ny = ny; _nz = nz; _v = new float[nx * ny * nz]; }

    public static SdfGrid Build(SdfScene scene, float step, float margin)
    {
        var lo = scene.Min - new Vector3(margin); var hi = scene.Max + new Vector3(margin);
        var g = new SdfGrid(lo, step, (int)MathF.Ceiling((hi.X - lo.X) / step) + 1, (int)MathF.Ceiling((hi.Y - lo.Y) / step) + 1, (int)MathF.Ceiling((hi.Z - lo.Z) / step) + 1);
        Parallel.For(0, g._nz, k =>
        {
            for (var j = 0; j < g._ny; j++)
                for (var i = 0; i < g._nx; i++)
                    g._v[(k * g._ny + j) * g._nx + i] = scene.Eval(lo + new Vector3(i, j, k) * step);
        });
        return g;
    }

    public float Sample(Vector3 p)
    {
        var f = (p - _origin) / _step;
        var c = Vector3.Clamp(f, Vector3.Zero, new Vector3(_nx - 1.001f, _ny - 1.001f, _nz - 1.001f));
        var outside = (f - c).Length() * _step;
        int i = (int)c.X, j = (int)c.Y, k = (int)c.Z;
        float tx = c.X - i, ty = c.Y - j, tz = c.Z - k;
        float V(int a, int b, int cc) => _v[((k + cc) * _ny + (j + b)) * _nx + (i + a)];
        var x00 = V(0, 0, 0) + (V(1, 0, 0) - V(0, 0, 0)) * tx; var x10 = V(0, 1, 0) + (V(1, 1, 0) - V(0, 1, 0)) * tx;
        var x01 = V(0, 0, 1) + (V(1, 0, 1) - V(0, 0, 1)) * tx; var x11 = V(0, 1, 1) + (V(1, 1, 1) - V(0, 1, 1)) * tx;
        var v = (x00 + (x10 - x00) * ty) + ((x01 + (x11 - x01) * ty) - (x00 + (x10 - x00) * ty)) * tz;
        return outside > 0 ? MathF.Max(v, 0) + outside : v;
    }
}
