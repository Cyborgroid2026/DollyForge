using System.Numerics;

namespace DollyForge;

/// <summary>
/// 나무: 콘텐츠 팩 지역의 나무 종(참나무·자작나무·소나무·가문비나무·벚나무)마다 변형 세 개를 거리장으로 짓고 (줄기·가지·잎 덩어리가 부드럽게 이어진 곡면),
/// 자리마다 크기만 달리해 인스턴스로 그린다. 빛(그늘·해·하늘)은 변형마다 미리 굽는다: 해 방향이 고정이라 돌리지 않는다.
///   땅에 지는 그림자와 충돌은 대리 형태로: 줄기 = 캡슐(부딪힌다), 수관 = 타원체(빛만 가린다).
///   자리: 야생은 노이즈 숲 덩어리, 시골·외곽은 드문드문, 집마다 뒷마당 나무, 길가 가로수, 밭 가장자리 산울타리. 길·집터·밭 안·가파른 비탈은 비운다.
/// </summary>
sealed class Flora
{
    public sealed class Variant
    {
        public required string Species;
        public required SdfScene Scene;
        public StaticMeshData Near = new(), Far = new();
        public float TrunkR, TrunkTop;            // 충돌 캡슐 (바닥 중심에서 위로)
        public Vector3 CanopyC, CanopyR;           // 빛 가림 타원체
    }

    public readonly record struct Tree(int Variant, Vector3 Pos, float Scale);

    public static readonly string[] Species = ["oak", "birch", "spruce", "pine", "cherry"];
    const int PerSpecies = 3;
    public readonly List<Variant> Variants = [];
    public readonly List<Tree> Trees = [];

    // 공간 격자: 거리장을 물을 때 가까운 나무만 본다
    const float Cell = 8, Reach = 6.5f;            // Reach = 가장 큰 나무가 줄기에서 수평으로 뻗는 거리 (크기 반영)
    readonly Dictionary<(int, int), List<int>> _cells = [];

    public Flora(Terrain terrain, int seed = 7)
    {
        for (var s = 0; s < Species.Length; s++)
            for (var v = 0; v < PerSpecies; v++)
                Variants.Add(Build(Species[s], new Random(seed * 131 + s * 17 + v)));
        Place(terrain, seed);
        for (var i = 0; i < Trees.Count; i++)
        {
            var key = ((int)MathF.Floor(Trees[i].Pos.X / Cell), (int)MathF.Floor(Trees[i].Pos.Z / Cell));
            if (!_cells.TryGetValue(key, out var l)) _cells[key] = l = [];
            l.Add(i);
        }
    }

    public int VariantOf(string species, int k) => Array.IndexOf(Species, species) * PerSpecies + k % PerSpecies;

    // ─── 나무 모양 ───────────────────────────────────────────────────────

    static SdfPrim Ball(Vector3 c, Vector3 r, Vector3 col, bool leaf)
    {
        var p = SdfPrim.Box(c - r, c + r, MathF.Min(r.X, MathF.Min(r.Y, r.Z)), col);
        p.Emissive = leaf ? 1 : 0;                  // 나무에서 Emissive 는 "잎" 표시 (색 a 로 간다)
        return p;
    }

    /// <summary>a 에서 b 까지 둥근 막대 (가지)</summary>
    static SdfPrim Limb(Vector3 a, Vector3 b, float r, Vector3 col)
    {
        var ey = Vector3.Normalize(b - a);
        var ex = Vector3.Normalize(Vector3.Cross(ey, MathF.Abs(ey.Y) < 0.95f ? Vector3.UnitY : Vector3.UnitX));
        var ez = Vector3.Cross(ex, ey);
        return SdfPrim.Oriented((a + b) * 0.5f, ex, ey, ez, new Vector3(r, (b - a).Length() * 0.5f + r, r), r, col);
    }

    static Vector3 Jit(Random rng, Vector3 c, float k) => c * (1 + ((float)rng.NextDouble() - 0.5f) * k);
    static float F(Random rng, float a, float b) => a + (float)rng.NextDouble() * (b - a);

