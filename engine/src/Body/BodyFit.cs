using System.Diagnostics;
using System.Numerics;
using System.Text.Json;

namespace DollyForge;

/// <summary>체형 배율 묶음 (이름 → 배율, 없으면 1). 레퍼런스 피팅 결과를 JSON 으로 둔다.</summary>
public sealed class BodyShape
{
    public Dictionary<string, float> Values { get; set; } = [];
    public float this[string key] => Values.TryGetValue(key, out var v) ? v : 1f;

    public static readonly string[] Keys =
    [
        "v0", "v1", "v2", "v3", "v4", "v5", "v6",                 // 세로 구간: 발목·정강이·허벅지·골반~허리·가슴·목·머리
        "tw0", "tw1", "tw2", "tw3", "tw4", "tw5", "tw6", "tw7",
        "tw8", "tw9", "tw10", "tw11", "tw12", "tw13", "tw14", "tw15", // 몸통 단면 너비
        "headW", "neckR", "shX", "delt", "armU", "armF", "armX", "hand",
        "hipX", "glute", "bust", "chest", "foot",
        "lr0", "lr1", "lr2", "lr3", "lr4", "lr5", "lr6", "lr7", "lr8", "lr9", "lr10", "lr11", "lr12",     // 다리 단면 너비 (발목 → 엉덩이)
        "lx0", "lx1", "lx2", "lx3", "lx4", "lx5", "lx6", "lx7", "lx8", "lx9", "lx10", "lx11", "lx12",     // 다리 축 x
    ];

    static string DefaultDir => Path.Combine(AppContext.BaseDirectory, "assets", "body");
    public static string PathFor(bool male, string? dir = null) => Path.Combine(dir ?? DefaultDir, male ? "male.json" : "female.json");

