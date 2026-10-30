using System.Numerics;
using DollyForge.Core;

namespace DollyForge;

/// <summary>메시 데이터 + 뼈대 (GPU 업로드 전 순수 배열)</summary>
public sealed class BodyMesh
{
    public Vector3[] Vertices = [];
    public Vector3[] Normals  = [];
    public Vector3[] Colors   = [];   // 모듈 색 (albedo)
    public int[]     Indices  = [];
    public int[]     Bones    = [];   // 정점당 4개
    public float[]   Weights  = [];   // 정점당 4개 (합=1)

    public const int BoneCount = 14;
    public Transform3D[] Rest   = new Transform3D[BoneCount]; // 부모 기준 쉼 자세
    public Transform3D[] Global = new Transform3D[BoneCount]; // 몸 기준 쉼 자세
    public int[]         Parent = new int[BoneCount];
}

/// <summary>런타임 인형 — 포즈 쿼터니언 + 애니메이션 상태</summary>
public sealed class CyborgBody
{
    public readonly BodyMesh Mesh;
    public float Height { get; }

    readonly Quaternion[] _restRot;
    readonly Quaternion[] _pose;
    readonly Node3DProxy[] _fingersL, _fingersR;
    readonly Node3DProxy   _thumbL, _thumbR;
    float _phase, _amp;

    CyborgBody(BodyMesh mesh, float height, Quaternion[] restRot,
               Node3DProxy[] fingersL, Node3DProxy fingersR0,
               Node3DProxy[] fingersR, Node3DProxy thumbL)
    {
        Mesh      = mesh;
        Height    = height;
        _restRot  = restRot;
        _pose     = new Quaternion[BodyMesh.BoneCount];
        for (var i = 0; i < _pose.Length; i++) _pose[i] = Quaternion.Identity;
        _fingersL = fingersL;
        _fingersR = fingersR;
        _thumbL   = thumbL;
        _thumbR   = fingersR0;   // 엄지는 따로
    }

    // 현재 뼈 포즈 행렬을 채운다 (스키닝 유니폼용)
    public void GetSkinMatrices(Span<Matrix4x4> out_)
    {
        Span<Transform3D> world = stackalloc Transform3D[BodyMesh.BoneCount];
        for (var b = 0; b < BodyMesh.BoneCount; b++)
        {
            var local = Transform3D.From(QuatToTransform(_pose[b]), Mesh.Rest[b].Origin);
            world[b] = Mesh.Parent[b] < 0 ? local : world[Mesh.Parent[b]] * local;
            out_[b]  = (world[b] * Mesh.Global[b].AffineInverse()).ToMatrix4x4();
        }
    }

    static Transform3D QuatToTransform(Quaternion q)
    {
        var m = Matrix4x4.CreateFromQuaternion(q);
        return new Transform3D(
            new Vector3(m.M11, m.M21, m.M31),
            new Vector3(m.M12, m.M22, m.M32),
            new Vector3(m.M13, m.M23, m.M33),
            Vector3.Zero);
    }

    void SetPose(Bone b, Quaternion q) => _pose[(int)b] = q;

    void Pose(Bone b, Vector3 euler) =>
        SetPose(b, _restRot[(int)b] * Transform3D.EulerToQuat(euler));

    public void Animate(float dt, float speed)
    {
        var target = M.Min(1, speed / 3);
        _amp = M.MoveToward(_amp, target, dt * (target > _amp ? 5.5f : 3.2f));
        var strideLength = M.Lerp(1.45f, 2.2f, target);
        var cadence = speed > 0.08f ? M.Tau * speed / strideLength : 1.2f;
        _phase = M.PosMod(_phase + dt * cadence, M.Tau);
        var s = M.Sin(_phase);
        var c = M.Cos(_phase);

        var sway = s * 0.035f * _amp;
        SetPose(Bone.Root, Transform3D.EulerToQuat(new Vector3(0, s * 0.06f * _amp, sway)));
        Pose(Bone.ThighL, new Vector3(s * 0.5f * _amp, 0, 0.03f * _amp));
        Pose(Bone.ThighR, new Vector3(-s * 0.5f * _amp, 0, -0.03f * _amp));
        SetPose(Bone.ShinL, Transform3D.EulerToQuat(new Vector3(M.Max(0, -c) * 0.7f * _amp, 0, 0)));
        SetPose(Bone.ShinR, Transform3D.EulerToQuat(new Vector3(M.Max(0, c) * 0.7f * _amp, 0, 0)));
        SetPose(Bone.FootL, Transform3D.EulerToQuat(new Vector3(-s * 0.12f * _amp, 0, 0)));
        SetPose(Bone.FootR, Transform3D.EulerToQuat(new Vector3(s * 0.12f * _amp, 0, 0)));
        Pose(Bone.UpperL, new Vector3(-s * 0.35f * _amp, 0, 0.03f * _amp));
        Pose(Bone.UpperR, new Vector3(s * 0.35f * _amp, 0, -0.03f * _amp));
        SetPose(Bone.ForeL, Transform3D.EulerToQuat(new Vector3(-0.14f - M.Max(0, s) * 0.22f * _amp, 0, 0)));
        SetPose(Bone.ForeR, Transform3D.EulerToQuat(new Vector3(-0.14f - M.Max(0, -s) * 0.22f * _amp, 0, 0)));
        SetPose(Bone.HandL, Transform3D.EulerToQuat(new Vector3(0.025f * s * _amp, 0, 0)));
        SetPose(Bone.HandR, Transform3D.EulerToQuat(new Vector3(-0.025f * s * _amp, 0, 0)));
        SetPose(Bone.Head,  Transform3D.EulerToQuat(new Vector3(
            M.Sin(_phase * 0.37f) * 0.018f * (1 - _amp),
            M.Sin(_phase * 0.23f) * 0.065f * (1 - _amp), -sway * 0.5f)));
    }

