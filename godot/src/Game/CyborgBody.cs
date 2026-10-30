using Godot;
using DollyForge.Core;

namespace DollyForge.Game;

/// <summary>
/// 전신 티타늄 인형. 경계 없는 하나의 연속 곡면: 몸통·머리·팔·다리·발의 단면을 거리장으로 만들어 부드럽게 합치고(smooth union),
/// 한 메시로 뽑아 뼈대에 스키닝한다. 관절을 굽혀도 곡면이 끊기지 않는다. 얼굴·문양·발광 표식은 없다.
/// 손가락은 곡면 격자보다 가늘어서 손만 따로 만들어 손목에 겹쳐 붙인다.
/// </summary>
public partial class CyborgBody : Node3D
{
    // ------------------------------------------------------------------ 로프트

    record struct Sec(float Y, float Rx, float F, float B, float Cx = 0, float Cz = 0, float Sq = 1, float Bust = 0, float Glute = 0, float Pec = 0)
    {
        public Sec Scale(float k) => this with { Rx = Rx * k, F = F * k, B = B * k };
    }

    static float Catmull(float p0, float p1, float p2, float p3, float t)
    {
        var t2 = t * t;
        var t3 = t2 * t;
        return 0.5f * (2 * p1 + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t2 + (-p0 + 3 * p1 - 3 * p2 + p3) * t3);
    }

    static Sec SectionAt(Sec[] s, int i, float t)
    {
        var (a, b, c, d) = (s[Math.Max(i - 1, 0)], s[i], s[i + 1], s[Math.Min(i + 2, s.Length - 1)]);
        // 구간마다 잘라 내면(clamp) 단면 경계에서 기울기가 끊겨 띠 모양 굴곡이 생긴다. 그대로 이어 C1 연속을 지킨다.
        float C(Func<Sec, float> f) => Catmull(f(a), f(b), f(c), f(d), t);
        return new Sec(C(x => x.Y), Mathf.Max(C(x => x.Rx), 0.001f), Mathf.Max(C(x => x.F), 0.001f), Mathf.Max(C(x => x.B), 0.001f),
            C(x => x.Cx), C(x => x.Cz), C(x => x.Sq), Mathf.Max(C(x => x.Bust), 0), Mathf.Max(C(x => x.Glute), 0), Mathf.Max(C(x => x.Pec), 0));
    }

    static float AngDiff(float a, float b) => Mathf.Atan2(Mathf.Sin(a - b), Mathf.Cos(a - b));
    static float Bump(float th, float center, float width) => Mathf.Exp(-Mathf.Pow(AngDiff(th, center) / width, 2));
    static float SPow(float v, float e) => Mathf.Sign(v) * Mathf.Pow(Mathf.Abs(v), 1 / e);

    /// <summary>단면 위의 점. θ = π/2 가 정면(+Z).</summary>
    static Vector3 RingPoint(Sec s, float th)
    {
        float c = Mathf.Cos(th), sn = Mathf.Sin(th);
        var x = s.Cx + s.Rx * SPow(c, s.Sq);
        var z = s.Cz + (sn >= 0 ? s.F : s.B) * SPow(sn, s.Sq);
        foreach (var side in new[] { -1f, 1f })
        {
            if (s.Bust > 0)
            {
                var g = Bump(th, Mathf.Pi / 2 + side * 0.52f, 0.40f);
                z += s.Bust * g;
                x += s.Bust * g * c * 0.28f;
            }
            if (s.Pec > 0) z += s.Pec * Bump(th, Mathf.Pi / 2 + side * 0.60f, 0.48f);
            if (s.Glute > 0)
            {
                var g = Bump(th, -Mathf.Pi / 2 + side * 0.52f, 0.48f);
                z -= s.Glute * g;
                x += s.Glute * g * c * 0.22f;
            }
        }
        return new Vector3(x, s.Y, z);
    }

    static ArrayMesh Loft(Sec[] s, int radial = 48, int perSpan = 6)
    {
        var rings = new List<Sec>();
        for (var i = 0; i < s.Length - 1; i++)
            for (var j = 0; j < perSpan; j++) rings.Add(SectionAt(s, i, (float)j / perSpan));
        rings.Add(s[^1]);

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        st.SetSmoothGroup(0);
        var pts = new List<Vector3>();
        foreach (var r in rings)
            for (var k = 0; k < radial; k++) pts.Add(RingPoint(r, k * Mathf.Tau / radial));
        foreach (var p in pts) st.AddVertex(p);
        void Tri(int a, int b, int c) { st.AddIndex(a); st.AddIndex(c); st.AddIndex(b); }
        for (var r = 0; r < rings.Count - 1; r++)
        for (var k = 0; k < radial; k++)
        {
            int a = r * radial + k, b = r * radial + (k + 1) % radial, c = a + radial, d = b + radial;
            Tri(a, c, b);
            Tri(b, c, d);
        }
        var next = pts.Count;
        void Cap(int r, bool top)
        {
            var center = Vector3.Zero;
            for (var k = 0; k < radial; k++) center += pts[r * radial + k];
            st.AddVertex(center / radial);
            var ci = next++;
            for (var k = 0; k < radial; k++)
            {
                int a = r * radial + k, b = r * radial + (k + 1) % radial;
                if (top) Tri(ci, b, a); else Tri(ci, a, b);
            }
        }
        Cap(0, false);
        Cap(rings.Count - 1, true);
        st.GenerateNormals();
        return st.Commit();
    }

    // ------------------------------------------------------------------ 체형 (m)

    sealed record Proportions(
        Sec[] Torso, float Sq, (float Y0, float Y1, float R) Neck, (float Base, float Scale) Head,
        (float X, float Y) Shoulder, (float Upper, float Fore, float R, float Bicep) Arm,
        (float X, float Y) Hip, (float Thigh, float Shin, float R, float Q) Leg, float Foot, float WaistY,
        float UnderbustY, float LowHipY);

