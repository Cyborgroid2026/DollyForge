using Godot;

namespace DollyForge.Game;

/// <summary>
/// 미래 세계의 풍경. 미학 원칙: 인공미와 자연미의 융합.
///  · 인공미(양자 아날로그 기술) — 통제된 계산: 정밀한 직선과 대칭의 브루탈리즘 탑, 거대한 양자 공명탑의 회전 고리,
///    하늘에 뜬 플랫폼, 고가 모노레일.
///  · 자연미 — 예측 불가능한 무작위성: 노이즈로 군락을 이루는 소나무 숲(크기·기울기 제각각), 불규칙한 바위.
///  · 인공물에도 미세한 불완전함: 탑 높이 편차, 무작위로 켜진 창, 콘크리트 색 편차.
/// 빛은 실시간 전역 조명과 볼류메트릭 안개가 계산한다.
/// </summary>
public partial class FutureCity : Node3D
{
    public World World { get; set; } = null!;
    readonly RandomNumberGenerator _rng = new() { Seed = 20261026 };
    readonly List<(Node3D Node, float Speed)> _spinners = new();
    readonly List<(Node3D Node, Vector3 Base, float Phase)> _floaters = new();
    Node3D? _train;
    Curve3D? _rail;
    float _railLength, _trainT;
    float _time;

    public override void _Ready()
    {
        BuildFarGround();
        BuildForest();
        BuildRocks();
        BuildSkyline();
        BuildResonanceTower(new Vector3(60, 0, -950));
        BuildFloatingPlatforms();
        BuildMonorail();
        BuildMasts();
    }

    float Jitter(float amount) => _rng.RandfRange(-amount, amount);

    // ------------------------------------------------------------------ 자연

    /// <summary>시험장 지형 바깥의 먼 평원 (낮은 해상도, 같은 스캔 재질)</summary>
    void BuildFarGround()
    {
        var mat = Look.Scanned("aerial_grass_rock", 6f, new Color(0.92f, 0.95f, 0.85f));
        AddChild(new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(2400, 2400) },
            MaterialOverride = mat,
            Position = new Vector3(0, -0.6f, 0),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
    }

    /// <summary>
    /// 소나무 가지: pine_tree_01 텍스처는 솔방울·껍질·잎·가지가 모인 아틀라스라서
    /// 왼쪽 위의 잔가지 한 송이(220×430 px)만 잘라 색과 알파를 합친다. 카드의 +Y 가 가지 끝.
    /// </summary>
    static Material? Foliage()
    {
        const string diff = "res://assets/tex/pine/twig_diff.jpg", alpha = "res://assets/tex/pine/twig_alpha.jpg";
        if (!ResourceLoader.Exists(diff) || !ResourceLoader.Exists(alpha)) return null;
        var src = GD.Load<Texture2D>(diff).GetImage();
        var mask = GD.Load<Texture2D>(alpha).GetImage();
        if (src.IsCompressed()) src.Decompress();
        if (mask.IsCompressed()) mask.Decompress();
        var scale = src.GetWidth() / 1024f;
        var rect = new Rect2I((int)(20 * scale), (int)(30 * scale), (int)(220 * scale), (int)(430 * scale));
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
        // 바람에 흔들리는 가지 (shaders/foliage.gdshader)
        var mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/foliage.gdshader") };
        mat.SetShaderParameter("albedo_tex", ImageTexture.CreateFromImage(img));
        return mat;
    }

