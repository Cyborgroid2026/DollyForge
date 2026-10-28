using Godot;

namespace DollyForge.Game;

/// <summary>
/// 한국 주택가 골목이 거리뷰 사진처럼 읽히게 하는 작은 것들.
/// 건물 쪽(건물 로컬 좌표, 원점 = 건물 상자 중심, 정면 = +Z): 노란 도시가스관과 계량기, 방범창, 실외기 앵글,
/// 옥상 난간·녹색 방수 바닥·물탱크·계단실, 가게 간판과 돌출 간판, 건물번호판.
/// 길 쪽(월드 좌표): 과속방지턱, 도로반사경, 도로명판, 종량제 봉투, 배달 오토바이, 스티로폼 텃밭, 주차금지 드럼.
/// </summary>
internal static class StreetDetail
{
    static readonly StandardMaterial3D GasYellow = new() { AlbedoColor = new Color(0.84f, 0.66f, 0.13f), Metallic = 0.25f, Roughness = 0.5f };
    static readonly StandardMaterial3D Galvanized = new() { AlbedoColor = new Color(0.56f, 0.57f, 0.56f), Metallic = 0.75f, Roughness = 0.42f };
    static readonly StandardMaterial3D DarkSteel = new() { AlbedoColor = new Color(0.17f, 0.18f, 0.18f), Metallic = 0.55f, Roughness = 0.52f };
    static readonly StandardMaterial3D AcWhite = new() { AlbedoColor = new Color(0.8f, 0.8f, 0.76f), Roughness = 0.58f };
    static readonly StandardMaterial3D Insulation = new() { AlbedoColor = new Color(0.86f, 0.86f, 0.82f), Roughness = 0.7f };
    static readonly StandardMaterial3D PlateBlue = new() { AlbedoColor = new Color(0.07f, 0.2f, 0.47f), Roughness = 0.38f };
    static readonly StandardMaterial3D Mirror = new() { AlbedoColor = new Color(0.86f, 0.88f, 0.9f), Metallic = 1f, Roughness = 0.04f };
    static readonly StandardMaterial3D SafetyOrange = new() { AlbedoColor = new Color(0.9f, 0.36f, 0.08f), Roughness = 0.55f };
    static readonly StandardMaterial3D ReflectWhite = new() { AlbedoColor = new Color(0.9f, 0.9f, 0.87f), Roughness = 0.45f };
    static readonly StandardMaterial3D Styrofoam = new() { AlbedoColor = new Color(0.9f, 0.9f, 0.86f), Roughness = 0.82f };
    static readonly StandardMaterial3D Rubber = new() { AlbedoColor = new Color(0.04f, 0.04f, 0.04f), Roughness = 0.88f };

    static readonly Color[] SignColors =
    [
        new(0.92f, 0.91f, 0.86f), new(0.12f, 0.3f, 0.6f), new(0.66f, 0.13f, 0.1f), new(0.95f, 0.78f, 0.2f), new(0.16f, 0.42f, 0.28f),
    ];

    static float Hash(int seed, int salt) => StreetEnvironment.Hash(seed, salt);

