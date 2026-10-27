using Godot;

namespace DollyForge.Game;

/// <summary>
/// 사람이 사는 자리: 모노레일 정류장(시작 지점), 오솔길과 가로등, 작업장 앞마당과 외관, 자작나무 숲.
/// 양식은 미래의 레트로퓨처 — 흰 도장 콘크리트의 곡면(볼트 지붕·곡면 차양), 진홍색 띠, 놋쇠 테두리,
/// 둥근 창과 아날로그 시계. 자연은 그 사이를 무작위로 채운다(자작나무 군락, 휘고 기운 줄기, 잎 색 편차).
/// </summary>
public partial class Settlement : Node3D
{
    public World World { get; set; } = null!;
    readonly RandomNumberGenerator _rng = new() { Seed = 3826_2026 };

    StandardMaterial3D _white = null!, _pavers = null!, _red = null!, _brass = null!, _glass = null!, _lamp = null!;
    Node3D? _train;
    Curve3D _rail = null!;
    float _railLength, _stationOffset, _trainS, _trainV;
    double _dwell;
    int _trainDir = -1; // -1: 동쪽에서 와서 서쪽으로
    Node3D? _hourHand, _minuteHand;

    public override void _Ready()
    {
        _white = Look.Painted("white_plaster_02", 2.5f, new Color(0.9f, 0.88f, 0.84f));
        _pavers = Look.Scanned("concrete_pavers", 1.6f, new Color(0.93f, 0.91f, 0.87f));
        _red = new StandardMaterial3D { AlbedoColor = new Color(0.66f, 0.09f, 0.07f), Roughness = 0.38f, Metallic = 0.15f };
        _brass = new StandardMaterial3D { AlbedoColor = new Color(0.86f, 0.66f, 0.36f), Roughness = 0.28f, Metallic = 1f };
        _glass = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.55f, 0.68f, 0.72f, 0.28f), Roughness = 0.04f, Metallic = 0.2f,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        _lamp = new StandardMaterial3D
        {
            AlbedoColor = new Color(1f, 0.95f, 0.85f), EmissionEnabled = true, Emission = new Color(1f, 0.82f, 0.55f), EmissionEnergyMultiplier = 1.2f,
        };
        BuildApron();
        BuildPath();
        BuildStation();
        BuildMonorail();
        BuildWorkshopExterior();
        BuildBirches();
    }

    float Jitter(float a) => _rng.RandfRange(-a, a);

    MeshInstance3D Place(Mesh mesh, Material mat, Vector3 at, Vector3? rotation = null, Node3D? parent = null)
    {
        var m = new MeshInstance3D { Mesh = mesh, MaterialOverride = mat, Position = at, Rotation = rotation ?? Vector3.Zero };
        (parent ?? this).AddChild(m);
        return m;
    }

    /// <summary>보이는 상자 + 충돌 (월드 층)</summary>
    StaticBody3D Solid(Vector3 size, Transform3D at, Material mat)
    {
        var body = new StaticBody3D { Transform = at, CollisionLayer = PartBody.LayerWorld, CollisionMask = 0 };
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
        body.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = size }, MaterialOverride = mat });
        AddChild(body);
        return body;
    }

    Label3D Sign(string text, Vector3 at, float yawDeg, float pixel = 0.0016f, Color? color = null)
    {
        var l = Look.Tag(text, pixel, Look.Pen);
        l.Billboard = BaseMaterial3D.BillboardModeEnum.Disabled;
        l.Modulate = color ?? new Color(0.97f, 0.95f, 0.9f);
        l.Position = at;
        l.RotationDegrees = new Vector3(0, yawDeg, 0);
        l.OutlineSize = 0;
        AddChild(l);
        return l;
    }

    // ------------------------------------------------------------------ 앞마당·오솔길·가로등

    void BuildApron()
    {
        var r = Site.Apron;
        var y = World.HeightAt(0, 4) + 0.03f;
        Solid(new Vector3(r.Size.X, 0.08f, r.Size.Y), new Transform3D(Basis.Identity, new Vector3(r.GetCenter().X, y - 0.04f, r.GetCenter().Y)), _pavers);
        // 마당 가장자리 연석 (흰 콘크리트)
        foreach (var x in new[] { r.Position.X, r.End.X })
            Solid(new Vector3(0.18f, 0.16f, r.Size.Y), new Transform3D(Basis.Identity, new Vector3(x, y, r.GetCenter().Y)), _white);

        // 시험장: 매끈한 콘크리트 판, 둘레에 진홍·흰 줄무늬 경계선, 1 m 눈금 (거리를 재며 기기를 굴린다)
        var t = Site.TestPad;
        var ty = World.HeightAt(t.GetCenter().X, t.GetCenter().Y) + 0.03f;
        var slab = Look.Scanned("rock_boulder_dry", 2.5f, new Color(0.86f, 0.86f, 0.84f));
        Solid(new Vector3(t.Size.X, 0.08f, t.Size.Y), new Transform3D(Basis.Identity, new Vector3(t.GetCenter().X, ty - 0.04f, t.GetCenter().Y)), slab);
        for (var k = 0; k < 24; k++)
        {
            var mat = k % 2 == 0 ? _red : _white;
            var along = (k + 0.5f) / 24f;
            Place(new BoxMesh { Size = new Vector3(t.Size.X / 24f, 0.005f, 0.12f) }, mat, new Vector3(t.Position.X + t.Size.X * along, ty + 0.003f, t.Position.Y + 0.1f));
            Place(new BoxMesh { Size = new Vector3(t.Size.X / 24f, 0.005f, 0.12f) }, mat, new Vector3(t.Position.X + t.Size.X * along, ty + 0.003f, t.End.Y - 0.1f));
            Place(new BoxMesh { Size = new Vector3(0.12f, 0.005f, t.Size.Y / 24f) }, mat, new Vector3(t.Position.X + 0.1f, ty + 0.003f, t.Position.Y + t.Size.Y * along));
            Place(new BoxMesh { Size = new Vector3(0.12f, 0.005f, t.Size.Y / 24f) }, mat, new Vector3(t.End.X - 0.1f, ty + 0.003f, t.Position.Y + t.Size.Y * along));
        }
        var tick = new StandardMaterial3D { AlbedoColor = new Color(0.25f, 0.25f, 0.27f), Roughness = 0.8f };
        for (var x = t.Position.X + 1; x < t.End.X; x += 1)
            Place(new BoxMesh { Size = new Vector3(0.02f, 0.004f, (int)(x - t.Position.X) % 5 == 0 ? 0.5f : 0.25f) }, tick, new Vector3(x, ty + 0.002f, t.End.Y - 0.45f));
        Sign("시험장", new Vector3(t.GetCenter().X, ty + 0.01f, t.End.Y - 1.1f), 0, 0.006f, new Color(0.62f, 0.08f, 0.06f)).RotationDegrees = new Vector3(-90, 0, 0);
    }

    /// <summary>오솔길 중심선 (모서리를 두 번 깎아 부드럽게)</summary>
    static List<Vector2> PathLine()
    {
        var pts = Site.Path.ToList();
        for (var pass = 0; pass < 3; pass++)
        {
            var next = new List<Vector2> { pts[0] };
            for (var i = 0; i < pts.Count - 1; i++)
            {
                next.Add(pts[i].Lerp(pts[i + 1], 0.25f));
                next.Add(pts[i].Lerp(pts[i + 1], 0.75f));
            }
            next.Add(pts[^1]);
            pts = next;
        }
        // 0.5 m 간격으로 다시 나눈다
        var even = new List<Vector2> { pts[0] };
        var carry = 0f;
        for (var i = 0; i < pts.Count - 1; i++)
        {
            var seg = pts[i + 1] - pts[i];
            var len = seg.Length();
            for (var d = 0.5f - carry; d <= len; d += 0.5f) even.Add(pts[i] + seg * (d / len));
            carry = (carry + len) % 0.5f;
        }
        even.Add(pts[^1]);
        return even;
    }

    void BuildPath()
    {
        var line = PathLine();
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        const int across = 5;
        for (var i = 0; i < line.Count; i++)
        {
            var dir = (line[Math.Min(i + 1, line.Count - 1)] - line[Math.Max(i - 1, 0)]).Normalized();
            var side = new Vector2(-dir.Y, dir.X);
            // 길 폭이 조금씩 들쭉날쭉 (손으로 깐 보도블록)
            var w = Site.PathHalfWidth * (1 + Mathf.Sin(i * 0.37f) * 0.04f);
            for (var k = 0; k < across; k++)
            {
                var t = k / (float)(across - 1) * 2 - 1;
                var p = line[i] + side * (t * w);
                st.SetUV(new Vector2(t, i * 0.5f));
                st.AddVertex(new Vector3(p.X, World.HeightAt(p.X, p.Y) + 0.035f, p.Y));
            }
        }
        for (var i = 0; i < line.Count - 1; i++)
        for (var k = 0; k < across - 1; k++)
        {
            int a = i * across + k, b = a + 1, c = a + across, d = c + 1;
            st.AddIndex(a); st.AddIndex(c); st.AddIndex(b);
            st.AddIndex(b); st.AddIndex(c); st.AddIndex(d);
        }
        st.GenerateNormals();
        AddChild(new MeshInstance3D { Mesh = st.Commit(), MaterialOverride = _pavers });

        // 가로등: 흰 기둥 끝이 앞으로 휘고 유백색 구가 달린 레트로 가로등, 길 양쪽에 번갈아 11 m 마다
        var side2 = 1;
        for (var i = 10; i < line.Count - 6; i += 22)
        {
            var dir = (line[i + 1] - line[i - 1]).Normalized();
            var n = new Vector2(-dir.Y, dir.X) * side2;
            var at = line[i] + n * (Site.PathHalfWidth + 0.7f);
            var y = World.HeightAt(at.X, at.Y);
            var lamp = new Node3D { Position = new Vector3(at.X, y, at.Y), Rotation = new Vector3(0, Mathf.Atan2(-n.X, -n.Y), 0) };
            AddChild(lamp);
            Place(new CylinderMesh { TopRadius = 0.035f, BottomRadius = 0.06f, Height = 3.4f, RadialSegments = 12 }, _white, new Vector3(0, 1.7f, 0), parent: lamp);
            Place(new CylinderMesh { TopRadius = 0.12f, BottomRadius = 0.14f, Height = 0.25f }, _red, new Vector3(0, 0.12f, 0), parent: lamp);
            Place(new TorusMesh { InnerRadius = 0.28f, OuterRadius = 0.33f, Rings = 24, RingSegments = 8 }, _white, new Vector3(0, 3.4f, 0.3f), new Vector3(Mathf.Pi / 2, 0, 0), lamp);
            Place(new SphereMesh { Radius = 0.17f, Height = 0.34f }, _lamp, new Vector3(0, 3.36f, 0.6f), parent: lamp);
            Place(new CylinderMesh { TopRadius = 0.06f, BottomRadius = 0.09f, Height = 0.08f }, _brass, new Vector3(0, 3.55f, 0.6f), parent: lamp);
            side2 = -side2;
        }
    }

    // ------------------------------------------------------------------ 정류장

    void BuildStation()
    {
        var g = Site.RailGroundAt(Site.StationX);
        var top = Site.PlatformTop;
        float x0 = Site.StationX0, x1 = Site.StationX1, z0 = Site.PlatformZ0, z1 = Site.PlatformZ1;
        var cx = (x0 + x1) / 2;
        var cz = (z0 + z1) / 2;
        // 승강장: 흰 콘크리트 몸체 + 보도블록 윗면 + 선로 쪽 진홍색 안전선
        Solid(new Vector3(x1 - x0, top - g + 0.3f, z1 - z0), new Transform3D(Basis.Identity, new Vector3(cx, (top + g - 0.3f) / 2, cz)), _white);
        Place(new BoxMesh { Size = new Vector3(x1 - x0 - 0.2f, 0.02f, z1 - z0 - 0.5f) }, _pavers, new Vector3(cx, top + 0.01f, cz - 0.15f));
        Place(new BoxMesh { Size = new Vector3(x1 - x0, 0.022f, 0.3f) }, _red, new Vector3(cx, top + 0.011f, z1 - 0.15f));

        // 경사로: 승강장 북쪽 가운데에서 오솔길로 (기울기 약 12°)
        var rampLen = 4.4f;
        var drop = top - g;
        var angle = Mathf.Atan2(drop, rampLen);
        var rampBasis = new Basis(Vector3.Right, -angle);
        var rampCenter = new Vector3(Site.StationX, g + drop / 2 - 0.12f, z0 - rampLen / 2);
        Solid(new Vector3(2.6f, 0.24f, Mathf.Sqrt(rampLen * rampLen + drop * drop) + 0.2f), new Transform3D(rampBasis, rampCenter), _pavers);
        foreach (var sx in new[] { -1.4f, 1.4f }) // 놋쇠 난간
        {
            Place(new CylinderMesh { TopRadius = 0.025f, BottomRadius = 0.025f, Height = Mathf.Sqrt(rampLen * rampLen + drop * drop) },
                _brass, rampCenter + new Vector3(sx, 0.95f, 0), new Vector3(Mathf.Pi / 2 - angle, 0, 0));
            for (var k = 0; k < 3; k++)
            {
                var t = (k + 0.5f) / 3f;
                Place(new CylinderMesh { TopRadius = 0.02f, BottomRadius = 0.02f, Height = 0.95f }, _brass,
                    new Vector3(Site.StationX + sx, g + drop * t + 0.47f - 0.1f, z0 - rampLen * (1 - t)));
            }
        }

        // 곡면 차양: 가는 진홍 기둥 위에 얇은 흰 껍질이 승강장과 선로를 덮는다
        var shell = ArcShell(radius: 7f, halfAngle: 0.45f, length: x1 - x0 - 4f, thickness: 0.08f);
        Place(shell, _white, new Vector3(cx, top + 4.5f - 7f, cz + 2.2f));
        for (var x = x0 + 3f; x <= x1 - 2.5f; x += 5.2f)
        {
            Place(new CylinderMesh { TopRadius = 0.11f, BottomRadius = 0.07f, Height = 3.9f }, _red, new Vector3(x, top + 1.95f, z0 + 0.9f));
            Place(new SphereMesh { Radius = 0.14f, Height = 0.28f }, _lamp, new Vector3(x, top + 4.1f, cz + 1.4f));
        }

        // 정류장 이름판 (동그란 놋쇠 테 + 흰 판, 양면)
        const float boardX = Site.StationX + 6.5f;
        var board = new Node3D { Position = new Vector3(boardX, top, z0 + 0.4f) };
        AddChild(board);
        Place(new CylinderMesh { TopRadius = 0.05f, BottomRadius = 0.07f, Height = 2.3f }, _white, new Vector3(0, 1.15f, 0), parent: board);
        Place(new CylinderMesh { TopRadius = 0.62f, BottomRadius = 0.62f, Height = 0.06f }, _white, new Vector3(0, 2.75f, 0), new Vector3(Mathf.Pi / 2, 0, 0), board);
        Place(new TorusMesh { InnerRadius = 0.6f, OuterRadius = 0.68f, Rings = 48, RingSegments = 8 }, _brass, new Vector3(0, 2.75f, 0), new Vector3(Mathf.Pi / 2, 0, 0), board);
        foreach (var (yaw, dz) in new[] { (0f, -0.04f), (180f, 0.04f) })
        {
            var t = Sign("들꽃\n정류장", new Vector3(boardX, top + 2.78f, z0 + 0.4f + dz), yaw, 0.0042f, new Color(0.62f, 0.08f, 0.06f));
            t.HorizontalAlignment = HorizontalAlignment.Center;
        }

        // 아날로그 시계: 실제 시각을 가리킨다
        var clock = new Node3D { Position = new Vector3(Site.StationX - 5.4f, top + 2.7f, z0 + 0.62f) };
        AddChild(clock);
        Place(new CylinderMesh { TopRadius = 0.36f, BottomRadius = 0.36f, Height = 0.08f }, _white, Vector3.Zero, new Vector3(Mathf.Pi / 2, 0, 0), clock);
        Place(new TorusMesh { InnerRadius = 0.35f, OuterRadius = 0.41f, Rings = 40, RingSegments = 8 }, _brass, Vector3.Zero, new Vector3(Mathf.Pi / 2, 0, 0), clock);
        var ink = new StandardMaterial3D { AlbedoColor = new Color(0.08f, 0.08f, 0.09f), Roughness = 0.5f };
        for (var h = 0; h < 12; h++)
        {
            var a = h * Mathf.Tau / 12;
            Place(new BoxMesh { Size = new Vector3(0.018f, h % 3 == 0 ? 0.07f : 0.04f, 0.01f) }, ink,
                new Vector3(Mathf.Sin(a) * 0.29f, Mathf.Cos(a) * 0.29f, 0.045f), new Vector3(0, 0, -a), clock);
        }
        _hourHand = Hand(clock, 0.17f, 0.022f, ink);
        _minuteHand = Hand(clock, 0.27f, 0.014f, ink);

        // 벤치 두 개: 나무 좌판, 진홍색 강철 다리
        var wood = Look.Scanned("weathered_planks", 1f, new Color(0.9f, 0.75f, 0.6f));
        foreach (var bx in new[] { Site.StationX - 9f, Site.StationX + 7f })
        {
            Solid(new Vector3(1.8f, 0.06f, 0.42f), new Transform3D(Basis.Identity, new Vector3(bx, top + 0.45f, z0 + 0.6f)), wood);
            Place(new BoxMesh { Size = new Vector3(1.8f, 0.36f, 0.05f) }, wood, new Vector3(bx, top + 0.72f, z0 + 0.38f), new Vector3(-0.15f, 0, 0));
            foreach (var lx in new[] { -0.75f, 0.75f })
                Place(new BoxMesh { Size = new Vector3(0.06f, 0.45f, 0.4f) }, _red, new Vector3(bx + lx, top + 0.22f, z0 + 0.6f));
        }

        // 승강장 양 끝의 유리 바람막이
        foreach (var x in new[] { x0 + 0.4f, x1 - 0.4f })
        {
            Place(new BoxMesh { Size = new Vector3(0.03f, 2.1f, 2.4f) }, _glass, new Vector3(x, top + 1.05f, cz - 0.2f));
            Place(new BoxMesh { Size = new Vector3(0.06f, 0.06f, 2.4f) }, _brass, new Vector3(x, top + 2.12f, cz - 0.2f));
        }
    }

    Node3D Hand(Node3D clock, float length, float width, Material mat)
    {
        var pivot = new Node3D { Position = new Vector3(0, 0, 0.06f) }; // 승강장 쪽(남쪽) 면
        clock.AddChild(pivot);
        Place(new BoxMesh { Size = new Vector3(width, length, 0.008f) }, mat, new Vector3(0, length / 2 - 0.03f, 0), parent: pivot);
        return pivot;
    }

    /// <summary>원통 일부(호) 모양의 얇은 껍질. 축은 X, 꼭대기는 +Y, 원점은 원의 중심.</summary>
    static ArrayMesh ArcShell(float radius, float halfAngle, float length, float thickness)
    {
        const int segs = 28;
        var verts = new List<Vector3>();
        var idx = new List<int>();
        void Surface(float r, bool inward)
        {
            var b = verts.Count;
            for (var i = 0; i <= segs; i++)
            {
                var a = -halfAngle + 2 * halfAngle * i / segs;
                var y = Mathf.Cos(a) * r;
                var z = Mathf.Sin(a) * r;
                verts.Add(new Vector3(-length / 2, y, z));
                verts.Add(new Vector3(length / 2, y, z));
            }
            for (var i = 0; i < segs; i++)
            {
                int a0 = b + i * 2, a1 = a0 + 1, b0 = a0 + 2, b1 = a0 + 3;
                if (inward) idx.AddRange([a0, b0, a1, a1, b0, b1]);
                else idx.AddRange([a0, a1, b0, a1, b1, b0]);
            }
        }
        Surface(radius, false);
        Surface(radius - thickness, true);
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        foreach (var v in verts) { st.SetUV(new Vector2(v.X, v.Z)); st.AddVertex(v); }
        foreach (var i in idx) st.AddIndex(i);
        st.GenerateNormals();
        return st.Commit();
    }

    // ------------------------------------------------------------------ 모노레일

    /// <summary>
    /// 고가 모노레일: 정류장에서는 땅 높이 선로(비탈을 깎은 자리), 시험장 밖에서는 기둥 위로 올라가
    /// 먼 도시 쪽으로 휘어 간다. 객차는 동쪽에서 와서 정류장에 12초 서고 서쪽으로 떠난다.
    /// </summary>
    void BuildMonorail()
    {
        _rail = new Curve3D { BakeInterval = 1 };
        void P(float x, float y, float z) => _rail.AddPoint(new Vector3(x, y, z));
        P(-900, 46, -700); P(-520, 34, -260); P(-260, 18, 20);
        for (var x = -120f; x <= 120f; x += 10f)
        {
            var inside = Mathf.Abs(x) <= 78;
            var y = inside ? Site.RailGroundAt(x) + 0.45f : Mathf.Lerp(Site.RailGroundAt(Mathf.Sign(x) * 78) + 0.45f, 9f, (Mathf.Abs(x) - 78) / 42f);
            P(x, y, Site.RailZ);
        }
        P(260, 20, 40); P(560, 36, -260); P(900, 48, -700);
        // 곡선 이음매를 부드럽게: 이웃 점으로 접선을 준다
        for (var i = 1; i < _rail.PointCount - 1; i++)
        {
            var t = (_rail.GetPointPosition(i + 1) - _rail.GetPointPosition(i - 1)) / 6f;
            _rail.SetPointIn(i, -t);
            _rail.SetPointOut(i, t);
        }
        _railLength = _rail.GetBakedLength();
        _stationOffset = _rail.GetClosestOffset(new Vector3(Site.StationX, Site.PlatformTop, Site.RailZ));

        var beams = new List<Transform3D>();
        var pillars = new List<Transform3D>();
        var step = 3f;
        for (float d = 0; d < _railLength - step; d += step)
        {
            var a = _rail.SampleBaked(d);
            var b = _rail.SampleBaked(d + step);
            var basis = Basis.LookingAt((b - a).Normalized(), Vector3.Up);
            var mid = (a + b) / 2;
            beams.Add(new Transform3D(basis.Scaled(new Vector3(0.9f, 1.0f, step + 0.04f)), mid - new Vector3(0, 0.5f, 0)));
            var ground = Mathf.Abs(mid.X) < 79 && Mathf.Abs(mid.Z) < 79 ? World.HeightAt(mid.X, mid.Z) : -0.6f;
            var clear = mid.Y - 1f - ground;
            if (clear > 0.6f && (int)(d / step) % 9 == 0)
                pillars.Add(new Transform3D(Basis.Identity.Scaled(new Vector3(1, clear, 1)), new Vector3(mid.X, ground + clear / 2, mid.Z)));
        }
        var bm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = new BoxMesh(), InstanceCount = beams.Count };
        for (var i = 0; i < beams.Count; i++) bm.SetInstanceTransform(i, beams[i]);
        AddChild(new MultiMeshInstance3D { Multimesh = bm, MaterialOverride = _white });
        var pm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, InstanceCount = pillars.Count,
            Mesh = new CylinderMesh { TopRadius = 0.55f, BottomRadius = 0.8f, Height = 1, RadialSegments = 16 },
        };
        for (var i = 0; i < pillars.Count; i++) pm.SetInstanceTransform(i, pillars[i]);
        AddChild(new MultiMeshInstance3D { Multimesh = pm, MaterialOverride = _white });

        // 객차 3량: 흰 유선형 앞뒤 코, 진홍색 몸통, 짙은 창 띠, 놋쇠 앞코
        _train = new Node3D();
        var windows = new StandardMaterial3D { AlbedoColor = new Color(0.07f, 0.09f, 0.11f), Metallic = 0.7f, Roughness = 0.08f };
        var gloss = new StandardMaterial3D { AlbedoColor = new Color(0.95f, 0.94f, 0.9f), Roughness = 0.22f, Metallic = 0.1f, ClearcoatEnabled = true, Clearcoat = 0.6f };
        for (var car = 0; car < 3; car++)
        {
            var z = car * 13.6f;
            Place(new CapsuleMesh { Radius = 1.35f, Height = 13f, RadialSegments = 32, Rings = 12 }, gloss, new Vector3(0, 1.55f, z), new Vector3(Mathf.Pi / 2, 0, 0), _train);
            Place(new CylinderMesh { TopRadius = 1.37f, BottomRadius = 1.37f, Height = 9.6f, RadialSegments = 32 }, _red, new Vector3(0, 1.55f, z), new Vector3(Mathf.Pi / 2, 0, 0), _train);
            Place(new BoxMesh { Size = new Vector3(2.62f, 0.62f, 9.6f) }, windows, new Vector3(0, 2.05f, z), parent: _train);
        }
        Place(new SphereMesh { Radius = 0.32f, Height = 0.64f }, _brass, new Vector3(0, 1.55f, -6.75f), parent: _train);
        AddChild(_train);
        // 시작: 막 정류장을 떠나는 참 (플레이어가 방금 내렸다)
        _trainS = _stationOffset - 3;
        _trainV = 0.5f;
        _dwell = 0;
        _trainDir = -1;
    }

    void MoveTrain(double delta)
    {
        if (_train is null) return;
        var dt = (float)delta;
        const float cruise = 18f, accel = 0.9f;
        if (_dwell > 0)
        {
            _dwell -= delta;
            _trainV = 0;
        }
        else
        {
            var toStation = (_stationOffset - _trainS) * _trainDir;
            var limit = toStation > 0 ? Mathf.Sqrt(2 * accel * toStation) : cruise;
            _trainV = Mathf.Min(Mathf.Min(_trainV + accel * dt, cruise), Mathf.Max(limit, 0.3f));
            var before = toStation;
            _trainS += _trainV * dt * _trainDir;
            var after = (_stationOffset - _trainS) * _trainDir;
            if (before > 0 && after <= 0) { _trainS = _stationOffset; _dwell = 12; }
        }
        // 선로 끝을 지나면 반대쪽 끝에서 다시 들어온다 (다음 열차)
        if (_trainS < 0 || _trainS > _railLength)
        {
            _trainS = _trainDir < 0 ? _railLength : 0;
            _trainV = cruise;
        }
        var s = Mathf.Clamp(_trainS, 0, _railLength - 2);
        var p = _rail.SampleBaked(s);
        var ahead = _rail.SampleBaked(Mathf.Clamp(s + 2 * _trainDir, 0, _railLength));
        if (ahead.IsEqualApprox(p)) return;
        _train.GlobalTransform = new Transform3D(Basis.LookingAt((ahead - p).Normalized(), Vector3.Up), p);
    }

    // ------------------------------------------------------------------ 작업장 외관

    /// <summary>
    /// 상자 같던 작업장에 레트로퓨처 외관을 입힌다: 낮은 볼트(원통형) 지붕, 진홍색 처마 띠,
    /// 놋쇠 테 둥근 창, 앞 박공의 부채꼴 유리창, 문 위 곡면 차양, 동쪽 벽의 구리 배관.
    /// </summary>
    void BuildWorkshopExterior()
    {
        const float halfW = 6.4f, rise = 2.0f, wallTop = 3.5f;
        var radius = (halfW * halfW + rise * rise) / (2 * rise);
        var half = Mathf.Asin(halfW / radius);
        var roofMat = Look.Painted("white_plaster_02", 3f, new Color(0.88f, 0.86f, 0.82f));
        // 볼트 지붕 (축 Z)
        var vault = Place(ArcShell(radius, half, 11.2f, 0.12f), roofMat, new Vector3(0, wallTop + rise - radius, -5f), new Vector3(0, Mathf.Pi / 2, 0));
        var roofBody = new StaticBody3D { CollisionLayer = PartBody.LayerWorld, CollisionMask = 0 };
        roofBody.AddChild(new CollisionShape3D { Shape = vault.Mesh.CreateTrimeshShape(), Transform = vault.Transform });
        AddChild(roofBody);
        // 처마 띠 (진홍)
        Place(new BoxMesh { Size = new Vector3(12.9f, 0.28f, 0.06f) }, _red, new Vector3(0, wallTop - 0.2f, 0.24f));
        Place(new BoxMesh { Size = new Vector3(12.9f, 0.28f, 0.06f) }, _red, new Vector3(0, wallTop - 0.2f, -10.24f));
        foreach (var x in new[] { -6.24f, 6.24f })
            Place(new BoxMesh { Size = new Vector3(0.06f, 0.28f, 10.5f) }, _red, new Vector3(x, wallTop - 0.2f, -5f));
        // 박공 (앞: 부채꼴 유리 + 놋쇠 살, 뒤: 흰 판)
        foreach (var (z, glass) in new[] { (0.22f, true), (-10.22f, false) })
        {
            var gable = Segment(radius, half, wallTop + rise - radius, wallTop);
            Place(gable, glass ? _glass : roofMat, new Vector3(0, 0, z));
            if (!glass) continue;
            for (var k = -4; k <= 4; k++)
            {
                var a = k / 4f * half * 0.92f;
                var len = radius - (radius - rise) / Mathf.Cos(a);
                var mid = (radius - len / 2);
                Place(new BoxMesh { Size = new Vector3(0.05f, len, 0.05f) }, _brass,
                    new Vector3(Mathf.Sin(a) * mid, wallTop + rise - radius + Mathf.Cos(a) * mid, z + 0.02f), new Vector3(0, 0, -a));
            }
            Place(new BoxMesh { Size = new Vector3(12.8f, 0.06f, 0.06f) }, _brass, new Vector3(0, wallTop + 0.02f, z + 0.02f));
        }
        // 둥근 창 (동·서 벽, 놋쇠 테, 안쪽의 따뜻한 빛)
        var warm = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.2f, 0.17f, 0.12f), Metallic = 0.5f, Roughness = 0.05f,
            EmissionEnabled = true, Emission = new Color(1f, 0.78f, 0.5f), EmissionEnergyMultiplier = 0.5f,
        };
        foreach (var sx in new[] { -1, 1 })
        foreach (var z in new[] { -2.4f, -5f, -7.6f })
        {
            var x = sx * 6.22f;
            Place(new CylinderMesh { TopRadius = 0.42f, BottomRadius = 0.42f, Height = 0.03f }, warm, new Vector3(x, 2.1f, z), new Vector3(0, 0, Mathf.Pi / 2));
            Place(new TorusMesh { InnerRadius = 0.42f, OuterRadius = 0.52f, Rings = 40, RingSegments = 10 }, _brass, new Vector3(x, 2.1f, z), new Vector3(0, 0, Mathf.Pi / 2));
        }
        // 문 위 곡면 차양 (반원판) 과 이름
        var canopy = (StandardMaterial3D)_white.Duplicate();
        canopy.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        Place(HalfDisc(2.7f, 0.1f), canopy, new Vector3(0, 3.05f, 0.21f));
        Place(new BoxMesh { Size = new Vector3(4.1f, 0.05f, 0.05f) }, _brass, new Vector3(0, 3.0f, 0.24f));
        // 문 옆 놋쇠 명판 (차양 밑에서도 읽히는 눈높이)
        Place(new BoxMesh { Size = new Vector3(1.5f, 0.5f, 0.03f) }, _brass, new Vector3(-3.1f, 1.75f, 0.23f));
        Sign("작업장\n조립 · 정비", new Vector3(-3.1f, 1.75f, 0.26f), 0, 0.0028f, new Color(0.18f, 0.12f, 0.06f)).HorizontalAlignment = HorizontalAlignment.Center;
        // 구리 배관 (동쪽 벽): 세로 두 줄과 가로 한 줄, 플랜지
        var copper = new StandardMaterial3D { AlbedoColor = new Color(0.78f, 0.45f, 0.3f), Metallic = 1, Roughness = 0.35f };
        foreach (var z in new[] { -1.2f, -1.5f })
            Place(new CylinderMesh { TopRadius = 0.05f, BottomRadius = 0.05f, Height = 3.3f }, copper, new Vector3(6.32f, 1.65f, z));
        Place(new CylinderMesh { TopRadius = 0.05f, BottomRadius = 0.05f, Height = 7.6f }, copper, new Vector3(6.32f, 3.0f, -5.2f), new Vector3(Mathf.Pi / 2, 0, 0));
        for (var z = -1.5f; z > -9.5f; z -= 1.6f)
            Place(new CylinderMesh { TopRadius = 0.08f, BottomRadius = 0.08f, Height = 0.05f }, copper, new Vector3(6.32f, 3.0f, z), new Vector3(Mathf.Pi / 2, 0, 0));
    }

    /// <summary>원의 활꼴 (현 아래 y = chordY, 원 중심 높이 centerY): 박공 채우기, XY 평면</summary>
    static ArrayMesh Segment(float radius, float half, float centerY, float chordY)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        const int segs = 24;
        for (var i = 0; i < segs; i++)
        {
            float a0 = -half + 2 * half * i / segs, a1 = -half + 2 * half * (i + 1) / segs;
            var p0 = new Vector3(Mathf.Sin(a0) * radius, centerY + Mathf.Cos(a0) * radius, 0);
            var p1 = new Vector3(Mathf.Sin(a1) * radius, centerY + Mathf.Cos(a1) * radius, 0);
            var b0 = p0 with { Y = chordY };
            var b1 = p1 with { Y = chordY };
            foreach (var v in new[] { b0, p0, p1, b0, p1, b1 }) { st.SetNormal(Vector3.Back); st.SetUV(new Vector2(v.X, v.Y)); st.AddVertex(v); }
        }
        return st.Commit();
    }

    /// <summary>반원판 (+Z 쪽 반), 두께 t</summary>
    static ArrayMesh HalfDisc(float r, float t)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        const int segs = 32;
        for (var i = 0; i < segs; i++)
        {
            float a0 = Mathf.Pi * i / segs, a1 = Mathf.Pi * (i + 1) / segs;
            var p0 = new Vector3(Mathf.Cos(a0) * r, 0, Mathf.Sin(a0) * r);
            var p1 = new Vector3(Mathf.Cos(a1) * r, 0, Mathf.Sin(a1) * r);
            var up = new Vector3(0, t / 2, 0);
            foreach (var v in new[] { up, p1 + up, p0 + up }) st.AddVertex(v);
            foreach (var v in new[] { -up, p0 - up, p1 - up }) st.AddVertex(v);
            foreach (var v in new[] { p0 - up, p0 + up, p1 + up, p0 - up, p1 + up, p1 - up }) st.AddVertex(v);
        }
        st.GenerateNormals();
        return st.Commit();
    }

    // ------------------------------------------------------------------ 자작나무

    /// <summary>자작나무 껍질 텍스처 맵: 크림빛 흰 바탕, 가로 껍질눈(lenticels), 노멀 범프 및 거칠기</summary>
    (Texture2D Albedo, Texture2D Normal, Texture2D Rough) BirchBarkMaps()
    {
        const int w = 128, h = 256;
        var alb = Image.CreateEmpty(w, h, true, Image.Format.Rgb8);
        var nor = Image.CreateEmpty(w, h, true, Image.Format.Rgb8);
        var rgh = Image.CreateEmpty(w, h, true, Image.Format.Rgb8);
        var n = new FastNoiseLite { Seed = 9, Frequency = 0.05f };

        for (var y = 0; y < h; y++)
        {
            var uY = (float)y / h;
            var baseDark = Mathf.SmoothStep(0.32f, 0.02f, uY);
            for (var x = 0; x < w; x++)
            {
                var grain = 0.88f + n.GetNoise2D(x * 2.5f, y * 0.7f) * 0.08f;
                var c = new Color(grain, grain * 0.985f, grain * 0.93f);
                c = c.Lerp(new Color(0.12f, 0.12f, 0.13f), baseDark * 0.9f);
                alb.SetPixel(x, y, c);
                rgh.SetPixel(x, y, new Color(Mathf.Lerp(0.55f, 0.92f, baseDark), 0, 0));
                nor.SetPixel(x, y, new Color(0.5f, 0.5f, 1.0f));
            }
        }
        for (var k = 0; k < 240; k++)
        {
            var y = _rng.RandiRange(0, h - 3);
            var x = _rng.RandiRange(0, w - 1);
            var len = _rng.RandiRange(5, 26);
            var th = _rng.RandiRange(1, 3);
            var dark = _rng.RandfRange(0.06f, 0.22f);
            for (var dx = 0; dx < len; dx++)
            for (var dy = 0; dy < th; dy++)
            {
                var px = (x + dx) % w;
                var py = y + dy;
                alb.SetPixel(px, py, new Color(dark, dark * 0.95f, dark * 0.9f));
                rgh.SetPixel(px, py, new Color(0.85f, 0, 0));
                nor.SetPixel(px, py, new Color(0.5f, dy == 0 ? 0.38f : 0.62f, 0.92f));
            }
        }
        alb.GenerateMipmaps();
        nor.GenerateMipmaps();
        rgh.GenerateMipmaps();
        return (ImageTexture.CreateFromImage(alb), ImageTexture.CreateFromImage(nor), ImageTexture.CreateFromImage(rgh));
    }

    /// <summary>유기적인 자작나무 줄기 메시: 밑동 플레어(root flare), 자연스러운 S자 곡선, 상부 테이퍼</summary>
    static ArrayMesh BirchTrunkMesh()
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        const int rings = 14;
        const int segs = 16;
        for (var r = 0; r <= rings; r++)
        {
            var t = (float)r / rings;
            var flare = r == 0 ? 1.70f : r == 1 ? 1.30f : 1.0f;
            var taper = Mathf.Lerp(1.0f, 0.22f, Mathf.Pow(t, 0.85f)) * flare;
            var curveX = Mathf.Sin(t * Mathf.Pi) * 0.14f + Mathf.Sin(t * Mathf.Tau) * 0.04f;
            var curveZ = Mathf.Cos(t * Mathf.Pi) * 0.09f;
            var y = t;

            for (var s = 0; s <= segs; s++)
            {
                var a = (float)s / segs * Mathf.Tau;
                var rootFlute = r <= 1 ? (1.0f + Mathf.Cos(a * 4) * 0.14f) : 1.0f;
                var rad = taper * rootFlute;
                var vx = curveX + Mathf.Cos(a) * rad;
                var vz = curveZ + Mathf.Sin(a) * rad;
                var norm = new Vector3(Mathf.Cos(a), 0.04f, Mathf.Sin(a)).Normalized();

                st.SetUV(new Vector2((float)s / segs * 2f, t * 4f));
                st.SetNormal(norm);
                st.AddVertex(new Vector3(vx, y, vz));
            }
        }
        for (var r = 0; r < rings; r++)
        for (var s = 0; s < segs; s++)
        {
            int i0 = r * (segs + 1) + s;
            int i1 = i0 + 1;
            int i2 = (r + 1) * (segs + 1) + s;
            int i3 = i2 + 1;
            st.AddIndex(i0); st.AddIndex(i2); st.AddIndex(i1);
            st.AddIndex(i1); st.AddIndex(i2); st.AddIndex(i3);
        }
        st.GenerateTangents();
        return st.Commit();
    }

    /// <summary>활엽수 잎 카드: tree_small_02 잎 아틀라스 오른쪽의 잔가지 한 줄기를 잘라 쓴다 (+Y 가 가지 끝)</summary>
    static Material? Leaves()
    {
        const string diff = "res://assets/tex/leaves/leaves_diff.jpg", alpha = "res://assets/tex/leaves/leaves_alpha.jpg";
        if (!ResourceLoader.Exists(diff) || !ResourceLoader.Exists(alpha)) return null;
        var src = GD.Load<Texture2D>(diff).GetImage();
        var mask = GD.Load<Texture2D>(alpha).GetImage();
        if (src.IsCompressed()) src.Decompress();
        if (mask.IsCompressed()) mask.Decompress();
        var s = src.GetWidth() / 1024f;
        var rect = new Rect2I((int)(560 * s), (int)(40 * s), (int)(320 * s), (int)(650 * s));
        mask.Resize(src.GetWidth(), src.GetHeight());
        var img = Image.CreateEmpty(rect.Size.X, rect.Size.Y, false, Image.Format.Rgba8);
        for (var y = 0; y < rect.Size.Y; y++)
        for (var x = 0; x < rect.Size.X; x++)
        {
            var c = src.GetPixel(rect.Position.X + x, rect.Position.Y + y);
            c.A = mask.GetPixel(rect.Position.X + x, rect.Position.Y + y).R;
            img.SetPixel(x, y, c);
        }
        img.GenerateMipmaps();
        var mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/foliage.gdshader") };
        mat.SetShaderParameter("albedo_tex", ImageTexture.CreateFromImage(img));
        return mat;
    }

    /// <summary>
    /// 자작나무 군락: 오솔길 양옆, 정류장 둘레, 연못 서쪽 둑에 모여 난다. 한 뿌리에서 줄기 1~3개가 갈라져
    /// 서로 다른 방향으로 기울고, 잎은 줄기 위쪽 절반에 늘어진 잔가지로 달린다.
    /// </summary>
    void BuildBirches()
    {
        var groves = new List<(Vector2 Center, float Radius, int Count)>
        {
            (new Vector2(-17, 44), 7, 9), (new Vector2(6, 46), 6, 7), (new Vector2(-15, 30), 6, 7),
            (new Vector2(4, 30), 5, 6), (new Vector2(10, 22), 5, 5), (new Vector2(-12, 14), 6, 6),
            (new Vector2(14, 12), 5, 5), (new Vector2(-24, 52), 6, 6), (new Vector2(18, 50), 5, 5),
        };
        var stems = new List<(Transform3D X, float H)>();
        foreach (var (c, r, count) in groves)
        for (int k = 0, planted = 0; k < count * 3 && planted < count; k++)
        {
            var p = c + new Vector2(_rng.RandfRange(-r, r), _rng.RandfRange(-r, r));
            if (p.DistanceTo(c) > r || !Site.Clear(p.X, p.Y, 1.8f)) continue;
            var ground = World.HeightAt(p.X, p.Y) - 0.1f;
            var n = _rng.Randf() < 0.35f ? _rng.RandiRange(2, 3) : 1;
            for (var s = 0; s < n; s++)
            {
                var lean = _rng.RandfRange(0.02f, n > 1 ? 0.2f : 0.09f);
                var dirA = _rng.RandfRange(0, Mathf.Tau);
                var basis = new Basis(new Vector3(Mathf.Cos(dirA), 0, Mathf.Sin(dirA)), lean);
                var h = _rng.RandfRange(8f, 13.5f) * (s == 0 ? 1 : 0.8f);
                stems.Add((new Transform3D(basis, new Vector3(p.X + Jitter(0.15f), ground, p.Y + Jitter(0.15f))), h));
            }
            planted++;
        }

        var (barkAlb, barkNor, barkRgh) = BirchBarkMaps();
        var bark = new StandardMaterial3D
        {
            AlbedoTexture = barkAlb,
            NormalEnabled = true,
            NormalTexture = barkNor,
            RoughnessTexture = barkRgh,
            Roughness = 0.82f,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
        };
        var trunkMesh = BirchTrunkMesh();
        var trunks = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, InstanceCount = stems.Count,
            Mesh = trunkMesh,
        };
        for (var i = 0; i < stems.Count; i++)
        {
            var (x, h) = stems[i];
            var r = h * 0.015f * _rng.RandfRange(0.85f, 1.15f);
            trunks.SetInstanceTransform(i, x * new Transform3D(Basis.Identity.Scaled(new Vector3(r, h, r)), Vector3.Zero));
        }
        AddChild(new MultiMeshInstance3D { Multimesh = trunks, MaterialOverride = bark });

        // 자작나무 늘어진 큰 가지들 (수관 골격)
        var branchMesh = new CylinderMesh { TopRadius = 0.02f, BottomRadius = 0.045f, Height = 1.0f, RadialSegments = 8 };
        var branchMat = new StandardMaterial3D { AlbedoColor = new Color(0.20f, 0.17f, 0.15f), Roughness = 0.88f };
        var bTransforms = new List<Transform3D>();
        foreach (var (x, h) in stems)
        {
            var bCount = _rng.RandiRange(5, 8);
            for (var b = 0; b < bCount; b++)
            {
                var u = _rng.RandfRange(0.42f, 0.86f);
                var a = _rng.RandfRange(0, Mathf.Tau);
                var droop = _rng.RandfRange(0.25f, 0.60f);
                var dir = new Vector3(Mathf.Cos(a) * Mathf.Cos(droop), -Mathf.Sin(droop), Mathf.Sin(a) * Mathf.Cos(droop)).Normalized();
                var len = _rng.RandfRange(1.6f, 3.2f);
                var bPos = new Vector3(0, u * h, 0);
                var bBasis = Basis.LookingAt(dir, Vector3.Up) * new Basis(Vector3.Right, Mathf.Pi / 2);
                bBasis = bBasis.Scaled(new Vector3(1, len, 1));
                bTransforms.Add(x * new Transform3D(bBasis, bPos + dir * (len * 0.5f)));
            }
        }
        var bMulti = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = branchMesh, InstanceCount = bTransforms.Count };
        for (var i = 0; i < bTransforms.Count; i++) bMulti.SetInstanceTransform(i, bTransforms[i]);
        AddChild(new MultiMeshInstance3D { Multimesh = bMulti, MaterialOverride = branchMat });

        if (Leaves() is not { } leaf) return;
        var cards = new List<(Transform3D X, Color C)>();
        foreach (var (x, h) in stems)
        {
            var crown = _rng.RandfRange(1.4f, 2.3f);
            var tint = _rng.RandfRange(-0.06f, 0.06f);
            var count = (int)(h * 11);
            for (var k = 0; k < count; k++)
            {
                var u = _rng.RandfRange(0.42f, 1f);                 // 줄기 높이 비율
                var spread = crown * Mathf.Sin(Mathf.Pi * Mathf.Clamp((u - 0.38f) / 0.66f, 0, 1)) * _rng.RandfRange(0.35f, 1f);
                var a = _rng.RandfRange(0, Mathf.Tau);
                var outward = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                var center = new Vector3(0, u * h, 0) + outward * spread;
                // 늘어진 잔가지: 끝(+Y)이 바깥 아래로
                var tip = (outward * _rng.RandfRange(0.3f, 0.8f) + Vector3.Down * _rng.RandfRange(0.4f, 1f)).Normalized();
                var side = tip.Cross(Vector3.Up).Normalized();
                if (side.LengthSquared() < 0.01f) side = Vector3.Right;
                side = side.Rotated(tip, _rng.RandfRange(-0.8f, 0.8f));
                var normal = side.Cross(tip).Normalized();
                var size = _rng.RandfRange(0.55f, 0.9f);
                var basis = new Basis(side * size * 0.5f, tip * size, normal);
                var g = 0.9f + tint + _rng.RandfRange(-0.08f, 0.08f);
                var color = _rng.Randf() < 0.05f ? new Color(1.1f, 1.05f, 0.55f) : new Color(g * 0.95f, g * 1.05f, g * 0.8f);
                cards.Add((x * new Transform3D(basis, center - tip * size * 0.5f), color));
            }
        }
        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true,
            Mesh = new QuadMesh { Size = Vector2.One }, InstanceCount = cards.Count,
        };
        for (var i = 0; i < cards.Count; i++) { mm.SetInstanceTransform(i, cards[i].X); mm.SetInstanceColor(i, cards[i].C); }
        AddChild(new MultiMeshInstance3D { Multimesh = mm, MaterialOverride = leaf });
        GD.Print($"[nature] 자작나무 줄기 {stems.Count}개, 잎 카드 {cards.Count}장");
    }

    public override void _Process(double delta)
    {
        MoveTrain(delta);
        if (_hourHand is null || _minuteHand is null) return;
        var t = Time.GetTimeDictFromSystem();
        float hour = t["hour"].AsInt32() % 12, minute = t["minute"].AsInt32(), second = t["second"].AsInt32();
        _minuteHand.Rotation = new Vector3(0, 0, -(minute + second / 60f) / 60f * Mathf.Tau);
        _hourHand.Rotation = new Vector3(0, 0, -(hour + minute / 60f) / 12f * Mathf.Tau);
    }
}
