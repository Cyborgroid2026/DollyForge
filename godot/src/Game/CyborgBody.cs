using Godot;

namespace DollyForge.Game;

/// <summary>
/// 전신 사이보그 몸. 해부학적 단면(타원)을 높이별로 정의하고 Catmull-Rom 으로 이어 하나의 곡면으로 만든다(loft).
/// 디자인 원칙: 어디에서도 참조하지 않은, 특징이 거의 없는 완벽한 사이보그.
/// 얼굴·문양·발광 표식 없이 균일한 새틴 티타늄, 관절에는 정밀 가공을 암시하는 아주 가는 분할선만.
/// 성별은 실루엣과 비례(키, 어깨/골반, 허리, 절제된 가슴·골반 곡선)로만 구분된다.
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
        return new Sec(C(x => x.Y), Mathf.Max(C(x => x.Rx), 0.001f), Mathf.Max(C(x => x.F), 0.001f), Mathf.Max(C(x => x.B), 0.001f),
            C(x => x.Cx), C(x => x.Cz), C(x => x.Sq), C(x => x.Bust), C(x => x.Glute), C(x => x.Pec));
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
                var g = Bump(th, Mathf.Pi / 2 + side * 0.56f, 0.36f);
                z += s.Bust * g;
                x += s.Bust * g * c * 0.3f;
            }
            if (s.Pec > 0) z += s.Pec * Bump(th, Mathf.Pi / 2 + side * 0.62f, 0.5f);
            if (s.Glute > 0)
            {
                var g = Bump(th, -Mathf.Pi / 2 + side * 0.52f, 0.48f);
                z -= s.Glute * g;
                x += s.Glute * g * c * 0.2f;
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
        var pts = new List<Vector3>();
        foreach (var r in rings)
            for (var k = 0; k < radial; k++) pts.Add(RingPoint(r, k * Mathf.Tau / radial));
        foreach (var p in pts) st.AddVertex(p);
        void Tri(int a, int b, int c) { st.AddIndex(a); st.AddIndex(c); st.AddIndex(b); } // Godot: 시계 방향이 앞면
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
        (float X, float Y, float Delt) Shoulder, (float Upper, float Fore, float R, float Bicep) Arm,
        (float X, float Y) Hip, (float Thigh, float Shin, float R, float Q) Leg, float Foot, float WaistY);

    // 여성: 키 1.66, 허리/골반 ≈ 0.68 / 남성: 키 1.78, 허리/골반 ≈ 0.88
    static readonly Proportions Female = new(
        [
            new(0.735f, 0.03f, 0.03f, 0.04f, Cz: -0.005f), new(0.765f, 0.1f, 0.065f, 0.08f),
            new(0.815f, 0.165f, 0.088f, 0.105f, Glute: 0.014f), new(0.87f, 0.185f, 0.095f, 0.12f, Glute: 0.02f),
            new(0.93f, 0.175f, 0.09f, 0.105f, Glute: 0.01f), new(1.0f, 0.14f, 0.08f, 0.08f), new(1.05f, 0.125f, 0.075f, 0.072f),
            new(1.11f, 0.132f, 0.078f, 0.076f), new(1.17f, 0.142f, 0.083f, 0.082f), new(1.22f, 0.15f, 0.085f, 0.084f, Bust: 0.026f),
            new(1.265f, 0.155f, 0.083f, 0.085f, Bust: 0.024f), new(1.31f, 0.158f, 0.075f, 0.08f, Bust: 0.004f),
            new(1.35f, 0.155f, 0.06f, 0.07f), new(1.385f, 0.1f, 0.05f, 0.055f), new(1.4f, 0.05f, 0.045f, 0.045f),
        ],
        1.0f, (1.36f, 1.5f, 0.042f), (1.425f, 1.0f, 0.054f, 0.022f), (0.165f, 1.335f, 0.042f), (0.285f, 0.24f, 1.0f, 0f),
        (0.088f, 0.84f), (0.4f, 0.38f, 1.04f, 0.045f), 0.225f, 1.05f);

    static readonly Proportions Male = new(
        [
            new(0.785f, 0.035f, 0.035f, 0.04f), new(0.815f, 0.105f, 0.07f, 0.08f), new(0.87f, 0.17f, 0.095f, 0.1f, Glute: 0.008f),
            new(0.92f, 0.176f, 0.098f, 0.108f, Glute: 0.012f), new(0.99f, 0.156f, 0.094f, 0.095f), new(1.06f, 0.138f, 0.092f, 0.088f),
            new(1.14f, 0.15f, 0.098f, 0.094f), new(1.22f, 0.172f, 0.104f, 0.102f), new(1.3f, 0.192f, 0.11f, 0.106f, Pec: 0.014f),
            new(1.37f, 0.203f, 0.108f, 0.102f, Pec: 0.01f), new(1.425f, 0.205f, 0.085f, 0.09f), new(1.465f, 0.14f, 0.065f, 0.07f),
            new(1.49f, 0.065f, 0.055f, 0.055f),
        ],
        1.18f, (1.44f, 1.6f, 0.055f), (1.525f, 1.08f, 0.066f, 0.032f), (0.205f, 1.415f, 0.056f), (0.31f, 0.27f, 1.22f, 0.012f),
        (0.09f, 0.9f), (0.44f, 0.42f, 1.07f, 0.02f), 0.26f, 1.06f);

    // ------------------------------------------------------------------ 재질

    static StandardMaterial3D? _shell, _seam;

    /// <summary>새틴 마감 티타늄: 거울 반사가 아닌, 정밀 가공된 금속의 부드러운 광택</summary>
    static StandardMaterial3D Shell => _shell ??= new StandardMaterial3D
    {
        AlbedoColor = new Color(0.765f, 0.78f, 0.8f), Metallic = 1, Roughness = 0.36f,
        ClearcoatEnabled = true, Clearcoat = 0.15f, ClearcoatRoughness = 0.4f,
    };

    static StandardMaterial3D Seam => _seam ??= new StandardMaterial3D { AlbedoColor = new Color(0.23f, 0.25f, 0.27f), Metallic = 0.8f, Roughness = 0.5f };

    // ------------------------------------------------------------------ 조립

    public float Height { get; private set; }
    Node3D _legL = null!, _legR = null!, _kneeL = null!, _kneeR = null!, _armL = null!, _armR = null!, _head = null!;
    float _phase, _amp, _time;

    public static CyborgBody Build(byte gender)
    {
        var P = gender == 1 ? Male : Female;
        var g = new CyborgBody { Height = gender == 1 ? 1.78f : 1.66f };
        Add(g, Loft(P.Torso.Select(s => s with { Sq = P.Sq }).ToArray(), 64, 7), Shell);
        var waist = P.Torso.MinBy(s => Mathf.Abs(s.Y - P.WaistY));
        SeamRing(g, P.WaistY, waist.Rx * 1.01f, (waist.F + waist.B) / 2 * 1.01f);
        Add(g, new CylinderMesh { TopRadius = P.Neck.R * 0.92f, BottomRadius = P.Neck.R * 1.15f, Height = P.Neck.Y1 - P.Neck.Y0, RadialSegments = 24 },
            Shell, new Vector3(0, (P.Neck.Y0 + P.Neck.Y1) / 2, -0.008f));
        SeamRing(g, P.Neck.Y1 - 0.03f, P.Neck.R * 0.98f, P.Neck.R * 0.98f, -0.008f);
        g._head = BuildHead(P, g);
        g._armL = BuildArm(P, -1);
        g._armR = BuildArm(P, 1);
        (g._legL, g._kneeL) = BuildLeg(P, -1);
        (g._legR, g._kneeR) = BuildLeg(P, 1);
        foreach (var n in new[] { g._armL, g._armR, g._legL, g._legR }) g.AddChild(n);
        return g;
    }

    static MeshInstance3D Add(Node3D parent, Mesh mesh, Material mat, Vector3 at = default)
    {
        var m = new MeshInstance3D { Mesh = mesh, MaterialOverride = mat, Position = at, CastShadow = GeometryInstance3D.ShadowCastingSetting.On };
        parent.AddChild(m);
        return m;
    }

    /// <summary>관절 분할선: 단면 타원을 따라 감는 얇은 고리</summary>
    static void SeamRing(Node3D parent, float y, float rx, float rz, float cz = 0)
    {
        var ring = Add(parent, new TorusMesh { InnerRadius = 0.975f, OuterRadius = 1.025f, Rings = 48, RingSegments = 6 }, Seam, new Vector3(0, y, cz));
        ring.Scale = new Vector3(rx * 1.004f, 0.05f, rz * 1.004f);
    }

    static Node3D BuildHead(Proportions P, Node3D g)
    {
        var (b, k, jaw, chin) = P.Head;
        var head = new Node3D { Position = new Vector3(0, b, 0) };
        // 아래(턱)에서 정수리까지. 얼굴은 없다.
        Sec[] s =
        [
            new(0, chin * 0.6f, 0.014f, 0.012f, Cz: 0.036f), new(0.016f, chin, 0.026f, 0.024f, Cz: 0.034f, Sq: P.Sq),
            new(0.048f, jaw, 0.056f, 0.058f, Cz: 0.016f, Sq: P.Sq), new(0.085f, jaw * 1.12f, 0.085f, 0.085f, Cz: 0.006f),
            new(0.125f, 0.073f, 0.092f, 0.1f), new(0.165f, 0.074f, 0.088f, 0.104f, Cz: -0.004f),
            new(0.2f, 0.066f, 0.072f, 0.094f, Cz: -0.008f), new(0.226f, 0.042f, 0.044f, 0.062f, Cz: -0.012f),
            new(0.238f, 0.006f, 0.006f, 0.008f, Cz: -0.014f),
        ];
        Add(head, Loft(s.Select(x => x with { Y = x.Y * k, Rx = x.Rx * k, F = x.F * k, B = x.B * k, Cz = x.Cz * k }).ToArray(), 56, 7), Shell);
        g.AddChild(head);
        return head;
    }

    static Node3D BuildArm(Proportions P, int side)
    {
        var (U, F, r, bicep) = P.Arm;
        var arm = new Node3D { Position = new Vector3(side * P.Shoulder.X, P.Shoulder.Y, 0), Rotation = new Vector3(0, 0, side * 0.09f) };
        Add(arm, new SphereMesh { Radius = P.Shoulder.Delt, Height = P.Shoulder.Delt * 2 }, Shell).Scale = new Vector3(1, 1.1f, 1.05f);
        Sec[] upper = [new(0, 0.031f, 0.032f, 0.033f), new(U * 0.25f, 0.035f, 0.037f + bicep * 0.5f, 0.037f), new(U * 0.55f, 0.038f, 0.041f + bicep, 0.04f),
                       new(U * 0.85f, 0.042f, 0.04f, 0.042f), new(U, 0.043f, 0.04f, 0.042f)];
        Add(arm, Loft(upper.Select(x => x.Scale(r)).ToArray(), 32, 5), Shell, new Vector3(0, -U, 0));
        var elbow = new Node3D { Position = new Vector3(0, -U, 0), Rotation = new Vector3(-0.14f, 0, 0) };
        arm.AddChild(elbow);
        SeamRing(elbow, 0, 0.033f * r, 0.034f * r);
        Sec[] fore = [new(0, 0.025f, 0.018f, 0.019f), new(F * 0.3f, 0.028f, 0.022f, 0.024f), new(F * 0.7f, 0.036f, 0.031f, 0.033f),
                      new(F * 0.92f, 0.035f, 0.032f, 0.033f), new(F, 0.031f, 0.031f, 0.031f)];
        Add(elbow, Loft(fore.Select(x => x.Scale(r)).ToArray(), 32, 5), Shell, new Vector3(0, -F, 0));
        SeamRing(elbow, -F + 0.004f, 0.026f * r, 0.02f * r);
        var hand = BuildHand(r);
        hand.Position = new Vector3(0, -F, 0);
        elbow.AddChild(hand);
        if (side < 0) arm.Scale = new Vector3(-1, 1, 1); // 왼팔은 거울 대칭
        return arm;
    }

    // 오른손 기준. 손바닥은 몸 안쪽(-X), 엄지는 정면(+Z).
    static Node3D BuildHand(float r)
    {
        var hand = new Node3D();
        var k = Mathf.Sqrt(r);
        Sec[] palm = [new(-0.095f, 0.011f, 0.034f, 0.036f), new(-0.07f, 0.014f, 0.039f, 0.04f), new(-0.03f, 0.015f, 0.038f, 0.037f), new(0, 0.013f, 0.026f, 0.026f)];
        Add(hand, Loft(palm.Select(x => x.Scale(k)).ToArray(), 28, 4), Shell);
        foreach (var (z, len) in new[] { (0.026f, 0.072f), (0.009f, 0.082f), (-0.009f, 0.077f), (-0.026f, 0.062f) })
        {
            var L = len * k;
            var f = new Node3D { Position = new Vector3(0, -0.093f * k, z * k), Rotation = new Vector3(0, 0, -0.22f) };
            Add(f, Loft([new(0, 0.0055f, 0.006f, 0.006f), new(L * 0.5f, 0.0075f, 0.0078f, 0.0078f), new(L, 0.0085f, 0.009f, 0.009f)], 12, 3), Shell, new Vector3(0, -L, 0));
            hand.AddChild(f);
        }
        var thumb = new Node3D { Position = new Vector3(-0.004f, -0.028f * k, 0.034f * k) };
        thumb.Basis = Basis.FromEuler(new Vector3(-0.7f, 0, -0.35f), EulerOrder.Xyz);
        Add(thumb, Loft([new(0, 0.0065f, 0.0068f, 0.0068f), new(0.03f * k, 0.009f, 0.0095f, 0.0095f), new(0.058f * k, 0.011f, 0.012f, 0.012f)], 12, 3), Shell, new Vector3(0, -0.058f * k, 0));
        hand.AddChild(thumb);
        return hand;
    }

    static (Node3D Leg, Node3D Knee) BuildLeg(Proportions P, int side)
    {
        var (T, N, r, q) = P.Leg;
        var leg = new Node3D { Position = new Vector3(side * P.Hip.X, P.Hip.Y, 0), Rotation = new Vector3(0, 0, -side * q) };
        Sec[] thigh = [new(0, 0.044f, 0.046f, 0.043f), new(T * 0.12f, 0.048f, 0.052f, 0.046f), new(T * 0.45f, 0.066f, 0.068f, 0.064f),
                       new(T * 0.8f, 0.08f, 0.076f, 0.08f), new(T, 0.08f, 0.076f, 0.082f), new(T + 0.05f, 0.06f, 0.058f, 0.064f, Cx: -0.012f),
                       new(T + 0.09f, 0.025f, 0.025f, 0.025f, Cx: -0.02f)];
        Add(leg, Loft(thigh.Select(x => x.Scale(r)).ToArray(), 40, 5), Shell, new Vector3(0, -T, 0));
        var knee = new Node3D { Position = new Vector3(0, -T, 0) };
        leg.AddChild(knee);
        SeamRing(knee, -0.004f, 0.044f * r, 0.045f * r);
        Sec[] shin = [new(0, 0.026f, 0.027f, 0.03f), new(N * 0.15f, 0.029f, 0.029f, 0.034f), new(N * 0.55f, 0.044f, 0.04f, 0.058f),
                      new(N * 0.75f, 0.046f, 0.041f, 0.056f), new(N * 0.92f, 0.043f, 0.045f, 0.043f), new(N, 0.044f, 0.046f, 0.043f)];
        Add(knee, Loft(shin.Select(x => x.Scale(r)).ToArray(), 40, 5), Shell, new Vector3(0, -N, 0));
        SeamRing(knee, -N + 0.004f, 0.027f * r, 0.029f * r);
        // 발: 로컬 Y(발끝 방향) → +Z 로 눕힌다, F(앞)=발바닥
        var FL = P.Foot;
        Sec[] foot = [new(0, 0.022f, 0.028f, 0.022f), new(FL * 0.12f, 0.03f, 0.033f, 0.04f), new(FL * 0.4f, 0.036f, 0.03f, 0.03f),
                      new(FL * 0.72f, 0.042f, 0.024f, 0.02f), new(FL * 0.92f, 0.036f, 0.016f, 0.014f), new(FL, 0.012f, 0.006f, 0.005f)];
        var f = Add(knee, Loft(foot.Select(x => x.Scale(Mathf.Sqrt(r))).ToArray(), 28, 4), Shell, new Vector3(0, -N - 0.032f, -0.05f));
        f.Rotation = new Vector3(Mathf.Pi / 2, 0, 0);
        if (side < 0) leg.Scale = new Vector3(-1, 1, 1);
        return (leg, knee);
    }

    /// <summary>팔을 든 자세 (기념상 등). angle 은 어깨의 X 회전(rad)이고 음수가 앞·위로 올라간다.</summary>
    public void RaiseArm(int side, float angle)
    {
        var arm = side < 0 ? _armL : _armR;
        arm.Rotation = arm.Rotation with { X = angle };
    }

    /// <summary>걷기 주기와 팔 흔들기를 속도(m/s)에 맞춘다. 서 있을 때는 아주 미세한 움직임만.</summary>
    public void Animate(float dt, float speed)
    {
        _time += dt;
        var target = Mathf.Min(1, speed / 3);
        _amp += (target - _amp) * Mathf.Min(1, dt * 8);
        _phase += dt * (2.2f + speed * 1.1f);
        var s = Mathf.Sin(_phase);
        _legL.Rotation = _legL.Rotation with { X = s * 0.5f * _amp };
        _legR.Rotation = _legR.Rotation with { X = -s * 0.5f * _amp };
        _kneeL.Rotation = new Vector3(Mathf.Max(0, -Mathf.Cos(_phase)) * 0.7f * _amp, 0, 0);
        _kneeR.Rotation = new Vector3(Mathf.Max(0, Mathf.Cos(_phase)) * 0.7f * _amp, 0, 0);
        _armL.Rotation = _armL.Rotation with { X = -s * 0.35f * _amp };
        _armR.Rotation = _armR.Rotation with { X = s * 0.35f * _amp };
        _head.Rotation = new Vector3(Mathf.Sin(_time / 0.9f) * 0.006f, 0, 0);
    }
}