    /// <summary>
    /// 소나무 숲: 노이즈가 높은 곳에만 군락을 이루고(자연스러운 숲 경계), 나무마다 크기·기울기·회전이 다르다.
    /// 가지는 줄기에서 바깥으로 뻗어 끝이 처지며, 아래 가지일수록 길다. 가지마다 길이·방향·처짐·색이 제각각.
    /// </summary>
    void BuildForest()
    {
        var noise = new FastNoiseLite { Seed = 7, Frequency = 0.02f };
        var trees = new List<(Transform3D X, float H)>();
        for (var tries = 0; tries < 6000 && trees.Count < 380; tries++)
        {
            var x = _rng.RandfRange(-78, 78);
            var z = _rng.RandfRange(-78, 78);
            if (new Vector2(x, z - 2).Length() < 22) continue; // 작업장과 자갈 마당은 비운다
            if (new Vector2(x - World.PondCenter.X, z - World.PondCenter.Y).Length() < World.PondRadius + 3) continue;
            if (noise.GetNoise2D(x, z) < 0.05f + _rng.Randf() * 0.25f) continue;
            var s = _rng.RandfRange(0.7f, 1.4f);
            var basis = new Basis(Vector3.Up, _rng.RandfRange(0, Mathf.Tau))
                * new Basis(new Vector3(1, 0, 0), Jitter(0.05f)) * new Basis(new Vector3(0, 0, 1), Jitter(0.05f));
            trees.Add((new Transform3D(basis.Scaled(Vector3.One * s), new Vector3(x, World.HeightAt(x, z) - 0.15f, z)), _rng.RandfRange(7.5f, 11f)));
        }

        var trunkMesh = new CylinderMesh { TopRadius = 0.05f, BottomRadius = 0.24f, Height = 1f, RadialSegments = 10 };
        var bark = Look.Scanned("bark_brown_02", 1.2f, Colors.White);
        var trunks = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = trunkMesh, InstanceCount = trees.Count };
        for (var i = 0; i < trees.Count; i++)
            trunks.SetInstanceTransform(i, trees[i].X * new Transform3D(Basis.Identity.Scaled(new Vector3(1, trees[i].H, 1)), new Vector3(0, trees[i].H / 2, 0)));
        AddChild(new MultiMeshInstance3D { Multimesh = trunks, MaterialOverride = bark });

