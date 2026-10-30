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
        "hipX", "kneeX", "ankX", "thighTop", "thighMid", "shin", "calf", "ankR", "thighOut", "glute", "bust", "chest", "foot",
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
    enum Kind { CoreWidth, ArmOuter, ArmWidth, LegOuter, LegInner, Crotch, Fingertip }

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
            var w = refs[i].K is Kind.Crotch or Kind.Fingertip ? 8f : 1f;   // 다리 길이·팔 길이는 비례의 뼈대
            var t = (m[i] - r) / MathF.Max(r, 0.05f);
            e += w * t * t;
        }
        var reg = 0f;
        foreach (var l in logp) reg += l * l;
        return e + 0.01f * reg;
    }

    /// <summary>dolly-engine --fit female|male [--out 폴더] [--min 100] [--max 10000]</summary>
    public static void FitCli(string[] args)
    {
        string Arg(string name, string def) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : def; }
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