    // ─── 정적 빌더 ───────────────────────────────────────────────────────

    record struct Sec(float Y, float Rx, float F, float B, float Cx = 0, float Cz = 0,
                      float Sq = 1, float Bust = 0, float Glute = 0, float Pec = 0, float Abs = 0)
    {
        public Sec Scale(float k) => this with { Rx = Rx * k, F = F * k, B = B * k };
    }

    static float Catmull(float p0, float p1, float p2, float p3, float t)
    {
        var t2 = t * t; var t3 = t2 * t;
        return 0.5f * (2 * p1 + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t2 + (-p0 + 3 * p1 - 3 * p2 + p3) * t3);
    }

    static Sec SectionAt(Sec[] s, int i, float t)
    {
        var (a, b, c, d) = (s[Math.Max(i - 1, 0)], s[i], s[i + 1], s[Math.Min(i + 2, s.Length - 1)]);
        float C(Func<Sec, float> f) => Catmull(f(a), f(b), f(c), f(d), t);
        return new Sec(C(x => x.Y), M.Max(C(x => x.Rx), 0.001f), M.Max(C(x => x.F), 0.001f), M.Max(C(x => x.B), 0.001f),
            C(x => x.Cx), C(x => x.Cz), C(x => x.Sq), M.Max(C(x => x.Bust), 0), M.Max(C(x => x.Glute), 0),
            M.Max(C(x => x.Pec), 0), M.Max(C(x => x.Abs), 0));
    }

    static float AngDiff(float a, float b) => M.Atan2(M.Sin(a - b), M.Cos(a - b));
    static float Bump(float th, float center, float width) => M.Exp(-M.Pow(AngDiff(th, center) / width, 2));
    static float SPow(float v, float e) => M.Sign(v) * M.Pow(M.Abs(v), 1 / e);

    static Vector3 RingPoint(Sec s, float th)
    {
        float c = M.Cos(th), sn = M.Sin(th);
        var x = s.Cx + s.Rx * SPow(c, s.Sq);
        var z = s.Cz + (sn >= 0 ? s.F : s.B) * SPow(sn, s.Sq);
        foreach (var side in new[] { -1f, 1f })
        {
            if (s.Bust > 0) { var g = Bump(th, M.Pi / 2 + side * 0.52f, 0.40f); z += s.Bust * g; x += s.Bust * g * c * 0.28f; }
            if (s.Pec  > 0) z += s.Pec  * Bump(th, M.Pi / 2 + side * 0.60f, 0.48f);
            if (s.Glute > 0) { var g = Bump(th, -M.Pi / 2 + side * 0.52f, 0.48f); z -= s.Glute * g; x += s.Glute * g * c * 0.22f; }
            if (s.Abs  > 0) z += s.Abs  * Bump(th, M.Pi / 2 + side * 0.26f, 0.16f);
        }
        return new Vector3(x, s.Y, z);
    }

    sealed record Proportions(
        Sec[] Torso, float Sq, (float Y0, float Y1, float R) Neck, (float Base, float Scale) Head,
        (float X, float Y) Shoulder, (float Upper, float Fore, float R, float Bicep) Arm,
        (float X, float Y) Hip, (float Thigh, float Shin, float R, float Q) Leg, float Foot,
        float WaistY, float UnderbustY, float LowHipY);

