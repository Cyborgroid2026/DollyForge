using Godot;
using DollyForge.Core;

namespace DollyForge.Game;

/// <summary>
/// 전신 사이보그 몸. 곡면 chassis 가 하중을 연속적으로 나누고, 관절은 유연한 외피 아래에서 움직인다.
/// Analog 설계의 topology(연결), chassis(지지), circuit(움직임), instrument(감각)를 몸의 형태로 드러낸다.
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
        float C(Func<Sec, float> f) => Catmull(f(a), f(b), f(c), f(d), t);
        float Bounded(Func<Sec, float> f) => Mathf.Clamp(C(f), Mathf.Min(f(b), f(c)), Mathf.Max(f(b), f(c)));
        return new Sec(C(x => x.Y), Mathf.Max(Bounded(x => x.Rx), 0.001f), Mathf.Max(Bounded(x => x.F), 0.001f), Mathf.Max(Bounded(x => x.B), 0.001f),
            Bounded(x => x.Cx), Bounded(x => x.Cz), Bounded(x => x.Sq), Bounded(x => x.Bust), Bounded(x => x.Glute), Bounded(x => x.Pec));
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
        Sec[] Torso, float Sq, (float Y0, float Y1, float R) Neck, (float Base, float Scale, float Jaw, float Chin) Head,
        (float X, float Y) Shoulder, (float Upper, float Fore, float R, float Bicep) Arm,
        (float X, float Y) Hip, (float Thigh, float Shin, float R, float Q) Leg, float Foot, float WaistY,
        float UnderbustY, float LowHipY);

    // 여성형: 성체 키 1.80 m, 허리·골반·가슴의 완만한 전환과 실물 크기의 인체 비례
    static readonly Proportions Female = new(
        [
            new(0.735f, 0.032f, 0.032f, 0.042f, Cz: -0.005f),
            new(0.765f, 0.115f, 0.072f, 0.088f),
            new(0.815f, 0.165f, 0.084f, 0.100f, Glute: 0.012f),
            new(0.87f, 0.172f, 0.088f, 0.107f, Glute: 0.018f),
            new(0.93f, 0.164f, 0.085f, 0.100f, Glute: 0.010f),
            new(0.99f, 0.148f, 0.082f, 0.088f),
            new(1.05f, 0.112f, 0.070f, 0.068f),                // 잘록한 허리 (폭 0.112m)
            new(1.11f, 0.124f, 0.076f, 0.074f),
            new(1.16f, 0.138f, 0.080f, 0.078f, Bust: 0.018f), // 언더버스트 시작 라인
            new(1.22f, 0.154f, 0.088f, 0.084f, Bust: 0.040f),
            new(1.265f, 0.158f, 0.086f, 0.084f, Bust: 0.037f),
            new(1.31f, 0.158f, 0.074f, 0.078f, Bust: 0.016f),
            new(1.35f, 0.152f, 0.058f, 0.068f),                // 쇄골 라인
            new(1.385f, 0.096f, 0.046f, 0.052f),
            new(1.4f, 0.048f, 0.040f, 0.040f),
        ],
        1.0f, (1.36f, 1.5f, 0.042f), (1.425f, 1.0f, 0.054f, 0.022f), (0.168f, 1.335f), (0.285f, 0.24f, 1.0f, 0f),
        (0.096f, 0.84f), (0.40f, 0.38f, 1.04f, 0.042f), 0.228f, 1.05f, 1.16f, 0.94f);

    // 남성형: 성체 키 1.86m, 역삼각형 흉근과 다부진 실루엣
    static readonly Proportions Male = new(
        [
            new(0.785f, 0.038f, 0.038f, 0.042f),
            new(0.815f, 0.115f, 0.076f, 0.085f),
            new(0.87f, 0.178f, 0.098f, 0.106f, Glute: 0.012f),
            new(0.92f, 0.182f, 0.102f, 0.114f, Glute: 0.016f),
            new(0.99f, 0.162f, 0.098f, 0.100f),
            new(1.06f, 0.144f, 0.094f, 0.092f),
            new(1.14f, 0.158f, 0.102f, 0.098f),
            new(1.22f, 0.180f, 0.110f, 0.106f),
            new(1.30f, 0.202f, 0.118f, 0.112f, Pec: 0.028f),  // 흉근 볼륨
            new(1.37f, 0.212f, 0.114f, 0.108f, Pec: 0.022f),
            new(1.425f, 0.210f, 0.090f, 0.095f),
            new(1.465f, 0.145f, 0.068f, 0.074f),
            new(1.49f, 0.068f, 0.058f, 0.058f),
        ],
        1.18f, (1.44f, 1.6f, 0.055f), (1.525f, 1.08f, 0.066f, 0.032f), (0.212f, 1.415f), (0.31f, 0.27f, 1.22f, 0.012f),
        (0.095f, 0.9f), (0.44f, 0.42f, 1.07f, 0.02f), 0.26f, 1.06f, 1.22f, 0.98f);

    // ------------------------------------------------------------------ 재질

    static StandardMaterial3D? _shell, _visorMat, _heavyTitanium, _lightTitanium;
    static StandardMaterial3D? _jointMat, _jointSeamMat;

    /// <summary>전신 외장과 지체를 이루는 브러시드 티타늄 합금</summary>
    static StandardMaterial3D Shell => _shell ??= new StandardMaterial3D
    {
        AlbedoColor = new Color(0.60f, 0.66f, 0.71f),
        Metallic = 0.82f,
        Roughness = 0.39f,
        ClearcoatEnabled = true,
        Clearcoat = 0.08f,
        ClearcoatRoughness = 0.34f,
        NormalEnabled = ResourceLoader.Exists("res://assets/tex/metal_plate/nor_gl.jpg"),
        NormalTexture = ResourceLoader.Exists("res://assets/tex/metal_plate/nor_gl.jpg")
            ? GD.Load<Texture2D>("res://assets/tex/metal_plate/nor_gl.jpg")
            : null,
        NormalScale = 0.055f,
    };

    static StandardMaterial3D HeavyTitanium => _heavyTitanium ??= new StandardMaterial3D
    {
        AlbedoColor = new Color(0.44f, 0.51f, 0.56f),
        Metallic = 0.88f,
        Roughness = 0.46f,
    };

    static StandardMaterial3D LightTitanium => _lightTitanium ??= new StandardMaterial3D
    {
        AlbedoColor = new Color(0.70f, 0.74f, 0.77f),
        Metallic = 0.84f,
        Roughness = 0.32f,
    };

    static Material ModuleTitanium(ModuleVariant variant) => variant switch
    {
        ModuleVariant.HeavyDuty => HeavyTitanium,
        ModuleVariant.Lite => LightTitanium,
        _ => Shell,
    };

    static StandardMaterial3D JointMaterial => _jointMat ??= new StandardMaterial3D
    {
        AlbedoColor = new Color(0.48f, 0.56f, 0.60f),
        Metallic = 0.76f,
        Roughness = 0.46f,
    };

    static StandardMaterial3D JointSeamMaterial => _jointSeamMat ??= new StandardMaterial3D
    {
        AlbedoColor = new Color(0.28f, 0.35f, 0.39f),
        Metallic = 0.86f,
        Roughness = 0.5f,
    };

    static StandardMaterial3D Visor => _visorMat ??= new StandardMaterial3D
    {
        AlbedoColor = new Color(0.12f, 0.20f, 0.21f),
        Metallic = 0.58f,
        Roughness = 0.28f,
        ClearcoatEnabled = true,
        Clearcoat = 0.18f,
        ClearcoatRoughness = 0.24f,
    };

    // ------------------------------------------------------------------ 조립

    public float Height { get; private set; }
    Node3D _legL = null!, _legR = null!, _kneeL = null!, _kneeR = null!, _ankleL = null!, _ankleR = null!;
    Node3D _armL = null!, _armR = null!, _elbowL = null!, _elbowR = null!, _handL = null!, _handR = null!, _thumbL = null!, _thumbR = null!, _head = null!;
    Node3D[] _fingersL = [], _fingersR = [];
    float _phase, _amp, _time;

    public static CyborgBody Build(byte gender) => Build(BodyConfig.Default(gender));

    public static CyborgBody Build(BodyConfig body)
    {
        var gender = body.Gender;
        var P = gender == 1 ? Male : Female;
        var scale = gender == 1 ? 1.045f : 1.08f;
        var g = new CyborgBody
        {
            Height = gender == 1 ? 1.86f : 1.80f,
            Scale = Vector3.One * scale,
        };
        var torso = P.Torso
            .Append(new Sec(P.Neck.Y1, P.Neck.R * 0.92f, P.Neck.R * 0.92f, P.Neck.R * 0.92f, Cz: -0.008f))
            .Select(s => s with { Sq = P.Sq })
            .ToArray();
        Add(g, Loft(torso, 64, 7), ModuleTitanium(body.VariantAt(BodySlot.Torso)));

        g._head = BuildHead(P, g);
        (g._armL, g._elbowL, g._handL, g._fingersL, g._thumbL) = BuildArm(P, -1,
            body.VariantAt(BodySlot.UpperArmL), body.VariantAt(BodySlot.ForearmL), body.VariantAt(BodySlot.HandL));
        (g._armR, g._elbowR, g._handR, g._fingersR, g._thumbR) = BuildArm(P, 1,
            body.VariantAt(BodySlot.UpperArmR), body.VariantAt(BodySlot.ForearmR), body.VariantAt(BodySlot.HandR));
        (g._legL, g._kneeL, g._ankleL) = BuildLeg(P, -1,
            body.VariantAt(BodySlot.ThighL), body.VariantAt(BodySlot.ShinL), body.VariantAt(BodySlot.FootL));
        (g._legR, g._kneeR, g._ankleR) = BuildLeg(P, 1,
            body.VariantAt(BodySlot.ThighR), body.VariantAt(BodySlot.ShinR), body.VariantAt(BodySlot.FootR));
        foreach (var n in new[] { g._armL, g._armR, g._legL, g._legR }) g.AddChild(n);
        return g;
    }

    static MeshInstance3D Add(Node3D parent, Mesh mesh, Material mat, Vector3 at = default)
    {
        var m = new MeshInstance3D { Mesh = mesh, MaterialOverride = mat, Position = at, CastShadow = GeometryInstance3D.ShadowCastingSetting.On };
        parent.AddChild(m);
        return m;
    }

    /// <summary>스케치(sketch_01.jpg)의 허벅지 외측 모듈형 장갑 플레이트</summary>
    static MeshInstance3D BuildThighPlate(float scale, ModuleVariant variant)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        float w = 0.050f * scale, h = 0.138f * scale, ch = 0.015f * scale, d = 0.014f * scale;
        float hw = w * 0.5f, hh = h * 0.5f;
        Vector2[] poly = [
            new(-hw + ch,  hh),
            new( hw - ch,  hh),
            new( hw,       hh - ch),
            new( hw,      -hh + ch),
            new( hw - ch, -hh),
            new(-hw + ch, -hh),
            new(-hw,      -hh + ch),
            new(-hw,       hh - ch),
        ];
        int n = poly.Length;
        for (int i = 0; i < n; i++)
        {
            int next = (i + 1) % n;
            var p0 = new Vector3(d, poly[i].Y, poly[i].X);
            var p1 = new Vector3(d, poly[next].Y, poly[next].X);
            var p2 = new Vector3(0, poly[next].Y, poly[next].X);
            var p3 = new Vector3(0, poly[i].Y, poly[i].X);

            var norm = new Vector3(0, (poly[i].Y + poly[next].Y) * 0.5f, (poly[i].X + poly[next].X) * 0.5f).Normalized();
            st.SetNormal(norm); st.AddVertex(p0);
            st.SetNormal(norm); st.AddVertex(p1);
            st.SetNormal(norm); st.AddVertex(p2);

            st.SetNormal(norm); st.AddVertex(p0);
            st.SetNormal(norm); st.AddVertex(p2);
            st.SetNormal(norm); st.AddVertex(p3);
        }
        var frontCenter = new Vector3(d, 0, 0);
        for (int i = 0; i < n; i++)
        {
            int next = (i + 1) % n;
            st.SetNormal(Vector3.Right);
            st.AddVertex(frontCenter);
            st.AddVertex(new Vector3(d, poly[i].Y, poly[i].X));
            st.AddVertex(new Vector3(d, poly[next].Y, poly[next].X));
        }
        st.GenerateNormals();
        return new MeshInstance3D { Mesh = st.Commit(), MaterialOverride = ModuleTitanium(variant) };
    }

    static Node3D BuildHead(Proportions P, Node3D g)
    {
        var (b, k, jaw, chin) = P.Head;
        var head = new Node3D { Position = new Vector3(0, b, 0) };

        // 얼굴은 하나의 둥근 외피로 잇고, 짙은 유리 감각부만 작게 얹는다.
        Sec[] s =
        [
            new(0, chin * 0.65f, 0.016f, 0.014f, Cz: 0.038f),
            new(0.018f, chin * 1.05f, 0.028f, 0.026f, Cz: 0.034f, Sq: P.Sq),
            new(0.052f, jaw * 1.05f, 0.060f, 0.062f, Cz: 0.016f, Sq: P.Sq),
            new(0.092f, jaw * 1.15f, 0.088f, 0.088f, Cz: 0.006f),
            new(0.132f, 0.076f, 0.094f, 0.100f),
            new(0.170f, 0.076f, 0.088f, 0.104f, Cz: -0.004f),
            new(0.204f, 0.068f, 0.072f, 0.094f, Cz: -0.008f),
            new(0.228f, 0.044f, 0.044f, 0.062f, Cz: -0.012f),
            new(0.240f, 0.006f, 0.006f, 0.008f, Cz: -0.014f),
        ];
        Add(head, Loft(s.Select(x => x with { Y = x.Y * k, Rx = x.Rx * k, F = x.F * k, B = x.B * k, Cz = x.Cz * k }).ToArray(), 56, 7), Shell);

        var visor = Add(head, new SphereMesh { Radius = 0.083f * k, Height = 0.09f * k, RadialSegments = 40, Rings = 20 },
            Visor, new Vector3(0, 0.148f * k, 0.090f * k));
        visor.Scale = new Vector3(1.05f, 0.66f, 0.19f);

        // 좌우 청각·균형 감각부
        foreach (var side in new[] { -1, 1 })
        {
            var ear = Add(head, new SphereMesh { Radius = 0.022f * k, Height = 0.042f * k, RadialSegments = 24, Rings = 12 },
                JointMaterial, new Vector3(side * 0.091f * k, 0.122f * k, -0.004f * k));
            ear.Scale = new Vector3(0.7f, 1f, 1.15f);
        }

        g.AddChild(head);
        return head;
    }

    static (Node3D Arm, Node3D Elbow, Node3D Hand, Node3D[] Fingers, Node3D Thumb) BuildArm(
        Proportions P, int side, ModuleVariant upperVariant, ModuleVariant forearmVariant, ModuleVariant handVariant)
    {
        var (U, F, r, bicep) = P.Arm;
        var arm = new Node3D { Position = new Vector3(side * P.Shoulder.X, P.Shoulder.Y, 0), Rotation = new Vector3(0, 0, side * 0.09f) };
        AddJointSeam(arm, 0.046f * r, 0.003f * r, new Vector3(0, -0.01f, 0), alongY: true);

        // 어깨에서 팔로 좁아지는 곡면을 키워 몸통과 팔이 한 덩어리로 겹쳐지게 한다.
        Sec[] upper = [
            new(0, 0.052f, 0.057f, 0.058f),
            new(U * 0.18f, 0.050f, 0.058f + bicep * 0.4f, 0.056f),
            new(U * 0.45f, 0.042f, 0.046f + bicep, 0.045f),
            new(U * 0.78f, 0.041f, 0.040f, 0.042f),
            new(U, 0.043f, 0.04f, 0.042f)
        ];
        var upperModule = new Node3D();
        arm.AddChild(upperModule);
        Add(upperModule, Loft(upper.Select(x => x.Scale(r)).ToArray(), 32, 5), ModuleTitanium(upperVariant), new Vector3(0, -U, 0));

        var elbow = new Node3D { Position = new Vector3(0, -U, 0), Rotation = new Vector3(-0.14f, 0, 0) };
        upperModule.AddChild(elbow);
        var elbowSkin = Add(elbow, new SphereMesh { Radius = 0.047f * r, Height = 0.108f * r, RadialSegments = 32, Rings = 16 }, JointMaterial);
        elbowSkin.Scale = new Vector3(0.9f, 1f, 0.86f);
        AddJointSeam(elbow, 0.042f * r, 0.0025f * r, Vector3.Zero, alongY: true);
        Sec[] fore = [
            new(0, 0.025f, 0.018f, 0.019f),
            new(F * 0.3f, 0.028f, 0.022f, 0.024f),
            new(F * 0.7f, 0.036f, 0.031f, 0.033f),
            new(F * 0.92f, 0.035f, 0.032f, 0.033f),
            new(F, 0.031f, 0.031f, 0.031f)
        ];
        var forearmModule = new Node3D();
        elbow.AddChild(forearmModule);
        Add(forearmModule, Loft(fore.Select(x => x.Scale(r)).ToArray(), 32, 5), ModuleTitanium(forearmVariant), new Vector3(0, -F, 0));
        var (hand, fingers, thumb) = BuildHand(r, handVariant);
        hand.Position = new Vector3(0, -F, 0);
        var wrist = Add(hand, new SphereMesh { Radius = 0.028f * r, Height = 0.058f * r, RadialSegments = 24, Rings = 12 }, JointMaterial);
        wrist.Position = new Vector3(0, -0.008f, 0);
        AddJointSeam(hand, 0.026f * r, 0.002f * r, new Vector3(0, -0.008f, 0), alongY: true);
        elbow.AddChild(hand);
        if (side < 0) arm.Scale = new Vector3(-1, 1, 1);
        return (arm, elbow, hand, fingers, thumb);
    }

    // 오른손 기준. 손바닥은 몸 안쪽(-X), 엄지는 정면(+Z).
    static (Node3D Hand, Node3D[] Fingers, Node3D Thumb) BuildHand(float r, ModuleVariant variant)
    {
        var hand = new Node3D();
        var k = Mathf.Sqrt(r);
        var material = ModuleTitanium(variant);
        var fingers = new List<Node3D>();
        Sec[] palm = [new(-0.095f, 0.011f, 0.034f, 0.036f), new(-0.07f, 0.014f, 0.039f, 0.04f), new(-0.03f, 0.015f, 0.038f, 0.037f), new(0, 0.013f, 0.026f, 0.026f)];
        Add(hand, Loft(palm.Select(x => x.Scale(k)).ToArray(), 28, 4), material);
        foreach (var (z, len) in new[] { (0.026f, 0.072f), (0.009f, 0.082f), (-0.009f, 0.077f), (-0.026f, 0.062f) })
        {
            var L = len * k;
            var f = new Node3D { Position = new Vector3(0, -0.093f * k, z * k), Rotation = new Vector3(0, 0, -0.22f) };
            fingers.Add(f);
            Add(f, new SphereMesh { Radius = 0.009f * k, Height = 0.018f * k, RadialSegments = 12, Rings = 8 }, material);
            Add(f, Loft([new(0, 0.0055f, 0.006f, 0.006f), new(L * 0.5f, 0.0075f, 0.0078f, 0.0078f), new(L, 0.0085f, 0.009f, 0.009f)], 12, 3), material, new Vector3(0, -L, 0));
            hand.AddChild(f);
        }
        var thumb = new Node3D { Position = new Vector3(-0.004f, -0.028f * k, 0.034f * k) };
        thumb.Rotation = new Vector3(-0.7f, 0, -0.35f);
        Add(thumb, Loft([new(0, 0.0065f, 0.0068f, 0.0068f), new(0.03f * k, 0.009f, 0.0095f, 0.0095f), new(0.058f * k, 0.011f, 0.012f, 0.012f)], 12, 3), material, new Vector3(0, -0.058f * k, 0));
        hand.AddChild(thumb);
        return (hand, fingers.ToArray(), thumb);
    }

    static (Node3D Leg, Node3D Knee, Node3D Ankle) BuildLeg(
        Proportions P, int side, ModuleVariant thighVariant, ModuleVariant shinVariant, ModuleVariant footVariant)
    {
        var (T, N, r, q) = P.Leg;
        var leg = new Node3D { Position = new Vector3(side * P.Hip.X, P.Hip.Y, 0), Rotation = new Vector3(0, 0, -side * q) };
        AddJointSeam(leg, 0.064f * r, 0.003f * r, new Vector3(0, -0.018f, 0), alongY: true);
        Sec[] thigh = [
            new(0, 0.046f, 0.048f, 0.044f),
            new(T * 0.12f, 0.052f, 0.054f, 0.048f),
            new(T * 0.45f, 0.068f, 0.070f, 0.066f),
            new(T * 0.8f, 0.082f, 0.078f, 0.082f),
            new(T, 0.082f, 0.078f, 0.084f),
            new(T + 0.05f, 0.062f, 0.060f, 0.066f, Cx: -0.010f),
            new(T + 0.09f, 0.026f, 0.026f, 0.026f, Cx: -0.018f)
        ];
        Add(leg, Loft(thigh.Select(x => x.Scale(r)).ToArray(), 40, 5), ModuleTitanium(thighVariant), new Vector3(0, -T, 0));

        // 스케치(sketch_01.jpg)의 허벅지 외측 장갑 패널
        var plate = BuildThighPlate(r, thighVariant);
        plate.Position = new Vector3(0.088f * r, -T * 0.42f, 0);
        leg.AddChild(plate);

        var knee = new Node3D { Position = new Vector3(0, -T, 0) };
        leg.AddChild(knee);
        var kneeSkin = Add(knee, new SphereMesh { Radius = 0.054f * r, Height = 0.12f * r, RadialSegments = 32, Rings = 16 }, JointMaterial);
        kneeSkin.Scale = new Vector3(0.9f, 1f, 0.88f);
        AddJointSeam(knee, 0.049f * r, 0.0025f * r, Vector3.Zero, alongY: true);
        Sec[] shin = [
            new(0, 0.026f, 0.027f, 0.03f),
            new(N * 0.15f, 0.029f, 0.029f, 0.034f),
            new(N * 0.55f, 0.044f, 0.04f, 0.058f),
            new(N * 0.75f, 0.046f, 0.041f, 0.056f),
            new(N * 0.92f, 0.043f, 0.045f, 0.043f),
            new(N, 0.044f, 0.046f, 0.043f)
        ];
        Add(knee, Loft(shin.Select(x => x.Scale(r)).ToArray(), 40, 5), ModuleTitanium(shinVariant), new Vector3(0, -N, 0));
        // 발: 로컬 Y(발끝 방향) → +Z 로 눕힌다, F(앞)=발바닥
        var FL = P.Foot;
        Sec[] foot = [
            new(0, 0.022f, 0.028f, 0.022f),
            new(FL * 0.12f, 0.03f, 0.033f, 0.04f),
            new(FL * 0.4f, 0.036f, 0.03f, 0.03f),
            new(FL * 0.72f, 0.042f, 0.024f, 0.02f),
            new(FL * 0.92f, 0.036f, 0.016f, 0.014f),
            new(FL, 0.012f, 0.006f, 0.005f)
        ];
        var ankle = new Node3D { Position = new Vector3(0, -N - 0.032f, -0.05f) };
        knee.AddChild(ankle);
        var ankleSkin = Add(ankle, new SphereMesh { Radius = 0.036f * r, Height = 0.07f * r, RadialSegments = 24, Rings = 12 }, JointMaterial);
        ankleSkin.Scale = new Vector3(1f, 0.8f, 0.95f);
        AddJointSeam(ankle, 0.034f * r, 0.002f * r, Vector3.Zero, alongY: true);
        var f = Add(ankle, Loft(foot.Select(x => x.Scale(Mathf.Sqrt(r))).ToArray(), 28, 4), ModuleTitanium(footVariant));
        f.Rotation = new Vector3(Mathf.Pi / 2, 0, 0);
        if (side < 0) leg.Scale = new Vector3(-1, 1, 1);
        return (leg, knee, ankle);
    }

    static void AddJointSeam(Node3D parent, float radius, float width, Vector3 at, bool alongY)
    {
        var seam = Add(parent, new TorusMesh
        {
            InnerRadius = radius - width,
            OuterRadius = radius,
            Rings = 32,
            RingSegments = 8,
        }, JointSeamMaterial, at);
        if (alongY) seam.Rotation = new Vector3(Mathf.Pi / 2, seam.Rotation.Y, seam.Rotation.Z);
    }

    /// <summary>걷기 주기와 팔 흔들기를 속도(m/s)에 맞춘다. 서 있을 때는 살아있는 듯한 미세 호흡과 자연스러운 휴식 자세.</summary>
    public void Animate(float dt, float speed)
    {
        _time += dt;
        var target = Mathf.Min(1, speed / 3);
        _amp += (target - _amp) * Mathf.Min(1, dt * 8);
        _phase += dt * (2.2f + speed * 1.1f);
        var s = Mathf.Sin(_phase);

        // 유기적인 호흡(약 4.2초 주기)과 휴식 자세
        float breath = Mathf.Sin(_time * 1.5f);
        float idleArmL = 0.05f + breath * 0.006f;
        float idleArmR = -0.05f - breath * 0.006f;
        float idlePitch = breath * 0.012f;

        _legL.Rotation = new Vector3(s * 0.5f * _amp, 0, 0);
        _legR.Rotation = new Vector3(-s * 0.5f * _amp, 0, 0);
        _kneeL.Rotation = new Vector3(Mathf.Max(0, -Mathf.Cos(_phase)) * 0.7f * _amp, 0, 0);
        _kneeR.Rotation = new Vector3(Mathf.Max(0, Mathf.Cos(_phase)) * 0.7f * _amp, 0, 0);
        _ankleL.Rotation = new Vector3(-s * 0.12f * _amp, 0, 0);
        _ankleR.Rotation = new Vector3(s * 0.12f * _amp, 0, 0);

        // 걷지 않을 때도 팔이 몸 옆으로 자연스럽게 벌어지고 미세 호흡
        _armL.Rotation = new Vector3(-s * 0.35f * _amp + idlePitch * (1 - _amp), 0, idleArmL * (1 - _amp) + 0.03f * _amp);
        _armR.Rotation = new Vector3(s * 0.35f * _amp + idlePitch * (1 - _amp), 0, idleArmR * (1 - _amp) - 0.03f * _amp);
        _elbowL.Rotation = new Vector3(-0.14f - Mathf.Max(0, s) * 0.22f * _amp, 0, 0);
        _elbowR.Rotation = new Vector3(-0.14f - Mathf.Max(0, -s) * 0.22f * _amp, 0, 0);
        _handL.Rotation = new Vector3(0.025f * s * _amp, 0, 0);
        _handR.Rotation = new Vector3(-0.025f * s * _amp, 0, 0);
        var fingerCurl = 0.08f + 0.035f * breath + 0.08f * _amp;
        _thumbL.Rotation = new Vector3(-0.7f + s * 0.025f * _amp, 0, -0.35f + fingerCurl * 0.14f);
        _thumbR.Rotation = new Vector3(-0.7f - s * 0.025f * _amp, 0, -0.35f - fingerCurl * 0.14f);
        foreach (var finger in _fingersL) finger.Rotation = new Vector3(0, 0, -0.22f - fingerCurl);
        foreach (var finger in _fingersR) finger.Rotation = new Vector3(0, 0, -0.22f - fingerCurl);

        // 머리: 미세한 호흡 고개 끄덕임과 자연스러운 시선 방향 탐색
        float headPitch = Mathf.Sin(_time * 1.5f) * 0.012f;
        float headYaw = Mathf.Sin(_time * 0.55f) * 0.025f;
        _head.Rotation = new Vector3(headPitch, headYaw, 0);
    }
}