    // 여성형: 성체 키 1.80 m. 극적인 모래시계 — 넓은 골반·극세 허리·풍성한 가슴·둥근 둔부·가는 팔다리.
    static readonly Proportions Female = new(
        [
            new(0.70f,  0.0297f, 0.030f, 0.035f, Cz: -0.004f),   // 가랑이
            new(0.725f, 0.0839f, 0.052f, 0.058f, Cz: -0.004f),
            new(0.76f,  0.1440f, 0.073f, 0.079f, Glute: 0.010f),
            new(0.80f,  0.1780f, 0.089f, 0.097f, Glute: 0.024f),
            new(0.83f,  0.2000f, 0.097f, 0.112f, Glute: 0.038f),  // 골반 최대
            new(0.88f,  0.1960f, 0.101f, 0.118f, Glute: 0.038f),
            new(0.945f, 0.1720f, 0.095f, 0.108f, Glute: 0.020f),
            new(0.99f,  0.1360f, 0.080f, 0.088f, Glute: 0.008f),
            new(1.05f,  0.0880f, 0.062f, 0.060f),                  // 극세 허리
            new(1.11f,  0.1080f, 0.071f, 0.069f),
            new(1.16f,  0.1300f, 0.079f, 0.077f, Bust: 0.022f),
            new(1.22f,  0.1530f, 0.085f, 0.083f, Bust: 0.050f),
            new(1.265f, 0.1570f, 0.084f, 0.083f, Bust: 0.048f),
            new(1.31f,  0.1550f, 0.073f, 0.077f, Bust: 0.018f),
            new(1.35f,  0.1480f, 0.056f, 0.066f),                  // 쇄골 라인
            new(1.385f, 0.0930f, 0.043f, 0.049f),
            new(1.4f,   0.0481f, 0.037f, 0.037f),
        ],
        1.0f, (1.36f, 1.5f, 0.040f), (1.425f, 0.86f), (0.155f, 1.315f), (0.300f, 0.260f, 0.94f, 0.008f),
        (0.086f, 0.84f), (0.46f, 0.44f, 1.06f, 0.045f), 0.228f, 1.05f, 1.16f, 0.94f);

    // 남성형: 성체 키 1.86 m. 역삼각 실루엣 — 넓은 어깨·강한 가슴근육·V-테이퍼 허리·두꺼운 팔다리·탄탄한 둔부.
    static readonly Proportions Male = new(
        [
            new(0.70f,  0.036f,  0.036f, 0.042f),
            new(0.735f, 0.112f,  0.068f, 0.076f),
            new(0.775f, 0.150f,  0.082f, 0.092f, Glute: 0.008f),
            new(0.82f,  0.168f,  0.092f, 0.106f, Glute: 0.016f),
            new(0.86f,  0.182f,  0.104f, 0.118f, Glute: 0.026f),
            new(0.945f, 0.196f,  0.110f, 0.124f, Glute: 0.034f),  // 둔부 최대
            new(0.99f,  0.178f,  0.100f, 0.110f, Glute: 0.022f),
            new(1.06f,  0.140f,  0.090f, 0.088f),                   // V-테이퍼 허리
            new(1.14f,  0.162f,  0.106f, 0.102f),
            new(1.22f,  0.188f,  0.116f, 0.112f),
            new(1.30f,  0.214f,  0.126f, 0.120f, Pec: 0.042f),     // 가슴근육 최대
            new(1.37f,  0.224f,  0.120f, 0.114f, Pec: 0.032f),     // 어깨 최대
            new(1.425f, 0.214f,  0.092f, 0.096f),
            new(1.465f, 0.148f,  0.070f, 0.076f),
            new(1.49f,  0.070f,  0.060f, 0.060f),
        ],
        1.20f, (1.44f, 1.6f, 0.056f), (1.525f, 0.94f), (0.232f, 1.395f), (0.34f, 0.300f, 1.34f, 0.022f),
        (0.094f, 0.9f), (0.50f, 0.48f, 1.18f, 0.02f), 0.26f, 1.06f, 1.22f, 0.98f);

    static readonly Sec[] HeadShell =
    [
        new(0, 0.045f, 0.048f, 0.048f), new(0.018f, 0.065f, 0.068f, 0.068f), new(0.052f, 0.084f, 0.088f, 0.088f),
        new(0.092f, 0.094f, 0.098f, 0.098f), new(0.132f, 0.095f, 0.100f, 0.100f), new(0.170f, 0.087f, 0.092f, 0.092f),
        new(0.204f, 0.068f, 0.074f, 0.074f), new(0.228f, 0.040f, 0.044f, 0.044f), new(0.240f, 0.006f, 0.007f, 0.007f),
    ];

    // ------------------------------------------------------------------ 재질

    static readonly Color StandardTitanium = new(0.68f, 0.71f, 0.74f);
    static readonly Color HeavyTitaniumColor = new(0.52f, 0.59f, 0.63f);
    static readonly Color LightTitaniumColor = new(0.64f, 0.68f, 0.71f);

    static Color VariantColor(ModuleVariant variant) => variant switch
    {
        ModuleVariant.HeavyDuty => HeavyTitaniumColor,
        ModuleVariant.Lite => LightTitaniumColor,
        _ => StandardTitanium,
    };

    static StandardMaterial3D? _skinMat;
    /// <summary>
    /// 몸 곡면: 모듈 종류의 색이 정점 색으로 부드럽게 섞인 새틴 티타늄 (색 경계 없음).
    /// 반사가 넓게 퍼지는 무광에 가까운 새틴이라, 지평선이 곡면 위에 날카로운 선으로 맺히지 않는다.
    /// </summary>
    static StandardMaterial3D SkinMaterial => _skinMat ??= new StandardMaterial3D
    {
        VertexColorUseAsAlbedo = true, Metallic = 0.68f, Roughness = 0.58f,
    };

    static readonly Dictionary<ModuleVariant, StandardMaterial3D> HandMats = new();
    static StandardMaterial3D ModuleTitanium(ModuleVariant variant)
    {
        if (HandMats.TryGetValue(variant, out var m)) return m;
        return HandMats[variant] = new StandardMaterial3D { AlbedoColor = VariantColor(variant), Metallic = 0.68f, Roughness = 0.58f };
    }

    // ------------------------------------------------------------------ 거리장