    static Variant Build(string species, Random rng)
    {
        var s = new SdfScene { Blend = 0.22f };
        Vector3 bark, leaf;
        float trunkR, trunkTop;
        var leanTop = 0f;
        void Trunk(float r0, float r1, float h, float lean)
        {
            // 위로 갈수록 가늘어지고 살짝 휜 줄기: 곡선 위의 점들을 둥근 막대로 잇는다 (부드러운 합집합이라 마디가 보이지 않는다)
            const int seg = 6;
            Vector3 P(float t) => new(lean * t * t, t * h, 0);
            for (var i = 0; i < seg; i++)
            {
                float t0 = (float)i / seg, t1 = (float)(i + 1) / seg;
                s.Add(Limb(P(t0), P(t1), r0 + (r1 - r0) * (t0 + t1) * 0.5f, bark));
            }
            s.Add(SdfPrim.Cyl(Vector3.Zero, r0 * 1.45f, 0.35f, 0.15f, bark));   // 뿌리 쪽이 퍼진다
            leanTop = lean;
        }
        void Crown(Vector3 c, Vector3 rad, int count, float rMin, float rMax, float flat = 1)
        {
            // 잎 덩어리: 타원체 안에 공들을 흩고, 부드러운 합집합으로 한 덩어리 곡면이 된다
            s.Add(Ball(c, rad * 0.62f, leaf, true));
            for (var i = 0; i < count; i++)
            {
                Vector3 d;
                do d = new Vector3(F(rng, -1, 1), F(rng, -1, 1), F(rng, -1, 1)); while (d.LengthSquared() > 1);
                var r = F(rng, rMin, rMax);
                var off = d * (rad - new Vector3(r * 0.7f));
                s.Add(Ball(c + off, new Vector3(r, r * flat, r), leaf, true));
            }
        }

        switch (species)
        {
            case "oak":     // 굵은 줄기, 사방으로 뻗은 큰 가지, 넓고 둥근 수관
            {
                bark = new(0.29f, 0.24f, 0.19f); leaf = new(0.19f, 0.31f, 0.10f);
                trunkR = F(rng, 0.30f, 0.38f); trunkTop = F(rng, 2.6f, 3.2f);
                Trunk(trunkR, trunkR * 0.7f, trunkTop, F(rng, -0.2f, 0.2f));
                var crownY = trunkTop + F(rng, 2.0f, 2.6f);
                for (var i = 0; i < 4; i++)
                {
                    var a = i * MathF.Tau / 4 + F(rng, -0.4f, 0.4f);
                    var dir = new Vector3(MathF.Cos(a), 0, MathF.Sin(a));
                    s.Add(Limb(new(0, trunkTop - 0.3f, 0), dir * F(rng, 1.6f, 2.3f) + new Vector3(0, crownY - 0.6f, 0), trunkR * 0.42f, bark));
                }
                Crown(new(0, crownY, 0), Jit(rng, new Vector3(3.3f, 2.1f, 3.3f), 0.25f), 18, 0.9f, 1.5f);
                break;
            }
            case "birch":   // 가늘고 흰 줄기, 위로 긴 좁은 수관, 밝은 잎
            {
                bark = new(0.84f, 0.83f, 0.79f); leaf = new(0.33f, 0.46f, 0.14f);
                trunkR = F(rng, 0.11f, 0.15f); trunkTop = F(rng, 7.0f, 8.5f);
                Trunk(trunkR, trunkR * 0.45f, trunkTop, F(rng, -0.5f, 0.5f));
                Crown(new(leanTop * 0.8f, trunkTop - 1.6f, 0), Jit(rng, new Vector3(1.7f, 3.0f, 1.7f), 0.2f), 14, 0.55f, 0.95f);
                break;
            }
            case "spruce":  // 곧은 줄기, 아래로 갈수록 넓어지는 층층의 원뿔 수관, 짙은 잎
            {
                bark = new(0.27f, 0.21f, 0.17f); leaf = new(0.09f, 0.19f, 0.10f);
                trunkR = F(rng, 0.18f, 0.24f); var h = F(rng, 9.5f, 12f); trunkTop = 1.2f;
                Trunk(trunkR, 0.05f, h, 0);
                var tiers = 10;
                for (var i = 0; i < tiers; i++)
                {
                    var t = (float)i / (tiers - 1);
                    var y = 1.1f + t * (h - 1.6f);
                    var r = (1 - t) * F(rng, 2.3f, 2.8f) + 0.3f;
                    s.Add(Ball(new(F(rng, -0.08f, 0.08f), y, F(rng, -0.08f, 0.08f)), new Vector3(r, 0.42f + 0.1f * (1 - t), r), leaf, true));
                }
                s.Add(Ball(new(0, h - 0.2f, 0), new Vector3(0.22f, 0.6f, 0.22f), leaf, true));
                break;
            }
            case "pine":    // 위쪽 줄기는 주황빛, 높이 올라간 납작한 잎 덩어리들
            {
                bark = new(0.42f, 0.27f, 0.18f); leaf = new(0.15f, 0.26f, 0.12f);
                trunkR = F(rng, 0.19f, 0.25f); trunkTop = F(rng, 7.0f, 8.5f);
                Trunk(trunkR, trunkR * 0.5f, trunkTop + 1.2f, F(rng, -0.8f, 0.8f));
                var lean = leanTop;
                for (var i = 0; i < 3; i++)
                {
                    var a = i * MathF.Tau / 3 + F(rng, 0, 1);
                    s.Add(Limb(new(lean * 0.8f, trunkTop - 0.4f, 0), new Vector3(MathF.Cos(a) * 1.6f + lean, trunkTop + 1.0f, MathF.Sin(a) * 1.6f), trunkR * 0.35f, bark));
                }
                Crown(new(lean, trunkTop + 1.5f, 0), Jit(rng, new Vector3(2.6f, 1.2f, 2.6f), 0.2f), 9, 0.8f, 1.2f, 0.55f);
                break;
            }
            default:        // cherry: 짧은 줄기에서 갈라진 가지, 넓게 퍼진 수관, 봄의 연분홍 꽃
            {
                bark = new(0.30f, 0.19f, 0.16f); leaf = new(0.92f, 0.74f, 0.80f);
                trunkR = F(rng, 0.17f, 0.22f); trunkTop = F(rng, 1.6f, 2.0f);
                Trunk(trunkR, trunkR * 0.8f, trunkTop, 0);
                var crownY = trunkTop + F(rng, 1.6f, 2.0f);
                for (var i = 0; i < 3; i++)
                {
                    var a = i * MathF.Tau / 3 + F(rng, -0.3f, 0.3f);
                    s.Add(Limb(new(0, trunkTop - 0.2f, 0), new Vector3(MathF.Cos(a) * 1.5f, crownY - 0.3f, MathF.Sin(a) * 1.5f), trunkR * 0.5f, bark));
                }
                Crown(new(0, crownY, 0), Jit(rng, new Vector3(2.8f, 1.5f, 2.8f), 0.2f), 14, 0.7f, 1.1f, 0.8f);
                break;
            }
        }

        Vector3 lo = new(float.MaxValue), hi = new(float.MinValue);
        foreach (var p in s.Adds) if (p.Emissive > 0) { lo = Vector3.Min(lo, p.AabbMin); hi = Vector3.Max(hi, p.AabbMax); }
        return new Variant { Species = species, Scene = s, TrunkR = trunkR, TrunkTop = trunkTop, CanopyC = (lo + hi) * 0.5f, CanopyR = (hi - lo) * 0.5f * 0.85f };
    }

