using Godot;

namespace DollyForge.Game;

/// <summary>
/// 환경: 실제 촬영 HDRI 하늘, 실시간 전역 조명(SDFGI), 앰비언트 오클루전, 볼류메트릭 안개.
/// 작업장 건물(콘크리트 바닥·회벽·셔터 출입구)과 바깥 시험장(자갈 마당과 풀 언덕)을 만든다.
/// </summary>
public partial class World : Node3D
{
    public const float YardSize = 160f;
    public static readonly Vector3 DeliveryPoint = new(0, 0.15f, 5.5f);
    public static readonly Vector3 WorkbenchTop = new(-3f, 0.92f, -6.5f);
    public static readonly Vector3 PlayerSpawn = new(0, 0.2f, -2.5f);

    public StaticBody3D Catalog { get; private set; } = null!;
    public Label3D LedgerTag { get; private set; } = null!;
    DirectionalLight3D _sun = null!;

    public override void _Ready()
    {
        BuildEnvironment();
        BuildTerrain();
        BuildWorkshop();
    }

    void BuildEnvironment()
    {
        var sky = new Sky
        {
            SkyMaterial = new PanoramaSkyMaterial
            {
                Panorama = ResourceLoader.Exists("res://assets/hdri/kloofendal_48d_partly_cloudy_puresky.hdr")
                    ? GD.Load<Texture2D>("res://assets/hdri/kloofendal_48d_partly_cloudy_puresky.hdr")
                    : null,
                EnergyMultiplier = 1.0f,
            },
            ProcessMode = Sky.ProcessModeEnum.Automatic,
            RadianceSize = Sky.RadianceSizeEnum.Size512,
        };
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = sky,
            AmbientLightSource = Godot.Environment.AmbientSource.Sky,
            ReflectedLightSource = Godot.Environment.ReflectionSource.Sky,
            TonemapMode = Godot.Environment.ToneMapper.Agx,
            TonemapExposure = 1.0f,
            SsaoEnabled = true,
            SsaoRadius = 1.2f,
            SsaoIntensity = 1.6f,
            SsilEnabled = true,
            SsrEnabled = true,
            SsrMaxSteps = 48,
            SdfgiEnabled = true,
            SdfgiUseOcclusion = true,
            SdfgiCascades = 4,
            SdfgiMinCellSize = 0.1f,
            SdfgiEnergy = 1.0f,
            GlowEnabled = true,
            GlowIntensity = 0.25f,
            GlowBloom = 0.02f,
            VolumetricFogEnabled = true,
            VolumetricFogDensity = 0.0006f,
            VolumetricFogAlbedo = new Color(0.9f, 0.92f, 0.95f),
            VolumetricFogLength = 120f,
        };
        AddChild(new WorldEnvironment { Environment = env });