    public static BodyShape LoadFor(bool male, string? dir = null)
    {
        try { return JsonSerializer.Deserialize<BodyShape>(File.ReadAllText(PathFor(male, dir))) ?? new(); }
        catch { return new(); }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var sorted = new BodyShape { Values = Keys.ToDictionary(k => k, k => MathF.Round(this[k], 4)) };
        File.WriteAllText(path, JsonSerializer.Serialize(sorted, new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>
/// 레퍼런스 이미지(tools/bodyfit/ref/front.png·back.png)에 몸을 맞추는 피팅.
/// 이미지에서 읽은 실루엣 치수(정면 정사영 기준, 미터)와 SDF 몸의 같은 치수의 상대 오차를 줄인다.
/// (1+1)-진화 전략, 100회 = 한 주기. 최소 100회, 최대 10000회, 한 주기 동안 개선이 거의 없으면 멈춘다.
/// </summary>
public sealed partial class CyborgBody
{
    enum Kind { CoreWidth, ArmOuter, ArmWidth, LegOuter, LegInner, Crotch, Fingertip, NavelRatio, ShoulderWaist }

    // (종류, 높이 m, 목표 m) — 레퍼런스 픽셀을 키(여 1.766 m·남 1.876 m)로 환산. 앞·뒤 이미지 평균.
    static readonly (Kind K, float H, float Ref)[] FemaleRef =
    [
        (Kind.CoreWidth, 1.634f, 0.166f), (Kind.CoreWidth, 1.479f, 0.108f), (Kind.CoreWidth, 1.315f, 0.292f),
        (Kind.CoreWidth, 1.122f, 0.242f), (Kind.CoreWidth, 0.919f, 0.349f),
        (Kind.ArmOuter, 1.383f, 0.189f), (Kind.ArmOuter, 1.131f, 0.226f), (Kind.ArmOuter, 0.977f, 0.236f),
        (Kind.ArmWidth, 1.267f, 0.058f), (Kind.ArmWidth, 0.996f, 0.052f),
        // 하반신: 앞·뒤 이미지에서 2~4 cm 간격으로 읽은 다리 윤곽 (바깥·안쪽 가장자리, 두 다리 평균)
        (Kind.LegOuter, 0.822f, 0.166f),
        (Kind.LegInner, 0.822f, 0.008f),
        (Kind.LegOuter, 0.783f, 0.163f),
        (Kind.LegInner, 0.783f, 0.007f),
        (Kind.LegOuter, 0.745f, 0.157f),
        (Kind.LegInner, 0.745f, 0.007f),
        (Kind.LegOuter, 0.706f, 0.150f),
        (Kind.LegInner, 0.706f, 0.007f),
        (Kind.LegOuter, 0.648f, 0.138f),
        (Kind.LegInner, 0.648f, 0.008f),
        (Kind.LegOuter, 0.590f, 0.128f),
        (Kind.LegInner, 0.590f, 0.010f),
        (Kind.LegOuter, 0.551f, 0.121f),
        (Kind.LegInner, 0.551f, 0.005f),
        (Kind.LegOuter, 0.493f, 0.116f),
        (Kind.LegInner, 0.493f, 0.010f),
        (Kind.LegOuter, 0.435f, 0.118f),
        (Kind.LegInner, 0.435f, 0.019f),
        (Kind.LegOuter, 0.377f, 0.114f),
        (Kind.LegInner, 0.377f, 0.027f),
        (Kind.LegOuter, 0.319f, 0.106f),
        (Kind.LegInner, 0.319f, 0.029f),
        (Kind.LegOuter, 0.261f, 0.095f),
        (Kind.LegInner, 0.261f, 0.017f),
        (Kind.LegOuter, 0.203f, 0.082f),
        (Kind.LegInner, 0.203f, 0.010f),
        (Kind.LegOuter, 0.145f, 0.080f),
        (Kind.LegInner, 0.145f, 0.013f),
        (Kind.LegOuter, 0.745f, 0.166f),
        (Kind.LegInner, 0.745f, 0.008f),
        (Kind.LegOuter, 0.667f, 0.155f),
        (Kind.LegInner, 0.667f, 0.008f),
        (Kind.LegOuter, 0.551f, 0.119f),
        (Kind.LegInner, 0.551f, 0.008f),
        (Kind.LegOuter, 0.474f, 0.117f),
        (Kind.LegInner, 0.474f, 0.010f),
        (Kind.LegOuter, 0.396f, 0.115f),
        (Kind.LegInner, 0.396f, 0.008f),
        (Kind.LegOuter, 0.319f, 0.106f),
        (Kind.LegInner, 0.319f, 0.012f),
        (Kind.LegOuter, 0.242f, 0.092f),
        (Kind.LegInner, 0.242f, 0.015f),
        (Kind.LegOuter, 0.164f, 0.080f),
        (Kind.LegInner, 0.164f, 0.019f),
        (Kind.LegOuter, 0.087f, 0.073f),
        (Kind.LegInner, 0.087f, 0.021f),
        (Kind.CoreWidth, 0.977f, 0.338f),
        (Kind.CoreWidth, 0.899f, 0.364f),
        (Kind.CoreWidth, 0.996f, 0.335f),
        (Kind.Crotch, 0, 0.851f), (Kind.Fingertip, 0, 0.716f),
        // 황금비: 배꼽 높이 / 키 = 1/φ, 어깨 폭 / 허리 폭 = φ
        (Kind.NavelRatio, 0, 0.618f), (Kind.ShoulderWaist, 1.383f, 1.618f),
    ];

    static readonly (Kind K, float H, float Ref)[] MaleRef =
    [
        (Kind.CoreWidth, 1.755f, 0.172f), (Kind.CoreWidth, 1.591f, 0.148f), (Kind.CoreWidth, 1.345f, 0.400f),
        (Kind.CoreWidth, 1.139f, 0.308f), (Kind.CoreWidth, 0.955f, 0.357f),
        (Kind.ArmOuter, 1.427f, 0.267f), (Kind.ArmOuter, 1.098f, 0.295f),
        (Kind.ArmWidth, 1.283f, 0.103f), (Kind.ArmWidth, 1.078f, 0.113f),
        // 하반신: 앞·뒤 이미지에서 2~4 cm 간격으로 읽은 다리 윤곽 (바깥·안쪽 가장자리, 두 다리 평균)
        (Kind.LegOuter, 0.873f, 0.169f),
        (Kind.LegInner, 0.873f, 0.010f),
        (Kind.LegOuter, 0.790f, 0.166f),
        (Kind.LegInner, 0.790f, 0.011f),
        (Kind.LegOuter, 0.708f, 0.163f),
        (Kind.LegInner, 0.708f, 0.016f),
        (Kind.LegOuter, 0.626f, 0.159f),
        (Kind.LegInner, 0.626f, 0.024f),
        (Kind.LegOuter, 0.544f, 0.149f),
        (Kind.LegInner, 0.544f, 0.021f),
        (Kind.LegOuter, 0.462f, 0.151f),
        (Kind.LegInner, 0.462f, 0.024f),
        (Kind.LegOuter, 0.380f, 0.147f),
        (Kind.LegInner, 0.380f, 0.026f),
        (Kind.LegOuter, 0.298f, 0.130f),
        (Kind.LegInner, 0.298f, 0.021f),
        (Kind.LegOuter, 0.216f, 0.101f),
        (Kind.LegInner, 0.216f, 0.015f),
        (Kind.LegOuter, 0.154f, 0.092f),
        (Kind.LegInner, 0.154f, 0.015f),
        (Kind.LegOuter, 0.831f, 0.164f),
        (Kind.LegInner, 0.831f, 0.012f),
        (Kind.LegOuter, 0.749f, 0.159f),
        (Kind.LegInner, 0.749f, 0.016f),
        (Kind.LegOuter, 0.667f, 0.159f),
        (Kind.LegInner, 0.667f, 0.021f),
        (Kind.LegOuter, 0.585f, 0.144f),
        (Kind.LegInner, 0.585f, 0.026f),
        (Kind.LegOuter, 0.503f, 0.147f),
        (Kind.LegInner, 0.503f, 0.018f),
        (Kind.LegOuter, 0.421f, 0.141f),
        (Kind.LegInner, 0.421f, 0.015f),
        (Kind.LegOuter, 0.339f, 0.125f),
        (Kind.LegInner, 0.339f, 0.021f),
        (Kind.LegOuter, 0.257f, 0.113f),
        (Kind.LegInner, 0.257f, 0.029f),
        (Kind.LegOuter, 0.175f, 0.098f),
        (Kind.LegInner, 0.175f, 0.031f),
        (Kind.LegOuter, 0.092f, 0.098f),
        (Kind.LegInner, 0.092f, 0.029f),
        (Kind.CoreWidth, 0.996f, 0.339f),
        (Kind.CoreWidth, 0.914f, 0.339f),
        (Kind.Crotch, 0, 0.903f), (Kind.Fingertip, 0, 0.807f),
        (Kind.NavelRatio, 0, 0.618f), (Kind.ShoulderWaist, 1.427f, 1.618f),
    ];

    const float Dx = 0.0025f, Dz = 0.006f;

    // 정면 정사영 한 줄: x 마다 몸통/팔이 z 어딘가에 있는지
    static (bool[] Core, bool[] Arm, float X0) Row(Design d, float y, float xMin = -0.45f, float xMax = 0.45f)
    {
        var n = (int)((xMax - xMin) / Dx) + 1;
        var core = new bool[n]; var arm = new bool[n];
        for (var i = 0; i < n; i++)
        {
            var x = xMin + i * Dx;
            for (var z = -0.25f; z <= 0.30f; z += Dz)
            {
                var q = d.ToDesign(new Vector3(x, y, z));
                var (c, a) = d.Eval(q);
                if (c < 0) core[i] = true;
                if (a < 0) arm[i] = true;
                if (core[i] && arm[i]) break;
            }
        }
        return (core, arm, xMin);
    }

    static float Measure(Design d, Kind k, float h)
    {
        switch (k)
        {
            case Kind.CoreWidth:
            {
                var (c, _, x0) = Row(d, h);
                int lo = Array.IndexOf(c, true), hi = Array.LastIndexOf(c, true);
                return lo < 0 ? 0 : (hi - lo + 1) * Dx;
            }
            case Kind.ArmOuter or Kind.ArmWidth:
            {
                var (_, a, x0) = Row(d, h, 0, 0.45f);
                var hi = Array.LastIndexOf(a, true);
                if (hi < 0) return 0;
                if (k == Kind.ArmOuter) return x0 + hi * Dx;
                var lo = hi; while (lo > 0 && a[lo - 1]) lo--;
                return (hi - lo + 1) * Dx;
            }
            case Kind.LegOuter or Kind.LegInner:
            {
                var (c, _, x0) = Row(d, h, 0, 0.30f);
                int lo = Array.IndexOf(c, true), hi = Array.LastIndexOf(c, true);
                if (lo < 0) return 0;
                return k == Kind.LegOuter ? x0 + hi * Dx : x0 + lo * Dx;
            }
            case Kind.NavelRatio:
                return d.Fwd(d.Male ? 1.030f : 1.035f) * d.Scale / d.TargetHeight;
            case Kind.ShoulderWaist:
            {
                var sh = 2 * Measure(d, Kind.ArmOuter, h);
                var waist = Measure(d, Kind.CoreWidth, d.Male ? 1.139f : 1.122f);
                return waist > 0 ? sh / waist : 0;
            }
            case Kind.Crotch:
                // x = 0 세로줄을 아래에서 올라가며 처음 몸통이 나오는 높이 (1 cm 로 훑고 2 mm 로 다듬는다)
                return FirstHit(y =>
                {
                    for (var z = -0.25f; z <= 0.30f; z += Dz)
                        if (d.Eval(d.ToDesign(new Vector3(0, y, z))).Core < 0) return true;
                    return false;
                });
            default:
                // 손끝: 팔이 보이는 가장 낮은 높이
                return FirstHit(y => Array.IndexOf(Row(d, y, 0.05f, 0.45f).Arm, true) >= 0);
        }
    }

    static float FirstHit(Func<float, bool> hit)
    {
        for (var y = 0.40f; y < 1.30f; y += 0.01f)
        {
            if (!hit(y)) continue;
            var lo = y - 0.01f;
            for (var f = lo; f < y; f += 0.002f) if (hit(f)) return f;
            return y;
        }
        return 1.3f;
    }

    static float[] MeasureAll(Design d, (Kind K, float H, float Ref)[] refs)
    {
        var m = new float[refs.Length];
        Parallel.For(0, refs.Length, i => m[i] = Measure(d, refs[i].K, refs[i].H));
        return m;
    }

    static float Loss(float[] m, (Kind K, float H, float Ref)[] refs, float[] logp)
    {
        var e = 0f;
        for (var i = 0; i < m.Length; i++)
        {
            var r = refs[i].Ref;
            var w = refs[i].K switch
            {
                Kind.Crotch or Kind.Fingertip => 8f,
                Kind.NavelRatio => 10f,
                Kind.ShoulderWaist => 4f,
                _ => 1f,
            };   // 다리 길이·팔 길이는 비례의 뼈대
            var t = (m[i] - r) / MathF.Max(r, 0.05f);
            e += w * t * t;
        }
        var reg = 0f;
        foreach (var l in logp) reg += l * l;
        // 곡선미: 이웃한 단면 배율이 튀면 윤곽에 꺾임(경계)이 생긴다 → 2차 차분을 강하게 벌한다
        var smooth = 0f;
        foreach (var prefix in new[] { "lr", "lx", "tw" })
        {
            var idx = BodyShape.Keys.Select((k, i) => (k, i)).Where(t => t.k.StartsWith(prefix) && char.IsDigit(t.k[prefix.Length]))
                                    .Select(t => t.i).ToArray();
            for (var j = 1; j + 1 < idx.Length; j++)
            {
                var c = logp[idx[j - 1]] - 2 * logp[idx[j]] + logp[idx[j + 1]];
                smooth += c * c;
            }
        }
        return e + 0.01f * reg + 2f * smooth;
    }

    /// <summary>dolly-engine --fit female|male [--out 폴더] [--min 100] [--max 10000]</summary>
    public static void FitCli(string[] args)
    {
        string Arg(string name, string def) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : def; }
        if (args.Length > 2 && args[1] == "report")
        {
            var male = args[2] == "male";
            var refs = male ? MaleRef : FemaleRef;
            var shape = BodyShape.LoadFor(male, Arg("--out", Path.Combine(AppContext.BaseDirectory, "assets", "body")));
            var m = MeasureAll(MakeDesign(male, shape), refs);
            for (var i = 0; i < refs.Length; i++)
                Console.WriteLine($"   {refs[i].K,-13} h={refs[i].H:F3}  목표 {refs[i].Ref:F3}  결과 {m[i]:F3}  {(m[i] - refs[i].Ref) / MathF.Max(refs[i].Ref, 0.05f) * 100,6:F1}%");
            return;
        }
        if (args.Length > 2 && args[1] == "profile")
        {
            // 진단: 정면 실루엣 몸통 너비를 1 cm 간격으로
            var male = args[2] == "male";
            var d = MakeDesign(male, BodyShape.LoadFor(male, Arg("--out", Path.Combine(AppContext.BaseDirectory, "assets", "body"))));
            for (var y = 0.6f; y < 1.6f; y += 0.01f)
            {
                var (c, a, x0) = Row(d, y);
                int lo = Array.IndexOf(c, true), hi = Array.LastIndexOf(c, true);
                var segs = 0; for (var i = 1; i < c.Length; i++) if (c[i] && !c[i - 1]) segs++;
                float zf = -1, zb = 1;
                for (var z = -0.3f; z <= 0.3f; z += 0.001f)
                    if (d.Eval(d.ToDesign(new Vector3(0, y, z))).Core < 0) { zb = MathF.Min(zb, z); zf = MathF.Max(zf, z); }
                Console.WriteLine($"{y:F2} core {(lo < 0 ? 0 : (hi - lo + 1) * Dx):F3} segs {segs + (c[0] ? 1 : 0)}  front {zf:F3} back {zb:F3}");
            }
            return;
        }
        if (args.Length > 1 && args[1] == "mh") { MhFitCli(args); return; }
        var who = args.Length > 1 ? args[1] : "female";
        var outDir = Arg("--out", Path.Combine(AppContext.BaseDirectory, "assets", "body"));
        var minIt = int.Parse(Arg("--min", "100"));
        var maxIt = int.Parse(Arg("--max", "10000"));
        foreach (var g in who == "both" ? new[] { "female", "male" } : [who])
            Fit(g == "male", outDir, minIt, maxIt);
    }

    static void Fit(bool male, string outDir, int minIt, int maxIt, int cycle = 100)
    {
        var refs = male ? MaleRef : FemaleRef;
        var keys = BodyShape.Keys;
        var start = BodyShape.LoadFor(male, outDir);
        var x = keys.Select(k => MathF.Log(start[k])).ToArray();
        var rng = new Random(male ? 2 : 1);

        float Evaluate(float[] lx, out float[] meas)
        {
            var shape = new BodyShape { Values = keys.Select((k, i) => (k, MathF.Exp(lx[i]))).ToDictionary(t => t.k, t => t.Item2) };
            meas = MeasureAll(MakeDesign(male, shape), refs);
            return Loss(meas, refs, lx);
        }

        var best = Evaluate(x, out var bestM);
        var first = best;
        var sigma = 0.06f; var succ = 0; var tries = 0; var restarts = 0;
        var sw = Stopwatch.StartNew();
        var name = male ? "male" : "female";
        Console.WriteLine($"[fit] {name}: 시작 손실 {best:F4}  (항목 {refs.Length}개, 변수 {keys.Length}개)");
        var cycleStart = best;

        for (var it = 1; it <= maxIt; it++)
        {
            var cand = (float[])x.Clone();
            var any = false;
            for (var i = 0; i < cand.Length; i++)
            {
                if (rng.NextSingle() > 0.25f) continue;
                any = true;
                cand[i] = Math.Clamp(cand[i] + sigma * Gauss(rng), MathF.Log(0.6f), MathF.Log(1.7f));
            }
            if (!any) { var i = rng.Next(cand.Length); cand[i] = Math.Clamp(cand[i] + sigma * Gauss(rng), MathF.Log(0.6f), MathF.Log(1.7f)); }

            var l = Evaluate(cand, out var m);
            tries++;
            if (l < best) { best = l; x = cand; bestM = m; succ++; }
            if (tries == 20)
            {
                sigma = Math.Clamp(succ / 20f > 0.2f ? sigma * 1.25f : sigma * 0.85f, 0.003f, 0.2f);
                succ = 0; tries = 0;
            }

            if (it % cycle == 0)
            {
                var shape = new BodyShape { Values = keys.Select((k, i) => (k, MathF.Exp(x[i]))).ToDictionary(t => t.k, t => t.Item2) };
                shape.Save(BodyShape.PathFor(male, outDir));
                var gain = (cycleStart - best) / MathF.Max(cycleStart, 1e-6f);
                Console.WriteLine($"[fit] {name} 주기 {it / cycle,3} (반복 {it,5}): 손실 {best:F4}  주기 개선 {gain * 100:F2}%  σ={sigma:F3}  {sw.Elapsed:mm\\:ss}");
                if (it >= minIt && gain < 0.0005f && sigma <= 0.004f)
                {
                    // 국소 최솟값 탈출: 큰 걸음으로 세 번까지 다시 흔들어 보고, 그래도 안 나아지면 멈춘다
                    if (restarts < 3) { restarts++; sigma = 0.08f; Console.WriteLine($"[fit] {name}: 재시작 {restarts}/3"); }
                    else { Console.WriteLine($"[fit] {name}: 수렴 — 반복 {it}회에서 멈춤"); break; }
                }
                cycleStart = best;
            }
        }

        Console.WriteLine($"[fit] {name}: 손실 {first:F4} → {best:F4}");
        for (var i = 0; i < refs.Length; i++)
            Console.WriteLine($"   {refs[i].K,-10} h={refs[i].H:F3}  목표 {refs[i].Ref:F3}  결과 {bestM[i]:F3}");
    }

    static float Gauss(Random r)
    {
        var u1 = 1 - r.NextDouble(); var u2 = r.NextDouble();
        return (float)(Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2));
    }
}

/// <summary>MakeHuman 몸 피팅: 단면으로 실루엣을 재고 모프 슬라이더를 레퍼런스에 맞춘다.</summary>
public sealed partial class CyborgBody
{
    static string MhShapePath(bool male, string? dir = null)
        => Path.Combine(dir ?? Path.Combine(AppContext.BaseDirectory, "assets", "body"), male ? "mh-male.json" : "mh-female.json");

    static BodyShape LoadMhShape(bool male, string? dir = null)
    {
        try { return JsonSerializer.Deserialize<BodyShape>(File.ReadAllText(MhShapePath(male, dir))) ?? new(); }
        catch { return new(); }
    }

    // (이름, 기본, 최소, 최대)
    static List<(string K, float Def, float Lo, float Hi)> MhParams(MhAssets A, bool male)
    {
        var ps = new List<(string, float, float, float)>
        {
            // 체격은 실루엣만으로 안 정해진다: 레퍼런스 체격에 맞춘 범위 (남: 근육질·마름, 여: 보통 근육·풍만한 가슴)
            // 비율은 MakeHuman 의 '이상 비율' 쪽(황금비에 가까운 고전 비례)으로 둔다
            ("muscle", male ? 1f : 0.55f, male ? 0.92f : 0.4f, male ? 1f : 0.75f),
            ("weight", male ? 0.32f : 0.45f, male ? 0.2f : 0.3f, male ? 0.45f : 0.6f),
            ("height", 0.5f, 0, 1), ("proportions", 0.9f, 0.6f, 1),
            ("african", 0.333f, 0, 1), ("asian", 0.333f, 0, 1), ("caucasian", 0.334f, 0, 1),
            ("armAngle", male ? 11f : 7f, 0, 25), ("headW", 1f, 0.8f, 1.6f), ("headH", 1f, 0.85f, 1.4f), ("legAngle", 1.5f, -12f, 8f),
        };
        if (!male) ps.Add(("breast/cup", 0.75f, 0.35f, 1));
        // 레퍼런스 체격 고정값 (기본, 최소, 최대): 실루엣으로는 근육 결·배 탄력이 안 보이므로 사전값으로 둔다
        var prior = male
            ? new Dictionary<string, (float, float, float)>
            {
                ["stomach/stomach-tone"] = (0.9f, 0.6f, 1f),            // 복근이 드러나는 배
                ["torso/torso-muscle-pectoral"] = (0.6f, 0.3f, 1f),     // 대흉근
                ["torso/torso-muscle-dorsi"] = (0.6f, 0.3f, 1f),        // 광배근
                ["torso/torso-vshape"] = (0.5f, 0.2f, 1f),              // 역삼각형
                ["measure/measure-bust-circ"] = (0f, -0.4f, 0.25f),     // 가슴이 '젖가슴'처럼 되지 않게
                ["measure/measure-underbust-circ"] = (0f, -0.4f, 0.25f),
                ["bodyshapes/bodyshapes-elvs-man-invert-triangle"] = (0.4f, 0f, 1f),
                ["armslegs/upperarm-muscle"] = (0.5f, 0.2f, 1f), ["armslegs/upperleg-muscle"] = (0.5f, 0.2f, 1f),
                ["armslegs/lowerleg-muscle"] = (0.5f, 0.2f, 1f),
            }
            : new Dictionary<string, (float, float, float)>
            {
                ["stomach/stomach-tone"] = (0.5f, 0.2f, 1f),            // 평평한 배
                ["bodyshapes/bodyshapes-elvs-fem-full-hourglass"] = (0.4f, 0f, 1f),
            };
        foreach (var (k, neg, _) in A.Sliders)
        {
            if (k.Contains(male ? "-fem-" : "-man-")) continue;          // 반대 성별 체형 타깃 제외
            ps.Add(prior.TryGetValue(k, out var pr) ? (k, pr.Item1, pr.Item2, pr.Item3) : (k, 0, neg is null ? 0 : -1, 1));
        }
        return ps;
    }

    // 높이 y 단면: (x, 팔인가)
    static void Section(MhAssets A, MhBody b, float y, List<(float X, bool Arm)> outp)
    {
        outp.Clear();
        var v = b.V; var t = A.Tris;
        for (var k = 0; k < t.Length; k += 3)
            for (var e = 0; e < 3; e++)
            {
                int i0 = t[k + e], i1 = t[k + (e + 1) % 3];
                float y0 = v[i0].Y, y1 = v[i1].Y;
                if ((y0 - y) * (y1 - y) >= 0 || i0 > i1) continue;   // 모서리를 한 번만
                var x = v[i0].X + (v[i1].X - v[i0].X) * (y - y0) / (y1 - y0);
                var arm = b.Owner[i0] is Bone.UpperL or Bone.UpperR or Bone.ForeL or Bone.ForeR or Bone.HandL or Bone.HandR;
                outp.Add((x, arm));
            }
    }

    static float MhMeasure(MhAssets A, MhBody b, bool male, Kind k, float h, List<(float X, bool Arm)> sec)
    {
        switch (k)
        {
            case Kind.CoreWidth:
            {
                Section(A, b, h, sec);
                var xs = sec.Where(s => !s.Arm).Select(s => s.X).ToArray();
                return xs.Length == 0 ? 0 : xs.Max() - xs.Min();
            }
            case Kind.ArmOuter or Kind.ArmWidth:
            {
                Section(A, b, h, sec);
                var xs = sec.Where(s => s.Arm && s.X > 0).Select(s => s.X).ToArray();
                if (xs.Length == 0) return 0;
                return k == Kind.ArmOuter ? xs.Max() : xs.Max() - xs.Min();
            }
            case Kind.LegOuter or Kind.LegInner:
            {
                Section(A, b, h, sec);
                var xs = sec.Where(s => !s.Arm && s.X > 0).Select(s => s.X).ToArray();
                if (xs.Length == 0) return 0;
                return k == Kind.LegOuter ? xs.Max() : xs.Min();
            }
            case Kind.Crotch:
                return A.Used.Where(i => MathF.Abs(b.V[i].X) < 0.003f && b.V[i].Y > 0.3f).Min(i => b.V[i].Y);
            case Kind.Fingertip:
                return A.Used.Where(i => b.Owner[i] is Bone.HandL or Bone.HandR).Min(i => b.V[i].Y);
            case Kind.NavelRatio:
                return b.Navel / b.Height;
            case Kind.ShoulderWaist:
            {
                var sh = 2 * MhMeasure(A, b, male, Kind.ArmOuter, h, sec);
                var waist = MhMeasure(A, b, male, Kind.CoreWidth, male ? 1.139f : 1.122f, sec);
                return waist > 0 ? sh / waist : 0;
            }
        }
        return 0;
    }

    static void MhFitCli(string[] args)
    {
        string Arg(string name, string def) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : def; }
        var A = MhAssets.Shared ?? throw new InvalidOperationException("MakeHuman 자산 없음: tools/fetch-makehuman.sh");
        var who = args.Length > 2 ? args[2] : "female";
        var outDir = Arg("--out", Path.Combine(AppContext.BaseDirectory, "assets", "body"));
        var minIt = int.Parse(Arg("--min", "100")); var maxIt = int.Parse(Arg("--max", "10000"));
        foreach (var g in who == "both" ? new[] { "female", "male" } : [who])
        {
            if (args.Contains("--report")) { MhReport(A, g == "male", outDir); continue; }
            MhFit(A, g == "male", outDir, minIt, maxIt);
        }
    }

    static void MhReport(MhAssets A, bool male, string dir)
    {
        var refs = male ? MaleRef : FemaleRef;
        var b = MhBuild(A, male, LoadMhShape(male, dir), false);
        var sec = new List<(float, bool)>();
        for (var i = 0; i < refs.Length; i++)
        {
            var m = MhMeasure(A, b, male, refs[i].K, refs[i].H, sec);
            Console.WriteLine($"   {refs[i].K,-13} h={refs[i].H:F3}  목표 {refs[i].Ref:F3}  결과 {m:F3}  {(m - refs[i].Ref) / MathF.Max(refs[i].Ref, 0.05f) * 100,6:F1}%");
        }
    }

    static void MhFit(MhAssets A, bool male, string outDir, int minIt, int maxIt, int cycle = 100)
    {
        var refs = male ? MaleRef : FemaleRef;
        var ps = MhParams(A, male);
        var start = LoadMhShape(male, outDir);
        var x = ps.Select(p => Math.Clamp(GetP(start, p.K, p.Def), p.Lo, p.Hi)).ToArray();
        var rng = new Random(male ? 12 : 11);
        var name = male ? "male" : "female";

        float Evaluate(float[] cand, out float[] meas)
        {
            var shape = new BodyShape { Values = ps.Select((p, i) => (p.K, cand[i])).ToDictionary(t => t.K, t => t.Item2) };
            var body = MhBuild(A, male, shape, false);
            var m = new float[refs.Length];
            Parallel.For(0, refs.Length, () => new List<(float, bool)>(), (i, _, sec) => { m[i] = MhMeasure(A, body, male, refs[i].K, refs[i].H, sec); return sec; }, _ => { });
            meas = m;
            var e = 0f;
            for (var i = 0; i < m.Length; i++)
            {
                var r = refs[i].Ref;
                var w = refs[i].K switch { Kind.Crotch or Kind.Fingertip => 8f, Kind.NavelRatio => 10f, Kind.ShoulderWaist => 4f, _ => 1f };
                var t = (m[i] - r) / MathF.Max(r, 0.05f);
                e += w * t * t;
            }
            // 모프는 원래 자연스러운 형태: 기본값에서 멀어질수록 조금씩 벌한다 (극단 조합 방지)
            var reg = 0f;
            for (var i = 0; i < ps.Count; i++)
            {
                var span = ps[i].Hi - ps[i].Lo;
                var dz = (cand[i] - ps[i].Def) / span;
                reg += dz * dz;
            }
            return e + 0.05f * reg;
        }

        var best = Evaluate(x, out var bestM);
        var first = best; var cycleStart = best;
        float sigma = 0.08f; int succ = 0, tries = 0, restarts = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Console.WriteLine($"[mhfit] {name}: 시작 손실 {best:F4}  (항목 {refs.Length}개, 슬라이더 {ps.Count}개)");
        for (var it = 1; it <= maxIt; it++)
        {
            var cand = (float[])x.Clone();
            var any = false;
            for (var i = 0; i < cand.Length; i++)
            {
                if (rng.NextSingle() > 0.15f) continue;
                any = true;
                cand[i] = Math.Clamp(cand[i] + sigma * (ps[i].Hi - ps[i].Lo) * Gauss(rng), ps[i].Lo, ps[i].Hi);
            }
            if (!any) { var i = rng.Next(cand.Length); cand[i] = Math.Clamp(cand[i] + sigma * (ps[i].Hi - ps[i].Lo) * Gauss(rng), ps[i].Lo, ps[i].Hi); }
            var l = Evaluate(cand, out var m);
            tries++;
            if (l < best) { best = l; x = cand; bestM = m; succ++; }
            if (tries == 20) { sigma = Math.Clamp(succ / 20f > 0.2f ? sigma * 1.25f : sigma * 0.85f, 0.002f, 0.3f); succ = 0; tries = 0; }

            if (it % cycle == 0)
            {
                var shape = new BodyShape { Values = ps.Select((p, i) => (p.K, MathF.Round(x[i], 4))).ToDictionary(t => t.K, t => t.Item2) };
                Directory.CreateDirectory(outDir);
                File.WriteAllText(MhShapePath(male, outDir), JsonSerializer.Serialize(shape, new JsonSerializerOptions { WriteIndented = true }));
                var gain = (cycleStart - best) / MathF.Max(cycleStart, 1e-6f);
                Console.WriteLine($"[mhfit] {name} 주기 {it / cycle,3} (반복 {it,5}): 손실 {best:F4}  주기 개선 {gain * 100:F2}%  σ={sigma:F3}  {sw.Elapsed:mm\\:ss}");
                if (it >= minIt && gain < 0.0005f && sigma <= 0.003f)
                {
                    if (restarts < 3) { restarts++; sigma = 0.1f; Console.WriteLine($"[mhfit] {name}: 재시작 {restarts}/3"); }
                    else { Console.WriteLine($"[mhfit] {name}: 수렴 — 반복 {it}회에서 멈춤"); break; }
                }
                cycleStart = best;
            }
        }
        Console.WriteLine($"[mhfit] {name}: 손실 {first:F4} → {best:F4}");
        for (var i = 0; i < refs.Length; i++)
            Console.WriteLine($"   {refs[i].K,-13} h={refs[i].H:F3}  목표 {refs[i].Ref:F3}  결과 {bestM[i]:F3}  {(bestM[i] - refs[i].Ref) / MathF.Max(refs[i].Ref, 0.05f) * 100,6:F1}%");
    }
}
