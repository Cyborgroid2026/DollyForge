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
    public float[]   Seams    = [];   // 외피 이음선까지 부호 거리 (m), 셰이더가 가는 선으로 그린다

    public const int BoneCount = 14;
    public Transform3D[] Rest   = new Transform3D[BoneCount]; // 부모 기준 쉼 자세
    public Transform3D[] Global = new Transform3D[BoneCount]; // 몸 기준 쉼 자세
    public int[]         Parent = new int[BoneCount];
}

/// <summary>
/// 크롬 사이보그 인형. 얼굴 없는 달걀형 머리, 거울 같은 은빛 외피.
/// 여성형 = 긴 목·좁은 어깨·가슴·잘록한 허리·넓은 골반·허벅지 사이 틈,
/// 남성형 = 넓은 승모·삼각근·대흉근·복근 8칸·광배 V자·대퇴사두·종아리.
/// 몸은 SDF 조형(로프트·원뿔·타원체의 부드러운 합집합)을 Surface Nets 로 메시화한다.
/// +Z 가 앞, +Y 가 위, 발바닥이 y = 0.
/// </summary>
public sealed class CyborgBody
{
    public readonly BodyMesh Mesh;
    public float Height { get; }

    readonly Quaternion[] _restRot;
    readonly Quaternion[] _pose;
    float _phase, _amp;

    CyborgBody(BodyMesh mesh, float height)
    {
        Mesh     = mesh;
        Height   = height;
        _restRot = mesh.Rest.Select(t => t.GetRotationQuaternion()).ToArray();
        _pose    = new Quaternion[BodyMesh.BoneCount];
        for (var i = 0; i < _pose.Length; i++) _pose[i] = _restRot[i];
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
        Pose(Bone.Root,   new Vector3(0, s * 0.06f * _amp, sway));
        Pose(Bone.ThighL, new Vector3(s * 0.5f * _amp, 0, 0.03f * _amp));
        Pose(Bone.ThighR, new Vector3(-s * 0.5f * _amp, 0, -0.03f * _amp));
        Pose(Bone.ShinL,  new Vector3(M.Max(0, -c) * 0.7f * _amp, 0, 0));
        Pose(Bone.ShinR,  new Vector3(M.Max(0, c) * 0.7f * _amp, 0, 0));
        Pose(Bone.FootL,  new Vector3(-s * 0.12f * _amp, 0, 0));
        Pose(Bone.FootR,  new Vector3(s * 0.12f * _amp, 0, 0));
        Pose(Bone.UpperL, new Vector3(-s * 0.35f * _amp, 0, 0.03f * _amp));
        Pose(Bone.UpperR, new Vector3(s * 0.35f * _amp, 0, -0.03f * _amp));
        Pose(Bone.ForeL,  new Vector3(-0.14f - M.Max(0, s) * 0.22f * _amp, 0, 0));
        Pose(Bone.ForeR,  new Vector3(-0.14f - M.Max(0, -s) * 0.22f * _amp, 0, 0));
        Pose(Bone.HandL,  new Vector3(0.025f * s * _amp, 0, 0));
        Pose(Bone.HandR,  new Vector3(-0.025f * s * _amp, 0, 0));
        Pose(Bone.Head,   new Vector3(
            M.Sin(_phase * 0.37f) * 0.018f * (1 - _amp),
            M.Sin(_phase * 0.23f) * 0.065f * (1 - _amp), -sway * 0.5f));
    }

    // ─── 재질 ────────────────────────────────────────────────────────────

    // 크롬 F0 (금속 반사율). 모듈 종류별로 톤만 다르다.
    static readonly Vector3 ChromeStandard = new(0.80f, 0.82f, 0.85f);
    static readonly Vector3 ChromeHeavy    = new(0.60f, 0.64f, 0.68f);
    static readonly Vector3 ChromeLite     = new(0.88f, 0.88f, 0.90f);
    static Vector3 VariantColor(ModuleVariant v) => v switch {
        ModuleVariant.HeavyDuty => ChromeHeavy,
        ModuleVariant.Lite      => ChromeLite,
        _                       => ChromeStandard,
    };

    // ─── 뼈대 ────────────────────────────────────────────────────────────

    enum Bone { Root, Head, UpperR, UpperL, ForeR, ForeL, HandR, HandL, ThighR, ThighL, ShinR, ShinL, FootR, FootL }

    static readonly BodySlot[] BoneSlot =
    [
        BodySlot.Torso, BodySlot.Head, BodySlot.UpperArmR, BodySlot.UpperArmL,
        BodySlot.ForearmR, BodySlot.ForearmL, BodySlot.HandR, BodySlot.HandL,
        BodySlot.ThighR, BodySlot.ThighL, BodySlot.ShinR, BodySlot.ShinL, BodySlot.FootR, BodySlot.FootL,
    ];

    const float Voxel = 0.0036f;

    static readonly Dictionary<byte, BodyMesh> Cache = new();

    public static CyborgBody Build(byte gender) => Build(BodyConfig.Default(gender));

