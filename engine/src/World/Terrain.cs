using System.Numerics;

namespace DollyForge;

/// <summary>
/// 지형: 높이 함수에서 나온 거리장 (사계절 온대, 0.3).
///   외곽 주택가(길과 집터)는 평평하고, 시골은 완만하게 구르고, 멀리 야생은 언덕이 된다. 지역 사이는 노이즈 가중치로 서서히 바뀐다.
///   재질도 정점마다 섞인 색이다: 풀밭 · 흙 · 아스팔트 길 · 자갈길 · 콘크리트 진입로 · 밭 · 경사진 바위.
/// 집은 원점 근처(x −6…6, z 0…7.5)에 앞면을 길(z ≈ −10.5) 쪽으로 두고 선다.
/// </summary>
sealed class Terrain
{
    readonly Noise2 _roll = new(11), _hill = new(23), _detail = new(37), _patch = new(41);
    readonly ContentPack _pack;
    // 배치는 콘텐츠 팩 world.json (Rust 세계 서버가 같은 표로 구역을 판정한다)
    static WorldLayout L => WorldLayout.Default;
    public static float RoadZ => L.RoadZ;
    public static float RoadHalf => L.RoadHalf;
    public static float[] HouseX => L.HouseX;                        // 길가 집들의 가운데 x

    // 가까운 지역(±66 m)의 높이를 0.25 m 격자에 미리 계산해 둔다: 메시·충돌·빛 굽기가 같은 값을 빠르게 읽는다
    public const float CacheStep = 0.25f, CacheHalf = 66;
    readonly int _cn;
    readonly float[] _cache;
    /// <summary>높이 캐시 (GPU 풀이 같은 높이를 읽는다): CacheCount² 표본, (−CacheHalf, −CacheHalf) 부터 CacheStep 간격</summary>
    public ReadOnlySpan<float> HeightCache => _cache;
    public int CacheCount => _cn;

    public Terrain(ContentPack pack)
    {
        _pack = pack;
        _cn = (int)(2 * CacheHalf / CacheStep) + 1;
        _cache = new float[_cn * _cn];
        Parallel.For(0, _cn, k => { for (var i = 0; i < _cn; i++) _cache[k * _cn + i] = HeightRaw(-CacheHalf + i * CacheStep, -CacheHalf + k * CacheStep); });
    }

    static float Smooth(float a, float b, float x) { var t = Math.Clamp((x - a) / (b - a), 0, 1); return t * t * (3 - 2 * t); }

    /// <summary>야생의 정도 (0 = 외곽·시골 평지, 1 = 야생): 나무 숲·풀 높이가 따른다</summary>
    public static float Wildness(float x, float z) => 1 - Flatness(x, z);

    /// <summary>집터·길처럼 평평해야 하는 정도 (1 = 완전히 평평)</summary>
    static float Flatness(float x, float z)
    {
        var c = L.SettledCenter; var r = L.SettledRadius;
        var e = MathF.Sqrt((x - c.X) * (x - c.X) / (r.X * r.X) + (z - c.Y) * (z - c.Y) / (r.Y * r.Y));
        return 1 - Smooth(L.SettledEdge.X, L.SettledEdge.Y, e);
    }

    public float Height(float x, float z)
    {
        var fx = (x + CacheHalf) / CacheStep; var fz = (z + CacheHalf) / CacheStep;
        if (fx < 0 || fz < 0 || fx >= _cn - 1 || fz >= _cn - 1) return HeightRaw(x, z);
        int i = (int)fx, k = (int)fz; float tx = fx - i, tz = fz - k;
        float H(int a, int b) => _cache[(k + b) * _cn + i + a];
        return (H(0, 0) + (H(1, 0) - H(0, 0)) * tx) + ((H(0, 1) + (H(1, 1) - H(0, 1)) * tx) - (H(0, 0) + (H(1, 0) - H(0, 0)) * tx)) * tz;
    }

    float HeightRaw(float x, float z)
    {
        var flat = Flatness(x, z);
        var r = MathF.Sqrt(x * x + (z + 4) * (z + 4));
        var rolling = _roll.Fbm(x / 140f, z / 140f, 4) * 4.5f;
        var hills = MathF.Max(0, _hill.Fbm(x / 420f, z / 420f, 5) + 0.15f) * 55f * Smooth(200, 420, r);
        var micro = _detail.Fbm(x / 9f, z / 9f, 3) * 0.12f;
        return (1 - flat) * (rolling + hills + micro) + flat * micro * 0.25f * (1 - HouseLot(x, z));
    }