    static readonly Proportions Female = new(
        [
            new(0.70f, 0.030f, 0.030f, 0.034f, Cz: -0.004f),
            new(0.73f, 0.084f, 0.052f, 0.058f, Cz: -0.004f),
            new(0.77f, 0.150f, 0.076f, 0.082f, Glute: 0.014f),
            new(0.81f, 0.196f, 0.092f, 0.104f, Glute: 0.040f),
            new(0.85f, 0.218f, 0.098f, 0.120f, Glute: 0.056f),
            new(0.91f, 0.212f, 0.102f, 0.124f, Glute: 0.056f),
            new(0.97f, 0.178f, 0.092f, 0.112f, Glute: 0.026f),
            new(1.02f, 0.126f, 0.072f, 0.080f, Glute: 0.006f),
            new(1.07f, 0.076f, 0.052f, 0.050f),
            new(1.12f, 0.108f, 0.068f, 0.066f),
            new(1.18f, 0.140f, 0.078f, 0.076f, Bust: 0.026f),
            new(1.24f, 0.158f, 0.084f, 0.082f, Bust: 0.068f),
            new(1.29f, 0.156f, 0.083f, 0.081f, Bust: 0.064f),
            new(1.34f, 0.152f, 0.072f, 0.076f, Bust: 0.018f),
            new(1.37f, 0.146f, 0.054f, 0.064f),
            new(1.39f, 0.088f, 0.041f, 0.047f),
            new(1.40f, 0.048f, 0.036f, 0.036f),
        ],
        1.0f, (1.36f, 1.5f, 0.040f), (1.425f, 0.86f), (0.154f, 1.315f),
        (0.300f, 0.260f, 0.86f, 0.007f), (0.086f, 0.84f),
        (0.46f, 0.44f, 1.06f, 0.045f), 0.228f, 1.07f, 1.18f, 0.97f);

    static readonly Proportions Male = new(
        [
            new(0.70f, 0.036f, 0.036f, 0.042f),
            new(0.74f, 0.112f, 0.068f, 0.076f),
            new(0.78f, 0.152f, 0.082f, 0.094f, Glute: 0.008f),
            new(0.83f, 0.170f, 0.093f, 0.108f, Glute: 0.020f),
            new(0.87f, 0.184f, 0.106f, 0.122f, Glute: 0.030f),
            new(0.95f, 0.198f, 0.112f, 0.130f, Glute: 0.040f),
            new(1.01f, 0.178f, 0.100f, 0.116f, Glute: 0.020f),
            new(1.07f, 0.138f, 0.088f, 0.086f, Abs: 0.010f),
            new(1.15f, 0.166f, 0.110f, 0.106f, Abs: 0.014f),
            new(1.23f, 0.192f, 0.118f, 0.114f, Abs: 0.014f),
            new(1.31f, 0.222f, 0.130f, 0.124f, Pec: 0.058f),
            new(1.38f, 0.240f, 0.124f, 0.118f, Pec: 0.042f),
            new(1.43f, 0.222f, 0.096f, 0.100f),
            new(1.47f, 0.154f, 0.074f, 0.080f),
            new(1.49f, 0.074f, 0.064f, 0.064f),
        ],
        1.22f, (1.44f, 1.6f, 0.058f), (1.525f, 0.94f), (0.252f, 1.395f),
        (0.36f, 0.310f, 1.46f, 0.032f), (0.094f, 0.9f),
        (0.50f, 0.48f, 1.30f, 0.02f), 0.26f, 1.07f, 1.23f, 1.01f);

    static readonly Sec[] HeadShell =
    [
        new(0,      0.045f, 0.048f, 0.048f), new(0.018f, 0.065f, 0.068f, 0.068f),
        new(0.052f, 0.084f, 0.088f, 0.088f), new(0.092f, 0.094f, 0.098f, 0.098f),
        new(0.132f, 0.095f, 0.100f, 0.100f), new(0.170f, 0.087f, 0.092f, 0.092f),
        new(0.204f, 0.068f, 0.074f, 0.074f), new(0.228f, 0.040f, 0.044f, 0.044f),
        new(0.240f, 0.006f, 0.007f, 0.007f),
    ];

    static readonly Vector3 StandardTitanium     = new(0.68f, 0.71f, 0.74f);
    static readonly Vector3 HeavyTitaniumColor   = new(0.52f, 0.59f, 0.63f);
    static readonly Vector3 LightTitaniumColor   = new(0.64f, 0.68f, 0.71f);
    static Vector3 VariantColor(ModuleVariant v) => v switch {
        ModuleVariant.HeavyDuty => HeavyTitaniumColor,
        ModuleVariant.Lite      => LightTitaniumColor,
        _                       => StandardTitanium,
    };

    static float Smin(float a, float b, float k)
    {
        if (k <= 0) return M.Min(a, b);
        var h = M.Max(k - M.Abs(a - b), 0) / k;
        return M.Min(a, b) - h * h * k * 0.25f;
    }

