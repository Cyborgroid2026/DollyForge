using Godot;

namespace DollyForge.Game;

/// <summary>
/// 미래 세계의 풍경. 미학 원칙: 인공미와 자연미의 융합.
///  · 인공미(양자 아날로그 기술) — 통제된 계산: 정밀한 직선과 대칭의 브루탈리즘 탑, 거대한 양자 공명탑의 회전 고리,
///    하늘에 뜬 플랫폼. (모노레일은 Settlement 의 정류장과 함께)
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
    float _time;

    public override void _Ready()
    {
        BuildFarGround();
        BuildForest();
        BuildRocks();
        BuildSkyline();
        BuildResonanceTower(new Vector3(60, 0, -950));
        BuildFloatingPlatforms();
        BuildMasts();
        BuildDrones();
        BuildGreenhouses(new Vector3(-150, -0.6f, -175));
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
            if (!Site.Clear(x, z, 5f) || new Vector2(x, z + 4).Length() < 16) continue; // 사람 자리·작업장 둘레는 비운다
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
            do { x = _rng.RandfRange(-76, 76); z = _rng.RandfRange(-76, 76); } while (!Site.Clear(x, z, 2.5f));
            var size = Mathf.Pow(_rng.Randf(), 2.2f) * 1.6f + 0.15f; // 작은 돌이 많고 큰 바위는 드물다
            var basis = new Basis(new Vector3(_rng.Randf(), _rng.Randf(), _rng.Randf()).Normalized(), _rng.RandfRange(0, Mathf.Tau))
                .Scaled(new Vector3(size * _rng.RandfRange(0.8f, 1.4f), size * _rng.RandfRange(0.45f, 0.8f), size * _rng.RandfRange(0.8f, 1.3f)));
            rocks.SetInstanceTransform(i, new Transform3D(basis, new Vector3(x, World.HeightAt(x, z) + size * 0.15f, z)));
        }
        AddChild(new MultiMeshInstance3D { Multimesh = rocks, MaterialOverride = Look.Scanned("rock_boulder_dry", 1.5f, Colors.White) });
    }

    // ------------------------------------------------------------------ 인공 (양자 아날로그 도시)

    /// <summary>
    /// 흰 외벽 재질: 층(3.6 m)마다 짙은 유리 창 띠, 창마다 반사·불빛 편차. 월드 기준 투영이라 어떤 크기의 탑에도
    /// 같은 층 높이로 입혀진다.
    /// </summary>
    StandardMaterial3D Facade()
    {
        const int w = 64, h = 64;
        var img = Image.CreateEmpty(w, h, true, Image.Format.Rgb8);
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var glass = y >= 34 && y < 58 && x % 16 != 0;
            var c = glass ? new Color(0.16f, 0.2f, 0.24f) * (0.8f + (x / 16) % 3 * 0.12f) : new Color(0.93f, 0.91f, 0.86f) * (0.97f + _rng.Randf() * 0.03f);
            img.SetPixel(x, y, c);
        }
        img.GenerateMipmaps();
        return new StandardMaterial3D
        {
            AlbedoTexture = ImageTexture.CreateFromImage(img), Roughness = 0.45f, Metallic = 0.05f,
            Uv1Triplanar = true, Uv1WorldTriplanar = true, Uv1Scale = new Vector3(1 / 4f, 1 / 3.6f, 1 / 4f),
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
        };
    }

    StandardMaterial3D? _facade;
    StandardMaterial3D FacadeMat => _facade ??= Facade();
    static readonly StandardMaterial3D Enamel = new() { AlbedoColor = new Color(0.95f, 0.94f, 0.9f), Roughness = 0.2f, ClearcoatEnabled = true, Clearcoat = 0.5f };
    static readonly StandardMaterial3D Crimson = new() { AlbedoColor = new Color(0.66f, 0.09f, 0.07f), Roughness = 0.35f, Metallic = 0.2f };
    static readonly StandardMaterial3D Brass = new() { AlbedoColor = new Color(0.86f, 0.66f, 0.36f), Roughness = 0.25f, Metallic = 1f };

    /// <summary>
    /// 지평선의 미래 도시 (레트로퓨처): 흰 원통 탑과 계단식 탑, 둥근 돔 지붕, 놋쇠 첨탑, 진홍색 고리 띠.
    /// 북쪽 공명탑을 축으로 좌우 대칭 배치(통제된 계산)지만 높이·굵기·모양 선택에는 미세한 무작위(불완전함).
    /// </summary>
    void BuildSkyline()
    {
        var shafts = new List<Transform3D>();   // 원통 몸체 (CylinderMesh r=1,h=1)
        var domes = new List<Transform3D>();    // 돔 (SphereMesh r=1)
        var rings = new List<Transform3D>();    // 진홍 고리 (TorusMesh)
        var spires = new List<Transform3D>();   // 놋쇠 첨탑 (원뿔)
        for (var ring = 0; ring < 3; ring++)
        {
            var r = 620 + ring * 170;
            var count = 6 + ring * 2;
            for (var i = 0; i < count; i++)
            foreach (var side in new[] { -1, 1 })
            {
                var a = -Mathf.Pi / 2 + side * (0.12f + i * (Mathf.Pi * 0.86f / count));
                var pos = new Vector3(Mathf.Cos(a) * r, -1, Mathf.Sin(a) * r);
                var height = 70 + ring * 50 + (count - i) * 5 + Jitter(10);
                var radius = 11 + ring * 3 + Jitter(2);
                var kind = (i + ring) % 3;
                if (kind == 1)
                {
                    // 계단식 탑: 세 단이 위로 갈수록 가늘어지고 단마다 진홍 고리, 꼭대기에 첨탑
                    var y = 0f;
                    for (var tier = 0; tier < 3; tier++)
                    {
                        var th = height * (tier == 0 ? 0.5f : 0.25f);
                        var tr = radius * (1 - tier * 0.25f);
                        shafts.Add(new Transform3D(Basis.Identity.Scaled(new Vector3(tr, th, tr)), pos + new Vector3(0, y + th / 2, 0)));
                        rings.Add(new Transform3D(Basis.Identity.Scaled(new Vector3(tr * 1.04f, 2, tr * 1.04f)), pos + new Vector3(0, y + th, 0)));
                        y += th;
                    }
                    spires.Add(new Transform3D(Basis.Identity.Scaled(new Vector3(radius * 0.18f, height * 0.35f, radius * 0.18f)), pos + new Vector3(0, y + height * 0.175f, 0)));
                }
                else
                {
                    // 원통 탑 + 돔 지붕 (가끔 돔 대신 진홍 띠 두 줄)
                    shafts.Add(new Transform3D(Basis.Identity.Scaled(new Vector3(radius, height, radius)), pos + new Vector3(0, height / 2, 0)));
                    domes.Add(new Transform3D(Basis.Identity.Scaled(new Vector3(radius, radius * (kind == 0 ? 0.7f : 0.35f), radius)), pos + new Vector3(0, height, 0)));
                    rings.Add(new Transform3D(Basis.Identity.Scaled(new Vector3(radius * 1.03f, 2.5f, radius * 1.03f)), pos + new Vector3(0, height * 0.62f, 0)));
                    if (kind == 0) spires.Add(new Transform3D(Basis.Identity.Scaled(new Vector3(1.6f, height * 0.25f, 1.6f)), pos + new Vector3(0, height + radius * 0.7f + height * 0.12f, 0)));
                }
            }
        }
        void Batch(Mesh mesh, List<Transform3D> xs, Material mat)
        {
            var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = mesh, InstanceCount = xs.Count };
            for (var i = 0; i < xs.Count; i++) mm.SetInstanceTransform(i, xs[i]);
            AddChild(new MultiMeshInstance3D { Multimesh = mm, MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        }
        Batch(new CylinderMesh { TopRadius = 1, BottomRadius = 1, Height = 1, RadialSegments = 32 }, shafts, FacadeMat);
        Batch(new SphereMesh { Radius = 1, Height = 2, RadialSegments = 32, Rings = 16 }, domes, Enamel);
        Batch(new CylinderMesh { TopRadius = 1, BottomRadius = 1, Height = 1, RadialSegments = 32 }, rings, Crimson);
        Batch(new CylinderMesh { TopRadius = 0, BottomRadius = 1, Height = 1, RadialSegments = 16 }, spires, Brass);
    }

    /// <summary>양자 공명탑: 300 m 콘크리트·강철 바늘 탑과 서로 반대로 도는 거대한 고리들</summary>
    void BuildResonanceTower(Vector3 at)
    {
        var steel = (StandardMaterial3D)Look.For(Core.Materials.Steel);
        var concrete = FacadeMat;
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
        var concrete = Enamel;
        var lamp = new StandardMaterial3D { AlbedoColor = new Color(1, 0.85f, 0.6f), EmissionEnabled = true, Emission = new Color(1, 0.75f, 0.45f), EmissionEnergyMultiplier = 3 };
        var spots = new[] { new Vector3(-520, 230, -640), new Vector3(470, 280, -720), new Vector3(-80, 320, -1100) };
        foreach (var p in spots)
        {
            var r = _rng.RandfRange(38, 60);
            var plat = new Node3D { Position = p };
            plat.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = r, BottomRadius = r * 0.55f, Height = 18, RadialSegments = 48 }, MaterialOverride = concrete });
            plat.AddChild(new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = r * 0.98f, OuterRadius = r * 1.04f, Rings = 64, RingSegments = 8 }, MaterialOverride = Crimson, Position = new Vector3(0, 8.5f, 0) });
            for (var k = 0; k < 16; k++)
            {
                var a = k * Mathf.Tau / 16;
                plat.AddChild(new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.9f, Height = 1.8f }, MaterialOverride = lamp, Position = new Vector3(Mathf.Cos(a) * r * 0.5f, -9.2f, Mathf.Sin(a) * r * 0.5f) });
            }
            for (var b = 0; b < 5; b++) // 위의 건물들
            {
                var h = _rng.RandfRange(12, 40);
                var br = _rng.RandfRange(4, 8);
                plat.AddChild(new MeshInstance3D
                {
                    Mesh = new CylinderMesh { TopRadius = br, BottomRadius = br, Height = h, RadialSegments = 24 }, MaterialOverride = FacadeMat,
                    Position = new Vector3(Jitter(r * 0.5f), 9 + h / 2, Jitter(r * 0.5f)),
                });
            }
            BuildHangingPods(plat, r);
            AddChild(plat);
            _floaters.Add((plat, p, _rng.Randf() * Mathf.Tau));
        }
    }

    /// <summary>시험장 둘레의 철탑 — 꼭대기 항공 장애등이 천천히 깜박인다</summary>
    void BuildMasts()
    {
        var steel = (StandardMaterial3D)Look.For(Core.Materials.Steel);
        foreach (var p in new[] { new Vector3(-62, 0, -40), new Vector3(64, 0, -34), new Vector3(-70, 0, 22) })
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
        AnimateLife(dt);
        foreach (var (node, b, phase) in _floaters) node.Position = b + new Vector3(0, Mathf.Sin(_time * 0.1f + phase) * 2.5f, 0);
    }
}