        if (Foliage() is not { } leaf) return;
        var cards = new List<(Transform3D X, Color C)>();
        foreach (var (t, height) in trees)
        {
            // 줄기 아래 30 % 는 가지가 없다 (숲속 소나무처럼). 위로 갈수록 가지가 짧고 촘촘하다.
            for (var y = height * 0.3f; y < height; y += _rng.RandfRange(0.28f, 0.45f))
            {
                var u = (y - height * 0.3f) / (height * 0.7f);
                var count = _rng.RandiRange(4, 6);
                for (var k = 0; k < count; k++)
                {
                    var a = _rng.RandfRange(0, Mathf.Tau);
                    var len = Mathf.Lerp(2.0f, 0.5f, u) * _rng.RandfRange(0.75f, 1.2f);
                    var droop = _rng.RandfRange(0.15f, 0.45f) + (1 - u) * 0.2f;
                    var dir = new Vector3(Mathf.Cos(a) * Mathf.Cos(droop), -Mathf.Sin(droop), Mathf.Sin(a) * Mathf.Cos(droop));
                    var side = new Vector3(-Mathf.Sin(a), 0, Mathf.Cos(a));
                    var normal = side.Cross(dir).Normalized();
                    var center = new Vector3(0, y, 0) + dir * len * 0.5f;
                    var tone = 0.85f + _rng.Randf() * 0.3f;
                    var color = new Color(tone, tone * 1.02f, tone * 0.92f);
                    // 가지 하나 = 수평 카드 + 세로 카드 (어느 방향에서 봐도 비어 보이지 않게)
                    foreach (var (bx, bz) in new[] { (side, normal), (normal, -side) })
                    {
                        var basis = new Basis(bx * len * 0.55f, dir * len, bz);
                        cards.Add((t * new Transform3D(basis, center), color));
                    }
                }
            }
        }
        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true,
            Mesh = new QuadMesh { Size = Vector2.One }, InstanceCount = cards.Count,
        };
        for (var i = 0; i < cards.Count; i++) { mm.SetInstanceTransform(i, cards[i].X); mm.SetInstanceColor(i, cards[i].C); }
        AddChild(new MultiMeshInstance3D { Multimesh = mm, MaterialOverride = leaf });
        GD.Print($"[nature] 소나무 {trees.Count}그루, 가지 카드 {cards.Count}장");
    }

    /// <summary>불규칙한 바위: 찌그러뜨린 다면체, 크기·회전·묻힌 깊이가 제각각</summary>
    void BuildRocks()
    {
        var mesh = new SphereMesh { Radius = 1, Height = 2, RadialSegments = 9, Rings = 6 };
        var rocks = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = mesh, InstanceCount = 90 };
        for (var i = 0; i < 90; i++)
        {
            float x, z;
            do { x = _rng.RandfRange(-76, 76); z = _rng.RandfRange(-76, 76); } while (new Vector2(x, z - 2).Length() < 20);
            var size = Mathf.Pow(_rng.Randf(), 2.2f) * 1.6f + 0.15f; // 작은 돌이 많고 큰 바위는 드물다
            var basis = new Basis(new Vector3(_rng.Randf(), _rng.Randf(), _rng.Randf()).Normalized(), _rng.RandfRange(0, Mathf.Tau))
                .Scaled(new Vector3(size * _rng.RandfRange(0.8f, 1.4f), size * _rng.RandfRange(0.45f, 0.8f), size * _rng.RandfRange(0.8f, 1.3f)));
            rocks.SetInstanceTransform(i, new Transform3D(basis, new Vector3(x, World.HeightAt(x, z) + size * 0.15f, z)));
        }
        AddChild(new MultiMeshInstance3D { Multimesh = rocks, MaterialOverride = Look.Scanned("rock_boulder_dry", 1.5f, Colors.White) });
    }

    // ------------------------------------------------------------------ 인공 (양자 아날로그 도시)

    /// <summary>
    /// 지평선의 브루탈리즘 탑: 북쪽 공명탑을 축으로 좌우 대칭 배치(통제된 계산),
    /// 높이·창 불빛·콘크리트 색에는 미세한 무작위(불완전함).
    /// </summary>
    void BuildSkyline()
    {
        var concrete = Look.Scanned("rock_boulder_dry", 4f, new Color(0.82f, 0.81f, 0.78f));
        concrete.VertexColorUseAsAlbedo = true;
        var windowMat = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.15f, 0.12f, 0.08f), EmissionEnabled = true, Emission = new Color(1f, 0.72f, 0.38f),
            EmissionEnergyMultiplier = 1.4f, VertexColorUseAsAlbedo = false,
        };
        var towers = new List<(Transform3D X, Color C)>();
        var windows = new List<Transform3D>();
        var cols = new[] { -1, 1 };
        for (var ring = 0; ring < 3; ring++)
        {
            var r = 620 + ring * 160;
            var count = 6 + ring * 2;
            for (var i = 0; i < count; i++)
            foreach (var side in cols)
            {
                // 북쪽(-Z) 축을 기준으로 좌우 대칭
                var a = -Mathf.Pi / 2 + side * (0.12f + i * (Mathf.Pi * 0.86f / count));
                var pos = new Vector3(Mathf.Cos(a) * r, 0, Mathf.Sin(a) * r);
                var w = 22 + ring * 6 + Jitter(2);
                var d = 18 + Jitter(2);
                var h = 70 + ring * 45 + (count - i) * 4 + Jitter(9);
                var basis = new Basis(Vector3.Up, -a + Mathf.Pi / 2);
                towers.Add((new Transform3D(basis.Scaled(new Vector3(w, h, d)), pos + new Vector3(0, h / 2 - 1, 0)),
                    new Color(1, 1, 1) * (0.88f + _rng.Randf() * 0.18f)));
                // 층마다 가로 창 띠, 일부만 불이 켜져 있다
                for (var y = 6f; y < h - 4; y += 3.6f)
                {
                    if (_rng.Randf() < 0.45f) continue;
                    var seg = _rng.RandfRange(0.25f, 0.9f) * w;
                    var off = Jitter((w - seg) / 2);
                    windows.Add(new Transform3D(basis.Scaled(new Vector3(seg, 0.9f, 0.3f)), pos + basis * new Vector3(off, 0, -d / 2 - 0.1f) + new Vector3(0, y, 0)));
                }
            }
        }
        var tm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, Mesh = new BoxMesh(), InstanceCount = towers.Count };
        for (var i = 0; i < towers.Count; i++) { tm.SetInstanceTransform(i, towers[i].X); tm.SetInstanceColor(i, towers[i].C); }
        AddChild(new MultiMeshInstance3D { Multimesh = tm, MaterialOverride = concrete, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        var wm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = new BoxMesh(), InstanceCount = windows.Count };
        for (var i = 0; i < windows.Count; i++) wm.SetInstanceTransform(i, windows[i]);
        AddChild(new MultiMeshInstance3D { Multimesh = wm, MaterialOverride = windowMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
    }

    /// <summary>양자 공명탑: 300 m 콘크리트·강철 바늘 탑과 서로 반대로 도는 거대한 고리들</summary>
    void BuildResonanceTower(Vector3 at)
    {
        var steel = (StandardMaterial3D)Look.For(Core.Materials.Steel);
        var concrete = Look.Scanned("rock_boulder_dry", 6f, new Color(0.8f, 0.79f, 0.76f));
        var tower = new Node3D { Position = at };
        AddChild(tower);
        tower.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 9, BottomRadius = 26, Height = 120, RadialSegments = 8 }, MaterialOverride = concrete, Position = new Vector3(0, 60, 0) });
        tower.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 1.5f, BottomRadius = 7, Height = 190, RadialSegments = 12 }, MaterialOverride = steel, Position = new Vector3(0, 215, 0) });
        var lamp = new StandardMaterial3D { AlbedoColor = new Color(0.8f, 0.95f, 1f), EmissionEnabled = true, Emission = new Color(0.6f, 0.9f, 1f), EmissionEnergyMultiplier = 4 };
        for (var i = 0; i < 4; i++)
        {
            var ring = new Node3D { Position = new Vector3(0, 150 + i * 32, 0), Rotation = new Vector3(0.18f * (i % 2 == 0 ? 1 : -1), 0, 0) };
            ring.AddChild(new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = 34 - i * 6, OuterRadius = 37 - i * 6, Rings = 96, RingSegments = 12 }, MaterialOverride = steel });
            // 고리 안쪽 아크 등 (양자 공명의 빛) — 정확히 등간격
            for (var k = 0; k < 12; k++)
            {
                var a = k * Mathf.Tau / 12;
                ring.AddChild(new MeshInstance3D
                {
                    Mesh = new SphereMesh { Radius = 1.1f, Height = 2.2f }, MaterialOverride = lamp,
                    Position = new Vector3(Mathf.Cos(a) * (33 - i * 6), 0, Mathf.Sin(a) * (33 - i * 6)),
                });
            }
            tower.AddChild(ring);
            _spinners.Add((ring, (i % 2 == 0 ? 1 : -1) * (0.05f + i * 0.02f)));
        }
        tower.AddChild(new OmniLight3D { Position = new Vector3(0, 312, 0), LightColor = new Color(0.6f, 0.9f, 1f), LightEnergy = 8, OmniRange = 120 });
    }

    /// <summary>하늘에 뜬 콘크리트 플랫폼 — 아래쪽에 등이 달리고 천천히 오르내린다</summary>
    void BuildFloatingPlatforms()
    {
        var concrete = Look.Scanned("rock_boulder_dry", 5f, new Color(0.78f, 0.77f, 0.74f));
        var lamp = new StandardMaterial3D { AlbedoColor = new Color(1, 0.85f, 0.6f), EmissionEnabled = true, Emission = new Color(1, 0.75f, 0.45f), EmissionEnergyMultiplier = 3 };
        var spots = new[] { new Vector3(-520, 230, -640), new Vector3(470, 280, -720), new Vector3(-80, 320, -1100) };
        foreach (var p in spots)
        {
            var r = _rng.RandfRange(38, 60);
            var plat = new Node3D { Position = p };
            plat.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = r, BottomRadius = r * 0.55f, Height = 18, RadialSegments = 24 }, MaterialOverride = concrete });
            for (var k = 0; k < 16; k++)
            {
                var a = k * Mathf.Tau / 16;
                plat.AddChild(new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.9f, Height = 1.8f }, MaterialOverride = lamp, Position = new Vector3(Mathf.Cos(a) * r * 0.5f, -9.2f, Mathf.Sin(a) * r * 0.5f) });
            }
            for (var b = 0; b < 5; b++) // 위의 건물들
            {
                var h = _rng.RandfRange(12, 40);
                plat.AddChild(new MeshInstance3D
                {
                    Mesh = new BoxMesh { Size = new Vector3(_rng.RandfRange(8, 16), h, _rng.RandfRange(8, 16)) }, MaterialOverride = concrete,
                    Position = new Vector3(Jitter(r * 0.5f), 9 + h / 2, Jitter(r * 0.5f)),
                });
            }
            AddChild(plat);
            _floaters.Add((plat, p, _rng.Randf() * Mathf.Tau));
        }
    }

    /// <summary>고가 모노레일: 시험장 남쪽을 가로지르는 정밀한 곡선 궤도와 기둥, 그 위를 오가는 객차</summary>
    void BuildMonorail()
    {
        var steel = (StandardMaterial3D)Look.For(Core.Materials.Steel);
        var concrete = Look.Scanned("rock_boulder_dry", 3f, new Color(0.8f, 0.79f, 0.76f));
        _rail = new Curve3D { BakeInterval = 2 };
        for (var i = -10; i <= 10; i++)
        {
            var x = i * 40f;
            _rail.AddPoint(new Vector3(x, 26 + Mathf.Sin(i * 0.4f) * 2, 210 + Mathf.Cos(i * 0.3f) * 30));
        }
        _railLength = _rail.GetBakedLength();
        var beams = new List<Transform3D>();
        var pillars = new List<Transform3D>();
        for (float d = 0; d < _railLength; d += 4)
        {
            var a = _rail.SampleBaked(d);
            var b = _rail.SampleBaked(Mathf.Min(d + 4, _railLength));
            var mid = (a + b) / 2;
            var basis = Basis.LookingAt((b - a).Normalized(), Vector3.Up);
            beams.Add(new Transform3D(basis.Scaled(new Vector3(1.2f, 1.6f, (b - a).Length() + 0.05f)), mid));
            if ((int)d % 32 == 0) pillars.Add(new Transform3D(Basis.Identity.Scaled(new Vector3(1.6f, mid.Y, 1.6f)), new Vector3(mid.X, mid.Y / 2 - 0.8f, mid.Z)));
        }
        var bm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = new BoxMesh(), InstanceCount = beams.Count };
        for (var i = 0; i < beams.Count; i++) bm.SetInstanceTransform(i, beams[i]);
        AddChild(new MultiMeshInstance3D { Multimesh = bm, MaterialOverride = concrete });
        var pm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = new CylinderMesh { TopRadius = 0.5f, BottomRadius = 0.6f, Height = 1 }, InstanceCount = pillars.Count };
        for (var i = 0; i < pillars.Count; i++) pm.SetInstanceTransform(i, pillars[i]);
        AddChild(new MultiMeshInstance3D { Multimesh = pm, MaterialOverride = concrete });

        _train = new Node3D();
        var paint = new StandardMaterial3D { AlbedoColor = new Color(0.72f, 0.12f, 0.1f), Metallic = 0.4f, Roughness = 0.35f };
        var glass = new StandardMaterial3D { AlbedoColor = new Color(0.1f, 0.12f, 0.15f), Metallic = 0.6f, Roughness = 0.1f };
        for (var car = 0; car < 3; car++)
        {
            var body = new MeshInstance3D { Mesh = new CapsuleMesh { Radius = 1.4f, Height = 13 }, MaterialOverride = paint, Rotation = new Vector3(Mathf.Pi / 2, 0, 0), Position = new Vector3(0, 2.1f, -car * 13.5f) };
            _train.AddChild(body);
            _train.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(2.9f, 0.6f, 9) }, MaterialOverride = glass, Position = new Vector3(0, 2.6f, -car * 13.5f) });
            _ = steel;
        }
        AddChild(_train);
    }

    /// <summary>시험장 둘레의 철탑 — 꼭대기 항공 장애등이 천천히 깜박인다</summary>
    void BuildMasts()
    {
        var steel = (StandardMaterial3D)Look.For(Core.Materials.Steel);
        foreach (var p in new[] { new Vector3(-55, 0, -40), new Vector3(60, 0, -30), new Vector3(-30, 0, 62) })
        {
            var y0 = World.HeightAt(p.X, p.Z);
            var mast = new Node3D { Position = p with { Y = y0 } };
            mast.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.15f, BottomRadius = 1.4f, Height = 38, RadialSegments = 4 }, MaterialOverride = steel, Position = new Vector3(0, 19, 0) });
            var red = new StandardMaterial3D { AlbedoColor = new Color(0.4f, 0.02f, 0.02f), EmissionEnabled = true, Emission = new Color(1, 0.1f, 0.05f), EmissionEnergyMultiplier = 6 };
            var light = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.35f, Height = 0.7f }, MaterialOverride = red, Position = new Vector3(0, 38.3f, 0) };
            mast.AddChild(light);
            light.SetMeta("blink", _rng.Randf() * Mathf.Tau);
            AddChild(mast);
        }
    }

    public override void _Process(double delta)
    {
        var dt = (float)delta;
        _time += dt;
        foreach (var (node, speed) in _spinners) node.RotateY(speed * dt);
        foreach (var (node, b, phase) in _floaters) node.Position = b + new Vector3(0, Mathf.Sin(_time * 0.1f + phase) * 2.5f, 0);
        if (_train is not null && _rail is not null)
        {
            _trainT = (_trainT + dt * 22f) % (_railLength + 400); // 약 80 km/h, 지나간 뒤 잠시 쉼
            var d = Mathf.Min(_trainT, _railLength - 1);
            var p = _rail.SampleBaked(d);
            var ahead = _rail.SampleBaked(Mathf.Min(d + 2, _railLength));
            _train.GlobalTransform = new Transform3D(Basis.LookingAt((ahead - p).Normalized(), Vector3.Up), p + new Vector3(0, 0.8f, 0));
            _train.Visible = _trainT < _railLength;
        }
    }
}