        // 태양: HDRI 의 해 방향(오후, 남서쪽 높이 약 45°)에 맞춘 실제 일사 수준 조명
        _sun = new DirectionalLight3D
        {
            LightEnergy = 1.6f,
            LightColor = new Color(1.0f, 0.96f, 0.9f),
            ShadowEnabled = true,
            DirectionalShadowMaxDistance = 80f,
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel4Splits,
            LightAngularDistance = 0.5f, // 실제 태양의 시직경 (반그림자)
            LightVolumetricFogEnergy = 0.6f,
        };
        _sun.RotationDegrees = new Vector3(-42, -140, 0);
        AddChild(_sun);
    }

    void BuildTerrain()
    {
        // 1 m 격자 높이맵. 작업장 주변은 평평한 자갈 마당, 바깥은 완만한 풀 언덕.
        const int n = 161;
        var noise = new FastNoiseLite { Seed = 20261026, Frequency = 0.012f, FractalOctaves = 5 };
        var heights = new float[n * n];
        for (var z = 0; z < n; z++)
        for (var x = 0; x < n; x++)
        {
            var wx = x - (n - 1) / 2f;
            var wz = z - (n - 1) / 2f;
            var yard = Mathf.SmoothStep(14f, 34f, new Vector2(wx, wz - 2).Length());
            heights[z * n + x] = yard * (noise.GetNoise2D(wx, wz) * 9f + 2.5f * yard) - 0.02f;
        }

        var shape = new HeightMapShape3D { MapWidth = n, MapDepth = n, MapData = heights };
        var body = new StaticBody3D { CollisionLayer = PartBody.LayerWorld, CollisionMask = 0 };
        body.AddChild(new CollisionShape3D { Shape = shape });
        AddChild(body);

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        for (var z = 0; z < n; z++)
        for (var x = 0; x < n; x++)
        {
            var wx = x - (n - 1) / 2f;
            var wz = z - (n - 1) / 2f;
            var h = heights[z * n + x];
            var yard = Mathf.SmoothStep(14f, 26f, new Vector2(wx, wz - 2).Length());
            // R 풀, G 자갈(마당), B 암석 — 셰이더가 경사에 따라 다시 조정한다
            st.SetColor(new Color(yard, 1 - yard, Mathf.Clamp((h - 6) / 4, 0, 1)));
            st.SetUV2(Vector2.Zero);
            st.AddVertex(new Vector3(wx, h, wz));
        }
        for (var z = 0; z < n - 1; z++)
        for (var x = 0; x < n - 1; x++)
        {
            int i = z * n + x;
            st.AddIndex(i); st.AddIndex(i + 1); st.AddIndex(i + n);
            st.AddIndex(i + 1); st.AddIndex(i + n + 1); st.AddIndex(i + n);
        }
        st.GenerateNormals();
        var shader = GD.Load<Shader>("res://shaders/terrain.gdshader");
        var mat = new ShaderMaterial { Shader = shader };
        void Set(string slot, string tex)
        {
            foreach (var (suffix, map) in new[] { ("albedo", "diff"), ("normal", "nor_gl"), ("rough", "rough") })
            {
                var path = $"res://assets/tex/{tex}/{map}.jpg";
                if (ResourceLoader.Exists(path)) mat.SetShaderParameter($"{slot}_{suffix}", GD.Load<Texture2D>(path));
            }
        }
        Set("grass", "aerial_grass_rock");
        Set("soil", "rocky_trail");
        Set("rock", "rock_face_03");
        AddChild(new MeshInstance3D { Mesh = st.Commit(), MaterialOverride = mat });
    }

    void BuildWorkshop()
    {
        var concrete = Look.Scanned("rock_boulder_dry", 2.5f, new Color(0.78f, 0.78f, 0.76f));
        concrete.Roughness = 0.9f;
        var plaster = Look.Scanned("white_plaster_02", 2.0f, Colors.White);
        var steel = (StandardMaterial3D)Look.For(Core.Materials.Steel);
        var plywood = Look.For(Core.Materials.Pine);

        // 바닥 슬래브 12 × 10 m (실내 x -6..6, z -10..0)
        Box(new Vector3(12.4f, 0.2f, 10.4f), new Vector3(0, -0.1f, -5f), concrete);
        // 벽 (두께 0.2, 높이 3.5). 앞면(z = 0)은 4 m 셔터 출입구
        Box(new Vector3(12.4f, 3.5f, 0.2f), new Vector3(0, 1.75f, -10.1f), plaster);
        Box(new Vector3(0.2f, 3.5f, 10.4f), new Vector3(-6.1f, 1.75f, -5f), plaster);
        Box(new Vector3(0.2f, 3.5f, 10.4f), new Vector3(6.1f, 1.75f, -5f), plaster);
        Box(new Vector3(4.2f, 3.5f, 0.2f), new Vector3(-4.1f, 1.75f, 0.1f), plaster);
        Box(new Vector3(4.2f, 3.5f, 0.2f), new Vector3(4.1f, 1.75f, 0.1f), plaster);
        Box(new Vector3(4.0f, 0.6f, 0.2f), new Vector3(0, 3.2f, 0.1f), plaster);
        // 지붕: 강판
        Box(new Vector3(12.8f, 0.12f, 10.8f), new Vector3(0, 3.56f, -5f), steel);

        // 실내 조명: 작업등 (실제 LED 투광등 수준)
        foreach (var x in new[] { -3f, 3f })
            AddChild(new OmniLight3D
            {
                Position = new Vector3(x, 3.2f, -5f),
                LightEnergy = 3.5f,
                OmniRange = 9f,
                LightColor = new Color(1f, 0.95f, 0.88f),
                ShadowEnabled = true,
            });

        // 작업대: 합판 상판 2.0 × 0.8 m, 높이 0.9 m, 강철 다리
        var top = WorkbenchTop;
        Box(new Vector3(2.0f, 0.04f, 0.8f), top - new Vector3(0, 0.02f, 0), plywood);
        foreach (var dx in new[] { -0.95f, 0.95f })
        foreach (var dz in new[] { -0.35f, 0.35f })
            Box(new Vector3(0.05f, 0.88f, 0.05f), new Vector3(top.X + dx, 0.44f, top.Z + dz), steel);

        // 주문 책상과 종이 카탈로그
        var desk = new Vector3(3.5f, 0.75f, -8.5f);
        Box(new Vector3(1.4f, 0.04f, 0.7f), desk - new Vector3(0, 0.02f, 0), plywood);
        foreach (var dx in new[] { -0.65f, 0.65f })
        foreach (var dz in new[] { -0.3f, 0.3f })
            Box(new Vector3(0.04f, 0.73f, 0.04f), new Vector3(desk.X + dx, 0.365f, desk.Z + dz), steel);
        Catalog = Box(new Vector3(0.30f, 0.035f, 0.22f), desk + new Vector3(-0.2f, 0.018f, 0),
            new StandardMaterial3D { AlbedoColor = new Color(0.12f, 0.2f, 0.35f), Roughness = 0.7f });
        Catalog.SetMeta("interact", "catalog");
        var title = Look.Tag("부품 카탈로그", 0.0005f);
        title.Modulate = new Color(0.92f, 0.88f, 0.75f);
        title.RotationDegrees = new Vector3(-90, 0, 0);
        title.Position = new Vector3(0, 0.019f, 0);
        Catalog.AddChild(title);
        // 장부: 잔액·소모품 재고를 적어 두는 종이
        var paper = Box(new Vector3(0.21f, 0.002f, 0.297f), desk + new Vector3(0.3f, 0.001f, 0),
            new StandardMaterial3D { AlbedoColor = new Color(0.95f, 0.93f, 0.87f), Roughness = 0.95f });
        LedgerTag = Look.Tag("", 0.0008f, Look.Pen);
        LedgerTag.RotationDegrees = new Vector3(-90, 0, 0);
        LedgerTag.Position = new Vector3(0, 0.002f, 0);
        LedgerTag.Modulate = new Color(0.1f, 0.12f, 0.25f);
        LedgerTag.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        LedgerTag.Width = 240;
        paper.AddChild(LedgerTag);

        // 택배 하차용 나무 팔레트 (1.2 × 1.0 m, 높이 0.144 m)
        Box(new Vector3(1.2f, 0.144f, 1.0f), new Vector3(DeliveryPoint.X, 0.072f, DeliveryPoint.Z), Look.Scanned("weathered_planks", 1f, Colors.White));
        var sign = Look.Tag("택배 하차장");
        sign.Position = new Vector3(DeliveryPoint.X, 1.2f, DeliveryPoint.Z - 0.9f);
        sign.Billboard = BaseMaterial3D.BillboardModeEnum.Disabled;
        sign.Modulate = new Color(0.95f, 0.95f, 0.9f);
        AddChild(sign);
    }

    StaticBody3D Box(Vector3 size, Vector3 at, Material mat)
    {
        var body = new StaticBody3D { Position = at, CollisionLayer = PartBody.LayerWorld, CollisionMask = 0 };
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
        body.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = size }, MaterialOverride = mat });
        AddChild(body);
        return body;
    }
}