    static float Smin(float a, float b, float k)
    {
        if (k <= 0) return Mathf.Min(a, b);
        var h = Mathf.Max(k - Mathf.Abs(a - b), 0) / k;
        return Mathf.Min(a, b) - h * h * k * 0.25f;
    }

    /// <summary>
    /// 단면 묶음이 만드는 곡면까지의 거리 (근사, 곡면 위에서 정확히 0). 점의 높이에서 단면을 매끄럽게 보간하고(SectionAt),
    /// 점과 같은 방향에 있는 단면 곡선 위의 점(RingPoint)까지의 반지름과 비교한다. 표를 쓰지 않아 각도·높이 방향으로
    /// 기울기가 꺾이지 않는다 (줄무늬 없음).
    /// </summary>
    sealed class LoftField
    {
        readonly Sec[] _s;
        public readonly float MaxRadius;

        public LoftField(Sec[] s)
        {
            _s = s;
            foreach (var sec in s) MaxRadius = Mathf.Max(MaxRadius, Mathf.Max(sec.Rx, Mathf.Max(sec.F, sec.B)) * 1.2f + Mathf.Abs(sec.Cx) + Mathf.Abs(sec.Cz) + 0.03f);
        }

        public (float Y0, float Y1) Span => (_s[0].Y, _s[^1].Y);

        public float Distance(Vector3 p)
        {
            var (y0, y1) = Span;
            var y = Mathf.Clamp(p.Y, y0, y1);
            var i = 0;
            while (i < _s.Length - 2 && _s[i + 1].Y < y) i++;
            var sec = SectionAt(_s, i, Mathf.Clamp((y - _s[i].Y) / (_s[i + 1].Y - _s[i].Y), 0, 1));
            float dx = p.X - sec.Cx, dz = p.Z - sec.Cz;
            // 초타원 단면에서 점과 같은 방향에 놓이는 매개변수: tan θ = (|dz|/F)^e / (|dx|/Rx)^e
            var fz = dz >= 0 ? sec.F : sec.B;
            var e = sec.Sq;
            var th = Mathf.Atan2(Mathf.Sign(dz) * Mathf.Pow(Mathf.Abs(dz) / fz, e), Mathf.Sign(dx) * Mathf.Pow(Mathf.Abs(dx) / sec.Rx, e));
            var ring = RingPoint(sec, th);
            var rad = new Vector2(ring.X - sec.Cx, ring.Z - sec.Cz).Length();
            var dr = Mathf.Sqrt(dx * dx + dz * dz) - rad;
            var over = Mathf.Max(y0 - p.Y, p.Y - y1);
            return over > 0 ? Mathf.Sqrt(Mathf.Max(dr, 0) * Mathf.Max(dr, 0) + over * over) : dr;
        }
    }