    // ─── 잎 표면: 덩어리 곡면에 작은 잎 뭉치 요철 (3차원 값 노이즈) ─────────────────────

    static float Hash(int x, int y, int z)
    {
        var h = (uint)(x * 374761393 + y * 668265263 + z * 1274126177);
        h = (h ^ (h >> 13)) * 1274126177u;
        return (h ^ (h >> 16)) / (float)uint.MaxValue;
    }

    static float Noise3(Vector3 p)
    {
        int x = (int)MathF.Floor(p.X), y = (int)MathF.Floor(p.Y), z = (int)MathF.Floor(p.Z);
        var f = p - new Vector3(x, y, z); f = f * f * (new Vector3(3) - 2 * f);
        float L(float a, float b, float t) => a + (b - a) * t;
        return L(L(L(Hash(x, y, z), Hash(x + 1, y, z), f.X), L(Hash(x, y + 1, z), Hash(x + 1, y + 1, z), f.X), f.Y),
                 L(L(Hash(x, y, z + 1), Hash(x + 1, y, z + 1), f.X), L(Hash(x, y + 1, z + 1), Hash(x + 1, y + 1, z + 1), f.X), f.Y), f.Z);
    }

    /// <summary>변형 하나의 거리장 + 색 (a = 잎 정도). 잎 쪽에만 요철을 더한다</summary>
    public static float TreeSdf(Variant v, Vector3 p, out Vector4 col)
    {
        var d = v.Scene.EvalColor(p, out var c, out var leafy);
        if (leafy > 0.5f) d += (Noise3(p * 1.7f) - 0.5f) * 0.32f + (Noise3(p * 4.3f + new Vector3(9)) - 0.5f) * 0.12f;
        col = new Vector4(c, leafy > 0.5f ? 1 : 0);
        return d;
    }