    sealed class LoftField
    {
        readonly Sec[] _s;
        public readonly float MaxRadius;
        public LoftField(Sec[] s)
        {
            _s = s;
            foreach (var sec in s) MaxRadius = M.Max(MaxRadius, M.Max(sec.Rx, M.Max(sec.F, sec.B)) * 1.2f + M.Abs(sec.Cx) + M.Abs(sec.Cz) + 0.03f);
        }
        public (float Y0, float Y1) Span => (_s[0].Y, _s[^1].Y);

        public float Distance(Vector3 p)
        {
            var (y0, y1) = Span;
            var y = M.Clamp(p.Y, y0, y1);
            var i = 0;
            while (i < _s.Length - 2 && _s[i + 1].Y < y) i++;
            var sec = SectionAt(_s, i, M.Clamp((y - _s[i].Y) / (_s[i + 1].Y - _s[i].Y), 0, 1));
            float dx = p.X - sec.Cx, dz = p.Z - sec.Cz;
            var fz = dz >= 0 ? sec.F : sec.B;
            var e  = sec.Sq;
            var th = M.Atan2(M.Sign(dz) * M.Pow(M.Abs(dz) / fz, e), M.Sign(dx) * M.Pow(M.Abs(dx) / sec.Rx, e));
            var ring = RingPoint(sec, th);
            var rad  = new Vector2(ring.X - sec.Cx, ring.Z - sec.Cz).Length();
            var dr   = M.Sqrt(dx * dx + dz * dz) - rad;
            var over = M.Max(y0 - p.Y, p.Y - y1);
            return over > 0 ? M.Sqrt(M.Max(dr, 0) * M.Max(dr, 0) + over * over) : dr;
        }
    }

    struct Aabb
    {
        public Vector3 Min, Max;
        public Aabb(Vector3 mn, Vector3 mx) { Min = mn; Max = mx; }
        public bool HasPoint(Vector3 p) => p.X >= Min.X && p.X <= Max.X && p.Y >= Min.Y && p.Y <= Max.Y && p.Z >= Min.Z && p.Z <= Max.Z;
        public Aabb Grow(float s) => new(Min - new Vector3(s), Max + new Vector3(s));
        public static Aabb Merge(Aabb a, Aabb b)
            => new(Vector3.Min(a.Min, b.Min), Vector3.Max(a.Max, b.Max));
    }

    sealed class Shape
    {
        public Transform3D WorldToLocal = Transform3D.Identity;
        public float Mirror = 1;
        public Vector3 Offset;
        public LoftField? Loft;
        public Vector3 Radii;
        public float Blend;
        public Aabb Bounds;

        public float Distance(Vector3 world)
        {
            var p = WorldToLocal * world;
            p = new Vector3(p.X * Mirror, p.Y, p.Z) - Offset;
            if (Loft is not null) return Loft.Distance(p);
            var k0 = (p / Radii).Length();
            var k1 = (p / (Radii * Radii)).Length();
            return k1 > 1e-6f ? k0 * (k0 - 1) / k1 : -M.Min(Radii.X, M.Min(Radii.Y, Radii.Z));
        }

        public void ComputeBounds()
        {
            Vector3 lo, hi;
            if (Loft is not null)
            {
                var (y0, y1) = Loft.Span;
                lo = new Vector3(-Loft.MaxRadius, y0, -Loft.MaxRadius) + Offset;
                hi = new Vector3(Loft.MaxRadius,  y1,  Loft.MaxRadius) + Offset;
            }
            else { lo = Offset - Radii; hi = Offset + Radii; }

            var toWorld = WorldToLocal.AffineInverse();
            var first = true; var box = new Aabb();
            for (var cc = 0; cc < 8; cc++)
            {
                var q = new Vector3((cc & 1) == 0 ? lo.X : hi.X, (cc & 2) == 0 ? lo.Y : hi.Y, (cc & 4) == 0 ? lo.Z : hi.Z);
                q = new Vector3(q.X * Mirror, q.Y, q.Z);
                var w = toWorld * q;
                box = first ? new Aabb(w, w) : Aabb.Merge(box, new Aabb(w, w));
                first = false;
            }
            Bounds = box.Grow(Blend + 0.02f);
        }
    }

    enum Bone { Root, Head, UpperR, UpperL, ForeR, ForeL, HandR, HandL, ThighR, ThighL, ShinR, ShinL, FootR, FootL }

    static readonly BodySlot[] BoneSlot =
    [
        BodySlot.Torso, BodySlot.Head, BodySlot.UpperArmR, BodySlot.UpperArmL,
        BodySlot.ForearmR, BodySlot.ForearmL, BodySlot.HandR, BodySlot.HandL,
        BodySlot.ThighR, BodySlot.ThighL, BodySlot.ShinR, BodySlot.ShinL, BodySlot.FootR, BodySlot.FootL,
    ];