    public static CyborgBody Build(BodyConfig body)
    {
        var gender = body.Gender;
        if (!Cache.TryGetValue(gender, out var geo)) Cache[gender] = geo = BuildGeometry(gender == 1);

        // 모듈 색 섞기
        var colors = new Vector3[geo.Vertices.Length];
        for (var v = 0; v < colors.Length; v++)
        {
            var c = Vector3.Zero;
            for (var k = 0; k < 4; k++)
                c += VariantColor(body.VariantAt(BoneSlot[geo.Bones[v * 4 + k]])) * geo.Weights[v * 4 + k];
            colors[v] = c;
        }

        var mesh = new BodyMesh
        {
            Vertices = geo.Vertices,
            Normals  = geo.Normals,
            Colors   = colors,
            Indices  = geo.Indices,
            Bones    = geo.Bones,
            Weights  = geo.Weights,
            Seams    = geo.Seams,
            Rest     = geo.Rest,
            Global   = geo.Global,
            Parent   = geo.Parent,
        };
        return new CyborgBody(mesh, geo.Vertices.Max(v => v.Y));
    }

    // ─── SDF 원형 ────────────────────────────────────────────────────────

    struct Aabb
    {
        public Vector3 Min, Max;
        public Aabb(Vector3 mn, Vector3 mx) { Min = mn; Max = mx; }
        public bool HasPoint(Vector3 p) => p.X >= Min.X && p.X <= Max.X && p.Y >= Min.Y && p.Y <= Max.Y && p.Z >= Min.Z && p.Z <= Max.Z;
        public Aabb Grow(float s) => new(Min - new Vector3(s), Max + new Vector3(s));
        public static Aabb Merge(Aabb a, Aabb b) => new(Vector3.Min(a.Min, b.Min), Vector3.Max(a.Max, b.Max));
    }

    abstract class Prim
    {
        public Bone  Bone;
        public float Blend;
        public bool  Carve;      // true = 부드럽게 파낸다 (근육 사이 홈)
        public bool  Arm;        // 팔 묶음: 어깨 높이에서만 몸통과 녹아 붙는다 (손이 허벅지에 붙지 않게)
        public Aabb  Bounds;
        public abstract float Distance(Vector3 p);
        protected abstract Aabb Box();
        public Prim Done() { Bounds = Box().Grow(Blend + 0.01f); return this; }
    }

    /// <summary>회전 가능한 타원체 (근사 SDF)</summary>
    sealed class Ellipsoid(Vector3 c, Vector3 r, Quaternion rot) : Prim
    {
        readonly Quaternion _inv = Quaternion.Inverse(rot);
        public override float Distance(Vector3 p)
        {
            var q  = Vector3.Transform(p - c, _inv);
            var k0 = (q / r).Length();
            var k1 = (q / (r * r)).Length();
            return k1 > 1e-6f ? k0 * (k0 - 1) / k1 : -M.Min(r.X, M.Min(r.Y, r.Z));
        }
        protected override Aabb Box()
        {
            var m = M.Max(r.X, M.Max(r.Y, r.Z));
            return new Aabb(c - new Vector3(m), c + new Vector3(m));
        }
    }

    /// <summary>두 끝 반지름이 다른 둥근 원뿔 (정확한 SDF)</summary>
    sealed class Cone(Vector3 a, Vector3 b, float ra, float rb) : Prim
    {
        public override float Distance(Vector3 p)
        {
            var ba = b - a;
            var l2 = Vector3.Dot(ba, ba);
            var rr = ra - rb;
            var a2 = l2 - rr * rr;
            var il2 = 1 / l2;
            var pa = p - a;
            var y  = Vector3.Dot(pa, ba);
            var z  = y - l2;
            var x2 = (pa * l2 - ba * y).LengthSquared();
            var y2 = y * y * l2;
            var z2 = z * z * l2;
            var k  = M.Sign(rr) * rr * rr * x2;
            if (M.Sign(z) * a2 * z2 > k) return M.Sqrt(x2 + z2) * il2 - rb;
            if (M.Sign(y) * a2 * y2 < k) return M.Sqrt(x2 + y2) * il2 - ra;
            return (M.Sqrt(x2 * a2 * il2) + y * rr) * il2 - ra;
        }
        protected override Aabb Box()
        {
            var r = M.Max(ra, rb);
            return new Aabb(Vector3.Min(a, b) - new Vector3(r), Vector3.Max(a, b) + new Vector3(r));
        }
    }

    /// <summary>높이별 단면(가로 반폭·앞·뒤 깊이·앞뒤 중심)을 잇는 로프트 — 몸통·머리용</summary>
    sealed class Loft : Prim
    {
        const int N = 192;
        readonly float _y0, _y1;
        readonly float[] _rx = new float[N], _f = new float[N], _b = new float[N], _cz = new float[N];