    /// <summary>메시 짓기 + 빛 굽기 (변형 하나 안에서: 잎 덩어리의 자기 그늘·해 그림자·하늘 보임)</summary>
    public void BuildMeshes()
    {
        Parallel.ForEach(Variants, v =>
        {
            v.Near = MeshVariant(v, 0.075f);
            v.Far = MeshVariant(v, 0.32f);
        });
    }

    static StaticMeshData MeshVariant(Variant v, float step)
    {
        var lo = v.Scene.Min - new Vector3(0.5f); var hi = v.Scene.Max + new Vector3(0.5f);
        lo.Y = MathF.Max(lo.Y, -0.3f);
        var n = ((int)MathF.Ceiling((hi.X - lo.X) / step), (int)MathF.Ceiling((hi.Y - lo.Y) / step), (int)MathF.Ceiling((hi.Z - lo.Z) / step));
        float Sdf(Vector3 p) => TreeSdf(v, p, out _);
        var m = SurfaceNets.Mesh(Sdf, lo, step, n, p =>
        {
            var e = step * 0.5f;
            var g = new Vector3(Sdf(p + new Vector3(e, 0, 0)) - Sdf(p - new Vector3(e, 0, 0)), Sdf(p + new Vector3(0, e, 0)) - Sdf(p - new Vector3(0, e, 0)), Sdf(p + new Vector3(0, 0, e)) - Sdf(p - new Vector3(0, 0, e)));
            return g.LengthSquared() > 1e-20f ? Vector3.Normalize(g) : Vector3.UnitY;
        }, p => { TreeSdf(v, p, out var c); return c; }, parallel: false);
        // 빛 굽기: 땅 위에 혼자 선 나무로 본다 (땅이 아래 반구를 가린다)
        float Occ(Vector3 p) => MathF.Min(Sdf(p), p.Y + 0.05f);
        for (var i = 0; i < m.VertexCount; i++)
        {
            var p = m.Pos[i]; var nn = m.Nrm[i];
            var occ = 0f; var w = 1f;
            foreach (var h in new[] { 0.1f, 0.3f, 0.7f, 1.4f }) { occ += w * MathF.Max(0, h - Occ(p + nn * h)) / h; w *= 0.6f; }
            var ao = Math.Clamp(1 - occ * 0.45f, 0.2f, 1);
            float Ray(Vector3 dir, int steps, float k)
            {
                var res = 1f; var t = 0.08f;
                for (var j = 0; j < steps && t < 16; j++)
                {
                    var hh = Occ(p + nn * 0.05f + dir * t);
                    if (hh < 0.002f) return 0;
                    res = MathF.Min(res, k * hh / t);
                    t += Math.Clamp(hh, 0.05f, 1.5f);
                }
                return Math.Clamp(res, 0, 1);
            }
            var sun = Ray(WorldScene.SunDir, 28, 6);
            var sky = 0.5f * Ray(Vector3.UnitY, 16, 3) + 0.5f * Ray(Vector3.Normalize(nn + Vector3.UnitY), 16, 3);
            m.Light.Add(new Vector4(ao, sun, sky, 0));
        }
        return m;
    }

