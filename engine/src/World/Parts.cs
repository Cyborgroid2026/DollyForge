using System.Numerics;

namespace DollyForge;

/// <summary>조립체 안의 부품 하나 (세계 서버 world/src/world.rs Part 와 같다): 아이템과 조립체 기준 자세</summary>
sealed record PartDef(string Item, Vector3 P, Quaternion Q);

/// <summary>두 부품 사이의 결합 (method = joining.json 의 id)</summary>
sealed record JointDef(int A, int B, string Method);

/// <summary>조립체 (M3): 부품들과 결합. 들고 있는 것도, 놓인 물건도 이것이다</summary>
sealed class AssemblyDef
{
    public PartDef[] Parts = [];
    public JointDef[] Joints = [];
    /// <summary>같은 모양이면 같은 열쇠 (메시를 한 번만 짓는다)</summary>
    public string Key = "";

    public static AssemblyDef Parse(System.Text.Json.JsonElement e)
    {
        static Vector3 V3(System.Text.Json.JsonElement a) => new(a[0].GetSingle(), a[1].GetSingle(), a[2].GetSingle());
        var a = new AssemblyDef
        {
            Parts = e.GetProperty("parts").EnumerateArray().Select(p =>
            {
                var q = p.GetProperty("q");
                return new PartDef(p.GetProperty("item").GetString()!, V3(p.GetProperty("p")), Quaternion.Normalize(new(q[0].GetSingle(), q[1].GetSingle(), q[2].GetSingle(), q[3].GetSingle())));
            }).ToArray(),
            Joints = e.TryGetProperty("joints", out var js)
                ? js.EnumerateArray().Select(j => new JointDef(j.GetProperty("a").GetInt32(), j.GetProperty("b").GetInt32(), j.GetProperty("method").GetString()!)).ToArray()
                : [],
        };
        a.Key = string.Join(";", a.Parts.Select(p => FormattableString.Invariant($"{p.Item}@{p.P.X:F4},{p.P.Y:F4},{p.P.Z:F4}/{p.Q.X:F3},{p.Q.Y:F3},{p.Q.Z:F3},{p.Q.W:F3}")))
              + "|" + string.Join(",", a.Joints.Select(j => $"{j.A}-{j.B}:{j.Method}"));
        return a;
    }
}

/// <summary>
/// 규격 부품의 모양 (M3). 좌표는 아이템 기준: 상자는 x = mm[0], z = mm[1], y = mm[2] 이고 경계 상자 가운데가 원점, 원기둥은 y 축.
/// 세계 서버의 충돌체(world.rs Geo)와 같은 치수다. 가구는 집 설계도와 같은 다리·상판 모양(HouseModel.ItemPrims)을 쓴다.
/// </summary>
static class Parts
{
    /// <summary>부품의 경계 상자 반 치수 (서버 Geo.half 와 같다)</summary>
    public static Vector3 Half(ContentPack pack, string item)
    {
        if (!pack.Items.TryGetValue(item, out var it) || it.Mm.Length == 0) return new(0.125f);
        var m = it.Mm.Select(x => x / 1000f).ToArray();
        return it.Shape switch
        {
            "box" when m.Length == 3 => new(m[0] / 2, m[2] / 2, m[1] / 2),
            "cyl" when m.Length == 2 => new(m[0] / 2, m[1] / 2, m[0] / 2),
            "tube" when m.Length == 3 => new(m[0] / 2, m[2] / 2, m[0] / 2),
            "sphere" => new(m[0] / 2),
            _ => new(0.125f),
        };
    }