        public Loft((float Y, float Rx, float F, float B, float Cz)[] s)
        {
            _y0 = s[0].Y; _y1 = s[^1].Y;
            for (var n = 0; n < N; n++)
            {
                var y = M.Lerp(_y0, _y1, n / (N - 1f));
                var i = 0;
                while (i < s.Length - 2 && s[i + 1].Y < y) i++;
                var t = M.Clamp((y - s[i].Y) / (s[i + 1].Y - s[i].Y), 0, 1);
                var (p0, p1, p2, p3) = (s[Math.Max(i - 1, 0)], s[i], s[i + 1], s[Math.Min(i + 2, s.Length - 1)]);
                float C(float a, float b, float c, float d) => Catmull(a, b, c, d, t);
                _rx[n] = M.Max(C(p0.Rx, p1.Rx, p2.Rx, p3.Rx), 0.002f);
                _f[n]  = M.Max(C(p0.F,  p1.F,  p2.F,  p3.F),  0.002f);
                _b[n]  = M.Max(C(p0.B,  p1.B,  p2.B,  p3.B),  0.002f);
                _cz[n] = C(p0.Cz, p1.Cz, p2.Cz, p3.Cz);
            }
        }

        static float Catmull(float p0, float p1, float p2, float p3, float t)
        {
            var t2 = t * t; var t3 = t2 * t;
            return 0.5f * (2 * p1 + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t2 + (-p0 + 3 * p1 - 3 * p2 + p3) * t3);
        }

        public override float Distance(Vector3 p)
        {
            var u  = M.Clamp((p.Y - _y0) / (_y1 - _y0), 0, 1) * (N - 1);
            var i  = Math.Min((int)u, N - 2);
            var t  = u - i;
            var rx = M.Lerp(_rx[i], _rx[i + 1], t);
            var cz = M.Lerp(_cz[i], _cz[i + 1], t);
            var dx = p.X;
            var dz = p.Z - cz;
            var rz = dz >= 0 ? M.Lerp(_f[i], _f[i + 1], t) : M.Lerp(_b[i], _b[i + 1], t);
            var len = M.Sqrt(dx * dx + dz * dz);
            var e   = M.Sqrt(dx * dx / (rx * rx) + dz * dz / (rz * rz));
            var dr  = e > 1e-5f ? len - len / e : -M.Min(rx, rz);
            var over = M.Max(_y0 - p.Y, p.Y - _y1);
            return over > 0 ? M.Sqrt(M.Max(dr, 0) * M.Max(dr, 0) + over * over) : dr;
        }

        protected override Aabb Box()
        {
            var r = 0f; var zmin = 0f; var zmax = 0f;
            for (var n = 0; n < N; n++)
            {
                r = M.Max(r, _rx[n]);
                zmin = M.Min(zmin, _cz[n] - _b[n]);
                zmax = M.Max(zmax, _cz[n] + _f[n]);
            }
            return new Aabb(new Vector3(-r, _y0, zmin), new Vector3(r, _y1, zmax));
        }
    }

    static float Smin(float a, float b, float k)
    {
        if (k <= 0) return M.Min(a, b);
        var h = M.Max(k - M.Abs(a - b), 0) / k;
        return M.Min(a, b) - h * h * k * 0.25f;
    }

    // ─── 체형 설계 ───────────────────────────────────────────────────────