    /// <summary>집 바닥 아래 (1 = 집 안): 집 바닥이 흙에 묻히지 않게</summary>
    static float HouseLot(float x, float z)
    {
        var best = 0f;
        foreach (var hx in HouseX)
        {
            var dx = MathF.Max(MathF.Abs(x - hx) - 6.5f, 0); var dz = MathF.Max(MathF.Max(-1 - z, z - 8.5f), 0);
            best = MathF.Max(best, 1 - Smooth(0, 2, MathF.Sqrt(dx * dx + dz * dz)));
        }
        return best;
    }

    public Vector2 Gradient(float x, float z, float e = 0.05f) =>
        new((Height(x + e, z) - Height(x - e, z)) / (2 * e), (Height(x, z + e) - Height(x, z - e)) / (2 * e));

    /// <summary>거리장: 높이 차를 기울기로 나눠 실제 거리에 가깝게 (충돌·그림자 모두 이 값)</summary>
    public float Sdf(Vector3 p)
    {
        var g = Gradient(p.X, p.Z, 0.25f);
        return (p.Y - Height(p.X, p.Z)) / MathF.Sqrt(1 + g.LengthSquared());
    }

    public Vector3 Normal(Vector3 p)
    {
        var g = Gradient(p.X, p.Z);
        return Vector3.Normalize(new Vector3(-g.X, 1, -g.Y));
    }

    /// <summary>풀색: 노이즈 얼룩으로 마른 풀·짙은 풀이 섞인다 (땅과 풀잎이 같은 색을 쓴다)</summary>
    public Vector3 GrassColor(float x, float z)
    {
        var patch = _patch.Fbm(x / 18f, z / 18f, 3);
        return Vector3.Lerp(new Vector3(0.30f, 0.43f, 0.19f), new Vector3(0.42f, 0.44f, 0.22f), Smooth(0.1f, 0.5f, patch));
    }

    /// <summary>재질 색 (rgb) + 풀의 정도 (a, 렌더가 풀 무늬를 더한다). 경계마다 부드럽게 섞는다</summary>
    public Vector4 Color(Vector3 p)
    {
        float x = p.X, z = p.Z;
        var soil = _pack.Color("soil", new(0.40f, 0.30f, 0.22f));
        var gravel = _pack.Color("gravel", new(0.52f, 0.50f, 0.48f)) * 0.78f;   // 젖은 듯 어두운 자갈 (밝은 자갈은 햇빛에 눈처럼 보인다)
        var concrete = _pack.Color("concrete", new(0.66f, 0.65f, 0.62f));
        var asphalt = new Vector3(0.17f, 0.17f, 0.18f);
        var rock = _pack.Color("granite", new(0.55f, 0.54f, 0.52f));
        // 풀밭: 노이즈 얼룩으로 마른 풀·짙은 풀이 섞인다
        var c = GrassColor(x, z);
        var grassAmt = 1f;
        void Mix(Vector3 col, float w, float keepGrass = 0) { c = Vector3.Lerp(c, col, w); grassAmt = MathF.Min(grassAmt, 1 - w * (1 - keepGrass)); }
        // 흙이 드러난 곳 (노이즈)
        Mix(soil, Smooth(0.35f, 0.6f, _patch.Fbm(x / 7f + 40, z / 7f, 3)) * 0.8f);
        // 시골의 밭 (길 건너): 이랑 줄무늬
        var fz = L.FieldZ;
        var field = Smooth(fz.Y, fz.Y - 4, z) * (1 - Smooth(fz.X + 4, fz.X, z)) * (1 - Smooth(L.FieldXHalf - 2, L.FieldXHalf + 4, MathF.Abs(x)));
        var furrow = 0.5f + 0.5f * MathF.Sin(x * MathF.Tau / 1.2f);
        Mix(Vector3.Lerp(soil * 0.8f, new Vector3(0.33f, 0.47f, 0.18f), furrow * Smooth(-40, -60, z)), field);
        // 가파른 비탈은 바위
        var n = Normal(p);
        Mix(rock, Smooth(0.82f, 0.68f, n.Y));
        // 길: 아스팔트 (가장자리는 자갈로 번진다)
        var road = MathF.Abs(z - RoadZ);
        Mix(gravel, 1 - Smooth(RoadHalf, RoadHalf + 0.6f, road));
        Mix(asphalt, 1 - Smooth(RoadHalf - 0.3f, RoadHalf + 0.05f, road));
        // 집마다 현관 자갈길과 차고 콘크리트 진입로
        foreach (var hx in HouseX)
        {
            var inZ = (1 - Smooth(-0.2f, 0.3f, z)) * Smooth(RoadZ + RoadHalf - 0.2f, RoadZ + RoadHalf + 0.3f, z);
            Mix(gravel, inZ * (1 - Smooth(0.55f, 0.85f, MathF.Abs(x - (hx - 1.45f)))));
            Mix(concrete, inZ * (1 - Smooth(1.1f, 1.3f, MathF.Abs(x - (hx + 4.25f)))));
        }
        return new Vector4(c, grassAmt);
    }
}