    const float LegLift   = 0.12f;
    const float ArmSplay  = 0.11f;
    const float Voxel     = 0.0055f;

    static readonly Dictionary<byte, BodyMesh> Cache = new();

    public static CyborgBody Build(byte gender) => Build(BodyConfig.Default(gender));

    public static CyborgBody Build(BodyConfig body)
    {
        var gender = body.Gender;
        var P = gender == 1 ? Male : Female;
        var headTop = P.Head.Base + 0.24f * P.Head.Scale;
        var scale   = (gender == 1 ? 1.045f : 1.08f) * headTop / (headTop + LegLift);

        if (!Cache.TryGetValue(gender, out var geo)) Cache[gender] = geo = BuildGeometry(P);

        // 모듈 색 섞기
        var colors = new Vector3[geo.Vertices.Length];
        for (var v = 0; v < colors.Length; v++)
        {
            var c = Vector3.Zero;
            for (var k = 0; k < 4; k++)
                c += VariantColor(body.VariantAt(BoneSlot[geo.Bones[v * 4 + k]])) * geo.Weights[v * 4 + k];
            colors[v] = c;
        }

        // 키 스케일 적용 (정점 복사)
        var verts = geo.Vertices.Select(v => v * scale).ToArray();
        var mesh = new BodyMesh
        {
            Vertices = verts,
            Normals  = geo.Normals,
            Colors   = colors,
            Indices  = geo.Indices,
            Bones    = geo.Bones,
            Weights  = geo.Weights,
            Rest     = geo.Rest,
            Global   = geo.Global,
            Parent   = geo.Parent,
        };

        var restRot = geo.Rest.Select(t => t.GetRotationQuaternion()).ToArray();
        return new CyborgBody(mesh, gender == 1 ? 1.86f : 1.80f, restRot,
                              [], new Node3DProxy(), [], new Node3DProxy());
    }