    // ─── 자리 ───────────────────────────────────────────────────────────

    void Place(Terrain t, int seed)
    {
        var forest = new Noise2(seed + 101); var mix = new Noise2(seed + 202);
        bool Blocked(float x, float z, float margin)
        {
            if (MathF.Abs(z - Terrain.RoadZ) < Terrain.RoadHalf + 1.2f + margin) return true;
            foreach (var hx in Terrain.HouseX)
                if (MathF.Abs(x - hx) < 7.5f + margin && z > -2.5f - margin && z < 10f + margin) return true;   // 집과 앞뒤 마당 (뒷마당 나무는 따로 심는다)
            var L = WorldLayout.Default;
            if (z < L.FieldZ.Y + 1 + margin && z > L.FieldZ.X - 1 - margin && MathF.Abs(x) < L.FieldXHalf + margin) return true;       // 밭 안
            return t.Gradient(x, z).Length() > 0.75f;
        }
        void Add(string sp, float x, float z, Random rng, float scale)
        {
            Trees.Add(new Tree(VariantOf(sp, rng.Next(PerSpecies)), new Vector3(x, t.Height(x, z) - 0.05f, z), scale));
        }

        var r0 = new Random(seed);
        // 집마다 뒷마당 나무 두 그루 · 앞마당 가장자리에 작은 벚나무
        string[] yard = ["cherry", "birch", "pine"];
        for (var i = 0; i < Terrain.HouseX.Length; i++)
        {
            var hx = Terrain.HouseX[i];
            Add(yard[i % 3], hx - 3.2f + F(r0, -0.6f, 0.6f), 11.8f + F(r0, -0.5f, 0.8f), r0, F(r0, 0.85f, 1.05f));
            Add(yard[(i + 1) % 3], hx + 3.6f + F(r0, -0.6f, 0.6f), 13.2f + F(r0, -0.5f, 0.8f), r0, F(r0, 0.85f, 1.05f));
            Add("cherry", hx - 5.6f, -4.2f, r0, 0.7f);
        }
        // 길가 가로수: 집 사이 (집 앞쪽 보도 가장자리) 와 길 건너 (밭 쪽)
        for (var x = -108f; x <= 108f; x += 18)
            if (MathF.Abs(x) > 1) Add("cherry", x + 9, Terrain.RoadZ + Terrain.RoadHalf + 1.9f, r0, F(r0, 0.8f, 0.95f));
        for (var x = -99f; x <= 99f; x += 11)
            Add(x % 2 == 0 ? "birch" : "oak", x + F(r0, -1, 1), Terrain.RoadZ - Terrain.RoadHalf - 2.2f, r0, F(r0, 0.75f, 0.95f));
        // 밭 가장자리 산울타리 (남쪽 끝)
        for (var x = -70f; x <= 70f; x += 6.5f)
            if (r0.NextDouble() < 0.7) Add("oak", x + F(r0, -1.5f, 1.5f), -99.5f + F(r0, -1, 1), r0, F(r0, 0.8f, 1.1f));

        // 넓은 땅: 6 m 칸마다 하나의 후보, 지역(평지 ↔ 야생)과 숲 노이즈로 확률
        const float grid = 6, half = 430;
        for (var gx = -half; gx < half; gx += grid)
            for (var gz = -half; gz < half; gz += grid)
            {
                var rng = new Random(HashInt((int)(gx / grid), (int)(gz / grid), seed));
                var x = gx + F(rng, 0.5f, grid - 0.5f); var z = gz + F(rng, 0.5f, grid - 0.5f);
                if (Blocked(x, z, 1.5f)) continue;
                var r = MathF.Sqrt(x * x + (z + 4) * (z + 4));
                if (r < 18) continue;
                var wild = Terrain.Wildness(x, z);
                var f = Smooth(-0.05f, 0.3f, forest.Fbm(x / 110f, z / 110f, 3));
                var prob = (1 - wild) * 0.025f + wild * (0.02f + 0.62f * f);
                if (rng.NextDouble() > prob) continue;
                var h = t.Height(x, z);
                var m = mix.Fbm(x / 60f, z / 60f, 2);
                string sp = wild < 0.5f
                    ? (m > 0.05f ? "oak" : m > -0.15f ? "cherry" : "birch")
                    : h > 16 ? (m > 0.1f ? "pine" : "spruce")
                    : m > 0.15f ? "oak" : m > -0.05f ? "birch" : m > -0.25f ? "pine" : "spruce";
                Add(sp, x, z, rng, F(rng, 0.75f, 1.2f));
            }
    }