    static MeshInstance3D Box(Node3D parent, Vector3 size, Vector3 position, Material material, Vector3? rotation = null)
    {
        var mesh = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = size },
            Position = position,
            Rotation = rotation ?? Vector3.Zero,
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        parent.AddChild(mesh);
        return mesh;
    }

    static MeshInstance3D Cylinder(Node3D parent, float radius, float height, Vector3 position, Material material, Vector3? rotation = null, int segments = 10)
    {
        var mesh = new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = radius, BottomRadius = radius, Height = height, RadialSegments = segments },
            Position = position,
            Rotation = rotation ?? Vector3.Zero,
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        parent.AddChild(mesh);
        return mesh;
    }

    static StandardMaterial3D Flat(Color color, float roughness = 0.6f) => new() { AlbedoColor = color, Roughness = roughness };

    // ------------------------------------------------------------------ 건물 (로컬 좌표)

    /// <summary>
    /// 노란 도시가스관: 바닥의 계량기에서 벽을 타고 올라가, 층마다 꺾여 벽 안으로 들어간다.
    /// </summary>
    public static void GasPipe(Node3D building, float width, float height, float frontZ, float floorHeight, int floors, float groundFloor, int seed)
    {
        var x = -width / 2f + 0.38f + Hash(seed, 70) * 0.25f;
        var z = frontZ + 0.07f;
        var bottom = -height / 2f + 0.55f;
        var top = -height / 2f + groundFloor + floorHeight * (floors - 1) + 0.45f;
        Cylinder(building, 0.024f, top - bottom, new Vector3(x, (top + bottom) / 2f, z), GasYellow);
        // 계량기 상자와 밸브
        Box(building, new Vector3(0.26f, 0.32f, 0.16f), new Vector3(x + 0.22f, -height / 2f + 0.62f, frontZ + 0.08f), Flat(new Color(0.62f, 0.63f, 0.6f), 0.5f));
        Cylinder(building, 0.024f, 0.22f, new Vector3(x + 0.11f, bottom, z), GasYellow, new Vector3(0, 0, Mathf.Pi / 2));
        for (var floor = 1; floor < floors; floor++)
        {
            var y = -height / 2f + groundFloor + floorHeight * (floor - 1) + 0.45f;
            var run = 0.35f + Hash(seed, 71 + floor) * 0.5f;
            Cylinder(building, 0.02f, run, new Vector3(x + run / 2f, y, z), GasYellow, new Vector3(0, 0, Mathf.Pi / 2));
            Box(building, new Vector3(0.06f, 0.06f, 0.08f), new Vector3(x + run, y, z - 0.03f), GasYellow);
            Box(building, new Vector3(0.05f, 0.03f, 0.04f), new Vector3(x, y, z + 0.02f), DarkSteel); // 배관 고정쇠
        }
    }

    /// <summary>창 앞의 방범창: 세로 쇠창살과 위아래 띠</summary>
    public static void Grille(Node3D building, float x, float y, float frontZ, float windowWidth, float windowHeight)
    {
        var z = frontZ + 0.1f;
        var bars = Mathf.Max(4, (int)(windowWidth / 0.12f));
        for (var i = 0; i <= bars; i++)
            Box(building, new Vector3(0.014f, windowHeight + 0.06f, 0.014f), new Vector3(x - windowWidth / 2f + windowWidth * i / bars, y, z), DarkSteel);
        foreach (var dy in new[] { -windowHeight / 2f, 0f, windowHeight / 2f })
            Box(building, new Vector3(windowWidth + 0.04f, 0.022f, 0.02f), new Vector3(x, y + dy, z), DarkSteel);
    }

    /// <summary>실외기와 앵글 받침, 벽을 타고 내려가는 보온재 감은 냉매관</summary>
    public static void AcUnit(Node3D building, float x, float y, float frontZ, int seed)
    {
        var z = frontZ + 0.36f;
        foreach (var dx in new[] { -0.3f, 0.3f })
        {
            Box(building, new Vector3(0.04f, 0.04f, 0.62f), new Vector3(x + dx, y - 0.27f, frontZ + 0.31f), Galvanized);
            Box(building, new Vector3(0.04f, 0.42f, 0.04f), new Vector3(x + dx, y - 0.46f, frontZ + 0.04f), Galvanized, new Vector3(-0.85f, 0, 0));
        }
        Box(building, new Vector3(0.78f, 0.52f, 0.28f), new Vector3(x, y, z), AcWhite);
        Cylinder(building, 0.2f, 0.02f, new Vector3(x - 0.12f, y, z + 0.145f), DarkSteel, new Vector3(Mathf.Pi / 2, 0, 0), 16);
        var drop = 0.9f + Hash(seed, 80) * 1.6f;
        Cylinder(building, 0.035f, drop, new Vector3(x + 0.43f, y - drop / 2f + 0.1f, frontZ + 0.08f), Insulation);
    }

    /// <summary>옥상: 난간벽, 녹색 우레탄 방수 바닥, 물탱크, 계단실</summary>
    public static void Rooftop(Node3D building, float width, float depth, float height, int seed, Material wall, Material coping)
    {
        var top = height / 2f + 0.17f;
        const float parapet = 0.85f;
        Box(building, new Vector3(width - 0.3f, 0.02f, depth - 0.3f), new Vector3(0, top + 0.01f, 0), Flat(new Color(0.24f, 0.45f, 0.3f), 0.45f));
        foreach (var sz in new[] { -1f, 1f })
        {
            Box(building, new Vector3(width, parapet, 0.16f), new Vector3(0, top + parapet / 2f, sz * (depth / 2f - 0.08f)), wall);
            Box(building, new Vector3(width + 0.06f, 0.06f, 0.24f), new Vector3(0, top + parapet + 0.03f, sz * (depth / 2f - 0.08f)), coping);
        }
        foreach (var sx in new[] { -1f, 1f })
        {
            Box(building, new Vector3(0.16f, parapet, depth), new Vector3(sx * (width / 2f - 0.08f), top + parapet / 2f, 0), wall);
            Box(building, new Vector3(0.24f, 0.06f, depth + 0.06f), new Vector3(sx * (width / 2f - 0.08f), top + parapet + 0.03f, 0), coping);
        }
        // 계단실 (옥탑): 뒤쪽 모서리
        var stairSide = Hash(seed, 90) < 0.5f ? -1f : 1f;
        var stair = new Vector3(stairSide * (width / 2f - 1.35f), top + 1.1f, -depth / 2f + 1.25f);
        Box(building, new Vector3(2.3f, 2.2f, 2.1f), stair, wall);
        Box(building, new Vector3(2.45f, 0.1f, 2.25f), stair + new Vector3(0, 1.15f, 0), coping);
        Box(building, new Vector3(0.8f, 1.85f, 0.04f), stair + new Vector3(0, -0.17f, 1.06f), Galvanized);
        // 물탱크: 파란 FRP 또는 스테인리스, 받침 위에
        var tank = new Vector3(-stairSide * (width / 2f - 1.1f), top, -depth / 2f + 1.1f);
        var tankMaterial = Hash(seed, 91) < 0.55f ? Flat(new Color(0.16f, 0.36f, 0.62f), 0.5f) : Galvanized;
        Box(building, new Vector3(1.2f, 0.25f, 1.2f), tank + new Vector3(0, 0.12f, 0), DarkSteel);
        Cylinder(building, 0.58f, 1.25f, tank + new Vector3(0, 0.88f, 0), tankMaterial, segments: 18);
        Cylinder(building, 0.2f, 0.08f, tank + new Vector3(0, 1.54f, 0), tankMaterial, segments: 12);
    }

    /// <summary>가게 가로 간판 (상호 글자판)과 옆으로 튀어나온 세로 돌출 간판</summary>
    public static void ShopSigns(Node3D building, float width, float height, float frontZ, int seed, StreetStyle style)
    {
        var name = style.ShopNames[(int)(Hash(seed, 100) * style.ShopNames.Length) % style.ShopNames.Length];
        var color = SignColors[(int)(Hash(seed, 101) * SignColors.Length) % SignColors.Length];
        var dark = color.Luminance < 0.5f;
        var y = -height / 2f + 2.72f;
        Box(building, new Vector3(width * 0.62f, 0.62f, 0.14f), new Vector3(0, y, frontZ + 0.12f), Flat(color, 0.4f));
        var label = Look.Tag(name, name.Length > 6 ? 0.0052f : 0.0072f, Look.Stamp);
        label.Position = new Vector3(0, y, frontZ + 0.2f);
        label.Modulate = dark ? new Color(0.97f, 0.96f, 0.9f) : new Color(0.1f, 0.1f, 0.11f);
        building.AddChild(label);

        if (Hash(seed, 102) < 0.6f)
        {
            var side = Hash(seed, 103) < 0.5f ? -1f : 1f;
            var px = side * (width / 2f - 0.5f);
            var py = -height / 2f + 4.0f;
            var projecting = Flat(SignColors[(int)(Hash(seed, 104) * SignColors.Length) % SignColors.Length], 0.4f);
            Box(building, new Vector3(0.16f, 1.5f, 0.62f), new Vector3(px, py, frontZ + 0.42f), projecting);
            Box(building, new Vector3(0.05f, 0.05f, 0.2f), new Vector3(px, py + 0.6f, frontZ + 0.08f), DarkSteel);
            Box(building, new Vector3(0.05f, 0.05f, 0.2f), new Vector3(px, py - 0.6f, frontZ + 0.08f), DarkSteel);
            // 한·중·일 간판은 글자를 세로로 쌓고, 로마자 간판은 짧은 낱말을 가로로 둔다
            var text = style.VerticalSigns ? string.Join('\n', name.ToCharArray()) : name[..Math.Min(name.Length, 4)];
            foreach (var face in new[] { -1f, 1f })
            {
                var tag = Look.Tag(text, style.VerticalSigns ? 0.0058f : 0.0034f, Look.Stamp);
                tag.Position = new Vector3(px + face * 0.085f, py, frontZ + 0.42f);
                tag.Rotation = new Vector3(0, face * Mathf.Pi / 2, 0);
                tag.Modulate = projecting.AlbedoColor.Luminance < 0.5f ? new Color(0.97f, 0.96f, 0.9f) : new Color(0.1f, 0.1f, 0.11f);
                building.AddChild(tag);
            }
        }
    }

    /// <summary>건물번호판: 파란 바탕에 흰 글씨 (주택용 가로 26 cm 이상)</summary>
    public static void NumberPlate(Node3D building, Vector3 position, int number, string road, Color plate)
    {
        if (plate.A == 0)
        {
            // 판 없이 벽에 붙인 놋쇠 숫자 (미국식)
            var digits = Look.Tag(number.ToString(), 0.0045f, Look.Stamp);
            digits.Position = position + new Vector3(0, 0, 0.01f);
            digits.Modulate = new Color(0.62f, 0.48f, 0.2f);
            building.AddChild(digits);
            return;
        }
        Box(building, new Vector3(0.34f, 0.24f, 0.03f), position, Flat(plate, 0.38f));
        var num = Look.Tag(number.ToString(), 0.0026f, Look.Stamp);
        num.Position = position + new Vector3(0, 0.03f, 0.02f);
        num.Modulate = new Color(0.96f, 0.96f, 0.94f);
        building.AddChild(num);
        var roadTag = Look.Tag(road, 0.0011f, Look.Stamp);
        roadTag.Position = position + new Vector3(0, -0.075f, 0.02f);
        roadTag.Modulate = new Color(0.96f, 0.96f, 0.94f);
        building.AddChild(roadTag);
    }

    // ------------------------------------------------------------------ 길 (월드 좌표)

    static Node3D Place(Node3D root, World world, Vector2 at, Vector2 facing, float lift = 0)
    {
        var node = new Node3D
        {
            Position = new Vector3(at.X, world.HeightAt(at.X, at.Y) + lift, at.Y),
            Rotation = new Vector3(0, Mathf.Atan2(facing.X, facing.Y), 0),
        };
        root.AddChild(node);
        return node;
    }

    /// <summary>
    /// 원호형 과속방지턱: 진행 방향 길이 3.6 m, 높이 10 cm, 흰색·노란색 45° 사선 도색.
    /// 노드의 +Z 가 길 방향이다.
    /// </summary>
    public static void SpeedHump(Node3D root, World world, Vector2 at, Vector2 tangent, float roadWidth)
    {
        var node = Place(root, world, at, tangent, 0.18f);
        var image = Image.CreateEmpty(64, 64, false, Image.Format.Rgb8);
        for (var y = 0; y < 64; y++)
        for (var x = 0; x < 64; x++)
            image.SetPixel(x, y, ((x + y) / 32) % 2 == 0 ? new Color(0.88f, 0.72f, 0.16f) : new Color(0.9f, 0.89f, 0.84f));
        image.GenerateMipmaps();
        var paint = new StandardMaterial3D
        {
            AlbedoTexture = ImageTexture.CreateFromImage(image),
            Roughness = 0.7f,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
        };
        const int along = 18;
        const float length = 3.6f, rise = 0.1f;
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        for (var i = 0; i <= along; i++)
        {
            var t = (float)i / along;
            var z = (t - 0.5f) * length;
            var y = rise * (1f - (2f * t - 1f) * (2f * t - 1f));
            var slope = -4f * rise * (2f * t - 1f) / length;
            var normal = new Vector3(0, 1, -slope).Normalized();
            foreach (var x in new[] { -roadWidth / 2f, roadWidth / 2f })
            {
                st.SetNormal(normal);
                st.SetUV(new Vector2((x + z) / 0.9f, (z - x) / 0.9f) * 0.5f);
                st.AddVertex(new Vector3(x, y, z));
            }
        }
        for (var i = 0; i < along; i++)
        {
            int a = i * 2, b = a + 1, c = a + 2, d = a + 3;
            st.AddIndex(a); st.AddIndex(b); st.AddIndex(c);
            st.AddIndex(b); st.AddIndex(d); st.AddIndex(c);
        }
        paint.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        node.AddChild(new MeshInstance3D { Mesh = st.Commit(), MaterialOverride = paint, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        var hump = new StaticBody3D { CollisionLayer = PartBody.LayerWorld, CollisionMask = 0, Position = new Vector3(0, -0.03f, 0) };
        hump.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(roadWidth, 0.1f, length * 0.6f) } });
        node.AddChild(hump);
    }

    /// <summary>도로반사경: 주황 지주 위 지름 80 cm 볼록거울, 굽은 길의 안쪽을 비춘다</summary>
    public static void ConvexMirror(Node3D root, World world, Vector2 at, Vector2 facing)
    {
        var node = Place(root, world, at, facing);
        Cylinder(node, 0.038f, 2.6f, new Vector3(0, 1.3f, 0), SafetyOrange);
        Box(node, new Vector3(0.05f, 0.05f, 0.24f), new Vector3(0, 2.45f, 0.1f), SafetyOrange);
        var housing = new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0.43f, BottomRadius = 0.43f, Height = 0.06f, RadialSegments = 28 },
            Position = new Vector3(0, 2.45f, 0.26f),
            Rotation = new Vector3(Mathf.Pi / 2 - 0.12f, 0, 0),
            MaterialOverride = SafetyOrange,
        };
        node.AddChild(housing);
        node.AddChild(new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 0.4f, Height = 0.16f, RadialSegments = 28, Rings = 8 },
            Position = new Vector3(0, 2.45f, 0.29f),
            Rotation = new Vector3(Mathf.Pi / 2 - 0.12f, 0, 0),
            Scale = new Vector3(1, 1, 1),
            MaterialOverride = Mirror,
        });
        // 흰 반사띠
        Box(node, new Vector3(0.09f, 0.3f, 0.09f), new Vector3(0, 1.0f, 0), ReflectWhite);
    }

    /// <summary>도로명판: 파란 바탕 흰 글씨, 화살표 방향으로 길이 이어진다</summary>
    public static void RoadNamePlate(Node3D root, World world, Vector2 at, Vector2 facing, string road, string range)
    {
        var node = Place(root, world, at, facing);
        Cylinder(node, 0.035f, 2.75f, new Vector3(0, 1.375f, 0), Galvanized);
        Box(node, new Vector3(0.82f, 0.24f, 0.03f), new Vector3(0.3f, 2.55f, 0.04f), PlateBlue);
        var name = Look.Tag(road, 0.0024f, Look.Stamp);
        name.Position = new Vector3(0.3f, 2.57f, 0.06f);
        name.Modulate = new Color(0.96f, 0.96f, 0.94f);
        node.AddChild(name);
        var sub = Look.Tag(range, 0.0011f, Look.Stamp);
        sub.Position = new Vector3(0.3f, 2.475f, 0.06f);
        sub.Modulate = new Color(0.96f, 0.96f, 0.94f);
        node.AddChild(sub);
    }

    /// <summary>수거일을 기다리는 종량제 봉투 더미 (흰색·노란색 반투명 비닐)</summary>
    public static void TrashBags(Node3D root, World world, Vector2 at, int seed)
    {
        var node = Place(root, world, at, Vector2.Down, 0.18f);
        var count = 3 + (int)(Hash(seed, 110) * 4.99f);
        for (var i = 0; i < count; i++)
        {
            var yellow = Hash(seed, 111 + i) < 0.35f;
            var bag = new StandardMaterial3D
            {
                AlbedoColor = yellow ? new Color(0.86f, 0.76f, 0.36f) : new Color(0.8f, 0.8f, 0.77f),
                Roughness = 0.5f,
                NormalEnabled = true,
                NormalTexture = Look.Tex("painted_concrete", "nor_gl"),
                NormalScale = 1.6f,
                Uv1Triplanar = true,
                Uv1Scale = Vector3.One * 3f,
            };
            var r = 0.15f + Hash(seed, 120 + i) * 0.1f;
            var angle = Hash(seed, 130 + i) * Mathf.Tau;
            var spread = Hash(seed, 140 + i) * 0.45f;
            var stack = i >= 4 ? r * 1.1f : 0f;
            node.AddChild(new MeshInstance3D
            {
                Mesh = new SphereMesh { Radius = r, Height = r * 2f, RadialSegments = 10, Rings = 6 },
                Position = new Vector3(Mathf.Cos(angle) * spread, r * 0.8f + stack, Mathf.Sin(angle) * spread),
                Scale = new Vector3(1.15f, 0.7f + Hash(seed, 150 + i) * 0.5f, 0.85f),
                Rotation = new Vector3((Hash(seed, 165 + i) - 0.5f) * 0.7f, angle, (Hash(seed, 160 + i) - 0.5f) * 0.9f),
                MaterialOverride = bag,
            });
            // 묶은 손잡이
            node.AddChild(new MeshInstance3D
            {
                Mesh = new SphereMesh { Radius = 0.05f, Height = 0.1f, RadialSegments = 6, Rings = 4 },
                Position = new Vector3(Mathf.Cos(angle) * spread, r * 1.55f + stack, Mathf.Sin(angle) * spread),
                MaterialOverride = bag,
            });
        }
    }

    /// <summary>배달 오토바이: 두 바퀴, 차체, 뒤의 배달통</summary>
    public static void Scooter(Node3D root, World world, Vector2 at, Vector2 facing, int seed)
    {
        var node = Place(root, world, at, facing, 0.18f);
        var body = Flat(Hash(seed, 170) < 0.5f ? new Color(0.72f, 0.1f, 0.08f) : new Color(0.85f, 0.85f, 0.82f), 0.35f);
        foreach (var z in new[] { -0.62f, 0.62f })
        {
            Cylinder(node, 0.27f, 0.11f, new Vector3(0, 0.27f, z), Rubber, new Vector3(0, 0, Mathf.Pi / 2), 16);
            Cylinder(node, 0.12f, 0.12f, new Vector3(0, 0.27f, z), Galvanized, new Vector3(0, 0, Mathf.Pi / 2), 12);
        }
        Box(node, new Vector3(0.36f, 0.32f, 0.95f), new Vector3(0, 0.5f, -0.05f), body);
        Box(node, new Vector3(0.3f, 0.1f, 0.62f), new Vector3(0, 0.72f, -0.2f), Rubber);
        Box(node, new Vector3(0.34f, 0.7f, 0.22f), new Vector3(0, 0.62f, 0.5f), body, new Vector3(-0.3f, 0, 0));
        Box(node, new Vector3(0.62f, 0.04f, 0.04f), new Vector3(0, 1.02f, 0.58f), DarkSteel);
        Box(node, new Vector3(0.48f, 0.42f, 0.42f), new Vector3(0, 0.98f, -0.6f), Flat(new Color(0.88f, 0.86f, 0.8f), 0.5f));
        Box(node, new Vector3(0.12f, 0.06f, 0.03f), new Vector3(0, 0.75f, 0.68f), Flat(new Color(0.9f, 0.88f, 0.75f), 0.3f));
    }

    /// <summary>담벼락 밑의 스티로폼 상자 텃밭: 흰 상자에 상추·고추·파</summary>
    public static void StyrofoamGarden(Node3D root, World world, Vector2 at, Vector2 tangent, int seed)
    {
        var node = Place(root, world, at, tangent, 0.18f);
        var leaves = Look.Scanned("leafy_grass", 0.4f, new Color(0.6f, 0.78f, 0.45f));
        var count = 2 + (int)(Hash(seed, 180) * 2.99f);
        for (var i = 0; i < count; i++)
        {
            var z = (i - (count - 1) / 2f) * 0.62f;
            var h = 0.26f + Hash(seed, 181 + i) * 0.08f;
            Box(node, new Vector3(0.42f, h, 0.56f), new Vector3(0, h / 2f, z), Styrofoam);
            for (var plant = 0; plant < 3; plant++)
                node.AddChild(new MeshInstance3D
                {
                    Mesh = new SphereMesh { Radius = 0.1f, Height = 0.2f, RadialSegments = 8, Rings = 5 },
                    Position = new Vector3((Hash(seed, 190 + i * 3 + plant) - 0.5f) * 0.24f, h + 0.06f, z + (plant - 1) * 0.15f),
                    Scale = new Vector3(1f, 0.7f + Hash(seed, 200 + i * 3 + plant) * 1.1f, 1f),
                    MaterialOverride = leaves,
                });
        }
    }

    /// <summary>집 앞 주차를 막는 주황·흰 줄무늬 플라스틱 드럼</summary>
    public static void ParkingDrum(Node3D root, World world, Vector2 at)
    {
        var node = Place(root, world, at, Vector2.Down, 0.18f);
        for (var band = 0; band < 4; band++)
        {
            var radius = 0.29f - band * 0.012f;
            node.AddChild(new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = radius - 0.012f, BottomRadius = radius, Height = 0.21f, RadialSegments = 18 },
                Position = new Vector3(0, 0.105f + band * 0.21f, 0),
                MaterialOverride = band % 2 == 0 ? SafetyOrange : ReflectWhite,
            });
        }
        Cylinder(node, 0.2f, 0.06f, new Vector3(0, 0.87f, 0), SafetyOrange, segments: 14);
    }

    // ------------------------------------------------------------------ 나라별

    /// <summary>위층 창 양옆의 나무 덧창 (프랑스)</summary>
    public static void Shutters(Node3D building, float x, float y, float frontZ, float windowWidth, float windowHeight, int seed)
    {
        Color[] paints = [new(0.36f, 0.48f, 0.56f), new(0.3f, 0.45f, 0.32f), new(0.9f, 0.89f, 0.84f), new(0.55f, 0.27f, 0.2f)];
        var paint = Flat(paints[(int)(Hash(seed, 240) * paints.Length) % paints.Length], 0.7f);
        foreach (var side in new[] { -1f, 1f })
        {
            var cx = x + side * (windowWidth * 0.75f + 0.02f);
            Box(building, new Vector3(windowWidth / 2f, windowHeight + 0.1f, 0.04f), new Vector3(cx, y, frontZ + 0.03f), paint);
            for (var slat = 0; slat < 8; slat++)
                Box(building, new Vector3(windowWidth / 2f - 0.06f, 0.02f, 0.02f), new Vector3(cx, y - windowHeight / 2f + 0.12f + slat * windowHeight / 8.6f, frontZ + 0.06f), paint);
        }
    }

    /// <summary>박공지붕: 처마가 길을 향하거나(평입) 박공면이 길을 향한다(처입)</summary>
    public static void PitchedRoof(Node3D building, float width, float depth, float height, int seed, Color roofColor)
    {
        var roof = Look.Scanned("grey_tiles", 0.7f, roofColor);
        var gableToStreet = Hash(seed, 250) < 0.35f;
        var rise = 1.3f + Hash(seed, 251) * 0.9f;
        var mesh = new PrismMesh
        {
            LeftToRight = 0.5f,
            Size = gableToStreet ? new Vector3(width + 0.5f, rise, depth + 0.5f) : new Vector3(depth + 0.5f, rise, width + 0.5f),
        };
        building.AddChild(new MeshInstance3D
        {
            Mesh = mesh,
            Position = new Vector3(0, height / 2f + 0.17f + rise / 2f, 0),
            Rotation = gableToStreet ? Vector3.Zero : new Vector3(0, Mathf.Pi / 2, 0),
            MaterialOverride = roof,
        });
    }

    /// <summary>음료 자판기 (일본): 흰 몸체, 밝은 진열창 세 줄, 아래 꺼내는 곳</summary>
    public static void VendingMachine(Node3D root, World world, Vector2 at, Vector2 facing, int seed)
    {
        var node = Place(root, world, at, facing, 0.18f);
        var body = Flat(Hash(seed, 260) < 0.5f ? new Color(0.88f, 0.88f, 0.86f) : new Color(0.7f, 0.12f, 0.1f), 0.4f);
        Box(node, new Vector3(1.0f, 1.83f, 0.72f), new Vector3(0, 0.915f, 0), body);
        var lit = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.92f, 0.95f, 0.98f), EmissionEnabled = true, Emission = new Color(0.85f, 0.92f, 1f), EmissionEnergyMultiplier = 0.8f,
        };
        Box(node, new Vector3(0.86f, 0.9f, 0.02f), new Vector3(0, 1.32f, 0.36f), lit);
        Color[] cans = [new(0.8f, 0.15f, 0.1f), new(0.15f, 0.35f, 0.7f), new(0.95f, 0.7f, 0.15f), new(0.2f, 0.55f, 0.3f), new(0.9f, 0.9f, 0.88f)];
        for (var row = 0; row < 3; row++)
        for (var col = 0; col < 7; col++)
            Cylinder(node, 0.033f, 0.13f, new Vector3(-0.36f + col * 0.12f, 1.02f + row * 0.29f, 0.38f), Flat(cans[(int)(Hash(seed * 31 + row * 7 + col, 261) * cans.Length) % cans.Length], 0.3f), segments: 8);
        Box(node, new Vector3(0.62f, 0.18f, 0.04f), new Vector3(0, 0.35f, 0.36f), DarkSteel);
        Box(node, new Vector3(0.14f, 0.3f, 0.03f), new Vector3(0.36f, 0.72f, 0.37f), Galvanized);
    }

    /// <summary>소화전 (미국): 빨간 몸통, 양옆 노즐, 둥근 머리</summary>
    public static void Hydrant(Node3D root, World world, Vector2 at)
    {
        var node = Place(root, world, at, Vector2.Down, 0.18f);
        var red = Flat(new Color(0.72f, 0.1f, 0.07f), 0.45f);
        Cylinder(node, 0.14f, 0.06f, new Vector3(0, 0.03f, 0), red, segments: 12);
        Cylinder(node, 0.11f, 0.55f, new Vector3(0, 0.33f, 0), red, segments: 12);
        node.AddChild(new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.12f, Height = 0.16f, RadialSegments = 12, Rings = 6 }, Position = new Vector3(0, 0.62f, 0), MaterialOverride = red });
        Cylinder(node, 0.04f, 0.32f, new Vector3(0, 0.42f, 0), red, new Vector3(0, 0, Mathf.Pi / 2), 8);
        Cylinder(node, 0.055f, 0.09f, new Vector3(0, 0.38f, 0.12f), red, new Vector3(Mathf.Pi / 2, 0, 0), 8);
        Cylinder(node, 0.025f, 0.05f, new Vector3(0, 0.72f, 0), Galvanized, segments: 5);
    }

    /// <summary>길가 우편함 (미국): 기둥 위 둥근 지붕의 금속 상자</summary>
    public static void Mailbox(Node3D root, World world, Vector2 at, Vector2 facing)
    {
        var node = Place(root, world, at, facing, 0.18f);
        Box(node, new Vector3(0.09f, 1.05f, 0.09f), new Vector3(0, 0.52f, 0), Flat(new Color(0.4f, 0.32f, 0.24f), 0.85f));
        Box(node, new Vector3(0.2f, 0.16f, 0.48f), new Vector3(0, 1.1f, 0), DarkSteel);
        Cylinder(node, 0.1f, 0.48f, new Vector3(0, 1.18f, 0), DarkSteel, new Vector3(Mathf.Pi / 2, 0, 0), 12);
        Box(node, new Vector3(0.02f, 0.14f, 0.04f), new Vector3(0.11f, 1.2f, -0.1f), Flat(new Color(0.75f, 0.1f, 0.08f), 0.5f));
    }

    /// <summary>
    /// 길가 볼라드. 한국: 도심의 노란 탄성 시선유도봉. 프랑스: 가늘고 긴 흰 기둥, 검은 모자, 다가오는 쪽에 빨간 반사판.
    /// </summary>
    public static void Bollard(Node3D root, World world, Vector2 at, Vector2 facing, BollardKind kind)
    {
        var node = Place(root, world, at, facing, 0.18f);
        if (kind == BollardKind.KoreaYellowFlex)
        {
            var yellow = Flat(new Color(0.93f, 0.74f, 0.1f), 0.45f);
            Cylinder(node, 0.12f, 0.04f, new Vector3(0, 0.02f, 0), Flat(new Color(0.1f, 0.1f, 0.1f), 0.8f), segments: 12);
            Cylinder(node, 0.04f, 0.75f, new Vector3(0, 0.41f, 0), yellow, segments: 10);
            foreach (var y in new[] { 0.58f, 0.68f })
                Cylinder(node, 0.042f, 0.05f, new Vector3(0, y, 0), ReflectWhite, segments: 10);
            return;
        }
        var white = Flat(new Color(0.9f, 0.9f, 0.88f), 0.5f);
        Cylinder(node, 0.055f, 1.0f, new Vector3(0, 0.5f, 0), white, segments: 10);
        Cylinder(node, 0.058f, 0.12f, new Vector3(0, 0.98f, 0), Flat(new Color(0.06f, 0.06f, 0.06f), 0.6f), segments: 10);
        Box(node, new Vector3(0.05f, 0.14f, 0.012f), new Vector3(0, 0.82f, 0.056f), Flat(new Color(0.75f, 0.06f, 0.05f), 0.25f));
    }

    /// <summary>카페 테라스 (프랑스): 보도 위 둥근 대리석 탁자 둘과 등나무 의자</summary>
    public static void CafeTerrace(Node3D root, World world, Vector2 at, Vector2 tangent, int seed)
    {
        var node = Place(root, world, at, tangent, 0.18f);
        var marble = Flat(new Color(0.88f, 0.87f, 0.84f), 0.25f);
        var rattan = Flat(new Color(0.62f, 0.45f, 0.26f), 0.75f);
        for (var table = 0; table < 2; table++)
        {
            var z = (table - 0.5f) * 1.5f + (Hash(seed, 270 + table) - 0.5f) * 0.3f;
            Cylinder(node, 0.3f, 0.03f, new Vector3(0, 0.74f, z), marble, segments: 16);
            Cylinder(node, 0.025f, 0.72f, new Vector3(0, 0.37f, z), DarkSteel, segments: 6);
            Cylinder(node, 0.2f, 0.02f, new Vector3(0, 0.01f, z), DarkSteel, segments: 12);
            foreach (var dz in new[] { -0.5f, 0.5f })
            {
                var chair = new Node3D { Position = new Vector3((Hash(seed, 280 + table) - 0.5f) * 0.2f, 0, z + dz), Rotation = new Vector3(0, dz < 0 ? 0 : Mathf.Pi, 0) };
                node.AddChild(chair);
                Box(chair, new Vector3(0.4f, 0.04f, 0.4f), new Vector3(0, 0.45f, 0), rattan);
                Box(chair, new Vector3(0.4f, 0.42f, 0.04f), new Vector3(0, 0.68f, -0.2f), rattan);
                foreach (var lx in new[] { -0.17f, 0.17f })
                foreach (var lz in new[] { -0.17f, 0.17f })
                    Box(chair, new Vector3(0.025f, 0.45f, 0.025f), new Vector3(lx, 0.225f, lz), DarkSteel);
            }
        }
    }

    /// <summary>약국의 초록 십자 (프랑스): 벽에서 튀어나온 발광 십자</summary>
    public static void PharmacyCross(Node3D building, Vector3 at)
    {
        var glow = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.1f, 0.7f, 0.25f), EmissionEnabled = true, Emission = new Color(0.1f, 0.85f, 0.3f), EmissionEnergyMultiplier = 1.4f,
        };
        Box(building, new Vector3(0.05f, 0.05f, 0.3f), at + new Vector3(0, 0, -0.18f), DarkSteel);
        Box(building, new Vector3(0.12f, 0.62f, 0.2f), at, glow);
        Box(building, new Vector3(0.12f, 0.2f, 0.62f), at, glow);
    }

    /// <summary>
    /// 길 이름 표지. 미국: 기둥 위 초록 날개판 둘(십자). 프랑스: 건물 모서리 벽의 파란 법랑판, 초록 테두리. 일본: 전주의 파란 번지판.
    /// </summary>
    public static void RoadSign(Node3D root, World world, Vector2 at, Vector2 facing, RoadSignKind kind, string road)
    {
        if (kind == RoadSignKind.KoreaPlate)
        {
            RoadNamePlate(root, world, at, facing, $"{road} →", "1 → 41");
            return;
        }
        var node = Place(root, world, at, facing);
        var white = new Color(0.96f, 0.96f, 0.94f);
        switch (kind)
        {
            case RoadSignKind.UsGreenBlade:
                Cylinder(node, 0.035f, 3.1f, new Vector3(0, 1.55f, 0), Galvanized);
                foreach (var (yaw, y) in new[] { (0f, 2.9f), (Mathf.Pi / 2, 3.08f) })
                {
                    var blade = new Node3D { Position = new Vector3(0, y, 0), Rotation = new Vector3(0, yaw, 0) };
                    node.AddChild(blade);
                    Box(blade, new Vector3(0.9f, 0.16f, 0.02f), Vector3.Zero, Flat(new Color(0.05f, 0.4f, 0.22f), 0.35f));
                    foreach (var face in new[] { -1f, 1f })
                    {
                        var tag = Look.Tag(road, 0.0019f, Look.Stamp);
                        tag.Position = new Vector3(0, 0, face * 0.012f);
                        tag.Rotation = new Vector3(0, face < 0 ? Mathf.Pi : 0, 0);
                        tag.Modulate = white;
                        blade.AddChild(tag);
                    }
                }
                break;
            case RoadSignKind.FranceWallPlate:
                Box(node, new Vector3(0.62f, 0.26f, 0.025f), new Vector3(0, 2.7f, 0), Flat(new Color(0.1f, 0.4f, 0.25f), 0.3f));
                Box(node, new Vector3(0.58f, 0.22f, 0.03f), new Vector3(0, 2.7f, 0), PlateBlue);
                var plate = Look.Tag(road.ToUpperInvariant(), 0.0018f, Look.Stamp);
                plate.Position = new Vector3(0, 2.7f, 0.02f);
                plate.Modulate = white;
                node.AddChild(plate);
                break;
            case RoadSignKind.JapanPlate:
                Box(node, new Vector3(0.26f, 0.4f, 0.02f), new Vector3(0, 2.3f, 0), PlateBlue);
                var jp = Look.Tag($"{road}\n1丁目", 0.0016f, Look.Stamp);
                jp.Position = new Vector3(0, 2.3f, 0.015f);
                jp.Modulate = white;
                node.AddChild(jp);
                break;
        }
    }

    /// <summary>
    /// 전봇대 아래쪽 경고 덮개. 한국: 땅에서 떨어져 끝나는 노랑·검정 사선 띠. 일본: 바닥부터 세로 노랑·검정 줄무늬.
    /// </summary>
    public static void PoleStripes(Node3D pole, PoleKind kind)
    {
        var image = Image.CreateEmpty(64, 64, false, Image.Format.Rgb8);
        for (var y = 0; y < 64; y++)
        for (var x = 0; x < 64; x++)
        {
            var band = kind == PoleKind.KoreaConcrete ? (x + y) / 16 : x / 8;
            image.SetPixel(x, y, band % 2 == 0 ? new Color(0.92f, 0.74f, 0.1f) : new Color(0.06f, 0.06f, 0.06f));
        }
        image.GenerateMipmaps();
        var stripes = new StandardMaterial3D
        {
            AlbedoTexture = ImageTexture.CreateFromImage(image),
            Roughness = 0.4f,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
            Uv1Scale = new Vector3(kind == PoleKind.KoreaConcrete ? 2f : 4f, kind == PoleKind.KoreaConcrete ? 2f : 1f, 1f),
        };
        var (bottom, top) = kind == PoleKind.KoreaConcrete ? (0.9f, 2.3f) : (0f, 1.9f);
        pole.AddChild(new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0.2f, BottomRadius = 0.205f, Height = top - bottom, RadialSegments = 16 },
            Position = new Vector3(0, (top + bottom) / 2f, 0),
            MaterialOverride = stripes,
        });
    }

    // ------------------------------------------------------------------ 차

    /// <summary>
    /// 옆모습 단면(z = 앞뒤, y = 높이)을 폭 방향(x)으로 밀어 낸 닫힌 메시. 차체처럼 둥근 실루엣을 만든다.
    /// </summary>
    public static MeshInstance3D Extrude(Vector2[] profile, float width, Material material, float x0 = 0)
    {
        var poly = profile;
        var area = 0f;
        for (var i = 0; i < poly.Length; i++) area += poly[i].X * poly[(i + 1) % poly.Length].Y - poly[(i + 1) % poly.Length].X * poly[i].Y;
        if (area < 0) poly = poly.Reverse().ToArray(); // 반시계 방향으로 맞춘다

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        Vector3 P(Vector2 p, float x) => new(x, p.Y, p.X);
        void Tri(Vector3 a, Vector3 b, Vector3 c, Vector3 n)
        {
            // Godot 의 앞면: (c - a) × (b - a) 가 바깥 법선
            if ((c - a).Cross(b - a).Dot(n) < 0) (b, c) = (c, b);
            st.SetNormal(n); st.AddVertex(a);
            st.SetNormal(n); st.AddVertex(b);
            st.SetNormal(n); st.AddVertex(c);
        }
        var half = width / 2f;
        var tris = Geometry2D.TriangulatePolygon(poly);
        for (var i = 0; i < tris.Length; i += 3)
        {
            Tri(P(poly[tris[i]], x0 + half), P(poly[tris[i + 1]], x0 + half), P(poly[tris[i + 2]], x0 + half), Vector3.Right);
            Tri(P(poly[tris[i]], x0 - half), P(poly[tris[i + 1]], x0 - half), P(poly[tris[i + 2]], x0 - half), Vector3.Left);
        }
        for (var i = 0; i < poly.Length; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Length];
            var d = b - a;
            var n = new Vector3(0, -d.X, d.Y).Normalized(); // 반시계 단면의 바깥쪽 (y = -dz, z = dy)
            Tri(P(a, x0 - half), P(b, x0 - half), P(b, x0 + half), n);
            Tri(P(a, x0 - half), P(b, x0 + half), P(a, x0 + half), n);
        }
        return new MeshInstance3D { Mesh = st.Commit(), MaterialOverride = material };
    }

    /// <summary>경사진 유리면: 단면 위 두 점(아래 a, 위 b)을 따라 바깥쪽으로 조금 띄운 얇은 판</summary>
    public static MeshInstance3D SlopePanel(Vector2 a, Vector2 b, float width, Material material, float inset = 0.06f)
    {
        var d = (b - a).Normalized();
        var outward = new Vector2(d.Y, -d.X);
        if (outward.Dot((a + b) / 2f) < 0) outward = -outward;
        a += d * inset; b -= d * inset;
        var thin = outward * 0.012f;
        return Extrude([a + thin * 2f, b + thin * 2f, b + thin, a + thin], width, material);
    }
}