    static BodyMesh BuildGeometry(Proportions P)
    {
        var geo  = new BodyMesh();
        var lift = LegLift;
        var (U, F, r, _) = P.Arm;
        var (T, N, lr, q) = P.Leg;
        var shapes = new List<Shape>();
        var segs   = new (Vector3 A, Vector3 B)[BodyMesh.BoneCount];

        void SetBone(Bone b, int parent, Transform3D rest)
        {
            geo.Parent[(int)b] = parent;
            geo.Rest[(int)b]   = rest;
            geo.Global[(int)b] = parent < 0 ? rest : geo.Global[parent] * rest;
        }

        Shape Add(Transform3D frame, float mirror, Vector3 offset, float blend, LoftField? loft = null, Vector3 radii = default)
        {
            var sh = new Shape { WorldToLocal = frame.AffineInverse(), Mirror = mirror, Offset = offset, Loft = loft, Radii = radii, Blend = blend };
            sh.ComputeBounds();
            shapes.Add(sh);
            return sh;
        }

        var torso = P.Torso
            .Append(new Sec(P.Neck.Y1, P.Neck.R * 0.92f, P.Neck.R * 0.92f, P.Neck.R * 0.92f, Cz: -0.008f))
            .Select(s => s with { Sq = P.Sq }).ToArray();

        SetBone(Bone.Root, -1, Transform3D.Identity);
        SetBone(Bone.Head, (int)Bone.Root, Transform3D.FromTranslation(new Vector3(0, P.Neck.Y0 + lift, 0)));
        Add(Transform3D.FromTranslation(new Vector3(0, lift, 0)), 1, Vector3.Zero, 0, new LoftField(torso));
        var k = P.Head.Scale;
        var head = HeadShell.Select(x => x with { Y = x.Y * k, Rx = x.Rx * k, F = x.F * k, B = x.B * k }).ToArray();
        Add(Transform3D.FromTranslation(new Vector3(0, P.Head.Base + lift, 0)), 1, Vector3.Zero, 0.03f, new LoftField(head));
        var headTop = P.Head.Base + lift + 0.24f * k;
        segs[(int)Bone.Root] = (new Vector3(0, torso[0].Y + lift, 0), new Vector3(0, P.Neck.Y0 + lift, 0));
        segs[(int)Bone.Head] = (new Vector3(0, P.Neck.Y0 + lift + 0.04f, 0), new Vector3(0, headTop, 0));

        Sec[] upper = [
            new(0, 0.036f, 0.035f, 0.036f), new(U*0.3f,  0.040f, 0.040f + P.Arm.Bicep*0.4f, 0.040f),
            new(U*0.6f, 0.044f, 0.045f + P.Arm.Bicep, 0.043f), new(U*0.85f, 0.047f, 0.046f, 0.046f),
            new(U,  0.048f, 0.046f, 0.047f), new(U+0.03f, 0.044f, 0.042f, 0.043f),
        ];
        Sec[] fore = [
            new(0, 0.034f, 0.032f, 0.034f), new(F*0.22f, 0.030f, 0.028f, 0.030f),
            new(F*0.52f, 0.028f, 0.026f, 0.028f), new(F*0.8f, 0.027f, 0.025f, 0.026f),
            new(F, 0.025f, 0.024f, 0.025f), new(F+0.014f, 0.022f, 0.021f, 0.022f),
        ];
        Sec[] thigh = [
            new(0, 0.044f, 0.045f, 0.046f), new(T*0.25f, 0.050f, 0.050f, 0.053f),
            new(T*0.5f, 0.064f, 0.062f, 0.066f, Cx: 0.003f), new(T*0.75f, 0.077f, 0.073f, 0.080f, Cx: 0.006f),
            new(T*0.9f, 0.082f, 0.077f, 0.086f, Cx: 0.006f), new(T, 0.080f, 0.075f, 0.084f, Cx: 0.004f),
            new(T+0.05f, 0.070f, 0.064f, 0.072f),
        ];
        Sec[] shin = [
            new(0, 0.030f, 0.031f, 0.033f), new(N*0.15f, 0.032f, 0.032f, 0.036f),
            new(N*0.45f, 0.040f, 0.038f, 0.050f), new(N*0.7f, 0.044f, 0.040f, 0.052f),
            new(N*0.9f, 0.044f, 0.043f, 0.046f), new(N, 0.044f, 0.045f, 0.046f),
            new(N+0.032f, 0.040f, 0.041f, 0.041f),
        ];
        var FL = P.Foot;
        Sec[] foot = [
            new(-0.012f, 0.014f, 0.016f, 0.014f), new(0, 0.026f, 0.030f, 0.026f),
            new(FL*0.12f, 0.033f, 0.034f, 0.042f), new(FL*0.4f, 0.039f, 0.031f, 0.034f),
            new(FL*0.72f, 0.045f, 0.026f, 0.024f), new(FL*0.9f, 0.040f, 0.020f, 0.018f),
            new(FL*0.98f, 0.026f, 0.013f, 0.012f), new(FL, 0.012f, 0.006f, 0.006f),
        ];
        var upperField = new LoftField(upper.Select(x => x.Scale(r)).ToArray());
        var foreField  = new LoftField(fore.Select(x => x.Scale(r)).ToArray());
        var thighField = new LoftField(thigh.Select(x => x.Scale(lr)).ToArray());
        var shinField  = new LoftField(shin.Select(x => x.Scale(lr)).ToArray());
        var footField  = new LoftField(foot.Select(x => x.Scale(M.Sqrt(lr))).ToArray());

        foreach (var side in new[] { 1, -1 })
        {
            var R = side > 0;
            Bone Pick(Bone right, Bone left) => R ? right : left;

            SetBone(Pick(Bone.UpperR, Bone.UpperL), (int)Bone.Root,
                Transform3D.From(Transform3D.FromAxisAngle(M.Back, side * ArmSplay),
                    new Vector3(side * P.Shoulder.X, P.Shoulder.Y + lift, 0)));
            SetBone(Pick(Bone.ForeR, Bone.ForeL), (int)Pick(Bone.UpperR, Bone.UpperL),
                Transform3D.From(Transform3D.FromAxisAngle(M.Right, -0.10f), new Vector3(0, -U, 0)));
            SetBone(Pick(Bone.HandR, Bone.HandL), (int)Pick(Bone.ForeR, Bone.ForeL),
                Transform3D.FromTranslation(new Vector3(0, -F - 0.014f * r, 0)));

            var up = geo.Global[(int)Pick(Bone.UpperR, Bone.UpperL)];
            var el = geo.Global[(int)Pick(Bone.ForeR,  Bone.ForeL)];
            var wr = geo.Global[(int)Pick(Bone.HandR,  Bone.HandL)];
            Add(up, side, new Vector3(0, -U, 0), 0.035f, upperField);
            Add(up, side, new Vector3(-0.008f * r, -0.022f, 0), 0.035f, radii: new Vector3(0.047f, 0.052f, 0.0456f) * r);
            Add(el, side, Vector3.Zero, 0.018f, radii: new Vector3(0.034f, 0.036f, 0.034f) * r);
            Add(el, side, new Vector3(0, -F - 0.014f * r, 0), 0.018f, foreField);
            Add(wr, side, new Vector3(0, -0.006f * r, 0), 0.012f, radii: new Vector3(0.024f, 0.023f, 0.024f) * r);
            segs[(int)Pick(Bone.UpperR, Bone.UpperL)] = (up.Origin, el.Origin);
            segs[(int)Pick(Bone.ForeR,  Bone.ForeL)]  = (el.Origin, wr.Origin);
            segs[(int)Pick(Bone.HandR,  Bone.HandL)]  = (wr.Origin, wr * new Vector3(0, -0.06f, 0));

            SetBone(Pick(Bone.ThighR, Bone.ThighL), (int)Bone.Root,
                Transform3D.From(Transform3D.FromAxisAngle(M.Back, -side * q),
                    new Vector3(side * P.Hip.X, P.Hip.Y + lift, 0)));
            SetBone(Pick(Bone.ShinR,  Bone.ShinL),  (int)Pick(Bone.ThighR, Bone.ThighL),
                Transform3D.FromTranslation(new Vector3(0, -T, 0)));
            SetBone(Pick(Bone.FootR,  Bone.FootL),  (int)Pick(Bone.ShinR, Bone.ShinL),
                Transform3D.FromTranslation(new Vector3(0, -N - 0.064f, -0.05f)));

            var hip = geo.Global[(int)Pick(Bone.ThighR, Bone.ThighL)];
            var kn  = geo.Global[(int)Pick(Bone.ShinR,  Bone.ShinL)];
            var an  = geo.Global[(int)Pick(Bone.FootR,  Bone.FootL)];
            Add(hip, side, new Vector3(0, -T, 0), 0.06f, thighField);
            Add(kn, side, Vector3.Zero, 0.02f, radii: new Vector3(0.0432f, 0.046f, 0.0428f) * lr);
            Add(kn, side, new Vector3(0, -N - 0.032f, 0), 0.02f, shinField);
            Add(an, side, Vector3.Zero, 0.018f, radii: new Vector3(0.032f, 0.031f, 0.032f) * lr);
            var footFrame = an * Transform3D.FromAxisAngle(M.Right, M.Pi / 2);
            Add(footFrame, side, Vector3.Zero, 0.02f, footField);
            segs[(int)Pick(Bone.ThighR, Bone.ThighL)] = (hip.Origin, kn.Origin);
            segs[(int)Pick(Bone.ShinR,  Bone.ShinL)]  = (kn.Origin, an.Origin);
            segs[(int)Pick(Bone.FootR,  Bone.FootL)]  = (an.Origin, footFrame * new Vector3(0, FL, 0));
        }

        float Field(Vector3 p)
        {
            var d = 1e3f;
            foreach (var sh in shapes)
                if (sh.Bounds.HasPoint(p)) d = Smin(d, sh.Distance(p), sh.Blend);
            return d;
        }

        var box = shapes.Select(s => s.Bounds).Aggregate(Aabb.Merge);
        (geo.Vertices, geo.Normals, geo.Indices) = SurfaceNets(Field, box, Voxel);
        (geo.Bones, geo.Weights) = SkinWeights(geo.Vertices, segs);
        return geo;
    }

