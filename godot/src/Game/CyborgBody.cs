using Godot;

namespace DollyForge.Game;

/// <summary>
/// 전신 사이보그 몸. 해부학적 단면(타원)을 높이별로 정의하고 Catmull-Rom 으로 이어 하나의 곡면으로 만든다(loft).
/// 스케치(sketch_01.jpg)의 실루엣 비례: 잘록한 모래시계 허리, 풍만한 가슴과 와이드 힙, 챔퍼 헬멧 셸.
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
        (float X, float Y, float Delt) Shoulder, (float Upper, float Fore, float R, float Bicep) Arm,
        (float X, float Y) Hip, (float Thigh, float Shin, float R, float Q) Leg, float Foot, float WaistY,
        float UnderbustY, float LowHipY);

    // 스케치(sketch_01.jpg)와 엄밀히 호환되는 비례:
    // 여성형: 키 1.66m, 잘록한 모래시계 허리(0.11m), 풍만하고 단단한 가슴(Bust 0.072m)과 와이드 힙(0.208m), 챔퍼 헬멧 셸
    static readonly Proportions Female = new(
        [
            new(0.735f, 0.032f, 0.032f, 0.042f, Cz: -0.005f),
            new(0.765f, 0.115f, 0.072f, 0.088f),
            new(0.815f, 0.188f, 0.094f, 0.116f, Glute: 0.026f),
            new(0.87f, 0.208f, 0.106f, 0.134f, Glute: 0.040f), // 골반 최대 곡률 (스케치의 풍만한 힙)
            new(0.93f, 0.194f, 0.098f, 0.118f, Glute: 0.022f),
            new(0.99f, 0.148f, 0.082f, 0.088f),
            new(1.05f, 0.112f, 0.070f, 0.068f),                // 잘록한 허리 (폭 0.112m)
            new(1.11f, 0.124f, 0.076f, 0.074f),
            new(1.16f, 0.138f, 0.080f, 0.078f, Bust: 0.018f), // 언더버스트 시작 라인
            new(1.22f, 0.154f, 0.088f, 0.084f, Bust: 0.072f), // 가슴 볼륨의 정점 (스케치 일치)
            new(1.265f, 0.158f, 0.086f, 0.084f, Bust: 0.068f),
            new(1.31f, 0.158f, 0.074f, 0.078f, Bust: 0.024f),
            new(1.35f, 0.152f, 0.058f, 0.068f),                // 쇄골 라인
            new(1.385f, 0.096f, 0.046f, 0.052f),
            new(1.4f, 0.048f, 0.040f, 0.040f),
        ],
        1.0f, (1.36f, 1.5f, 0.042f), (1.425f, 1.0f, 0.054f, 0.022f), (0.168f, 1.335f, 0.044f), (0.285f, 0.24f, 1.0f, 0f),
        (0.096f, 0.84f), (0.40f, 0.38f, 1.04f, 0.042f), 0.228f, 1.05f, 1.16f, 0.94f);

    // 남성형: 키 1.78m, 역삼각형 흉근과 다부진 실루엣
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
        1.18f, (1.44f, 1.6f, 0.055f), (1.525f, 1.08f, 0.066f, 0.032f), (0.212f, 1.415f, 0.058f), (0.31f, 0.27f, 1.22f, 0.012f),
        (0.095f, 0.9f), (0.44f, 0.42f, 1.07f, 0.02f), 0.26f, 1.06f, 1.22f, 0.98f);

    // ------------------------------------------------------------------ 재질

    static StandardMaterial3D? _shell, _cowlShell, _seam, _brassMat;

    /// <summary>고순도 새틴 화이트 세라믹/펄 에나멜 외장: 비금속 유전체 표면으로 따뜻한 미색 실크 광택과 리퀴드 클리어코트</summary>
    static StandardMaterial3D Shell => _shell ??= new StandardMaterial3D
    {
        AlbedoColor = new Color(0.96f, 0.955f, 0.94f),
        Metallic = 0.0f, // 비금속 유전체! 크롬/양철통 느낌 전면 제거
        Roughness = 0.20f,
        ClearcoatEnabled = true,
        Clearcoat = 1.0f,
        ClearcoatRoughness = 0.05f,
        SubsurfaceScatteringEnabled = true,
        SubsurfaceScatteringStrength = 0.08f,
        SubsurfaceScatteringTransmittanceColor = new Color(1.0f, 0.97f, 0.94f),
    };

    /// <summary>머리 헬멧 셸 및 외장 플레이트용 고강도 무광 세라믹 복합체</summary>
    static StandardMaterial3D CowlShell => _cowlShell ??= new StandardMaterial3D
    {
        AlbedoColor = new Color(0.93f, 0.925f, 0.91f),
        Metallic = 0.0f,
        Roughness = 0.26f,
        ClearcoatEnabled = true,
        Clearcoat = 0.5f,
        ClearcoatRoughness = 0.12f,
    };

    /// <summary>정밀 관절 및 분할선 (어두운 건메탈 티타늄)</summary>
    static StandardMaterial3D Seam => _seam ??= new StandardMaterial3D
    {
        AlbedoColor = new Color(0.14f, 0.15f, 0.17f),
        Metallic = 0.92f,
        Roughness = 0.38f,
    };

    /// <summary>레트로퓨처 정밀 가공 놋쇠 악센트</summary>
    static StandardMaterial3D Brass => _brassMat ??= new StandardMaterial3D
    {
        AlbedoColor = new Color(0.92f, 0.72f, 0.36f),
        Metallic = 1.0f,
        Roughness = 0.20f,
    };

    // ------------------------------------------------------------------ 조립

    public float Height { get; private set; }
    Node3D _legL = null!, _legR = null!, _kneeL = null!, _kneeR = null!, _armL = null!, _armR = null!, _head = null!;
    float _phase, _amp, _time;

    public static CyborgBody Build(byte gender)
    {
        var P = gender == 1 ? Male : Female;
        var g = new CyborgBody { Height = gender == 1 ? 1.78f : 1.66f };
        Add(g, Loft(P.Torso.Select(s => s with { Sq = P.Sq }).ToArray(), 64, 7), Shell);

        // 파팅선 1: 언더버스트 밴드 (스케치의 가슴 밑 절개선)
        if (gender == 0)
        {
            var under = P.Torso.MinBy(s => Mathf.Abs(s.Y - P.UnderbustY));
            SeamRing(g, P.UnderbustY, under.Rx * 1.01f, (under.F + under.B) / 2 * 1.015f, cz: 0.005f);
            BrassRing(g, P.UnderbustY, under.Rx * 1.006f, (under.F + under.B) / 2 * 1.008f, cz: 0.005f);
        }

        // 파팅선 2: 허리 링 (잘록한 허리 분할선)
        var waist = P.Torso.MinBy(s => Mathf.Abs(s.Y - P.WaistY));
        SeamRing(g, P.WaistY, waist.Rx * 1.01f, (waist.F + waist.B) / 2 * 1.01f);

        // 파팅선 3: 골반 로우라이즈 밴드 (스케치의 쇼츠/하복부 셸 분할)
        var hip = P.Torso.MinBy(s => Mathf.Abs(s.Y - P.LowHipY));
        SeamRing(g, P.LowHipY, hip.Rx * 1.008f, (hip.F + hip.B) / 2 * 1.008f);

        // 목: 실린더 + 상/하부 칼라 링
        Add(g, new CylinderMesh { TopRadius = P.Neck.R * 0.92f, BottomRadius = P.Neck.R * 1.15f, Height = P.Neck.Y1 - P.Neck.Y0, RadialSegments = 24 },
            Shell, new Vector3(0, (P.Neck.Y0 + P.Neck.Y1) / 2, -0.008f));
        SeamRing(g, P.Neck.Y0 + 0.01f, P.Neck.R * 1.12f, P.Neck.R * 1.12f, -0.008f);
        BrassRing(g, P.Neck.Y1 - 0.025f, P.Neck.R * 0.98f, P.Neck.R * 0.98f, -0.008f);

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

    static void BrassRing(Node3D parent, float y, float rx, float rz, float cz = 0)
    {
        var ring = Add(parent, new TorusMesh { InnerRadius = 0.985f, OuterRadius = 1.015f, Rings = 48, RingSegments = 6 }, Brass, new Vector3(0, y, cz));
        ring.Scale = new Vector3(rx * 1.002f, 0.035f, rz * 1.002f);
    }

    /// <summary>스케치(sketch_01.jpg)의 상징인 챔퍼 사각형 헬멧 외장 셸</summary>
    static MeshInstance3D BuildCowl(float scale = 1f)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        float w = 0.198f * scale, h = 0.225f * scale, ch = 0.044f * scale;
        float hw = w * 0.5f, hh = h * 0.5f;
        Vector2[] oct = [
            new(-hw + ch,  hh),
            new( hw - ch,  hh),
            new( hw,       hh - ch),
            new( hw,      -hh + ch),
            new( hw - ch, -hh),
            new(-hw + ch, -hh),
            new(-hw,      -hh + ch),
            new(-hw,       hh - ch),
        ];
        float zFront = 0.024f * scale, zBack = -0.122f * scale;
        int n = oct.Length;

        for (int i = 0; i < n; i++)
        {
            int next = (i + 1) % n;
            var p0 = new Vector3(oct[i].X, oct[i].Y, zFront);
            var p1 = new Vector3(oct[next].X, oct[next].Y, zFront);
            var p2 = new Vector3(oct[next].X, oct[next].Y, zBack);
            var p3 = new Vector3(oct[i].X, oct[i].Y, zBack);

            var norm = new Vector3((oct[i].X + oct[next].X) * 0.5f, (oct[i].Y + oct[next].Y) * 0.5f, 0).Normalized();
            st.SetNormal(norm); st.AddVertex(p0);
            st.SetNormal(norm); st.AddVertex(p1);
            st.SetNormal(norm); st.AddVertex(p2);

            st.SetNormal(norm); st.AddVertex(p0);
            st.SetNormal(norm); st.AddVertex(p2);
            st.SetNormal(norm); st.AddVertex(p3);
        }

        var backCenter = new Vector3(0, 0, zBack);
        for (int i = 0; i < n; i++)
        {
            int next = (i + 1) % n;
            st.SetNormal(-Vector3.Back);
            st.AddVertex(backCenter);
            st.AddVertex(new Vector3(oct[next].X, oct[next].Y, zBack));
            st.AddVertex(new Vector3(oct[i].X, oct[i].Y, zBack));
        }

        st.GenerateNormals();
        return new MeshInstance3D { Mesh = st.Commit(), MaterialOverride = CowlShell };
    }

    /// <summary>스케치(sketch_01.jpg)의 허벅지 외측 모듈형 장갑 플레이트</summary>
    static MeshInstance3D BuildThighPlate(float scale = 1f)
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
        return new MeshInstance3D { Mesh = st.Commit(), MaterialOverride = CowlShell };
    }

    static Node3D BuildHead(Proportions P, Node3D g)
    {
        var (b, k, jaw, chin) = P.Head;
        var head = new Node3D { Position = new Vector3(0, b, 0) };

        // 1. 전면 페이스플레이트: 부드럽고 매끄러운 달걀형 마스크 (이목구비 없는 완벽한 실루엣)
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

        // 2. 스케치 일치: 머리 뒤 챔퍼 헬멧 카울 셸
        var cowl = BuildCowl(k);
        cowl.Position = new Vector3(0, 0.125f * k, 0);
        head.AddChild(cowl);

        // 3. 좌우 센서 이어캡 (원형 디스크 + 놋쇠 링)
        foreach (var side in new[] { -1, 1 })
        {
            var ear = Add(head, new CylinderMesh { TopRadius = 0.024f * k, BottomRadius = 0.024f * k, Height = 0.012f * k, RadialSegments = 24 },
                Brass, new Vector3(side * 0.098f * k, 0.122f * k, -0.008f * k));
            ear.RotationDegrees = new Vector3(0, 0, 90);
        }

        g.AddChild(head);
        return head;
    }

    static Node3D BuildArm(Proportions P, int side)
    {
        var (U, F, r, bicep) = P.Arm;
        var arm = new Node3D { Position = new Vector3(side * P.Shoulder.X, P.Shoulder.Y, 0), Rotation = new Vector3(0, 0, side * 0.09f) };

        // 어깨 델토이드 캡 + 놋쇠 림
        Add(arm, new SphereMesh { Radius = P.Shoulder.Delt, Height = P.Shoulder.Delt * 2 }, Shell).Scale = new Vector3(1, 1.1f, 1.05f);
        var ring = Add(arm, new TorusMesh { InnerRadius = 0.96f, OuterRadius = 1.04f, Rings = 32, RingSegments = 6 }, Brass, new Vector3(0, -P.Shoulder.Delt * 0.3f, 0));
        ring.Scale = new Vector3(P.Shoulder.Delt * 1.02f, 0.03f, P.Shoulder.Delt * 1.02f);

        Sec[] upper = [
            new(0, 0.031f, 0.032f, 0.033f),
            new(U * 0.25f, 0.035f, 0.037f + bicep * 0.5f, 0.037f),
            new(U * 0.55f, 0.038f, 0.041f + bicep, 0.04f),
            new(U * 0.85f, 0.042f, 0.04f, 0.042f),
            new(U, 0.043f, 0.04f, 0.042f)
        ];
        Add(arm, Loft(upper.Select(x => x.Scale(r)).ToArray(), 32, 5), Shell, new Vector3(0, -U, 0));

        var elbow = new Node3D { Position = new Vector3(0, -U, 0), Rotation = new Vector3(-0.14f, 0, 0) };
        arm.AddChild(elbow);
        SeamRing(elbow, 0, 0.033f * r, 0.034f * r);

        Sec[] fore = [
            new(0, 0.025f, 0.018f, 0.019f),
            new(F * 0.3f, 0.028f, 0.022f, 0.024f),
            new(F * 0.7f, 0.036f, 0.031f, 0.033f),
            new(F * 0.92f, 0.035f, 0.032f, 0.033f),
            new(F, 0.031f, 0.031f, 0.031f)
        ];
        Add(elbow, Loft(fore.Select(x => x.Scale(r)).ToArray(), 32, 5), Shell, new Vector3(0, -F, 0));
        SeamRing(elbow, -F + 0.004f, 0.026f * r, 0.02f * r);

        var hand = BuildHand(r);
        hand.Position = new Vector3(0, -F, 0);
        elbow.AddChild(hand);
        if (side < 0) arm.Scale = new Vector3(-1, 1, 1);
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
        Sec[] thigh = [
            new(0, 0.046f, 0.048f, 0.044f),
            new(T * 0.12f, 0.052f, 0.054f, 0.048f),
            new(T * 0.45f, 0.068f, 0.070f, 0.066f),
            new(T * 0.8f, 0.082f, 0.078f, 0.082f),
            new(T, 0.082f, 0.078f, 0.084f),
            new(T + 0.05f, 0.062f, 0.060f, 0.066f, Cx: -0.010f),
            new(T + 0.09f, 0.026f, 0.026f, 0.026f, Cx: -0.018f)
        ];
        Add(leg, Loft(thigh.Select(x => x.Scale(r)).ToArray(), 40, 5), Shell, new Vector3(0, -T, 0));

        // 스케치(sketch_01.jpg)의 허벅지 외측 장갑 패널
        var plate = BuildThighPlate(r);
        plate.Position = new Vector3(0.088f * r, -T * 0.42f, 0);
        leg.AddChild(plate);

        var knee = new Node3D { Position = new Vector3(0, -T, 0) };
        leg.AddChild(knee);
        SeamRing(knee, -0.004f, 0.044f * r, 0.045f * r);

        Sec[] shin = [
            new(0, 0.026f, 0.027f, 0.03f),
            new(N * 0.15f, 0.029f, 0.029f, 0.034f),
            new(N * 0.55f, 0.044f, 0.04f, 0.058f),
            new(N * 0.75f, 0.046f, 0.041f, 0.056f),
            new(N * 0.92f, 0.043f, 0.045f, 0.043f),
            new(N, 0.044f, 0.046f, 0.043f)
        ];
        Add(knee, Loft(shin.Select(x => x.Scale(r)).ToArray(), 40, 5), Shell, new Vector3(0, -N, 0));
        SeamRing(knee, -N + 0.004f, 0.027f * r, 0.029f * r);

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
        var f = Add(knee, Loft(foot.Select(x => x.Scale(Mathf.Sqrt(r))).ToArray(), 28, 4), Shell, new Vector3(0, -N - 0.032f, -0.05f));
        f.Rotation = new Vector3(Mathf.Pi / 2, 0, 0);
        if (side < 0) leg.Scale = new Vector3(-1, 1, 1);
        return (leg, knee);
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

        // 걷지 않을 때도 팔이 몸 옆으로 자연스럽게 벌어지고 미세 호흡
        _armL.Rotation = new Vector3(-s * 0.35f * _amp + idlePitch * (1 - _amp), 0, idleArmL * (1 - _amp) + 0.03f * _amp);
        _armR.Rotation = new Vector3(s * 0.35f * _amp + idlePitch * (1 - _amp), 0, idleArmR * (1 - _amp) - 0.03f * _amp);

        // 머리: 미세한 호흡 고개 끄덕임과 자연스러운 시선 방향 탐색
        float headPitch = Mathf.Sin(_time * 1.5f) * 0.012f;
        float headYaw = Mathf.Sin(_time * 0.55f) * 0.025f;
        _head.Rotation = new Vector3(headPitch, headYaw, 0);
    }
}
