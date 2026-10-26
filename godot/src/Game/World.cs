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
    /// <summary>정비 수술대 상판 중심. 바꿔 끼울 몸 모듈을 이 근처(2 m)에 둔다.</summary>
    public static readonly Vector3 SurgeryTop = new(-4.6f, 0.86f, -3.0f);

    public StaticBody3D Catalog { get; private set; } = null!;
    public Label3D LedgerTag { get; private set; } = null!;
    DirectionalLight3D _sun = null!;
    const int TerrainN = 161;
    /// <summary>연못: 작업장 문(남쪽) 너머 언덕 사이의 오목한 곳</summary>
    public static readonly Vector2 PondCenter = new(22f, 34f);
    public const float PondRadius = 9f, PondDepth = 2.2f, PondBank = 1.4f;
    float[] _heights = [];

    /// <summary>지형 높이 (1 m 격자 보간). 격자 밖은 0.</summary>
    public float HeightAt(float x, float z)
    {
        const int n = TerrainN;
        var fx = x + (n - 1) / 2f;
        var fz = z + (n - 1) / 2f;
        if (fx < 0 || fz < 0 || fx >= n - 1 || fz >= n - 1) return 0;
        int i = (int)fz, j = (int)fx;
        float tx = fx - j, tz = fz - i;
        float H(int a, int b) => _heights[a * n + b];
        return Mathf.Lerp(Mathf.Lerp(H(i, j), H(i, j + 1), tx), Mathf.Lerp(H(i + 1, j), H(i + 1, j + 1), tx), tz);
    }

    public override void _Ready()
    {
        BuildEnvironment();
        BuildTerrain();
        BuildWorkshop();
        AddChild(new Settlement { World = this });
        AddChild(new FutureCity { World = this });
        AddChild(new Nature { World = this });
    }

    /// <summary>아침 하늘 HDRI (Poly Haven qwantani_mid_morning) 속 해의 방향, 고도 약 40°</summary>
    static readonly Vector3 HdriSun = new Vector3(0.450f, 0.645f, -0.617f).Normalized();
    /// <summary>하늘을 돌려 해가 동남동에서 비치게 한다: 정류장에서 북쪽(작업장·도시)을 볼 때 오른쪽 옆빛</summary>
    const float SkyYaw = -1.32f;
    public static Vector3 SunDirection => HdriSun.Rotated(Vector3.Up, SkyYaw);

    void BuildEnvironment()
    {
        const string hdri = "res://assets/hdri/qwantani_mid_morning_puresky.hdr";
        var sky = new Sky
        {
            SkyMaterial = new PanoramaSkyMaterial
            {
                Panorama = ResourceLoader.Exists(hdri) ? GD.Load<Texture2D>(hdri) : null,
                EnergyMultiplier = 1.0f,
            },
            ProcessMode = Sky.ProcessModeEnum.Automatic,
            RadianceSize = Sky.RadianceSizeEnum.Size512,
        };
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = sky,
            SkyRotation = new Vector3(0, SkyYaw, 0),
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
            VolumetricFogDensity = 0.0012f,
            VolumetricFogAlbedo = new Color(0.95f, 0.93f, 0.9f),
            VolumetricFogAnisotropy = 0.55f, // 해 쪽으로 빛이 번지는 앞방향 산란
            VolumetricFogLength = 140f,
            // 공기 원근: 멀수록 하늘빛에 잠기는 실제 대기 산란 + 낮은 곳에 고이는 아침 안개
            FogEnabled = true,
            FogLightColor = new Color(0.72f, 0.78f, 0.86f),
            FogDensity = 0.0013f,
            FogAerialPerspective = 0.8f,
            FogSunScatter = 0.35f,
            FogSkyAffect = 0.0f,
            FogHeight = 1.2f,
            FogHeightDensity = 0.035f,
            // 색: 맑은 아침의 채도와 대비 (과하지 않게)
            AdjustmentEnabled = true,
            AdjustmentSaturation = 1.12f,
            AdjustmentContrast = 1.05f,
        };
        AddChild(new WorldEnvironment { Environment = env });

        // 태양: HDRI 의 해 방향에 맞춘 아침 햇빛 (약간 따뜻한 색)
        _sun = new DirectionalLight3D
        {
            LightEnergy = 1.9f,
            LightColor = new Color(1.0f, 0.92f, 0.82f),
            ShadowEnabled = true,
            DirectionalShadowMaxDistance = 80f,
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel4Splits,
            LightAngularDistance = 0.5f, // 실제 태양의 시직경 (반그림자)
            LightVolumetricFogEnergy = 0.6f,
        };
        _sun.Basis = Basis.LookingAt(-SunDirection, Vector3.Up);
        AddChild(_sun);
    }

    void BuildTerrain()
    {
        // 1 m 격자 높이맵. 작업장 주변은 평평한 자갈 마당, 바깥은 완만한 풀 언덕.
        const int n = TerrainN;
        // 자연 지형: 도메인 왜곡(물결치듯 흐르는 지형선) + 능선 노이즈(침식된 등성이) + 잔결 노이즈
        var noise = new FastNoiseLite { Seed = 20261026, Frequency = 0.012f, FractalOctaves = 5 };
        var warp = new FastNoiseLite { Seed = 11, Frequency = 0.009f, FractalOctaves = 3 };
        var ridge = new FastNoiseLite { Seed = 5, Frequency = 0.011f, FractalType = FastNoiseLite.FractalTypeEnum.Ridged, FractalOctaves = 4 };
        var fine = new FastNoiseLite { Seed = 23, Frequency = 0.15f, FractalOctaves = 2 };
        var heights = _heights = new float[n * n];
        for (var z = 0; z < n; z++)
        for (var x = 0; x < n; x++)
        {
            var wx = x - (n - 1) / 2f;
            var wz = z - (n - 1) / 2f;
            // 작업장 터만 평평하게 (건물·앞마당 둘레 10 m 안쪽), 그 바깥은 바로 자연 지형
            var yard = Mathf.SmoothStep(11f, 26f, new Vector2(wx, (wz + 1) * 0.85f).Length());
            var ux = wx + warp.GetNoise2D(wx, wz) * 22f;
            var uz = wz + warp.GetNoise2D(wz + 137f, wx - 59f) * 22f;
            var hills = noise.GetNoise2D(ux, uz) * 7f + ridge.GetNoise2D(ux, uz) * 3.5f + fine.GetNoise2D(wx, wz) * 0.35f;
            // 연못: 둘레는 고른 둑으로 가라앉고, 안쪽은 우묵한 바닥
            var pd = new Vector2(wx - PondCenter.X, wz - PondCenter.Y).Length();
            var bank = Mathf.SmoothStep(PondRadius + 9f, PondRadius + 2f, pd);
            var bowl = Mathf.SmoothStep(PondRadius + 1f, PondRadius * 0.3f, pd);
            var ground = yard * (hills + 2.5f * yard) - 0.02f;
            heights[z * n + x] = Mathf.Lerp(ground, PondBank, bank) - bowl * PondDepth;
        }
        Site.ShapeTerrain(heights, n);

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
            // 자갈은 건물 벽 둘레와 선로 바닥에만. 나머지는 풀밭 (오솔길·마당은 따로 포장한다)
            var p = new Vector2(wx, wz);
            var gravel = Mathf.Max(1 - Mathf.SmoothStep(1.5f, 4f, DistanceToRect(Site.Building, p)),
                                   1 - Mathf.SmoothStep(2.4f, 4.2f, Mathf.Abs(wz - Site.RailZ)));
            // R 풀, G 자갈, B 암석 — 셰이더가 경사에 따라 다시 조정한다
            st.SetColor(new Color(1 - gravel, gravel, Mathf.Clamp((h - 6) / 4, 0, 1)));
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
        var plaster = Look.Scanned("white_plaster_02", 2.5f, new Color(0.98f, 0.96f, 0.92f));
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

        // 정비 수술대: 스테인리스 상판 0.7 × 2.0 m, 높이 0.86 m, 유압 기둥 하나. 위에 무영등.
        var bed = Box(new Vector3(0.7f, 0.05f, 2.0f), SurgeryTop - new Vector3(0, 0.025f, 0),
            new StandardMaterial3D { AlbedoColor = new Color(0.78f, 0.8f, 0.82f), Metallic = 1f, Roughness = 0.28f });
        bed.SetMeta("interact", "surgery");
        var column = new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0.09f, BottomRadius = 0.12f, Height = SurgeryTop.Y - 0.05f },
            Position = new Vector3(SurgeryTop.X, (SurgeryTop.Y - 0.05f) / 2, SurgeryTop.Z),
            MaterialOverride = steel,
        };
        AddChild(column);
        Box(new Vector3(0.55f, 0.04f, 0.8f), new Vector3(SurgeryTop.X, 0.02f, SurgeryTop.Z), steel);
        AddChild(new SpotLight3D
        {
            Position = SurgeryTop + new Vector3(0, 1.6f, 0),
            RotationDegrees = new Vector3(-90, 0, 0),
            SpotAngle = 28, SpotRange = 2.5f, LightEnergy = 6f,
            LightColor = new Color(0.97f, 0.98f, 1f), ShadowEnabled = true,
        });
        var bedSign = Look.Tag("정비 수술대 — 바꿀 몸 모듈을 곁에 두고 E", 0.0009f);
        bedSign.Position = SurgeryTop + new Vector3(-0.3f, 0.95f, 0); // 침대 머리 쪽 벽 높이
        bedSign.RotationDegrees = new Vector3(0, 90, 0);
        bedSign.Billboard = BaseMaterial3D.BillboardModeEnum.Disabled;
        bedSign.Modulate = new Color(0.95f, 0.95f, 0.9f);
        AddChild(bedSign);

        // 택배 하차용 나무 팔레트 (1.2 × 1.0 m, 높이 0.144 m)
        Box(new Vector3(1.2f, 0.144f, 1.0f), new Vector3(DeliveryPoint.X, 0.072f, DeliveryPoint.Z), Look.Scanned("weathered_planks", 1f, Colors.White));
        var sign = Look.Tag("택배 하차장");
        sign.Position = new Vector3(DeliveryPoint.X, 1.2f, DeliveryPoint.Z - 0.9f);
        sign.Billboard = BaseMaterial3D.BillboardModeEnum.Disabled;
        sign.Modulate = new Color(0.95f, 0.95f, 0.9f);
        AddChild(sign);
    }

    static float DistanceToRect(Rect2 r, Vector2 p)
    {
        var dx = Mathf.Max(Mathf.Max(r.Position.X - p.X, p.X - r.End.X), 0);
        var dz = Mathf.Max(Mathf.Max(r.Position.Y - p.Y, p.Y - r.End.Y), 0);
        return Mathf.Sqrt(dx * dx + dz * dz);
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