    static (Vector3[] V, Vector3[] N, int[] I) SurfaceNets(Func<Vector3, float> f, Aabb box, float h)
    {
        var padded = box.Grow(h * 2);
        int nx = (int)((padded.Max.X - padded.Min.X) / h) + 2;
        int ny = (int)((padded.Max.Y - padded.Min.Y) / h) + 2;
        int nz = (int)((padded.Max.Z - padded.Min.Z) / h) + 2;
        var o  = padded.Min;
        var grid = new float[nx * ny * nz];
        int Id(int i, int j, int kk) => (kk * ny + j) * nx + i;
        Parallel.For(0, nz, kk =>
        {
            for (var j = 0; j < ny; j++)
            for (var i = 0; i < nx; i++)
                grid[Id(i, j, kk)] = f(o + new Vector3(i, j, kk) * h);
        });

        var cell = new int[nx * ny * nz]; Array.Fill(cell, -1);
        var verts = new List<Vector3>();
        int[] cx = [0,1,0,1,0,1,0,1], cy = [0,0,1,1,0,0,1,1], cz = [0,0,0,0,1,1,1,1];
        int[,] edges = {{0,1},{2,3},{4,5},{6,7},{0,2},{1,3},{4,6},{5,7},{0,4},{1,5},{2,6},{3,7}};
        var val = new float[8];
        for (var kk = 0; kk < nz - 1; kk++)
        for (var j  = 0; j  < ny - 1; j++)
        for (var i  = 0; i  < nx - 1; i++)
        {
            var inside = 0;
            for (var c = 0; c < 8; c++) { val[c] = grid[Id(i+cx[c], j+cy[c], kk+cz[c])]; if (val[c] < 0) inside++; }
            if (inside == 0 || inside == 8) continue;
            var sum = Vector3.Zero; var n = 0;
            for (var e = 0; e < 12; e++)
            {
                int a = edges[e,0], b = edges[e,1];
                if ((val[a] < 0) == (val[b] < 0)) continue;
                var t = val[a] / (val[a] - val[b]);
                sum += new Vector3(cx[a]+(cx[b]-cx[a])*t, cy[a]+(cy[b]-cy[a])*t, cz[a]+(cz[b]-cz[a])*t);
                n++;
            }
            cell[Id(i, j, kk)] = verts.Count;
            verts.Add(o + (new Vector3(i, j, kk) + sum / n) * h);
        }

        var normals = new Vector3[verts.Count];
        Parallel.For(0, verts.Count, v =>
        {
            var p = verts[v]; var e = h * 0.5f;
            var gr = new Vector3(f(p + new Vector3(e,0,0)) - f(p - new Vector3(e,0,0)),
                                 f(p + new Vector3(0,e,0)) - f(p - new Vector3(0,e,0)),
                                 f(p + new Vector3(0,0,e)) - f(p - new Vector3(0,0,e)));
            normals[v] = gr.LengthSquared() > 1e-12f ? Vector3.Normalize(gr) : Vector3.UnitY;
        });

        var idx = new List<int>();
        void Quad(int a, int b, int c, int d, bool flip)
        {
            if (a < 0 || b < 0 || c < 0 || d < 0) return;
            var nrm   = normals[a] + normals[b] + normals[c] + normals[d];
            var cross = Vector3.Cross(verts[b] - verts[a], verts[c] - verts[a]);
            if ((Vector3.Dot(cross, nrm) > 0) != flip) (b, d) = (d, b);
            idx.AddRange([a, b, c, a, c, d]);
        }
        for (var kk = 1; kk < nz - 1; kk++)
        for (var j  = 1; j  < ny - 1; j++)
        for (var i  = 1; i  < nx - 1; i++)
        {
            var v0 = grid[Id(i,j,kk)] < 0;
            if (i < nx-1 && v0 != grid[Id(i+1,j,kk)] < 0) Quad(cell[Id(i,j-1,kk-1)],cell[Id(i,j,kk-1)],cell[Id(i,j,kk)],cell[Id(i,j-1,kk)],false);
            if (j < ny-1 && v0 != grid[Id(i,j+1,kk)] < 0) Quad(cell[Id(i-1,j,kk-1)],cell[Id(i,j,kk-1)],cell[Id(i,j,kk)],cell[Id(i-1,j,kk)],false);
            if (kk< nz-1 && v0 != grid[Id(i,j,kk+1)] < 0) Quad(cell[Id(i-1,j-1,kk)],cell[Id(i,j-1,kk)],cell[Id(i,j,kk)],cell[Id(i-1,j,kk)],false);
        }
        return (verts.ToArray(), normals, idx.ToArray());
    }

