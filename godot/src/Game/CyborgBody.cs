using Godot;
using DollyForge.Core;

namespace DollyForge.Game;

/// <summary>
/// 전신 티타늄 인형. 성체 비례의 연속 곡면과 분해 가능한 관절을 갖고, 생체 기관이나 감각부는 표현하지 않는다.
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
        Sec[] Torso, float Sq, (float Y0, float Y1, float R) Neck, (float Base, float Scale) Head,
        (float X, float Y) Shoulder, (float Upper, float Fore, float R, float Bicep) Arm,
        (float X, float Y) Hip, (float Thigh, float Shin, float R, float Q) Leg, float Foot, float WaistY,
        float UnderbustY, float LowHipY);

    // 여성형: 성체 키 1.80 m, 허리·골반·가슴의 완만한 전환과 실물 크기의 인체 비례
    static readonly Proportions Female = new(
        [
            new(0.70f, 0.16f, 0.098f, 0.108f, Cz: -0.005f),
            new(0.75f, 0.17f, 0.10f, 0.116f, Glute: 0.014f),
            new(0.80f, 0.19f, 0.102f, 0.122f, Glute: 0.028f),
            new(0.85f, 0.198f, 0.104f, 0.126f, Glute: 0.036f),
            new(0.89f, 0.198f, 0.104f, 0.126f, Glute: 0.036f),
            new(0.945f, 0.188f, 0.098f, 0.116f, Glute: 0.026f),
            new(0.99f, 0.158f, 0.084f, 0.096f, Glute: 0.016f),
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
        1.0f, (1.36f, 1.5f, 0.042f), (1.425f, 1.0f),         (0.168f, 1.335f), (0.285f, 0.24f, 1.0f, 0.009f),
        (0.065f, 0.84f), (0.40f, 0.38f, 1.04f, 0.10f), 0.228f, 1.05f, 1.16f, 0.94f);

    // 남성형: 성체 키 1.86m, 역삼각형 흉근과 다부진 실루엣
    static readonly Proportions Male = new(
        [
            new(0.74f, 0.15f, 0.09f, 0.10f),
            new(0.80f, 0.16f, 0.098f, 0.112f, Glute: 0.008f),
            new(0.86f, 0.181f, 0.102f, 0.114f, Glute: 0.018f),
            new(0.945f, 0.194f, 0.108f, 0.12f, Glute: 0.026f),
            new(0.99f, 0.176f, 0.098f, 0.108f, Glute: 0.020f),
            new(1.06f, 0.144f, 0.094f, 0.092f),
            new(1.14f, 0.158f, 0.102f, 0.098f),
            new(1.22f, 0.180f, 0.110f, 0.106f),
            new(1.30f, 0.202f, 0.118f, 0.112f, Pec: 0.028f),  // 흉근 볼륨
            new(1.37f, 0.212f, 0.114f, 0.108f, Pec: 0.022f),
            new(1.425f, 0.210f, 0.090f, 0.095f),
            new(1.465f, 0.145f, 0.068f, 0.074f),
            new(1.49f, 0.068f, 0.058f, 0.058f),
        ],
        1.18f, (1.44f, 1.6f, 0.055f), (1.525f, 1.08f), (0.212f, 1.415f), (0.31f, 0.27f, 1.22f, 0.012f),
        (0.074f, 0.9f), (0.44f, 0.42f, 1.07f, 0.02f), 0.26f, 1.06f, 1.22f, 0.98f);

    // ------------------------------------------------------------------ 재질

    static StandardMaterial3D? _shell, _heavyTitanium, _lightTitanium;
    static StandardMaterial3D? _jointMat, _jointSeamMat;

    /// <summary>전신 외장과 지체를 이루는 브러시드 티타늄 합금</summary>
    static StandardMaterial3D Shell => _shell ??= new StandardMaterial3D
    {
        AlbedoColor = new Color(0.60f, 0.66f, 0.71f),
        Metallic = 0.78f,
        Roughness = 0.52f,
        NormalEnabled = ResourceLoader.Exists("res://assets/tex/metal_plate/nor_gl.jpg"),
        NormalTexture = ResourceLoader.Exists("res://assets/tex/metal_plate/nor_gl.jpg")
            ? GD.Load<Texture2D>("res://assets/tex/metal_plate/nor_gl.jpg")
            : null,
        NormalScale = 0.035f,
    };

    static StandardMaterial3D HeavyTitanium => _heavyTitanium ??= new StandardMaterial3D
    {
        AlbedoColor = new Color(0.52f, 0.59f, 0.63f),
        Metallic = 0.78f,
        Roughness = 0.56f,
    };

    static StandardMaterial3D LightTitanium => _lightTitanium ??= new StandardMaterial3D
    {
        AlbedoColor = new Color(0.64f, 0.68f, 0.71f),
        Metallic = 0.78f,
        Roughness = 0.46f,
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

    // ------------------------------------------------------------------ 조립

    public float Height { get; private set; }
    Node3D _head = null!;
    Node3D _legL = null!, _legR = null!, _kneeL = null!, _kneeR = null!, _ankleL = null!, _ankleR = null!;
    Node3D _armL = null!, _armR = null!, _elbowL = null!, _elbowR = null!, _handL = null!, _handR = null!, _thumbL = null!, _thumbR = null!;
    Node3D[] _fingersL = [], _fingersR = [];
    float _phase, _amp;

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

    static Node3D BuildHead(Proportions P, Node3D g)
    {
        var (b, k) = P.Head;
        var head = new Node3D { Position = new Vector3(0, b, 0) };

        // 얼굴 요소와 센서 없이 앞뒤가 같은 둥근 티타늄 양철 머리 셸.
        Sec[] s =
        [
            new(0, 0.045f, 0.048f, 0.048f),
            new(0.018f, 0.065f, 0.068f, 0.068f),
            new(0.052f, 0.084f, 0.088f, 0.088f),
            new(0.092f, 0.094f, 0.098f, 0.098f),
            new(0.132f, 0.095f, 0.100f, 0.100f),
            new(0.170f, 0.087f, 0.092f, 0.092f),
            new(0.204f, 0.068f, 0.074f, 0.074f),
            new(0.228f, 0.040f, 0.044f, 0.044f),
            new(0.240f, 0.006f, 0.007f, 0.007f),
        ];
        Add(head, Loft(s.Select(x => x with { Y = x.Y * k, Rx = x.Rx * k, F = x.F * k, B = x.B * k, Cz = x.Cz * k }).ToArray(), 56, 7), Shell);

        g.AddChild(head);
        return head;
    }

    static (Node3D Arm, Node3D Elbow, Node3D Hand, Node3D[] Fingers, Node3D Thumb) BuildArm(
        Proportions P, int side, ModuleVariant upperVariant, ModuleVariant forearmVariant, ModuleVariant handVariant)
    {
        var (U, F, r, bicep) = P.Arm;
        var arm = new Node3D { Position = new Vector3(side * P.Shoulder.X, P.Shoulder.Y, 0), Rotation = new Vector3(0, 0, side * 0.05f) };

        // 어깨 접합부: 몸통 외피 안으로 살짝 묻혀 경계 없이 연결된다.
        var shoulderOverlap = 0.018f * r;

        // 상완: 어깨에서 이두 볼륨을 거쳐 팔꿈치로 가늘게 taper.
        // Bust 파라미터로 정면(+Z)에 이두볼륨을, Sq로 단면을 둥글게 마감한다.
        Sec[] upper = [
            new(0, 0.056f, 0.052f, 0.054f, Bust: bicep * 0.45f, Sq: 1.15f),      // 어깨 연결부 (몸통과 겹침)
            new(U * 0.15f, 0.053f, 0.060f + bicep, 0.056f, Bust: bicep, Sq: 1.2f),  // 이두 볼륨 정면
            new(U * 0.38f, 0.047f, 0.052f + bicep * 0.7f, 0.050f, Bust: bicep * 0.6f, Sq: 1.15f),
            new(U * 0.65f, 0.042f, 0.044f, 0.046f, Bust: bicep * 0.3f, Sq: 1.1f),
            new(U, 0.039f, 0.037f, 0.039f, Bust: bicep * 0.15f, Sq: 1.1f),        // 팔끝
            new(U + shoulderOverlap, 0.035f, 0.033f, 0.035f),                      // 팔꿈치 관절부
        ];
        var upperModule = new Node3D();
        arm.AddChild(upperModule);
        Add(upperModule, Loft(upper.Select(x => x.Scale(r)).ToArray(), 36, 6), ModuleTitanium(upperVariant), new Vector3(0, -U - shoulderOverlap, 0));

        var elbow = new Node3D { Position = new Vector3(0, -U - shoulderOverlap, 0), Rotation = new Vector3(-0.10f, 0, 0) };
        upperModule.AddChild(elbow);
        // 팔꿈치: 도드라진 구 형태보다 살에 파묻힌 관절 느낌으로, 이음선을 아주 얇게.
        var elbowSkin = Add(elbow, new SphereMesh { Radius = 0.040f * r, Height = 0.085f * r, RadialSegments = 32, Rings = 14 }, JointMaterial);
        elbowSkin.Scale = new Vector3(0.85f, 1f, 0.9f);
        AddJointSeam(elbow, 0.034f * r, 0.0012f * r, Vector3.Zero, alongY: true);

        Sec[] fore = [
            new(0, 0.034f, 0.032f, 0.034f),                                   // 팔꿈치에서 시작
            new(F * 0.22f, 0.030f, 0.028f, 0.030f),
            new(F * 0.52f, 0.028f, 0.026f, 0.028f),
            new(F * 0.8f, 0.027f, 0.025f, 0.026f),
            new(F, 0.025f, 0.024f, 0.025f),                                    // 손목
            new(F + 0.014f, 0.022f, 0.021f, 0.022f),                           // 손목 관절부
        ];
        var forearmModule = new Node3D();
        elbow.AddChild(forearmModule);
        Add(forearmModule, Loft(fore.Select(x => x.Scale(r)).ToArray(), 36, 6), ModuleTitanium(forearmVariant), new Vector3(0, -F - 0.014f, 0));
        var (hand, fingers, thumb) = BuildHand(r, handVariant);
        hand.Position = new Vector3(0, -F - 0.014f, 0);
        var wrist = Add(hand, new SphereMesh { Radius = 0.026f * r, Height = 0.050f * r, RadialSegments = 24, Rings = 12 }, JointMaterial);
        wrist.Position = new Vector3(0, -0.006f, 0);
        AddJointSeam(hand, 0.021f * r, 0.0012f * r, new Vector3(0, -0.006f, 0), alongY: true);
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
        // 골반 접합부는 외부 링을 드러내지 않고 겹치는 티타늄 외피 안에 숨긴다.
        Sec[] thigh = [
            new(0, 0.062f, 0.060f, 0.058f),
            new(T * 0.12f, 0.074f, 0.070f, 0.066f),
            new(T * 0.38f, 0.084f, 0.078f, 0.076f),
            new(T * 0.7f, 0.086f, 0.080f, 0.082f),
            new(T, 0.082f, 0.078f, 0.084f),
            new(T + 0.05f, 0.072f, 0.070f, 0.074f, Cx: -0.008f),
            new(T + 0.12f, 0.064f, 0.064f, 0.068f, Cx: -0.014f),
            new(T + 0.18f, 0.042f, 0.044f, 0.046f, Cx: -0.018f)
        ];
        Add(leg, Loft(thigh.Select(x => x.Scale(r)).ToArray(), 40, 5), ModuleTitanium(thighVariant), new Vector3(0, -T, 0));

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

    /// <summary>인형의 관절은 이동할 때만 움직인다. 정지 중에는 고정된 중립 자세를 유지한다.</summary>
    public void Animate(float dt, float speed)
    {
        var target = Mathf.Min(1, speed / 3);
        _amp = Mathf.MoveToward(_amp, target, dt * (target > _amp ? 5.5f : 3.2f));
        var strideLength = Mathf.Lerp(1.45f, 2.2f, target);
        var cadence = speed > 0.08f ? Mathf.Tau * speed / strideLength : 1.2f;
        _phase = Mathf.PosMod(_phase + dt * cadence, Mathf.Tau);
        var s = Mathf.Sin(_phase);

        _legL.Rotation = new Vector3(s * 0.5f * _amp, 0, 0);
        _legR.Rotation = new Vector3(-s * 0.5f * _amp, 0, 0);
        _kneeL.Rotation = new Vector3(Mathf.Max(0, -Mathf.Cos(_phase)) * 0.7f * _amp, 0, 0);
        _kneeR.Rotation = new Vector3(Mathf.Max(0, Mathf.Cos(_phase)) * 0.7f * _amp, 0, 0);
        _ankleL.Rotation = new Vector3(-s * 0.12f * _amp, 0, 0);
        _ankleR.Rotation = new Vector3(s * 0.12f * _amp, 0, 0);

        _armL.Rotation = new Vector3(-s * 0.35f * _amp, 0, 0.03f * _amp);
        _armR.Rotation = new Vector3(s * 0.35f * _amp, 0, -0.03f * _amp);
        _elbowL.Rotation = new Vector3(-0.14f - Mathf.Max(0, s) * 0.22f * _amp, 0, 0);
        _elbowR.Rotation = new Vector3(-0.14f - Mathf.Max(0, -s) * 0.22f * _amp, 0, 0);
        _handL.Rotation = new Vector3(0.025f * s * _amp, 0, 0);
        _handR.Rotation = new Vector3(-0.025f * s * _amp, 0, 0);
        var fingerCurl = 0.08f + 0.08f * _amp;
        _thumbL.Rotation = new Vector3(-0.7f + s * 0.025f * _amp, 0, -0.35f + fingerCurl * 0.14f);
        _thumbR.Rotation = new Vector3(-0.7f - s * 0.025f * _amp, 0, -0.35f - fingerCurl * 0.14f);
        foreach (var finger in _fingersL) finger.Rotation = new Vector3(0, 0, -0.22f - fingerCurl);
        foreach (var finger in _fingersR) finger.Rotation = new Vector3(0, 0, -0.22f - fingerCurl);
        var idle = 1 - _amp;
        _head.Rotation = new Vector3(Mathf.Sin(_phase * 0.37f) * 0.018f * idle,
            Mathf.Sin(_phase * 0.23f) * 0.065f * idle, 0);
    }
}