    static BodyMesh BuildGeometry(bool male)
    {
        var prims = new List<Prim>();
        float G(float female, float m) => male ? m : female;
        static Vector3 V(float x, float y, float z) => new(x, y, z);

        static bool IsArm(Bone b) => b is Bone.UpperL or Bone.UpperR or Bone.ForeL or Bone.ForeR or Bone.HandL or Bone.HandR;
        void Ell(Bone b, Vector3 c, Vector3 r, float blend, Quaternion? rot = null)
            => prims.Add(new Ellipsoid(c, r, rot ?? Quaternion.Identity) { Bone = b, Blend = blend, Arm = IsArm(b) }.Done());
        void Cap(Bone b, Vector3 a, Vector3 e, float ra, float rb, float blend)
            => prims.Add(new Cone(a, e, ra, rb) { Bone = b, Blend = blend, Arm = IsArm(b) }.Done());

        // ── 몸통 ─────────────────────────────────────────────
        (float, float, float, float, float)[] torso = male
            ? [
                (0.815f, 0.040f, 0.035f, 0.035f,  0.000f),
                (0.855f, 0.110f, 0.086f, 0.075f,  0.000f),
                (0.910f, 0.155f, 0.096f, 0.098f,  0.000f),
                (0.960f, 0.160f, 0.099f, 0.104f, -0.005f),
                (1.010f, 0.154f, 0.100f, 0.100f, -0.005f),
                (1.060f, 0.145f, 0.100f, 0.092f,  0.000f),
                (1.110f, 0.146f, 0.101f, 0.090f,  0.000f),
                (1.170f, 0.160f, 0.103f, 0.094f,  0.000f),
                (1.230f, 0.180f, 0.105f, 0.102f,  0.000f),
                (1.290f, 0.198f, 0.108f, 0.108f,  0.000f),
                (1.350f, 0.212f, 0.106f, 0.110f,  0.000f),
                (1.410f, 0.212f, 0.098f, 0.108f, -0.005f),
                (1.460f, 0.196f, 0.080f, 0.098f, -0.010f),
                (1.500f, 0.150f, 0.064f, 0.082f, -0.012f),
                (1.535f, 0.085f, 0.055f, 0.066f, -0.012f),
                (1.560f, 0.060f, 0.050f, 0.055f, -0.012f),
            ]
            : [
                (0.750f, 0.030f, 0.030f, 0.030f,  0.000f),
                (0.790f, 0.100f, 0.080f, 0.070f,  0.000f),
                (0.840f, 0.158f, 0.088f, 0.092f,  0.000f),
                (0.890f, 0.182f, 0.090f, 0.104f, -0.005f),
                (0.940f, 0.170f, 0.088f, 0.100f, -0.005f),
                (0.990f, 0.150f, 0.085f, 0.088f, -0.005f),
                (1.040f, 0.126f, 0.082f, 0.072f,  0.000f),
                (1.075f, 0.112f, 0.080f, 0.066f,  0.000f),
                (1.120f, 0.124f, 0.081f, 0.070f,  0.000f),
                (1.180f, 0.137f, 0.084f, 0.080f,  0.000f),
                (1.240f, 0.142f, 0.087f, 0.082f,  0.000f),
                (1.300f, 0.145f, 0.083f, 0.080f,  0.000f),
                (1.350f, 0.142f, 0.071f, 0.074f,  0.000f),
                (1.390f, 0.128f, 0.058f, 0.066f, -0.005f),
                (1.425f, 0.085f, 0.046f, 0.052f, -0.008f),
                (1.450f, 0.045f, 0.036f, 0.040f, -0.008f),
            ];
        prims.Add(new Loft(torso) { Bone = Bone.Root }.Done());
        Ell(Bone.Root, V(0, G(0.790f, 0.855f), 0.004f), V(G(0.060f, 0.066f), G(0.050f, 0.050f), G(0.058f, 0.064f)), 0.045f); // 샅

        // ── 목·머리 (얼굴 없는 달걀형) ─────────────────────────
        Cap(Bone.Head, V(0, G(1.420f, 1.520f), G(-0.012f, -0.015f)), V(0, G(1.590f, 1.730f), -0.004f),
            G(0.040f, 0.072f), G(0.033f, 0.056f), G(0.025f, 0.035f));
        (float, float, float, float, float)[] head = male
            ? [
                (1.638f, 0.012f, 0.012f, 0.012f,  0.024f),
                (1.655f, 0.042f, 0.038f, 0.034f,  0.020f),
                (1.680f, 0.062f, 0.060f, 0.062f,  0.012f),
                (1.710f, 0.073f, 0.072f, 0.080f,  0.006f),
                (1.750f, 0.078f, 0.083f, 0.096f, -0.002f),
                (1.790f, 0.080f, 0.085f, 0.099f, -0.006f),
                (1.825f, 0.075f, 0.078f, 0.090f, -0.008f),
                (1.850f, 0.062f, 0.064f, 0.074f, -0.008f),
                (1.868f, 0.040f, 0.042f, 0.048f, -0.006f),
                (1.876f, 0.008f, 0.010f, 0.010f, -0.004f),
            ]
            : [
                (1.535f, 0.012f, 0.012f, 0.012f,  0.028f),
                (1.548f, 0.036f, 0.034f, 0.030f,  0.026f),
                (1.570f, 0.052f, 0.052f, 0.055f,  0.016f),
                (1.600f, 0.064f, 0.068f, 0.075f,  0.006f),
                (1.640f, 0.073f, 0.080f, 0.092f, -0.002f),
                (1.680f, 0.076f, 0.082f, 0.096f, -0.006f),
                (1.715f, 0.072f, 0.076f, 0.088f, -0.008f),
                (1.740f, 0.060f, 0.062f, 0.072f, -0.008f),
                (1.758f, 0.038f, 0.040f, 0.046f, -0.006f),
                (1.766f, 0.008f, 0.010f, 0.010f, -0.004f),
            ];
        prims.Add(new Loft(head) { Bone = Bone.Head, Blend = G(0.03f, 0.06f) }.Done());

        // ── 성별 근육·볼륨 (몸통 뼈) ───────────────────────────
        foreach (var sd in new[] { 1f, -1f })
        {
            var yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, sd * 0.28f);
            if (male)
            {
                Ell(Bone.Root, V(sd * 0.085f, 1.365f, 0.078f), V(0.090f, 0.064f, 0.046f), 0.026f,
                    yaw * Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.12f));                      // 대흉근
                foreach (var (ay, az) in new[] { (1.255f, 0.095f), (1.190f, 0.093f), (1.125f, 0.091f), (1.064f, 0.089f) })
                    Ell(Bone.Root, V(sd * 0.036f, ay, az), V(0.029f, 0.028f, 0.016f), 0.022f);        // 복근 8칸
                Ell(Bone.Root, V(sd * 0.145f, 1.300f, -0.055f), V(0.070f, 0.150f, 0.055f), 0.040f,
                    Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -sd * 0.18f));                       // 광배근
                Ell(Bone.Root, V(sd * 0.036f, 1.180f, -0.078f), V(0.030f, 0.120f, 0.028f), 0.035f);  // 척추기립근
                Ell(Bone.Root, V(sd * 0.075f, 0.965f, -0.070f), V(0.085f, 0.085f, 0.070f), 0.035f);  // 대둔근
                Cap(Bone.Root, V(sd * 0.030f, 1.585f, -0.030f), V(sd * 0.185f, 1.505f, -0.020f), 0.058f, 0.046f, 0.050f); // 승모근
            }
            else
            {
                Ell(Bone.Root, V(sd * 0.074f, 1.246f, 0.070f), V(0.071f, 0.069f, 0.066f), 0.042f,
                    yaw * Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.18f));                   // 가슴
                Ell(Bone.Root, V(sd * 0.072f, 0.905f, -0.058f), V(0.088f, 0.092f, 0.075f), 0.035f);    // 둔부
                Cap(Bone.Root, V(sd * 0.035f, 1.445f, -0.012f), V(sd * 0.150f, 1.405f, -0.008f), 0.030f, 0.036f, 0.030f); // 쇄골·어깨선
            }
        }

        if (male)   // 승모근 아래쪽 마름모
            Ell(Bone.Root, V(0, 1.420f, -0.080f), V(0.075f, 0.120f, 0.032f), 0.045f);
        if (male)   // 복근 좌우 사이 백선
            prims.Add(new Cone(V(0, 1.030f, 0.106f), V(0, 1.300f, 0.114f), 0.008f, 0.008f) { Bone = Bone.Root, Blend = 0.014f, Carve = true }.Done());
        prims.Add(new Ellipsoid(V(0, G(1.035f, 1.030f), G(0.084f, 0.104f)), V(0.006f, 0.008f, 0.006f), Quaternion.Identity)
            { Bone = Bone.Root, Blend = 0.008f, Carve = true }.Done());                                       // 배꼽

        // ── 팔·손 ─────────────────────────────────────────────
        var joints = new Vector3[BodyMesh.BoneCount];
        joints[(int)Bone.Root] = V(0, G(0.855f, 0.925f), 0);
        joints[(int)Bone.Head] = V(0, G(1.440f, 1.545f), -0.010f);
        var arms = new List<(float Sd, Vector3 S, Vector3 E, Vector3 W)>();

        foreach (var sd in new[] { 1f, -1f })
        {
            // 캐릭터 기준 왼쪽 = +X (앞이 +Z)
            var L = sd > 0;
            Bone Pick(Bone left, Bone right) => L ? left : right;
            var bU = Pick(Bone.UpperL, Bone.UpperR);
            var bF = Pick(Bone.ForeL,  Bone.ForeR);
            var bH = Pick(Bone.HandL,  Bone.HandR);

            var S = V(sd * G(0.153f, 0.198f), G(1.385f, 1.465f), G(-0.012f, -0.015f));
            var E = V(sd * G(0.188f, 0.245f), G(1.095f, 1.170f), G(-0.030f, -0.035f));
            var W = V(sd * G(0.200f, 0.262f), G(0.855f, 0.920f), -0.008f);
            joints[(int)bU] = S; joints[(int)bF] = E; joints[(int)bH] = W;
            arms.Add((sd, S, E, W));

            Ell(bU, V(sd * G(0.159f, 0.208f), G(1.360f, 1.430f), -0.010f),
                V(G(0.044f, 0.066f), G(0.080f, 0.112f), G(0.046f, 0.068f)), G(0.045f, 0.055f), AlongY(E - S));                  // 삼각근
            Cap(bU, S, E, G(0.038f, 0.060f), G(0.030f, 0.044f), 0.035f);
            if (male)
            {
                Ell(bU, Vector3.Lerp(S, E, 0.55f) + V(0, 0, 0.022f), V(0.052f, 0.088f, 0.054f), 0.026f, AlongY(E - S)); // 이두
                Ell(bU, Vector3.Lerp(S, E, 0.40f) + V(0, 0, -0.025f), V(0.052f, 0.100f, 0.050f), 0.026f, AlongY(E - S)); // 삼두
            }
            else
                Ell(bU, Vector3.Lerp(S, E, 0.45f) + V(0, 0, -0.010f), V(0.033f, 0.075f, 0.035f), 0.020f, AlongY(E - S));

            Cap(bF, E, W, G(0.031f, 0.054f), G(0.020f, 0.030f), 0.015f);
            Ell(bF, Vector3.Lerp(E, W, 0.22f) + V(sd * G(0.003f, 0.008f), 0, G(0.004f, 0.008f)),
                V(G(0.031f, 0.047f), G(0.060f, 0.080f), G(0.030f, 0.044f)), 0.024f, AlongY(W - E));  // 전완근

            AddHand(bH, W, sd, G(1f, 1.13f));

            // ── 다리·발 ───────────────────────────────────────
            var bT = Pick(Bone.ThighL, Bone.ThighR);
            var bS = Pick(Bone.ShinL,  Bone.ShinR);
            var bP = Pick(Bone.FootL,  Bone.FootR);
            var H = V(sd * G(0.088f, 0.098f), G(0.855f, 0.925f), 0);
            var K = V(sd * G(0.072f, 0.092f), G(0.475f, 0.505f), G(0.008f, 0.010f));
            var A = V(sd * G(0.070f, 0.088f), G(0.085f, 0.090f), -0.012f);
            joints[(int)bT] = H; joints[(int)bS] = K; joints[(int)bP] = A;

            var Tm = Vector3.Lerp(H, K, 0.45f) + V(0, 0, G(0.004f, 0.008f));
            Cap(bT, V(sd * G(0.086f, 0.094f), G(0.840f, 0.900f), -0.006f), Tm, G(0.090f, 0.108f), G(0.076f, 0.094f), 0.060f);
            Cap(bT, Tm, K + V(0, 0, 0.008f), G(0.076f, 0.094f), G(0.046f, 0.056f), 0.040f);
            if (male)
            {
                Ell(bT, V(sd * 0.100f, 0.700f, 0.040f), V(0.090f, 0.175f, 0.072f), 0.040f);  // 대퇴직근
                Ell(bT, V(sd * 0.068f, 0.565f, 0.035f), V(0.040f, 0.065f, 0.040f), 0.025f);  // 내측광근
                Ell(bT, V(sd * 0.135f, 0.730f, 0.000f), V(0.045f, 0.130f, 0.060f), 0.035f);  // 외측광근
                Ell(bT, V(sd * 0.095f, 0.720f, -0.040f), V(0.060f, 0.150f, 0.050f), 0.035f); // 햄스트링
            }
            else
                Ell(bT, V(sd * 0.106f, 0.740f, -0.005f), V(0.076f, 0.150f, 0.082f), 0.045f); // 허벅지 바깥 곡선

            Ell(bS, K + V(0, 0.004f, 0.014f), V(G(0.036f, 0.042f), G(0.042f, 0.048f), G(0.034f, 0.040f)), 0.030f); // 무릎
            Cap(bS, K + V(0, 0, 0.008f), A, G(0.043f, 0.049f), G(0.027f, 0.031f), 0.025f);
            if (male)
            {
                Ell(bS, V(sd * 0.074f, 0.375f, -0.035f), V(0.044f, 0.095f, 0.046f), 0.030f); // 비복근 안쪽
                Ell(bS, V(sd * 0.108f, 0.390f, -0.030f), V(0.038f, 0.085f, 0.040f), 0.030f); // 비복근 바깥
                Ell(bS, V(sd * 0.098f, 0.360f, 0.020f), V(0.030f, 0.100f, 0.030f), 0.025f);  // 전경골근
            }
            else
                Ell(bS, V(sd * 0.074f, 0.335f, -0.022f), V(0.045f, 0.100f, 0.046f), 0.035f); // 종아리

            AddFoot(bP, A, sd, G(1f, 1.12f));
        }

        void AddHand(Bone b, Vector3 w, float sd, float k)
        {
            var inward = -sd;                         // 손바닥이 허벅지 쪽을 향한다
            Cap(b, w, w + V(0, -0.02f * k, 0.002f), 0.020f * k, 0.019f * k, 0.012f);
            Ell(b, w + V(inward * 0.002f, -0.046f * k, 0.004f), V(0.0135f, 0.043f, 0.036f) * k, 0.012f); // 손바닥
            // 검지 → 새끼: 손가락 뿌리 위치(z), 길이, 반지름
            (float Z, float Len, float R)[] fingers =
                [(0.022f, 0.072f, 0.0080f), (0.007f, 0.080f, 0.0083f), (-0.008f, 0.074f, 0.0079f), (-0.022f, 0.058f, 0.0070f)];
            foreach (var (fz, len, r) in fingers)
            {
                var kn  = w + V(inward * 0.001f, -0.084f * k, fz * k);
                var mid = kn  + V(inward * 0.004f * k, -len * 0.52f * k, fz * 0.30f * k + len * 0.06f * k);
                var tip = mid + V(inward * 0.009f * k, -len * 0.44f * k, fz * 0.25f * k + len * 0.16f * k);
                Cap(b, kn,  mid, r * k, r * 0.92f * k, 0.006f);
                Cap(b, mid, tip, r * 0.92f * k, r * 0.80f * k, 0.003f);
            }
            var t0 = w + V(inward * 0.008f * k, -0.028f * k, 0.026f * k);
            var t1 = t0 + V(inward * 0.010f * k, -0.030f * k, 0.016f * k);
            var t2 = t1 + V(inward * 0.008f * k, -0.026f * k, 0.008f * k);
            Cap(b, t0, t1, 0.0115f * k, 0.0095f * k, 0.010f);                     // 엄지
            Cap(b, t1, t2, 0.0095f * k, 0.0080f * k, 0.003f);
        }

        void AddFoot(Bone b, Vector3 a, float sd, float k)
        {
            Ell(b, a + V(0, 0, 0.004f), V(0.026f, 0.030f, 0.027f) * k, 0.018f);                // 발목
            var x = a.X;
            Ell(b, V(x, 0.032f, a.Z - 0.035f * k), V(0.030f, 0.032f, 0.040f) * k, 0.020f);     // 뒤꿈치
            Ell(b, V(x + sd * 0.004f, 0.036f, a.Z + 0.050f * k), V(0.036f, 0.030f, 0.080f) * k, 0.022f); // 발등
            Ell(b, V(x + sd * 0.004f, 0.022f, a.Z + 0.122f * k), V(0.042f, 0.022f, 0.030f) * k, 0.018f); // 볼
            // 엄지발가락(안쪽) → 새끼발가락(바깥)
            (float X, float Tip, float R)[] toes =
                [(-0.024f, 0.195f, 0.0125f), (-0.006f, 0.185f, 0.0090f), (0.008f, 0.176f, 0.0085f), (0.020f, 0.166f, 0.0080f), (0.030f, 0.156f, 0.0075f)];
            foreach (var (tx, tip, r) in toes)
            {
                var root = V(x + sd * tx * k, 0.020f, a.Z + 0.140f * k);
                var end  = V(x + sd * tx * 1.08f * k, r * k, a.Z + tip * k);
                Cap(b, root, end, r * 1.05f * k, r * k, 0.006f);
            }
        }

        // ── 메시화 ───────────────────────────────────────────
        var all = prims.ToArray();
        var armJoinY = G(1.385f, 1.465f) - 0.16f;
        float Field(Vector3 p)
        {
            float core = 1e3f, arm = 1e3f;
            foreach (var sh in all)
            {
                if (!sh.Bounds.HasPoint(p)) continue;
                if (sh.Arm)        arm  = Smin(arm, sh.Distance(p), sh.Blend);
                else if (sh.Carve) core = -Smin(-core, sh.Distance(p), sh.Blend);
                else               core = Smin(core, sh.Distance(p), sh.Blend);
            }
            return Smin(core, arm, 0.045f * M.Clamp((p.Y - armJoinY) / 0.08f, 0, 1));
        }

        var geo = new BodyMesh();
        var box = all.Select(s => s.Bounds).Aggregate(Aabb.Merge);
        (geo.Vertices, geo.Normals, geo.Indices) = SurfaceNets(Field, box, Voxel);

        // 발바닥을 y = 0 에 붙인다
        var lift = -geo.Vertices.Min(v => v.Y);
        for (var v = 0; v < geo.Vertices.Length; v++) geo.Vertices[v].Y += lift;
        for (var j = 0; j < joints.Length; j++) joints[j].Y += lift;
        foreach (var sh in all) sh.Bounds = new Aabb(sh.Bounds.Min + V(0, lift, 0), sh.Bounds.Max + V(0, lift, 0));
        (geo.Bones, geo.Weights) = SkinWeights(geo.Vertices, all, V(0, lift, 0));

        // 외피 이음선: 평면과 몸 표면이 만나는 곡선 (레퍼런스의 어깨·하이컷·목·손목 선)
        float SeamAt(Vector3 p, Bone owner)
        {
            var d = 1f;
            void Cut(Vector3 o, Vector3 n, bool region)
            {
                if (!region) return;
                var s = Vector3.Dot(p - o, Vector3.Normalize(n));
                if (M.Abs(s) < M.Abs(d)) d = s;
            }
            var torsoOrLeg = owner is Bone.Root or Bone.ThighL or Bone.ThighR;
            foreach (var (sd, S, E, W) in arms)
            {
                if (p.X * sd <= 0) continue;
                var top = V(sd * 0.150f, G(1.000f, 1.050f), 0);
                var bot = V(sd * 0.030f, G(0.790f, 0.855f), 0);
                Cut(top, Vector3.Cross(bot - top, Vector3.UnitZ), torsoOrLeg && p.Y > bot.Y - 0.01f && p.Y < top.Y + 0.02f);
                var dir = Vector3.Normalize(W - E);
                Cut(W + dir * 0.004f, dir, owner is Bone.ForeL or Bone.ForeR or Bone.HandL or Bone.HandR && Vector3.Distance(p, W) < 0.045f);
                if (!male)
                    Cut(S + V(-sd * 0.022f, 0, 0), V(sd, -0.45f, 0),
                        owner is Bone.Root or Bone.UpperL or Bone.UpperR && p.Y > S.Y - 0.085f && p.Y < S.Y + 0.08f && M.Abs(p.X) > 0.09f);
            }
            if (!male)
                Cut(V(0, 1.555f, 0), V(0, 1, -0.4f), p.Y > 1.50f && p.Y < 1.62f && p.X * p.X + p.Z * p.Z < 0.0036f);
            return d;
        }
        geo.Seams = new float[geo.Vertices.Length];
        for (var v = 0; v < geo.Vertices.Length; v++)
            geo.Seams[v] = SeamAt(geo.Vertices[v] - V(0, lift, 0), (Bone)geo.Bones[v * 4]);

        // 뼈: 모두 쉼 회전 없음, 위치만
        int[] parent = [-1, 0, 0, 0, (int)Bone.UpperR, (int)Bone.UpperL, (int)Bone.ForeR, (int)Bone.ForeL,
                        0, 0, (int)Bone.ThighR, (int)Bone.ThighL, (int)Bone.ShinR, (int)Bone.ShinL];
        for (var b = 0; b < BodyMesh.BoneCount; b++)
        {
            geo.Parent[b] = parent[b];
            geo.Global[b] = Transform3D.FromTranslation(joints[b]);
            geo.Rest[b]   = Transform3D.FromTranslation(parent[b] < 0 ? joints[b] : joints[b] - joints[parent[b]]);
        }
        return geo;
    }

    // 기본 Y 축을 주어진 방향으로 돌리는 회전 (팔 근육 타원체를 뼈 방향에 맞춘다)
    static Quaternion AlongY(Vector3 dir)
    {
        var d = Vector3.Normalize(dir);
        var down = -Vector3.UnitY;
        var axis = Vector3.Cross(down, d);
        var s = axis.Length();
        if (s < 1e-6f) return Quaternion.Identity;
        return Quaternion.CreateFromAxisAngle(axis / s, M.Atan2(s, Vector3.Dot(down, d)));
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

        // 크롬은 면 각짐이 그대로 비치므로 정점을 등위면 위로 한 번 더 끌어당긴다
        var vs = verts.ToArray();
        var normals = new Vector3[vs.Length];
        Parallel.For(0, vs.Length, v =>
        {
            var p = vs[v]; var e = h * 0.5f;
            for (var it = 0; it < 2; it++)
            {
                var gr = new Vector3(f(p + new Vector3(e,0,0)) - f(p - new Vector3(e,0,0)),
                                     f(p + new Vector3(0,e,0)) - f(p - new Vector3(0,e,0)),
                                     f(p + new Vector3(0,0,e)) - f(p - new Vector3(0,0,e))) / (2 * e);
                var g2 = gr.LengthSquared();
                if (g2 < 1e-10f) break;
                var step = f(p) * gr / g2;
                if (step.Length() > h) break;
                p -= step;
                normals[v] = Vector3.Normalize(gr);
            }
            vs[v] = p;
            if (normals[v] == Vector3.Zero) normals[v] = Vector3.UnitY;
        });

        var idx = new List<int>();
        void Quad(int a, int b, int c, int d)
        {
            if (a < 0 || b < 0 || c < 0 || d < 0) return;
            var nrm   = normals[a] + normals[b] + normals[c] + normals[d];
            var cross = Vector3.Cross(vs[b] - vs[a], vs[c] - vs[a]);
            if (Vector3.Dot(cross, nrm) > 0) (b, d) = (d, b);
            idx.AddRange([a, b, c, a, c, d]);
        }
        for (var kk = 1; kk < nz - 1; kk++)
        for (var j  = 1; j  < ny - 1; j++)
        for (var i  = 1; i  < nx - 1; i++)
        {
            var v0 = grid[Id(i,j,kk)] < 0;
            if (v0 != grid[Id(i+1,j,kk)] < 0) Quad(cell[Id(i,j-1,kk-1)],cell[Id(i,j,kk-1)],cell[Id(i,j,kk)],cell[Id(i,j-1,kk)]);
            if (v0 != grid[Id(i,j+1,kk)] < 0) Quad(cell[Id(i-1,j,kk-1)],cell[Id(i,j,kk-1)],cell[Id(i,j,kk)],cell[Id(i-1,j,kk)]);
            if (v0 != grid[Id(i,j,kk+1)] < 0) Quad(cell[Id(i-1,j-1,kk)],cell[Id(i,j-1,kk)],cell[Id(i,j,kk)],cell[Id(i-1,j,kk)]);
        }
        return (vs, normals, idx.ToArray());
    }

    /// <summary>
    /// 정점마다 어느 원형(뼈) 표면에 가장 가까운지로 가중치를 매긴다.
    /// 몸통 옆면이 팔에 끌려가는 거리 기반 방식의 문제를 피한다.
    /// </summary>
    static (int[] Bones, float[] Weights) SkinWeights(Vector3[] verts, Prim[] prims, Vector3 lift)
    {
        const float falloff = 0.008f;
        var bones   = new int[verts.Length * 4];
        var weights = new float[verts.Length * 4];
        Parallel.For(0, verts.Length, v =>
        {
            Span<float> d = stackalloc float[BodyMesh.BoneCount];
            d.Fill(float.MaxValue);
            var p = verts[v] - lift;                  // 원형은 들어올리기 전 좌표
            var pw = verts[v];
            foreach (var pr in prims)
            {
                if (pr.Carve || !pr.Bounds.HasPoint(pw)) continue;
                var b = (int)pr.Bone;
                d[b] = M.Min(d[b], pr.Distance(p));
            }
            var min = float.MaxValue;
            for (var b = 0; b < d.Length; b++) min = M.Min(min, d[b]);

            Span<int>   best = stackalloc int[4];
            Span<float> w    = stackalloc float[4];
            for (var s = 0; s < 4; s++) { best[s] = 0; w[s] = 0; }
            for (var b = 0; b < d.Length; b++)
            {
                if (d[b] == float.MaxValue) continue;
                var wb = M.Exp(-(d[b] - min) / falloff);
                for (var s = 0; s < 4; s++)
                {
                    if (wb <= w[s]) continue;
                    for (var u = 3; u > s; u--) { w[u] = w[u-1]; best[u] = best[u-1]; }
                    w[s] = wb; best[s] = b; break;
                }
            }
            var total = w[0] + w[1] + w[2] + w[3];
            if (total <= 0) { w[0] = 1; total = 1; }
            for (var s = 0; s < 4; s++) { bones[v*4+s] = best[s]; weights[v*4+s] = w[s] / total; }
        });
        return (bones, weights);
    }
}
