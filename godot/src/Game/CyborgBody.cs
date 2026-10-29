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

    // 여성형: 성체 키 1.80 m, 허리·골반·가슴의 완만한 전환과 실물 크기의 인체 비례
    static readonly Proportions Female = new(
        [
            // 골반 아래는 두 허벅지 안쪽에 숨을 만큼 좁고 얇게 줄어 가랑이에서 닫힌다 (치마처럼 부풀지 않게)
            new(0.70f, 0.03f, 0.03f, 0.035f, Cz: -0.004f),       // 가랑이: 두 허벅지 안쪽에서 닫힌다
            new(0.725f, 0.085f, 0.052f, 0.058f, Cz: -0.004f),
            // 엉덩이·가슴 볼륨은 절제한다: 성별은 실루엣과 비례로만, 성적인 외형은 도드라지지 않게
            new(0.76f, 0.142f, 0.07f, 0.074f, Glute: 0.006f),
            new(0.80f, 0.170f, 0.084f, 0.088f, Glute: 0.014f),
            new(0.83f, 0.182f, 0.092f, 0.098f, Glute: 0.018f),   // 엉덩이·골반의 가장 넓은 곳
            new(0.88f, 0.184f, 0.096f, 0.106f, Glute: 0.018f),
            new(0.945f, 0.176f, 0.094f, 0.102f, Glute: 0.012f),
            new(0.99f, 0.152f, 0.084f, 0.092f, Glute: 0.006f),
            new(1.05f, 0.112f, 0.070f, 0.068f),                // 잘록한 허리 (폭 0.112m)
            new(1.11f, 0.124f, 0.076f, 0.074f),
            new(1.16f, 0.138f, 0.080f, 0.078f, Bust: 0.012f), // 언더버스트 시작 라인
            new(1.22f, 0.154f, 0.086f, 0.084f, Bust: 0.026f),
            new(1.265f, 0.158f, 0.085f, 0.084f, Bust: 0.024f),
            new(1.31f, 0.158f, 0.074f, 0.078f, Bust: 0.010f),
            new(1.35f, 0.152f, 0.058f, 0.068f),                // 쇄골 라인
            new(1.385f, 0.096f, 0.046f, 0.052f),
            new(1.4f, 0.048f, 0.040f, 0.040f),
        ],
        1.0f, (1.36f, 1.5f, 0.042f), (1.425f, 0.86f),         (0.160f, 1.315f), (0.305f, 0.265f, 1.0f, 0.009f),
        (0.092f, 0.84f), (0.46f, 0.44f, 1.04f, 0.045f), 0.228f, 1.05f, 1.16f, 0.94f);

    // 남성형: 성체 키 1.86m, 역삼각형 흉근과 다부진 실루엣
    static readonly Proportions Male = new(
        [
            // 골반 아래는 두 허벅지 안쪽에 숨을 만큼 좁고 얇게 줄어 가랑이에서 닫힌다
            new(0.70f, 0.035f, 0.035f, 0.04f),
            new(0.735f, 0.11f, 0.066f, 0.074f),
            new(0.775f, 0.148f, 0.080f, 0.090f, Glute: 0.006f),
            new(0.82f, 0.166f, 0.090f, 0.104f, Glute: 0.012f),
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
        1.18f, (1.44f, 1.6f, 0.055f), (1.525f, 0.94f), (0.214f, 1.395f), (0.33f, 0.295f, 1.22f, 0.012f),
        (0.094f, 0.9f), (0.50f, 0.48f, 1.07f, 0.02f), 0.26f, 1.06f, 1.22f, 0.98f);

    // ------------------------------------------------------------------ 재질

    static StandardMaterial3D? _shell, _heavyTitanium, _lightTitanium;
    static StandardMaterial3D? _jointSeamMat;

    /// <summary>전신 외장과 지체를 이루는 브러시드 티타늄 합금</summary>
    static StandardMaterial3D Shell => _shell ??= new StandardMaterial3D
    {
        AlbedoColor = new Color(0.68f, 0.71f, 0.74f),
        Metallic = 0.8f,
        Roughness = 0.42f,
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

    static StandardMaterial3D JointSeamMaterial => _jointSeamMat ??= new StandardMaterial3D
    {
        AlbedoColor = new Color(0.28f, 0.35f, 0.39f),
        Metallic = 0.86f,
        Roughness = 0.5f,
    };

    // ------------------------------------------------------------------ 조립

    public float Height { get; private set; }
    /// <summary>다리를 늘린 길이 (m, 허벅지·정강이 각 6 cm)</summary>
    const float LegLift = 0.12f;
    /// <summary>팔이 몸에서 벌어진 각도 (rad): 골반에 닿지 않는 편안한 선 자세</summary>
    const float ArmSplay = 0.11f;
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
        // 다리를 사람 비례로 늘린 만큼(LegLift) 위 몸통을 올리고, 전체 크기를 줄여 키는 그대로 둔다.
        // 가랑이 높이: 키의 42 % → 약 47 % (실제 사람 46–47 %)
        var headTop = P.Head.Base + 0.24f * P.Head.Scale;
        var scale = (gender == 1 ? 1.045f : 1.08f) * headTop / (headTop + LegLift);
        var g = new CyborgBody
        {
            Height = gender == 1 ? 1.86f : 1.80f,
            Scale = Vector3.One * scale,
        };
        var upper = new Node3D { Name = "Upper", Position = new Vector3(0, LegLift, 0) };
        g.AddChild(upper);
        var torso = P.Torso
            .Append(new Sec(P.Neck.Y1, P.Neck.R * 0.92f, P.Neck.R * 0.92f, P.Neck.R * 0.92f, Cz: -0.008f))
            .Select(s => s with { Sq = P.Sq })
            .ToArray();
        Add(upper, Loft(torso, 64, 7), ModuleTitanium(body.VariantAt(BodySlot.Torso)));

        g._head = BuildHead(P, upper);
        (g._armL, g._elbowL, g._handL, g._fingersL, g._thumbL) = BuildArm(P, -1,
            body.VariantAt(BodySlot.UpperArmL), body.VariantAt(BodySlot.ForearmL), body.VariantAt(BodySlot.HandL));
        (g._armR, g._elbowR, g._handR, g._fingersR, g._thumbR) = BuildArm(P, 1,
            body.VariantAt(BodySlot.UpperArmR), body.VariantAt(BodySlot.ForearmR), body.VariantAt(BodySlot.HandR));
        (g._legL, g._kneeL, g._ankleL) = BuildLeg(P, -1,
            body.VariantAt(BodySlot.ThighL), body.VariantAt(BodySlot.ShinL), body.VariantAt(BodySlot.FootL));
        (g._legR, g._kneeR, g._ankleR) = BuildLeg(P, 1,
            body.VariantAt(BodySlot.ThighR), body.VariantAt(BodySlot.ShinR), body.VariantAt(BodySlot.FootR));
        foreach (var n in new[] { g._armL, g._armR }) upper.AddChild(n);
        foreach (var n in new[] { g._legL, g._legR })
        {
            n.Position += new Vector3(0, LegLift, 0);
            g.AddChild(n);
        }
        return g;
    }

    static MeshInstance3D Add(Node3D parent, Mesh mesh, Material mat, Vector3 at = default)
    {
        var m = new MeshInstance3D { Mesh = mesh, MaterialOverride = mat, Position = at, CastShadow = GeometryInstance3D.ShadowCastingSetting.On };
        parent.AddChild(m);
        return m;
    }

    static Node3D BuildHead(Proportions P, Node3D parent)
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

        parent.AddChild(head);
        return head;
    }

    static (Node3D Arm, Node3D Elbow, Node3D Hand, Node3D[] Fingers, Node3D Thumb) BuildArm(
        Proportions P, int side, ModuleVariant upperVariant, ModuleVariant forearmVariant, ModuleVariant handVariant)
    {
        var (U, F, r, bicep) = P.Arm;
        var arm = new Node3D { Position = new Vector3(side * P.Shoulder.X, P.Shoulder.Y, 0), Rotation = new Vector3(0, 0, side * ArmSplay) };

        // 위팔: 둥근 단면(초타원 아님)이 어깨에서 팔꿈치로 서서히 가늘어진다. 위쪽 끝은 몸통 안으로 들어가 숨고,
        // 어깨 바깥은 삼각근 곡면(납작한 구)이 몸통 어깨선과 위팔을 한 덩어리로 감싼다.
        Sec[] upper = [
            new(0, 0.036f, 0.035f, 0.036f),                       // 팔꿈치
            new(U * 0.3f, 0.040f, 0.040f + bicep * 0.4f, 0.040f),
            new(U * 0.6f, 0.044f, 0.045f + bicep, 0.043f),        // 이두
            new(U * 0.85f, 0.047f, 0.046f, 0.046f),
            new(U, 0.048f, 0.046f, 0.047f),                       // 어깨 관절
            new(U + 0.03f, 0.044f, 0.042f, 0.043f),               // 몸통 안으로 (보이지 않음)
        ];
        var upperModule = new Node3D();
        arm.AddChild(upperModule);
        var upperMat = ModuleTitanium(upperVariant);
        Add(upperModule, Loft(upper.Select(x => x.Scale(r)).ToArray(), 36, 6), upperMat, new Vector3(0, -U, 0));
        // 삼각근: 몸통 어깨선보다 솟지 않게 낮고 둥글게 (견장처럼 각지지 않게)
        var deltoid = Add(upperModule, new SphereMesh { Radius = 0.048f * r, Height = 0.096f * r, RadialSegments = 32, Rings = 16 }, upperMat, new Vector3(-0.008f * r, -0.022f, 0));
        deltoid.Scale = new Vector3(0.98f, 1.08f, 0.95f);

        var elbow = new Node3D { Position = new Vector3(0, -U, 0), Rotation = new Vector3(-0.10f, 0, 0) };
        upperModule.AddChild(elbow);
        // 관절 곡면은 같은 티타늄이라 색 경계가 없고, 가는 분할선만 모듈의 이음을 보여 준다
        var elbowSkin = Add(elbow, new SphereMesh { Radius = 0.036f * r, Height = 0.072f * r, RadialSegments = 32, Rings = 14 }, upperMat);
        elbowSkin.Scale = new Vector3(0.95f, 1f, 0.95f);
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
        Add(forearmModule, Loft(fore.Select(x => x.Scale(r)).ToArray(), 36, 6), ModuleTitanium(forearmVariant), new Vector3(0, -F - 0.014f * r, 0));
        var (hand, fingers, thumb) = BuildHand(r, handVariant);
        hand.Position = new Vector3(0, -F - 0.014f * r, 0);
        var wrist = Add(hand, new SphereMesh { Radius = 0.024f * r, Height = 0.046f * r, RadialSegments = 24, Rings = 12 }, ModuleTitanium(forearmVariant));
        wrist.Position = new Vector3(0, -0.006f * r, 0);
        AddJointSeam(hand, 0.021f * r, 0.0012f * r, new Vector3(0, -0.006f * r, 0), alongY: true);
        elbow.AddChild(hand);
        if (side < 0) arm.Scale = new Vector3(-1, 1, 1);
        return (arm, elbow, hand, fingers, thumb);
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

    static (Node3D Leg, Node3D Knee, Node3D Ankle) BuildLeg(
        Proportions P, int side, ModuleVariant thighVariant, ModuleVariant shinVariant, ModuleVariant footVariant)
    {
        var (T, N, r, q) = P.Leg;
        var leg = new Node3D { Position = new Vector3(side * P.Hip.X, P.Hip.Y, 0), Rotation = new Vector3(0, 0, -side * q) };

        // 허벅지: 둥근 단면이 고관절에서 무릎으로 가늘어진다. 바깥쪽(Cx)이 살짝 부풀어 사람 허벅지의 곡선을 만들고,
        // 위쪽 끝은 골반 안으로 들어가 숨는다 (몸통 골반 폭 안쪽).
        Sec[] thigh = [
            new(0, 0.044f, 0.045f, 0.046f),                       // 무릎 위
            new(T * 0.25f, 0.050f, 0.050f, 0.053f),
            new(T * 0.5f, 0.064f, 0.062f, 0.066f, Cx: 0.003f),
            new(T * 0.75f, 0.077f, 0.073f, 0.080f, Cx: 0.006f),
            new(T * 0.9f, 0.087f, 0.080f, 0.090f, Cx: 0.007f),    // 고관절 아래 볼륨: 골반 바깥선을 그대로 이어받는다
            new(T, 0.092f, 0.083f, 0.094f, Cx: 0.006f),           // 골반 바깥선과 맞닿는다 (두 허벅지 안쪽은 서로 맞닿음)
            new(T + 0.08f, 0.084f, 0.074f, 0.084f),               // 골반 안으로 (보이지 않음)
        ];
        var thighMat = ModuleTitanium(thighVariant);
        Add(leg, Loft(thigh.Select(x => x.Scale(r)).ToArray(), 40, 5), thighMat, new Vector3(0, -T, 0));

        var knee = new Node3D { Position = new Vector3(0, -T, 0) };
        leg.AddChild(knee);
        // 무릎: 도드라진 구 형태보다 매끄러운 관절 감싸는 형태.
        var kneeSkin = Add(knee, new SphereMesh { Radius = 0.045f * r, Height = 0.092f * r, RadialSegments = 32, Rings = 16 }, thighMat);
        kneeSkin.Scale = new Vector3(0.96f, 1f, 0.95f);
        AddJointSeam(knee, 0.045f * r, 0.0012f * r, Vector3.Zero, alongY: true);

        // 하퇴: 발목(아래, 가늘어짐)에서 무릎(위)으로 넓어지며 종아리 볼륨.
        Sec[] shin = [
            new(0, 0.030f, 0.031f, 0.033f),                       // 발목
            new(N * 0.15f, 0.032f, 0.032f, 0.036f),
            new(N * 0.45f, 0.040f, 0.038f, 0.050f),               // 종아리 (뒤쪽 볼륨)
            new(N * 0.7f, 0.044f, 0.040f, 0.052f),
            new(N * 0.9f, 0.044f, 0.043f, 0.046f),
            new(N, 0.044f, 0.045f, 0.046f),                       // 무릎 아래 (허벅지와 같은 폭)
            new(N + 0.032f, 0.040f, 0.041f, 0.041f),
        ];
        Add(knee, Loft(shin.Select(x => x.Scale(r)).ToArray(), 40, 5), ModuleTitanium(shinVariant), new Vector3(0, -N - 0.032f, 0));
        // 발: 로컬 Y(발끝 방향) → +Z 로 눕힌다, F(앞)=발바닥
        var FL = P.Foot;
        // 발: 둥근 뒤꿈치, 도톰한 발등, 둥글게 닫히는 발끝 (뾰족한 슬리퍼처럼 보이지 않게). F = 발바닥 쪽, B = 발등 쪽
        Sec[] foot = [
            new(-0.012f, 0.014f, 0.016f, 0.014f),
            new(0, 0.026f, 0.030f, 0.026f),
            new(FL * 0.12f, 0.033f, 0.034f, 0.042f),
            new(FL * 0.4f, 0.039f, 0.031f, 0.034f),
            new(FL * 0.72f, 0.045f, 0.026f, 0.024f),
            new(FL * 0.9f, 0.040f, 0.020f, 0.018f),
            new(FL * 0.98f, 0.026f, 0.013f, 0.012f),
            new(FL, 0.012f, 0.006f, 0.006f)
        ];
        var ankle = new Node3D { Position = new Vector3(0, -N - 0.032f - 0.032f, -0.05f) };
        knee.AddChild(ankle);
        var ankleSkin = Add(ankle, new SphereMesh { Radius = 0.032f * r, Height = 0.062f * r, RadialSegments = 24, Rings = 12 }, ModuleTitanium(shinVariant));
        ankleSkin.Scale = new Vector3(1f, 0.8f, 0.95f);
        AddJointSeam(ankle, 0.032f * r, 0.0012f * r, Vector3.Zero, alongY: true);
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

        _armL.Rotation = new Vector3(-s * 0.35f * _amp, 0, -ArmSplay + 0.03f * _amp);
        _armR.Rotation = new Vector3(s * 0.35f * _amp, 0, ArmSplay - 0.03f * _amp);
        _elbowL.Rotation = new Vector3(-0.14f - Mathf.Max(0, s) * 0.22f * _amp, 0, 0);
        _elbowR.Rotation = new Vector3(-0.14f - Mathf.Max(0, -s) * 0.22f * _amp, 0, 0);
        _handL.Rotation = new Vector3(0.025f * s * _amp, 0, 0);
        _handR.Rotation = new Vector3(-0.025f * s * _amp, 0, 0);
        var fingerCurl = 0.08f + 0.08f * _amp;
        _thumbL.Rotation = new Vector3(-0.7f + s * 0.025f * _amp, 0, -0.35f + fingerCurl * 0.14f);
        _thumbR.Rotation = new Vector3(-0.7f - s * 0.025f * _amp, 0, -0.35f - fingerCurl * 0.14f);
        foreach (var finger in _fingersL) finger.Rotation = new Vector3(0, 0, -0.18f - fingerCurl);
        foreach (var finger in _fingersR) finger.Rotation = new Vector3(0, 0, -0.18f - fingerCurl);
        var idle = 1 - _amp;
        _head.Rotation = new Vector3(Mathf.Sin(_phase * 0.37f) * 0.018f * idle,
            Mathf.Sin(_phase * 0.23f) * 0.065f * idle, 0);
    }
}