    static int HashInt(int a, int b, int c) => (int)((uint)(a * 73856093) ^ (uint)(b * 19349663) ^ (uint)(c * 83492791)) & 0x7fffffff;
    static float Smooth(float a, float b, float x) { var t = Math.Clamp((x - a) / (b - a), 0, 1); return t * t * (3 - 2 * t); }

    // ─── 거리장 (세계에서 쓰는 대리 형태) ───────────────────────────────────

    static float Capsule(Vector3 p, float r, float top)
    {
        var y = Math.Clamp(p.Y, 0, top);
        return (p - new Vector3(0, y, 0)).Length() - r;
    }

    static float Ellipsoid(Vector3 p, Vector3 r)
    {
        var k0 = (p / r).Length(); var k1 = (p / (r * r)).Length();
        return k1 < 1e-6f ? -MathF.Min(r.X, MathF.Min(r.Y, r.Z)) : k0 * (k0 - 1) / k1;
    }

    /// <param name="optical">true: 수관도 (빛 가림), false: 줄기만 (충돌)</param>
    public float Sdf(Vector3 p, bool optical)
    {
        int cx = (int)MathF.Floor(p.X / Cell), cz = (int)MathF.Floor(p.Z / Cell);
        // 3×3 칸 밖의 나무는 칸 경계까지의 거리 − Reach 보다 가깝지 않다
        var fx = p.X - cx * Cell; var fz = p.Z - cz * Cell;
        var d = MathF.Min(MathF.Min(fx, Cell - fx), MathF.Min(fz, Cell - fz)) + Cell - Reach;
        for (var a = -1; a <= 1; a++)
            for (var b = -1; b <= 1; b++)
            {
                if (!_cells.TryGetValue((cx + a, cz + b), out var list)) continue;
                foreach (var i in list)
                {
                    var tr = Trees[i]; var v = Variants[tr.Variant];
                    var l = (p - tr.Pos) / tr.Scale;
                    var di = Capsule(l, v.TrunkR, optical ? v.TrunkTop : v.TrunkTop + 1);
                    if (optical) di = MathF.Min(di, Ellipsoid(l - v.CanopyC, v.CanopyR));
                    d = MathF.Min(d, di * tr.Scale);
                }
            }
        return d;
    }

    /// <summary>수관 아래 정도 (0 = 하늘이 트임, 1 = 수관 한가운데 아래): 풀의 하늘빛</summary>
    public float Cover(Vector3 p)
    {
        int cx = (int)MathF.Floor(p.X / Cell), cz = (int)MathF.Floor(p.Z / Cell);
        var c = 0f;
        for (var a = -1; a <= 1; a++)
            for (var b = -1; b <= 1; b++)
            {
                if (!_cells.TryGetValue((cx + a, cz + b), out var list)) continue;
                foreach (var i in list)
                {
                    var tr = Trees[i]; var v = Variants[tr.Variant];
                    var l = (p - tr.Pos) / tr.Scale - v.CanopyC;
                    var q = new Vector2(l.X / v.CanopyR.X, l.Z / v.CanopyR.Z).Length();
                    c = MathF.Max(c, 1 - Smooth(0.5f, 1.15f, q));
                }
            }
        return c;
    }
}
