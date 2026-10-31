using System.Numerics;

namespace DollyForge;

/// <summary>
/// 거리장(SDF) 기본형: 둥근 상자와 둥근 원기둥. 축(ex·ey·ez)으로 돌릴 수 있고, 평면으로 잘라 박공벽을 만든다.
/// 모든 모서리는 반지름 R 로 둥글다 (0.6 포지 규격, 0.10 곡선미 회복).
/// </summary>
sealed class SdfPrim
{
    public enum Kind { Box, Cylinder }
    public Kind Shape;
    public Vector3 Center, Half, Ex = Vector3.UnitX, Ey = Vector3.UnitY, Ez = Vector3.UnitZ;
    public float R;
    public Vector3 Color;
    public float Emissive;                         // 1 = 스스로 빛나는 면 (전등 갓)
    public (Vector3 N, float C)[] Clip = [];       // 이 평면들의 바깥(dot(n,p) > c)은 잘라 낸다
    public Vector3 AabbMin, AabbMax;

    public static SdfPrim Box(Vector3 min, Vector3 max, float r, Vector3 color) =>
        new SdfPrim { Shape = Kind.Box, Center = (min + max) * 0.5f, Half = (max - min) * 0.5f, R = r, Color = color }.Fit();

    /// <summary>세로 원기둥: 바닥 중심 base, 반지름 rad, 높이 h</summary>
    public static SdfPrim Cyl(Vector3 baseCenter, float rad, float h, float r, Vector3 color) =>
        new SdfPrim { Shape = Kind.Cylinder, Center = baseCenter + new Vector3(0, h / 2, 0), Half = new Vector3(rad, h / 2, rad), R = r, Color = color }.Fit();

    /// <summary>축을 지정한 둥근 상자 (지붕판처럼 기울인 것)</summary>
    public static SdfPrim Oriented(Vector3 center, Vector3 ex, Vector3 ey, Vector3 ez, Vector3 half, float r, Vector3 color) =>
        new SdfPrim { Shape = Kind.Box, Center = center, Ex = ex, Ey = ey, Ez = ez, Half = half, R = r, Color = color }.Fit();

    public SdfPrim Fit()
    {
        var e = new Vector3(
            MathF.Abs(Ex.X) * Half.X + MathF.Abs(Ey.X) * Half.Y + MathF.Abs(Ez.X) * Half.Z,
            MathF.Abs(Ex.Y) * Half.X + MathF.Abs(Ey.Y) * Half.Y + MathF.Abs(Ez.Y) * Half.Z,
            MathF.Abs(Ex.Z) * Half.X + MathF.Abs(Ey.Z) * Half.Y + MathF.Abs(Ez.Z) * Half.Z);
        AabbMin = Center - e; AabbMax = Center + e;
        return this;
    }

    public float Eval(Vector3 p)
    {
        var d = p - Center;
        var q = new Vector3(Vector3.Dot(d, Ex), Vector3.Dot(d, Ey), Vector3.Dot(d, Ez));
        float dist;
        var r = MathF.Min(R, MathF.Min(Half.X, MathF.Min(Half.Y, Half.Z)) * 0.999f);
        if (Shape == Kind.Box)
        {
            var b = Vector3.Abs(q) - (Half - new Vector3(r));
            dist = Vector3.Max(b, Vector3.Zero).Length() + MathF.Min(MathF.Max(b.X, MathF.Max(b.Y, b.Z)), 0) - r;
        }
        else
        {
            var dx = new Vector2(q.X, q.Z).Length() - (Half.X - r);
            var dy = MathF.Abs(q.Y) - (Half.Y - r);
            dist = new Vector2(MathF.Max(dx, 0), MathF.Max(dy, 0)).Length() + MathF.Min(MathF.Max(dx, dy), 0) - r;
        }
        foreach (var (n, c) in Clip) dist = MathF.Max(dist, Vector3.Dot(n, p) - c);
        return dist;
    }

    public float AabbDistance(Vector3 p)
    {
        var o = Vector3.Max(Vector3.Max(AabbMin - p, p - AabbMax), Vector3.Zero);
        return o.Length();
    }
}

/// <summary>
/// 기본형들의 장면: 더하기는 부드러운 합집합(반지름 Blend, 접합부가 필렛으로 이어진다), 빼기(문·창 개구부)는 차집합.
/// 고체(Solids)는 빼기 뒤에 더한다: 가구·문짝·유리처럼 개구부에 깎이면 안 되는 것.
/// 멀리 있는 기본형은 경계 상자 거리로 건너뛴다.
/// </summary>
sealed class SdfScene
{
    public readonly List<SdfPrim> Adds = [], Subs = [], Solids = [];
    public float Blend = 0.02f;
    public Vector3 Min = new(float.MaxValue), Max = new(float.MinValue);

    public SdfScene Add(SdfPrim p) { Adds.Add(p); Min = Vector3.Min(Min, p.AabbMin); Max = Vector3.Max(Max, p.AabbMax); return this; }
    public SdfScene Sub(SdfPrim p) { Subs.Add(p); return this; }
    public SdfScene AddSolid(SdfPrim p) { Solids.Add(p); Min = Vector3.Min(Min, p.AabbMin); Max = Vector3.Max(Max, p.AabbMax); return this; }

    public float Eval(Vector3 p) => EvalColor(p, out _, out _);

    public float EvalColor(Vector3 p, out Vector3 color, out float emissive)
    {
        var d = 1e9f; color = new Vector3(0.5f); emissive = 0;
        var best = 1e9f;
        foreach (var s in Adds)
        {
            if (s.AabbDistance(p) - Blend > d) continue;
            var di = s.Eval(p);
            if (di < best) { best = di; color = s.Color; emissive = s.Emissive; }
            // 부드러운 합집합 (다항식 smin)
            var h = MathF.Max(Blend - MathF.Abs(d - di), 0) / Blend;
            d = MathF.Min(d, di) - h * h * Blend * 0.25f;
        }
        foreach (var s in Subs)
        {
            if (s.AabbDistance(p) > 0.3f) continue;
            var ds = s.Eval(p);
            var h = MathF.Max(Blend - MathF.Abs(d + ds), 0) / Blend;
            d = MathF.Max(d, -ds) + h * h * Blend * 0.25f;
        }
        foreach (var s in Solids)
        {
            if (s.AabbDistance(p) > d) continue;
            var di = s.Eval(p);
            if (di < d) { d = di; color = s.Color; emissive = s.Emissive; }
        }
        return d;
    }

    public Vector3 Normal(Vector3 p, float e = 0.004f)
    {
        var g = new Vector3(Eval(p + new Vector3(e, 0, 0)) - Eval(p - new Vector3(e, 0, 0)),
                            Eval(p + new Vector3(0, e, 0)) - Eval(p - new Vector3(0, e, 0)),
                            Eval(p + new Vector3(0, 0, e)) - Eval(p - new Vector3(0, 0, e)));
        return g.LengthSquared() > 1e-20f ? Vector3.Normalize(g) : Vector3.UnitY;
    }
}