    /// <summary>거리장의 한 조각: 단면 곡면 또는 타원체. 뼈의 쉼 자세 공간에 놓이고, 왼쪽은 로컬 X 를 뒤집어 같은 모양을 쓴다.</summary>
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
            p.X *= Mirror;
            p -= Offset;
            if (Loft is not null) return Loft.Distance(p);
            // 타원체 거리 근사 (경계 안팎에서 부호가 정확)
            var k0 = (p / Radii).Length();
            var k1 = (p / (Radii * Radii)).Length();
            return k1 > 1e-6f ? k0 * (k0 - 1) / k1 : -Mathf.Min(Radii.X, Mathf.Min(Radii.Y, Radii.Z));
        }

        public void ComputeBounds()
        {
            Vector3 lo, hi;
            if (Loft is not null)
            {
                var (y0, y1) = Loft.Span;
                lo = new Vector3(-Loft.MaxRadius, y0, -Loft.MaxRadius) + Offset;
                hi = new Vector3(Loft.MaxRadius, y1, Loft.MaxRadius) + Offset;
            }
            else
            {
                lo = Offset - Radii;
                hi = Offset + Radii;
            }
            var toWorld = WorldToLocal.AffineInverse();
            var box = new Aabb();
            var first = true;
            for (var c = 0; c < 8; c++)
            {
                var q = new Vector3((c & 1) == 0 ? lo.X : hi.X, (c & 2) == 0 ? lo.Y : hi.Y, (c & 4) == 0 ? lo.Z : hi.Z);
                q.X *= Mirror; // 로컬에서 뒤집힌 X 를 되돌린다 (상자는 대칭이 아닐 수 있다)
                var w = toWorld * q;
                box = first ? new Aabb(w, Vector3.Zero) : box.Expand(w);
                first = false;
            }
            Bounds = box.Grow(Blend + 0.02f);
        }
    }

    // ------------------------------------------------------------------ 뼈대

    enum Bone { Root, Head, UpperR, UpperL, ForeR, ForeL, HandR, HandL, ThighR, ThighL, ShinR, ShinL, FootR, FootL, Count }

    static readonly BodySlot[] BoneSlot =
    [
        BodySlot.Torso, BodySlot.Head, BodySlot.UpperArmR, BodySlot.UpperArmL, BodySlot.ForearmR, BodySlot.ForearmL,
        BodySlot.HandR, BodySlot.HandL, BodySlot.ThighR, BodySlot.ThighL, BodySlot.ShinR, BodySlot.ShinL, BodySlot.FootR, BodySlot.FootL,
    ];

    /// <summary>한 체형의 곡면·스키닝 데이터 (체형마다 한 번 계산해 둔다)</summary>
    sealed class Geometry
    {
        public Vector3[] Vertices = [];
        public Vector3[] Normals = [];
        public int[] Indices = [];
        public int[] Bones = [];
        public float[] Weights = [];
        public Transform3D[] Rest = new Transform3D[(int)Bone.Count];   // 부모 기준 쉼 자세
        public Transform3D[] Global = new Transform3D[(int)Bone.Count]; // 몸 기준 쉼 자세
        public int[] Parent = new int[(int)Bone.Count];
    }

    static readonly Dictionary<byte, Geometry> Cache = new();

    public float Height { get; private set; }
    /// <summary>다리를 늘린 길이 (m, 허벅지·정강이 각 6 cm)</summary>
    const float LegLift = 0.12f;
    /// <summary>팔이 몸에서 벌어진 각도 (rad): 골반에 닿지 않는 편안한 선 자세</summary>
    const float ArmSplay = 0.11f;
    /// <summary>곡면 격자 간격 (m)</summary>
    const float Voxel = 0.0055f;

    Skeleton3D _skeleton = null!;
    Quaternion[] _restRot = [];
    Node3D _thumbL = null!, _thumbR = null!;
    Node3D[] _fingersL = [], _fingersR = [];
    float _phase, _amp;

    public static CyborgBody Build(byte gender) => Build(BodyConfig.Default(gender));

    public static CyborgBody Build(BodyConfig body)
    {
        var gender = body.Gender;
        var P = gender == 1 ? Male : Female;
        // 다리를 사람 비례로 늘린 만큼(LegLift) 위 몸통을 올리고, 전체 크기를 줄여 키는 그대로 둔다.
        var headTop = P.Head.Base + 0.24f * P.Head.Scale;
        var scale = (gender == 1 ? 1.045f : 1.08f) * headTop / (headTop + LegLift);
        var g = new CyborgBody { Height = gender == 1 ? 1.86f : 1.80f, Scale = Vector3.One * scale };
        if (!Cache.TryGetValue(gender, out var geo)) Cache[gender] = geo = BuildGeometry(P);

        var skeleton = new Skeleton3D { Name = "Skeleton" };
        g.AddChild(skeleton);
        for (var b = 0; b < (int)Bone.Count; b++)
        {
            skeleton.AddBone(((Bone)b).ToString());
            if (geo.Parent[b] >= 0) skeleton.SetBoneParent(b, geo.Parent[b]);
            skeleton.SetBoneRest(b, geo.Rest[b]);
        }
        skeleton.ResetBonePoses();
        g._skeleton = skeleton;
        g._restRot = geo.Rest.Select(t => t.Basis.GetRotationQuaternion()).ToArray();

        // 정점 색: 뼈 가중치로 모듈 색을 섞는다 (고출력·경량 모듈도 이음에서 색이 부드럽게 바뀐다)
        var colors = new Color[geo.Vertices.Length];
        for (var v = 0; v < colors.Length; v++)
        {
            var c = new Color(0, 0, 0);
            for (var k = 0; k < 4; k++)
                c += VariantColor(body.VariantAt(BoneSlot[geo.Bones[v * 4 + k]])) * geo.Weights[v * 4 + k];
            colors[v] = c with { A = 1 };
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = geo.Vertices;
        arrays[(int)Mesh.ArrayType.Normal] = geo.Normals;
        arrays[(int)Mesh.ArrayType.Color] = colors;
        arrays[(int)Mesh.ArrayType.Bones] = geo.Bones;
        arrays[(int)Mesh.ArrayType.Weights] = geo.Weights;
        arrays[(int)Mesh.ArrayType.Index] = geo.Indices;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        var skin = new Skin();
        for (var b = 0; b < (int)Bone.Count; b++) skin.AddBind(b, geo.Global[b].AffineInverse());
        skeleton.AddChild(new MeshInstance3D
        {
            Name = "Surface", Mesh = mesh, Skin = skin, MaterialOverride = SkinMaterial,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.On,
        });

        // 손: 손목 뼈에 붙인다 (왼손은 거울상)
        var r = P.Arm.R;
        foreach (var side in new[] { 1, -1 })
        {
            var attach = new BoneAttachment3D { BoneName = (side > 0 ? Bone.HandR : Bone.HandL).ToString() };
            skeleton.AddChild(attach);
            var mirror = new Node3D { Scale = new Vector3(side, 1, 1) };
            attach.AddChild(mirror);
            var (hand, fingers, thumb) = BuildHand(r, body.VariantAt(side > 0 ? BodySlot.HandR : BodySlot.HandL));
            mirror.AddChild(hand);
            if (side > 0) (g._fingersR, g._thumbR) = (fingers, thumb);
            else (g._fingersL, g._thumbL) = (fingers, thumb);
        }
        return g;
    }

    /// <summary>
    /// 쉼 자세의 뼈대와 거리장 조각들을 놓고, 하나의 곡면을 뽑아 가중치를 매긴다.
    /// 조각 사이는 부드러운 합집합이라 몸통–팔다리, 관절 앞뒤가 경계 없이 이어진다.
    /// </summary>
    static Geometry BuildGeometry(Proportions P)
    {
        var geo = new Geometry();
        var lift = LegLift;
        var (U, F, r, _) = P.Arm;
        var (T, N, lr, q) = P.Leg;
        var shapes = new List<Shape>();
        var segments = new (Vector3 A, Vector3 B)[(int)Bone.Count];

        void SetBone(Bone b, int parent, Transform3D rest)
        {
            geo.Parent[(int)b] = parent;
            geo.Rest[(int)b] = rest;
            geo.Global[(int)b] = parent < 0 ? rest : geo.Global[parent] * rest;
        }
        Shape Add(Transform3D frame, float mirror, Vector3 offset, float blend, LoftField? loft = null, Vector3 radii = default)
        {
            var sh = new Shape { WorldToLocal = frame.AffineInverse(), Mirror = mirror, Offset = offset, Loft = loft, Radii = radii, Blend = blend };
            sh.ComputeBounds();
            shapes.Add(sh);
            return sh;
        }

        // 몸통과 목, 머리
        var torso = P.Torso
            .Append(new Sec(P.Neck.Y1, P.Neck.R * 0.92f, P.Neck.R * 0.92f, P.Neck.R * 0.92f, Cz: -0.008f))
            .Select(s => s with { Sq = P.Sq })
            .ToArray();
        SetBone(Bone.Root, -1, Transform3D.Identity);
        SetBone(Bone.Head, (int)Bone.Root, new Transform3D(Basis.Identity, new Vector3(0, P.Neck.Y0 + lift, 0)));
        Add(new Transform3D(Basis.Identity, new Vector3(0, lift, 0)), 1, Vector3.Zero, 0, new LoftField(torso));
        var k = P.Head.Scale;
        var head = HeadShell.Select(x => x with { Y = x.Y * k, Rx = x.Rx * k, F = x.F * k, B = x.B * k }).ToArray();
        Add(new Transform3D(Basis.Identity, new Vector3(0, P.Head.Base + lift, 0)), 1, Vector3.Zero, 0.03f, new LoftField(head));
        var headTop = P.Head.Base + lift + 0.24f * k;
        segments[(int)Bone.Root] = (new Vector3(0, torso[0].Y + lift, 0), new Vector3(0, P.Neck.Y0 + lift, 0));
        segments[(int)Bone.Head] = (new Vector3(0, P.Neck.Y0 + lift + 0.04f, 0), new Vector3(0, headTop, 0));

        Sec[] upper = [
            new(0, 0.036f, 0.035f, 0.036f), new(U * 0.3f, 0.040f, 0.040f + P.Arm.Bicep * 0.4f, 0.040f),
            new(U * 0.6f, 0.044f, 0.045f + P.Arm.Bicep, 0.043f), new(U * 0.85f, 0.047f, 0.046f, 0.046f),
            new(U, 0.048f, 0.046f, 0.047f), new(U + 0.03f, 0.044f, 0.042f, 0.043f),
        ];
        Sec[] fore = [
            new(0, 0.034f, 0.032f, 0.034f), new(F * 0.22f, 0.030f, 0.028f, 0.030f), new(F * 0.52f, 0.028f, 0.026f, 0.028f),
            new(F * 0.8f, 0.027f, 0.025f, 0.026f), new(F, 0.025f, 0.024f, 0.025f), new(F + 0.014f, 0.022f, 0.021f, 0.022f),
        ];
        // 허벅지는 바깥쪽(Cx)이 살짝 부풀고 위쪽 끝은 골반 안으로 들어간다. 종아리는 뒤쪽 반경으로 볼륨.
        Sec[] thigh = [
            new(0, 0.044f, 0.045f, 0.046f), new(T * 0.25f, 0.050f, 0.050f, 0.053f),
            new(T * 0.5f, 0.064f, 0.062f, 0.066f, Cx: 0.003f), new(T * 0.75f, 0.077f, 0.073f, 0.080f, Cx: 0.006f),
            new(T * 0.9f, 0.082f, 0.077f, 0.086f, Cx: 0.006f), new(T, 0.080f, 0.075f, 0.084f, Cx: 0.004f),
            new(T + 0.05f, 0.070f, 0.064f, 0.072f),
        ];
        Sec[] shin = [
            new(0, 0.030f, 0.031f, 0.033f), new(N * 0.15f, 0.032f, 0.032f, 0.036f), new(N * 0.45f, 0.040f, 0.038f, 0.050f),
            new(N * 0.7f, 0.044f, 0.040f, 0.052f), new(N * 0.9f, 0.044f, 0.043f, 0.046f), new(N, 0.044f, 0.045f, 0.046f),
            new(N + 0.032f, 0.040f, 0.041f, 0.041f),
        ];
        var FL = P.Foot;
        // 발: 둥근 뒤꿈치, 도톰한 발등, 둥글게 닫히는 발끝. F = 발바닥 쪽, B = 발등 쪽
        Sec[] foot = [
            new(-0.012f, 0.014f, 0.016f, 0.014f), new(0, 0.026f, 0.030f, 0.026f), new(FL * 0.12f, 0.033f, 0.034f, 0.042f),
            new(FL * 0.4f, 0.039f, 0.031f, 0.034f), new(FL * 0.72f, 0.045f, 0.026f, 0.024f), new(FL * 0.9f, 0.040f, 0.020f, 0.018f),
            new(FL * 0.98f, 0.026f, 0.013f, 0.012f), new(FL, 0.012f, 0.006f, 0.006f),
        ];
        var upperField = new LoftField(upper.Select(x => x.Scale(r)).ToArray());
        var foreField = new LoftField(fore.Select(x => x.Scale(r)).ToArray());
        var thighField = new LoftField(thigh.Select(x => x.Scale(lr)).ToArray());
        var shinField = new LoftField(shin.Select(x => x.Scale(lr)).ToArray());
        var footField = new LoftField(foot.Select(x => x.Scale(Mathf.Sqrt(lr))).ToArray());

        foreach (var side in new[] { 1, -1 })
        {
            var R = side > 0;
            Bone Pick(Bone right, Bone left) => R ? right : left;
            // 팔: 어깨 → 팔꿈치 → 손목
            SetBone(Pick(Bone.UpperR, Bone.UpperL), (int)Bone.Root,
                new Transform3D(new Basis(Vector3.Back, side * ArmSplay), new Vector3(side * P.Shoulder.X, P.Shoulder.Y + lift, 0)));
            SetBone(Pick(Bone.ForeR, Bone.ForeL), (int)Pick(Bone.UpperR, Bone.UpperL), new Transform3D(new Basis(Vector3.Right, -0.10f), new Vector3(0, -U, 0)));
            SetBone(Pick(Bone.HandR, Bone.HandL), (int)Pick(Bone.ForeR, Bone.ForeL), new Transform3D(Basis.Identity, new Vector3(0, -F - 0.014f * r, 0)));
            var up = geo.Global[(int)Pick(Bone.UpperR, Bone.UpperL)];
            var el = geo.Global[(int)Pick(Bone.ForeR, Bone.ForeL)];
            var wr = geo.Global[(int)Pick(Bone.HandR, Bone.HandL)];
            Add(up, side, new Vector3(0, -U, 0), 0.035f, upperField);
            Add(up, side, new Vector3(-0.008f * r, -0.022f, 0), 0.035f, radii: new Vector3(0.047f, 0.052f, 0.0456f) * r); // 삼각근
            Add(el, side, Vector3.Zero, 0.018f, radii: new Vector3(0.034f, 0.036f, 0.034f) * r);                         // 팔꿈치
            Add(el, side, new Vector3(0, -F - 0.014f * r, 0), 0.018f, foreField);
            Add(wr, side, new Vector3(0, -0.006f * r, 0), 0.012f, radii: new Vector3(0.024f, 0.023f, 0.024f) * r);      // 손목
            segments[(int)Pick(Bone.UpperR, Bone.UpperL)] = (up.Origin, el.Origin);
            segments[(int)Pick(Bone.ForeR, Bone.ForeL)] = (el.Origin, wr.Origin);
            segments[(int)Pick(Bone.HandR, Bone.HandL)] = (wr.Origin, wr * new Vector3(0, -0.06f, 0));

            // 다리: 고관절 → 무릎 → 발목 → 발
            SetBone(Pick(Bone.ThighR, Bone.ThighL), (int)Bone.Root,
                new Transform3D(new Basis(Vector3.Back, -side * q), new Vector3(side * P.Hip.X, P.Hip.Y + lift, 0)));
            SetBone(Pick(Bone.ShinR, Bone.ShinL), (int)Pick(Bone.ThighR, Bone.ThighL), new Transform3D(Basis.Identity, new Vector3(0, -T, 0)));
            SetBone(Pick(Bone.FootR, Bone.FootL), (int)Pick(Bone.ShinR, Bone.ShinL), new Transform3D(Basis.Identity, new Vector3(0, -N - 0.064f, -0.05f)));
            var hip = geo.Global[(int)Pick(Bone.ThighR, Bone.ThighL)];
            var kn = geo.Global[(int)Pick(Bone.ShinR, Bone.ShinL)];
            var an = geo.Global[(int)Pick(Bone.FootR, Bone.FootL)];
            Add(hip, side, new Vector3(0, -T, 0), 0.06f, thighField);
            Add(kn, side, Vector3.Zero, 0.02f, radii: new Vector3(0.0432f, 0.046f, 0.0428f) * lr);                     // 무릎
            Add(kn, side, new Vector3(0, -N - 0.032f, 0), 0.02f, shinField);
            Add(an, side, Vector3.Zero, 0.018f, radii: new Vector3(0.032f, 0.031f, 0.032f) * lr);                        // 발목
            var footFrame = an * new Transform3D(new Basis(Vector3.Right, Mathf.Pi / 2), Vector3.Zero);
            Add(footFrame, side, Vector3.Zero, 0.02f, footField);
            segments[(int)Pick(Bone.ThighR, Bone.ThighL)] = (hip.Origin, kn.Origin);
            segments[(int)Pick(Bone.ShinR, Bone.ShinL)] = (kn.Origin, an.Origin);
            segments[(int)Pick(Bone.FootR, Bone.FootL)] = (an.Origin, footFrame * new Vector3(0, FL, 0));
        }

        float Field(Vector3 p)
        {
            var d = 1e3f;
            foreach (var sh in shapes)
                if (sh.Bounds.HasPoint(p)) d = Smin(d, sh.Distance(p), sh.Blend);
            return d;
        }

        var box = shapes.Select(s => s.Bounds).Aggregate((a, b) => a.Merge(b));
        (geo.Vertices, geo.Normals, geo.Indices) = SurfaceNets(Field, box, Voxel);
        (geo.Bones, geo.Weights) = SkinWeights(geo.Vertices, segments);
        return geo;
    }

    /// <summary>
    /// Naive surface nets: 격자 칸마다 부호가 바뀌면 칸 안에 정점 하나(모서리 교차점들의 평균), 부호가 바뀌는 모서리마다 사각형 하나.
    /// 법선은 거리장의 기울기라 곡면이 매끄럽게 음영진다.
    /// </summary>
    static (Vector3[] V, Vector3[] N, int[] I) SurfaceNets(Func<Vector3, float> f, Aabb box, float h)
    {
        box = box.Grow(h * 2);
        int nx = (int)(box.Size.X / h) + 2, ny = (int)(box.Size.Y / h) + 2, nz = (int)(box.Size.Z / h) + 2;
        var o = box.Position;
        var grid = new float[nx * ny * nz];
        int Id(int i, int j, int k) => (k * ny + j) * nx + i;
        Parallel.For(0, nz, k =>
        {
            for (var j = 0; j < ny; j++)
            for (var i = 0; i < nx; i++)
                grid[Id(i, j, k)] = f(o + new Vector3(i, j, k) * h);
        });

        var cell = new int[nx * ny * nz];
        Array.Fill(cell, -1);
        var verts = new List<Vector3>();
        int[] cx = [0, 1, 0, 1, 0, 1, 0, 1], cy = [0, 0, 1, 1, 0, 0, 1, 1], cz = [0, 0, 0, 0, 1, 1, 1, 1];
        int[,] edges = { { 0, 1 }, { 2, 3 }, { 4, 5 }, { 6, 7 }, { 0, 2 }, { 1, 3 }, { 4, 6 }, { 5, 7 }, { 0, 4 }, { 1, 5 }, { 2, 6 }, { 3, 7 } };
        var val = new float[8];
        for (var k = 0; k < nz - 1; k++)
        for (var j = 0; j < ny - 1; j++)
        for (var i = 0; i < nx - 1; i++)
        {
            var inside = 0;
            for (var c = 0; c < 8; c++)
            {
                val[c] = grid[Id(i + cx[c], j + cy[c], k + cz[c])];
                if (val[c] < 0) inside++;
            }
            if (inside == 0 || inside == 8) continue;
            var sum = Vector3.Zero;
            var n = 0;
            for (var e = 0; e < 12; e++)
            {
                int a = edges[e, 0], b = edges[e, 1];
                if ((val[a] < 0) == (val[b] < 0)) continue;
                var t = val[a] / (val[a] - val[b]);
                sum += new Vector3(cx[a] + (cx[b] - cx[a]) * t, cy[a] + (cy[b] - cy[a]) * t, cz[a] + (cz[b] - cz[a]) * t);
                n++;
            }
            cell[Id(i, j, k)] = verts.Count;
            verts.Add(o + (new Vector3(i, j, k) + sum / n) * h);
        }

        var normals = new Vector3[verts.Count];
        Parallel.For(0, verts.Count, v =>
        {
            var p = verts[v];
            var e = h * 0.5f;
            var gr = new Vector3(f(p + new Vector3(e, 0, 0)) - f(p - new Vector3(e, 0, 0)),
                                 f(p + new Vector3(0, e, 0)) - f(p - new Vector3(0, e, 0)),
                                 f(p + new Vector3(0, 0, e)) - f(p - new Vector3(0, 0, e)));
            normals[v] = gr.LengthSquared() > 1e-12f ? gr.Normalized() : Vector3.Up;
        });

        var idx = new List<int>();
        void Quad(int a, int b, int c, int d, bool flip)
        {
            if (a < 0 || b < 0 || c < 0 || d < 0) return;
            // Godot 는 시계 방향이 앞면: 바깥에서 볼 때 시계 방향이 되도록 감는다
            var nrm = normals[a] + normals[b] + normals[c] + normals[d];
            var cross = (verts[b] - verts[a]).Cross(verts[c] - verts[a]);
            if ((cross.Dot(nrm) > 0) != flip) (b, d) = (d, b);
            idx.AddRange([a, b, c, a, c, d]);
        }
        for (var k = 1; k < nz - 1; k++)
        for (var j = 1; j < ny - 1; j++)
        for (var i = 1; i < nx - 1; i++)
        {
            var v0 = grid[Id(i, j, k)] < 0;
            if (i < nx - 1 && v0 != grid[Id(i + 1, j, k)] < 0)
                Quad(cell[Id(i, j - 1, k - 1)], cell[Id(i, j, k - 1)], cell[Id(i, j, k)], cell[Id(i, j - 1, k)], false);
            if (j < ny - 1 && v0 != grid[Id(i, j + 1, k)] < 0)
                Quad(cell[Id(i - 1, j, k - 1)], cell[Id(i, j, k - 1)], cell[Id(i, j, k)], cell[Id(i - 1, j, k)], false);
            if (k < nz - 1 && v0 != grid[Id(i, j, k + 1)] < 0)
                Quad(cell[Id(i - 1, j - 1, k)], cell[Id(i, j - 1, k)], cell[Id(i, j, k)], cell[Id(i - 1, j, k)], false);
        }
        return (verts.ToArray(), normals, idx.ToArray());
    }

    /// <summary>정점마다 가까운 뼈 마디 넷까지의 거리로 가중치를 매긴다 (관절 둘레는 두 뼈에 부드럽게 나뉜다)</summary>
    static (int[] Bones, float[] Weights) SkinWeights(Vector3[] verts, (Vector3 A, Vector3 B)[] segs)
    {
        const float falloff = 0.022f;
        var bones = new int[verts.Length * 4];
        var weights = new float[verts.Length * 4];
        Parallel.For(0, verts.Length, v =>
        {
            Span<float> d = stackalloc float[segs.Length];
            var min = float.MaxValue;
            for (var b = 0; b < segs.Length; b++)
            {
                var (a, e) = segs[b];
                var ab = e - a;
                var t = Mathf.Clamp((verts[v] - a).Dot(ab) / Mathf.Max(ab.LengthSquared(), 1e-8f), 0, 1);
                d[b] = verts[v].DistanceTo(a + ab * t);
                min = Mathf.Min(min, d[b]);
            }
            Span<int> best = stackalloc int[4];
            Span<float> w = stackalloc float[4];
            for (var s = 0; s < 4; s++) { best[s] = 0; w[s] = 0; }
            for (var b = 0; b < segs.Length; b++)
            {
                var wb = Mathf.Exp(-(d[b] - min) / falloff);
                for (var s = 0; s < 4; s++)
                {
                    if (wb <= w[s]) continue;
                    for (var u = 3; u > s; u--) { w[u] = w[u - 1]; best[u] = best[u - 1]; }
                    w[s] = wb; best[s] = b;
                    break;
                }
            }
            var total = w[0] + w[1] + w[2] + w[3];
            for (var s = 0; s < 4; s++)
            {
                bones[v * 4 + s] = best[s];
                weights[v * 4 + s] = w[s] / total;
            }
        });
        return (bones, weights);
    }

    static MeshInstance3D Add(Node3D parent, Mesh mesh, Material mat, Vector3 at = default)
    {
        var m = new MeshInstance3D { Mesh = mesh, MaterialOverride = mat, Position = at, CastShadow = GeometryInstance3D.ShadowCastingSetting.On };
        parent.AddChild(m);
        return m;
    }

    // 오른손 기준. 손바닥은 몸 안쪽(-X), 엄지는 정면(+Z).
    // 사람 손처럼: 도톰한 손바닥, 서로 붙은 손가락, 손가락마다 두 마디로 손바닥 쪽으로 자연스럽게 굽는다.
    static (Node3D Hand, Node3D[] Fingers, Node3D Thumb) BuildHand(float r, ModuleVariant variant)
    {
        var hand = new Node3D();
        var k = Mathf.Sqrt(r);
        var material = ModuleTitanium(variant);
        var fingers = new List<Node3D>();
        // 손바닥: Rx = 두께의 절반, F·B = 폭의 절반 (손목에서 손가락 뿌리로 넓어진다)
        Sec[] palm = [
            new(-0.094f, 0.012f, 0.040f, 0.040f), new(-0.065f, 0.0155f, 0.041f, 0.041f),
            new(-0.03f, 0.016f, 0.036f, 0.036f), new(0, 0.012f, 0.025f, 0.025f),
        ];
        Add(hand, Loft(palm.Select(x => x.Scale(k)).ToArray(), 28, 4), material);
        foreach (var (z, len) in new[] { (0.029f, 0.075f), (0.0095f, 0.083f), (-0.0095f, 0.078f), (-0.028f, 0.062f) })
            fingers.Add(BuildDigit(hand, new Vector3(0, -0.094f * k, z * k), len * k, 0.0095f * k, material, -0.18f, -0.35f));
        var thumb = BuildDigit(hand, new Vector3(-0.006f * k, -0.03f * k, 0.036f * k), 0.062f * k, 0.0105f * k, material, 0, -0.3f);
        thumb.Rotation = new Vector3(-0.7f, 0, -0.35f);
        return (hand, fingers.ToArray(), thumb);
    }

    /// <summary>
    /// 손가락 하나: 뿌리 마디(관절 구) → 첫 마디 → 가운데 관절 → 둥근 끝 마디. 끝 마디는 손바닥 쪽으로 늘 조금 굽어 있다.
    /// 돌려주는 노드의 회전(Z)이 손가락 전체의 굽힘이다.
    /// </summary>
    static Node3D BuildDigit(Node3D hand, Vector3 at, float length, float radius, Material material, float baseCurl, float tipCurl)
    {
        var digit = new Node3D { Position = at, Rotation = new Vector3(0, 0, baseCurl) };
        hand.AddChild(digit);
        var l1 = length * 0.55f;
        var l2 = length - l1;
        Add(digit, new SphereMesh { Radius = radius * 1.03f, Height = radius * 2.06f, RadialSegments = 12, Rings = 8 }, material);
        Add(digit, Loft([new(0, radius * 0.92f, radius * 0.9f, radius * 0.9f), new(l1, radius, radius * 0.97f, radius * 0.97f)], 14, 3), material, new Vector3(0, -l1, 0));
        var tip = new Node3D { Position = new Vector3(0, -l1, 0), Rotation = new Vector3(0, 0, tipCurl) };
        digit.AddChild(tip);
        Add(tip, new SphereMesh { Radius = radius * 0.93f, Height = radius * 1.86f, RadialSegments = 12, Rings = 8 }, material);
        Add(tip, Loft([
            new(0, radius * 0.35f, radius * 0.33f, radius * 0.33f), new(radius * 0.6f, radius * 0.75f, radius * 0.72f, radius * 0.72f),
            new(l2 * 0.55f, radius * 0.86f, radius * 0.84f, radius * 0.84f), new(l2, radius * 0.9f, radius * 0.88f, radius * 0.88f),
        ], 14, 4), material, new Vector3(0, -l2, 0));
        return digit;
    }

    void Pose(Bone b, Vector3 euler) =>
        _skeleton.SetBonePoseRotation((int)b, _restRot[(int)b] * Basis.FromEuler(euler).GetRotationQuaternion());

    /// <summary>인형의 관절은 이동할 때만 움직인다. 정지 중에는 고정된 중립 자세를 유지한다.</summary>
    public void Animate(float dt, float speed)
    {
        var target = Mathf.Min(1, speed / 3);
        _amp = Mathf.MoveToward(_amp, target, dt * (target > _amp ? 5.5f : 3.2f));
        var strideLength = Mathf.Lerp(1.45f, 2.2f, target);
        var cadence = speed > 0.08f ? Mathf.Tau * speed / strideLength : 1.2f;
        _phase = Mathf.PosMod(_phase + dt * cadence, Mathf.Tau);
        var s = Mathf.Sin(_phase);
        var c = Mathf.Cos(_phase);

        // 걸음: 발을 몸 중심선 쪽으로 딛고(다리 모음), 골반이 디딤발 쪽으로 살짝 흔들리고, 팔은 반대로 흔들리며 팔꿈치가 조금 굽는다
        var sway = s * 0.035f * _amp;
        _skeleton.SetBonePoseRotation((int)Bone.Root, Basis.FromEuler(new Vector3(0, s * 0.06f * _amp, sway)).GetRotationQuaternion());
        Pose(Bone.ThighL, new Vector3(s * 0.5f * _amp, 0, 0.03f * _amp));
        Pose(Bone.ThighR, new Vector3(-s * 0.5f * _amp, 0, -0.03f * _amp));
        _skeleton.SetBonePoseRotation((int)Bone.ShinL, Basis.FromEuler(new Vector3(Mathf.Max(0, -c) * 0.7f * _amp, 0, 0)).GetRotationQuaternion());
        _skeleton.SetBonePoseRotation((int)Bone.ShinR, Basis.FromEuler(new Vector3(Mathf.Max(0, c) * 0.7f * _amp, 0, 0)).GetRotationQuaternion());
        _skeleton.SetBonePoseRotation((int)Bone.FootL, Basis.FromEuler(new Vector3(-s * 0.12f * _amp, 0, 0)).GetRotationQuaternion());
        _skeleton.SetBonePoseRotation((int)Bone.FootR, Basis.FromEuler(new Vector3(s * 0.12f * _amp, 0, 0)).GetRotationQuaternion());

        Pose(Bone.UpperL, new Vector3(-s * 0.35f * _amp, 0, 0.03f * _amp));
        Pose(Bone.UpperR, new Vector3(s * 0.35f * _amp, 0, -0.03f * _amp));
        _skeleton.SetBonePoseRotation((int)Bone.ForeL, Basis.FromEuler(new Vector3(-0.14f - Mathf.Max(0, s) * 0.22f * _amp, 0, 0)).GetRotationQuaternion());
        _skeleton.SetBonePoseRotation((int)Bone.ForeR, Basis.FromEuler(new Vector3(-0.14f - Mathf.Max(0, -s) * 0.22f * _amp, 0, 0)).GetRotationQuaternion());
        _skeleton.SetBonePoseRotation((int)Bone.HandL, Basis.FromEuler(new Vector3(0.025f * s * _amp, 0, 0)).GetRotationQuaternion());
        _skeleton.SetBonePoseRotation((int)Bone.HandR, Basis.FromEuler(new Vector3(-0.025f * s * _amp, 0, 0)).GetRotationQuaternion());
        var fingerCurl = 0.08f + 0.08f * _amp;
        _thumbL.Rotation = new Vector3(-0.7f + s * 0.025f * _amp, 0, -0.35f + fingerCurl * 0.14f);
        _thumbR.Rotation = new Vector3(-0.7f - s * 0.025f * _amp, 0, -0.35f - fingerCurl * 0.14f);
        foreach (var finger in _fingersL) finger.Rotation = new Vector3(0, 0, -0.18f - fingerCurl);
        foreach (var finger in _fingersR) finger.Rotation = new Vector3(0, 0, -0.18f - fingerCurl);
        var idle = 1 - _amp;
        _skeleton.SetBonePoseRotation((int)Bone.Head,
            Basis.FromEuler(new Vector3(Mathf.Sin(_phase * 0.37f) * 0.018f * idle, Mathf.Sin(_phase * 0.23f) * 0.065f * idle, -sway * 0.5f)).GetRotationQuaternion());
    }
}