    static (int[] Bones, float[] Weights) SkinWeights(Vector3[] verts, (Vector3 A, Vector3 B)[] segs)
    {
        const float falloff = 0.022f;
        var bones   = new int[verts.Length * 4];
        var weights = new float[verts.Length * 4];
        Parallel.For(0, verts.Length, v =>
        {
            Span<float> d = stackalloc float[segs.Length];
            var min = float.MaxValue;
            for (var b = 0; b < segs.Length; b++)
            {
                var (a, eb) = segs[b];
                var ab = eb - a;
                var t  = M.Clamp(Vector3.Dot(verts[v] - a, ab) / M.Max(ab.LengthSquared(), 1e-8f), 0, 1);
                d[b]   = Vector3.Distance(verts[v], a + ab * t);
                min    = M.Min(min, d[b]);
            }
            Span<int>   best = stackalloc int[4];
            Span<float> w    = stackalloc float[4];
            for (var s = 0; s < 4; s++) { best[s] = 0; w[s] = 0; }
            for (var b = 0; b < segs.Length; b++)
            {
                var wb = M.Exp(-(d[b] - min) / falloff);
                for (var s = 0; s < 4; s++)
                {
                    if (wb <= w[s]) continue;
                    for (var u = 3; u > s; u--) { w[u] = w[u-1]; best[u] = best[u-1]; }
                    w[s] = wb; best[s] = b; break;
                }
            }
            var total = w[0] + w[1] + w[2] + w[3];
            for (var s = 0; s < 4; s++) { bones[v*4+s] = best[s]; weights[v*4+s] = w[s] / total; }
        });
        return (bones, weights);
    }

    // 손 지오메트리는 현재 빌더에서 생략 — BoneAttachment 없이 손목 뼈에 skinning으로 처리
    sealed class Node3DProxy { public Vector3 Rotation; }
}