    /// <summary>
    /// 놓일 때의 기본 자세 (서버 world.rs rest_rotation 과 같은 생각): 가구는 서 있고, 판·봉·각재·조립체는 가장 낮게 눕는다
    /// </summary>
    public static Quaternion Rest(ContentPack pack, AssemblyDef a)
    {
        if (a.Parts.Length == 1 && pack.Items.TryGetValue(a.Parts[0].Item, out var it) && it.Category is "home" or "workshop") return Quaternion.Identity;
        // 세 가지 눕힘 중 높이가 가장 낮은 것 (같으면 그대로)
        Quaternion[] options = [Quaternion.Identity, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2), Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2)];
        float Height(Quaternion q)
        {
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (var p in a.Parts)
            {
                var m = Matrix4x4.CreateFromQuaternion(p.Q) * Matrix4x4.CreateTranslation(p.P) * Matrix4x4.CreateFromQuaternion(q);
                var h = Half(pack, p.Item);
                var e = MathF.Abs(m.M12) * h.X + MathF.Abs(m.M22) * h.Y + MathF.Abs(m.M32) * h.Z;
                lo = MathF.Min(lo, m.M42 - e); hi = MathF.Max(hi, m.M42 + e);
            }
            return hi - lo;
        }
        return options.MinBy(q => Height(q) + (q == Quaternion.Identity ? -1e-4f : 0));
    }

    /// <summary>가장 가까운 90° 단위 자세 (24가지): 회전 행렬의 각 열을 가장 가까운 축으로 (서버 snap_rotation 과 같다)</summary>
    public static Quaternion Snap90(Quaternion q)
    {
        var m = Matrix4x4.CreateFromQuaternion(q);
        Vector3[] cols = [new(m.M11, m.M12, m.M13), new(m.M21, m.M22, m.M23), new(m.M31, m.M32, m.M33)];
        var used = new bool[3]; var outv = new Vector3[3];
        static float Comp(Vector3 v, int k) => k == 0 ? v.X : k == 1 ? v.Y : v.Z;
        foreach (var c in Enumerable.Range(0, 3).OrderByDescending(c => MathF.Max(MathF.Abs(cols[c].X), MathF.Max(MathF.Abs(cols[c].Y), MathF.Abs(cols[c].Z)))))
        {
            var best = Enumerable.Range(0, 3).Where(k => !used[k]).MaxBy(k => MathF.Abs(Comp(cols[c], k)));
            used[best] = true;
            var s = MathF.Sign(Comp(cols[c], best)); if (s == 0) s = 1;
            outv[c] = best == 0 ? new(s, 0, 0) : best == 1 ? new(0, s, 0) : new(0, 0, s);
        }
        if (Vector3.Dot(Vector3.Cross(outv[0], outv[1]), outv[2]) < 0) outv[2] = -outv[2];
        var r = new Matrix4x4(outv[0].X, outv[0].Y, outv[0].Z, 0, outv[1].X, outv[1].Y, outv[1].Z, 0, outv[2].X, outv[2].Y, outv[2].Z, 0, 0, 0, 0, 1);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(r));
    }

    /// <summary>둥근 모서리 반지름 (충돌·시선 판정용 근사)</summary>
    public static float Round(ContentPack pack, string item)
    {
        var h = Half(pack, item);
        if (!pack.Items.TryGetValue(item, out var it)) return 0.01f;
        return it.Shape == "box" ? MathF.Min(it.Edge / 1000f, MathF.Min(h.X, MathF.Min(h.Y, h.Z)) * 0.9f) : MathF.Min(h.X, h.Z) * 0.95f;
    }

    /// <summary>부품 하나의 거리장 장면 (아이템 기준 좌표)</summary>
    public static SdfScene Scene(ContentPack pack, string item)
    {
        var sc = new SdfScene { Blend = 0.004f };
        if (!pack.Items.TryGetValue(item, out var it)) { sc.Add(SdfPrim.Box(new(-0.125f), new(0.125f), 0.01f, new(0.5f))); return sc; }
        var col = it.Id == "soil_sack" ? new Vector3(0.28f, 0.22f, 0.14f) : pack.Color(it.Mat, new(0.5f, 0.45f, 0.4f));   // 마대 포대 (속은 흙)
        var half = Half(pack, item);
        if (it.Shape == "sphere") { sc.Add(SdfPrim.Box(-half, half, half.X, col)); return sc; }
        if (it.Shape == "tube")
        {
            var ri = it.Mm[1] / 2000f;
            sc.Add(new SdfPrim { Shape = SdfPrim.Kind.Cylinder, Center = Vector3.Zero, Half = half, R = MathF.Min(0.002f, (half.X - ri) * 0.4f), Color = col }.Fit());
            sc.Sub(new SdfPrim { Shape = SdfPrim.Kind.Cylinder, Center = Vector3.Zero, Half = new(ri, half.Y + 0.01f, ri), R = 0.0005f, Color = col }.Fit());
            return sc;
        }
        var prims = it.Id == "soil_sack" ? null : HouseModel.ItemPrims(pack, it, 2.6f, out _, out _, out _);
        if (prims is null) { sc.Add(SdfPrim.Box(-half, half, Round(pack, item), col)); return sc; }
        // 바닥 가운데 원점 → 경계 상자 가운데 원점
        foreach (var p in prims) { p.Center -= new Vector3(0, half.Y, 0); sc.Add(p.Fit()); }
        if (it.Id == "soil_sack") sc.Blend = 0.02f;
        return sc;
    }

    static float Thinnest(SdfScene s) => s.Adds.Min(p => MathF.Min(p.Half.X, MathF.Min(p.Half.Y, p.Half.Z)) * 2);
    static float Area(SdfScene s) => s.Adds.Sum(p => 8 * (p.Half.X * p.Half.Y + p.Half.Y * p.Half.Z + p.Half.Z * p.Half.X));

    /// <summary>
    /// 조립체 거리장: 용접(fillet)으로 이어진 부품 무리는 필렛 반지름의 부드러운 합집합이라 접합부에 선이 없다.
    /// 무리끼리(볼트·나사, seam)는 작은 둘레 필렛으로 붙고, 맞닿은 곳에 머리카락 이음선을 칠한다 (분해할 수 있는 곳만 경계가 남는다, 0.10).
    /// </summary>
    sealed class Field
    {
        readonly (SdfScene Scene, Matrix4x4 Inv, Vector3 C, float Rad, int G)[] _parts;
        readonly float[] _metal;           // 금속 부품 = 1 (렌더가 주변을 비춘다)
        readonly float[] _groupK;
        readonly float _betweenK;
        readonly int _groups;
        public Vector3 Lo = new(float.MaxValue), Hi = new(float.MinValue);
        public float Thin = 1, AreaSum;

        public Field(ContentPack pack, AssemblyDef a)
        {
            var n = a.Parts.Length;
            var root = Enumerable.Range(0, n).ToArray();
            int Root(int i) { while (root[i] != i) i = root[i] = root[root[i]]; return i; }
            var weldK = 0.006f; var seamK = 0.0015f;
            foreach (var j in a.Joints)
            {
                var def = pack.Joins.GetValueOrDefault(j.Method);
                // 다항식 smin 은 k/4 만큼 부풀어 오른다: 필렛 반지름 r 이 보이려면 k ≈ 4r (접합부의 골이 둥글게 차오른다)
                if (def?.Render == "fillet") { root[Root(j.A)] = Root(j.B); weldK = def.FilletMm / 1000f * 4; }
                else if (def is not null) seamK = def.FilletMm / 1000f * 4;
            }
            var ids = Enumerable.Range(0, n).Select(Root).Distinct().ToList();
            _groups = ids.Count;
            _groupK = ids.Select(r => Enumerable.Range(0, n).Count(i => Root(i) == r) > 1 ? weldK : 0f).ToArray();
            _betweenK = a.Joints.Length > 0 ? seamK : 0;
            _parts = new (SdfScene, Matrix4x4, Vector3, float, int)[n];
            _metal = a.Parts.Select(p => pack.Items.TryGetValue(p.Item, out var it) && pack.Materials.TryGetValue(it.Mat, out var mt) && mt.Kind == "metal" ? 1f : 0f).ToArray();
            for (var i = 0; i < n; i++)
            {
                var p = a.Parts[i];
                var sc = Scene(pack, p.Item);
                var m = Matrix4x4.CreateFromQuaternion(p.Q) * Matrix4x4.CreateTranslation(p.P);
                Matrix4x4.Invert(m, out var inv);
                var c = Vector3.Transform((sc.Min + sc.Max) * 0.5f, m);
                var rad = ((sc.Max - sc.Min) * 0.5f).Length();
                _parts[i] = (sc, inv, c, rad, ids.IndexOf(Root(i)));
                Lo = Vector3.Min(Lo, c - new Vector3(rad)); Hi = Vector3.Max(Hi, c + new Vector3(rad));
                Thin = MathF.Min(Thin, Thinnest(sc)); AreaSum += Area(sc);
            }
        }

        static float SMin(float a, float b, float k)
        {
            if (k <= 0) return MathF.Min(a, b);
            var h = MathF.Max(k - MathF.Abs(a - b), 0) / k;
            return MathF.Min(a, b) - h * h * k * 0.25f;
        }

        public float Eval(Vector3 p, out Vector3 color, out float seam) => Eval(p, out color, out seam, out _);

        public float Eval(Vector3 p, out Vector3 color, out float seam, out float metal)
        {
            Span<float> gd = stackalloc float[_groups];
            gd.Fill(1e9f);
            color = new Vector3(0.5f); var best = 1e9f; metal = 0;
            for (var i = 0; i < _parts.Length; i++)
            {
                var (sc, inv, c, rad, g) = _parts[i];
                var bound = Vector3.Distance(p, c) - rad;
                if (bound > gd[g] + 0.02f && bound > best + 0.02f) continue;
                var d = sc.EvalColor(Vector3.Transform(p, inv), out var col, out _);
                if (d < best) { best = d; color = col; metal = _metal[i]; }
                gd[g] = SMin(gd[g], d, _groupK[g]);
            }
            float d1 = 1e9f, d2 = 1e9f, total = 1e9f;
            for (var g = 0; g < _groups; g++)
            {
                total = SMin(total, gd[g], _betweenK);
                if (gd[g] < d1) { d2 = d1; d1 = gd[g]; } else if (gd[g] < d2) d2 = gd[g];
            }
            seam = d2 - d1;
            return total;
        }

        public float Eval(Vector3 p) => Eval(p, out _, out _);

        public Vector3 Normal(Vector3 p, float e)
        {
            var g = new Vector3(Eval(p + new Vector3(e, 0, 0)) - Eval(p - new Vector3(e, 0, 0)),
                                Eval(p + new Vector3(0, e, 0)) - Eval(p - new Vector3(0, e, 0)),
                                Eval(p + new Vector3(0, 0, e)) - Eval(p - new Vector3(0, 0, e)));
            return g.LengthSquared() > 1e-20f ? Vector3.Normalize(g) : Vector3.UnitY;
        }
    }

    /// <summary>
    /// 조립체 메시 (조립체 기준 좌표). 간격은 가장 얇은 부분의 1/5 (1.5–12 mm), 겉넓이가 크면 정점 수를 맞추려고 넓힌다.
    /// 빛: x = 스스로 가린 그늘(AO), y·z·w = 1 (해·하늘·전등은 렌더가 물건 자리마다 곱한다). 색 a = 금속(1)
    /// </summary>
    public static StaticMeshData Mesh(ContentPack pack, AssemblyDef a)
    {
        var f = new Field(pack, a);
        var step = Math.Clamp(f.Thin / 5, 0.0015f, 0.012f);
        step = MathF.Max(step, MathF.Sqrt(f.AreaSum / 450_000f));
        var mesh = SurfaceNets.MeshSparse(f.Eval, f.Lo - new Vector3(step * 2), f.Hi + new Vector3(step * 2), step,
            p => f.Normal(p, step * 0.5f),
            p =>
            {
                f.Eval(p, out var c, out var seam, out var metal);
                // 머리카락 이음선: 두 무리의 거리가 거의 같은 곳 (맞닿은 경계) — 정점 간격에 맞춘 폭
                var line = a.Joints.Length > 0 ? 1 - Smooth(0.15f * step, 0.9f * step, seam) : 0;
                return new Vector4(c * (1 - 0.6f * line), metal);   // a = 금속
            });
        var size = MathF.Max(0.02f, MathF.Min(f.Hi.X - f.Lo.X, MathF.Min(f.Hi.Y - f.Lo.Y, f.Hi.Z - f.Lo.Z)));
        var hs = new[] { 0.06f, 0.15f, 0.3f, 0.6f }.Select(x => x * MathF.Min(size, 0.25f)).ToArray();
        var light = new Vector4[mesh.VertexCount];
        Parallel.For(0, mesh.VertexCount, v =>
        {
            var p = mesh.Pos[v]; var n = mesh.Nrm[v];
            var occ = 0f; var w = 1f;
            foreach (var h in hs) { occ += w * MathF.Max(0, h - f.Eval(p + n * h)) / h; w *= 0.6f; }
            light[v] = new Vector4(Math.Clamp(1 - occ * 0.35f, 0.4f, 1), 1, 1, 1);
        });
        mesh.Light.AddRange(light);
        return mesh;
    }

    static float Smooth(float a, float b, float x) { var t = Math.Clamp((x - a) / (b - a), 0, 1); return t * t * (3 - 2 * t); }
}
