using Godot;

namespace DollyForge.Game;

/// <summary>기존 작업장 동선을 따라 만든 한국형 저층 상가 골목.</summary>
internal static class StreetEnvironment
{
    /// <summary>길 중심에서 건물 전면까지 (보도 바깥 4.9 m 뒤에 화분·전봇대가 설 0.7 m)</summary>
    const float FrontageOffset = 5.6f;

    static readonly StandardMaterial3D TactileTileMaterial = new()
    {
        AlbedoColor = new Color(0.68f, 0.57f, 0.34f),
        Roughness = 0.92f,
    };
    static readonly StandardMaterial3D TactileBarMaterial = new()
    {
        AlbedoColor = new Color(0.78f, 0.66f, 0.4f),
        Roughness = 0.9f,
    };

    /// <summary>
    /// 길가에 놓인 것의 목록 (종류, 길을 따라 간 거리 m, 쪽 -1/0/+1).
    /// 거리뷰 기준 "10 m 마다 여러 가지가 보인다"를 자체 시험에서 확인한다.
    /// </summary>
    public static readonly List<(string Kind, float Along, int Side)> Census = [];

    static void Count(string kind, float along, float side) => Census.Add((kind, along, Math.Sign(side)));

    /// <summary>정수 시드에서 0..1 값 (같은 골목은 언제나 같은 모양)</summary>
    internal static float Hash(int seed, int salt)
    {
        var h = (uint)(seed * 73856093) ^ (uint)(salt * 19349663) ^ 0x9E3779B9u;
        h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12; h *= 0x297A2D39u; h ^= h >> 15;
        return h / (float)uint.MaxValue;
    }

    /// <summary>지금 골목의 나라 양식. 시작은 한국, DOLLY_STREET_STYLE=jp|fr|us 또는 F4 로 바꾼다.</summary>
    public static StreetStyle Style { get; private set; } = StreetStyle.ById(OS.GetEnvironment("DOLLY_STREET_STYLE"));

    static Node3D? _parent;
    static World? _world;
    static IReadOnlyList<Vector2>? _path;
    static Material? _sidewalk;

    /// <summary>같은 길 위에 다른 나라 양식으로 골목을 다시 짓는다</summary>
    public static void Rebuild(StreetStyle style)
    {
        Style = style;
        if (_parent is null || _world is null || _path is null || _sidewalk is null) return;
        if (_parent.GetNodeOrNull("StreetEnvironment") is { } old)
        {
            _parent.RemoveChild(old);
            old.QueueFree();
        }
        Build(_parent, _world, _path, _sidewalk);
    }

    /// <summary>다음 나라 양식으로 (F4)</summary>
    public static string NextStyle()
    {
        var next = StreetStyle.All[(Array.IndexOf(StreetStyle.All, Style) + 1) % StreetStyle.All.Length];
        Rebuild(next);
        return $"거리 양식: {next.Name} ({next.RoadName}) — F4 로 다음 나라";
    }

    public static void Build(Node3D parent, World world, IReadOnlyList<Vector2> path, Material sidewalk)
    {
        (_parent, _world, _path, _sidewalk) = (parent, world, path, sidewalk);
        Census.Clear();
        var root = new Node3D { Name = "StreetEnvironment" };
        parent.AddChild(root);

        var asphalt = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/asphalt.gdshader") };
        if (Look.Tex("worn_asphalt", "diff") is { } asphaltScan)
        {
            asphalt.SetShaderParameter("asphalt_albedo", asphaltScan);
            asphalt.SetShaderParameter("asphalt_normal", Look.Tex("worn_asphalt", "nor_gl"));
            asphalt.SetShaderParameter("asphalt_roughness", Look.Tex("worn_asphalt", "rough"));
            asphalt.SetShaderParameter("has_texture", true);
        }
        var concrete = Look.Painted("painted_concrete", 2f, new Color(0.55f, 0.54f, 0.51f));
        BuildSurface(root, world, path, -3.2f, 3.2f, 0.18f, asphalt);
        BuildSurface(root, world, path, -4.9f, -3.2f, 0.18f, sidewalk);
        BuildSurface(root, world, path, 3.2f, 4.9f, 0.18f, sidewalk);
        BuildSurface(root, world, path, -3.38f, -3.18f, 0.25f, concrete);
        BuildSurface(root, world, path, 3.18f, 3.38f, 0.25f, concrete);
        AddSidewalkJoints(root, world, path);
        AddRoadMarkings(root, world, path);
        AddRoadFurniture(root, world, path);
        BuildFrontage(root, world, path);
        BuildStreetLights(root, world, path);
        BuildUtilityLines(root, world, path);
        AddAlleySigns(root, world, path);
    }

    /// <summary>과속방지턱 하나, 굽은 곳 바깥의 도로반사경, 길 시작·끝의 도로명판</summary>
    static void AddAlleySigns(Node3D root, World world, IReadOnlyList<Vector2> path)
    {
        var length = PathLength(path);
        if (Style.SpeedHump)
        {
            var humpAt = length * 0.46f;
            var (hump, humpTangent) = AtDistance(path, humpAt);
            StreetDetail.SpeedHump(root, world, hump, humpTangent, 6.4f, RoadY(world, hump, new Vector2(-humpTangent.Y, humpTangent.X), 0) + 0.18f);
            Count("과속방지턱", humpAt, 0);
        }
        if (Style.Bollard != BollardKind.None)
        {
            // 한국: 굽은 곳·입구에 노란 시선유도봉 / 프랑스: 길 들머리와 끝의 흰 볼라드
            var spots = Style.Bollard == BollardKind.KoreaYellowFlex ? new[] { 1.5f, 3.0f, length - 3.5f, length - 2f } : new[] { 1f, 6f, length - 6f, length - 1.5f };
            foreach (var at in spots)
            foreach (var side in new[] { -1f, 1f })
            {
                var (p, t) = AtDistance(path, at);
                StreetDetail.Bollard(root, world, p + new Vector2(-t.Y, t.X) * side * 3.45f, -t * side, Style.Bollard);
                Count("볼라드", at, side);
            }
        }

        // 방향이 가장 많이 꺾이는 곳 두 군데 (10 m 이상 떨어져서)
        var bends = new List<(float At, float Turn)>();
        for (var d = 6f; d < length - 6f; d += 0.5f)
        {
            var (_, t1) = AtDistance(path, d - 4f);
            var (_, t2) = AtDistance(path, d + 4f);
            bends.Add((d, t1.X * t2.Y - t1.Y * t2.X));
        }
        var chosen = new List<float>();
        foreach (var (at, turn) in bends.OrderByDescending(b => Mathf.Abs(b.Turn)))
        {
            if (!Style.Mirror) break;
            if (chosen.Count == 2 || Mathf.Abs(turn) < 0.05f) break;
            if (chosen.Any(c => Mathf.Abs(c - at) < 10f)) continue;
            chosen.Add(at);
            var (p, t) = AtDistance(path, at);
            var normal = new Vector2(-t.Y, t.X);
            var outer = -normal * Mathf.Sign(turn);
            StreetDetail.ConvexMirror(root, world, p + outer * 4.6f, -outer);
            Count("도로반사경", at, Mathf.Sign(-turn));
        }

        // 프랑스 법랑판은 건물 벽에 붙으므로 벽이 있는 곳까지 들어가서 단다
        var wallPlate = Style.RoadSign == RoadSignKind.FranceWallPlate;
        var startAt = wallPlate ? 6.5f : 2.5f;
        var endAt = wallPlate ? length - 8f : length - 4f;
        var (start, startTangent) = AtDistance(path, startAt);
        var startNormal = new Vector2(-startTangent.Y, startTangent.X);
        if (Style.RoadSign == RoadSignKind.FranceWallPlate)
            StreetDetail.RoadSign(root, world, start - startNormal * (FrontageOffset - 0.02f), startNormal, Style.RoadSign, Style.RoadName);
        else
            StreetDetail.RoadSign(root, world, start - startNormal * 4.6f, startNormal, Style.RoadSign, Style.RoadName);
        Count("도로명판", startAt, -1);
        var (end, endTangent) = AtDistance(path, endAt);
        var endNormal = new Vector2(-endTangent.Y, endTangent.X);
        if (Style.RoadSign == RoadSignKind.KoreaPlate)
            StreetDetail.RoadNamePlate(root, world, end + endNormal * 4.6f, -endNormal, $"{Style.RoadName} →", "41 → 1");
        else
            StreetDetail.RoadSign(root, world, end + endNormal * (Style.RoadSign == RoadSignKind.FranceWallPlate ? FrontageOffset - 0.02f : 4.6f), -endNormal, Style.RoadSign, Style.RoadName);
        Count("도로명판", endAt, 1);
    }

    static void BuildSurface(Node3D root, World world, IReadOnlyList<Vector2> path, float inner, float outer, float lift, Material material)
    {
        if (path.Count < 2) return;
        var surfaceMaterial = material.Duplicate() as Material ?? material;
        if (surfaceMaterial is BaseMaterial3D baseMaterial)
            baseMaterial.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        float distance = 0;
        for (var i = 0; i < path.Count; i++)
        {
            var tangent = Tangent(path, i);
            var side = new Vector2(-tangent.Y, tangent.X);
            if (i > 0) distance += path[i].DistanceTo(path[i - 1]);
            foreach (var offset in new[] { inner, outer })
            {
                var p = path[i] + side * offset;
                st.SetNormal(Vector3.Up);
                st.SetUV(new Vector2(offset, distance));
                st.AddVertex(new Vector3(p.X, RoadY(world, path[i], side, offset) + lift, p.Y));
            }
        }
        for (var i = 0; i < path.Count - 1; i++)
        {
            var a = i * 2;
            var b = a + 1;
            var c = a + 2;
            var d = a + 3;
            st.AddIndex(a); st.AddIndex(c); st.AddIndex(b);
            st.AddIndex(b); st.AddIndex(c); st.AddIndex(d);
        }
        root.AddChild(new MeshInstance3D { Mesh = st.Commit(), MaterialOverride = surfaceMaterial, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
    }

    /// <summary>
    /// 차로 위 높이. 아스팔트는 양쪽 가장자리(±3.2 m) 높이 사이를 평평하게 잇는 면이라,
    /// 그 위의 도색·과속방지턱은 지형 높이가 아니라 이 면을 따라야 묻히지 않는다.
    /// </summary>
    internal static float RoadY(World world, Vector2 center, Vector2 normal, float offset)
    {
        if (Mathf.Abs(offset) > RoadHalfWidth) return world.HeightAt(center.X + normal.X * offset, center.Y + normal.Y * offset);
        var a = center - normal * RoadHalfWidth;
        var b = center + normal * RoadHalfWidth;
        return Mathf.Lerp(world.HeightAt(a.X, a.Y), world.HeightAt(b.X, b.Y), (offset + RoadHalfWidth) / (2f * RoadHalfWidth));
    }

    const float RoadHalfWidth = 3.2f;

    static Vector2 Tangent(IReadOnlyList<Vector2> path, int i)
    {
        var before = path[Math.Max(i - 1, 0)];
        var after = path[Math.Min(i + 1, path.Count - 1)];
        return (after - before).Normalized();
    }

    static void AddRoadMarkings(Node3D root, World world, IReadOnlyList<Vector2> path)
    {
        var edgePaint = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.82f, 0.8f, 0.71f),
            Roughness = 0.92f,
        };
        var centerPaint = new StandardMaterial3D
        {
            AlbedoColor = Style.CenterColor.Darkened(0.08f),
            Roughness = 0.92f,
        };
        // 가장자리선: 실선, 또는 프랑스처럼 긴 점선
        foreach (var side in new[] { -1f, 1f })
        {
            if (Style.Edge == LinePattern.Solid)
                BuildSurface(root, world, path, side < 0 ? -2.83f : 2.72f, side < 0 ? -2.72f : 2.83f, 0.205f, edgePaint);
            else if (Style.Edge == LinePattern.LongDashed)
                Dashes(root, world, path, side * 2.78f, 0.11f, 9f, 3f, edgePaint);
        }
        switch (Style.Center)
        {
            case LinePattern.Solid:
                BuildSurface(root, world, path, -0.06f, 0.06f, 0.205f, centerPaint);
                break;
            case LinePattern.Double:
                BuildSurface(root, world, path, -0.2f, -0.09f, 0.205f, centerPaint);
                BuildSurface(root, world, path, 0.09f, 0.2f, 0.205f, centerPaint);
                break;
            case LinePattern.Dashed:
                Dashes(root, world, path, 0, 0.11f, 3f, 5f, centerPaint);
                break;
        }
    }

    /// <summary>길을 따라 offset 위치에 길이 dash, 틈 gap 의 점선</summary>
    static void Dashes(Node3D root, World world, IReadOnlyList<Vector2> path, float offset, float width, float dash, float gap, Material paint)
    {
        var length = PathLength(path);
        for (var distance = 2f + dash / 2f; distance < length - 1f; distance += dash + gap)
        {
            var (position, tangent) = AtDistance(path, distance);
            var normal = new Vector2(-tangent.Y, tangent.X);
            var p = position + normal * offset;
            root.AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(width, 0.012f, dash) },
                MaterialOverride = paint,
                Position = new Vector3(p.X, RoadY(world, position, normal, offset) + 0.2f, p.Y),
                Rotation = new Vector3(0, Mathf.Atan2(tangent.X, tangent.Y), 0),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            });
        }
    }

    static float PathLength(IReadOnlyList<Vector2> path)
    {
        var length = 0f;
        for (var i = 1; i < path.Count; i++) length += path[i].DistanceTo(path[i - 1]);
        return length;
    }

    static (Vector2 Position, Vector2 Tangent) AtDistance(IReadOnlyList<Vector2> path, float distance)
    {
        for (var i = 1; i < path.Count; i++)
        {
            var segment = path[i] - path[i - 1];
            var length = segment.Length();
            if (distance <= length)
                return (path[i - 1] + segment * (distance / length), segment / length);
            distance -= length;
        }
        return (path[^1], Tangent(path, path.Count - 1));
    }

    static void AddSidewalkJoints(Node3D root, World world, IReadOnlyList<Vector2> path)
    {
        var joint = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.35f, 0.35f, 0.33f),
            Roughness = 0.96f,
        };
        for (var distance = 2.4f; distance < PathLength(path); distance += 2.4f)
        {
            var (position, tangent) = AtDistance(path, distance);
            var side = new Vector2(-tangent.Y, tangent.X);
            foreach (var direction in new[] { -1f, 1f })
            {
                var p = position + side * direction * 4.05f;
                root.AddChild(new MeshInstance3D
                {
                    Mesh = new BoxMesh { Size = new Vector3(1.58f, 0.008f, 0.022f) },
                    MaterialOverride = joint,
                    Position = new Vector3(p.X, world.HeightAt(p.X, p.Y) + 0.185f, p.Y),
                    Rotation = new Vector3(0, Mathf.Atan2(tangent.X, tangent.Y), 0),
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                });
            }
        }
    }

    static void AddRoadFurniture(Node3D root, World world, IReadOnlyList<Vector2> path)
    {
        var iron = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.16f, 0.18f, 0.18f),
            Metallic = 0.68f,
            Roughness = 0.62f,
        };
        var cover = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.29f, 0.3f, 0.29f),
            Metallic = 0.58f,
            Roughness = 0.72f,
        };

        var length = PathLength(path);
        for (var distance = 6f; distance < length - 4f; distance += 11f + Hash((int)distance, 3) * 4f)
        {
            var (position, _) = AtDistance(path, distance);
            Count("맨홀", distance, 0);
            var manhole = new Node3D
            {
                Position = new Vector3(position.X, world.HeightAt(position.X, position.Y) + 0.182f, position.Y),
            };
            root.AddChild(manhole);
            manhole.AddChild(new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = 0.43f, BottomRadius = 0.43f, Height = 0.025f, RadialSegments = 24 },
                MaterialOverride = iron,
            });
            manhole.AddChild(new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = 0.36f, BottomRadius = 0.36f, Height = 0.035f, RadialSegments = 24 },
                Position = new Vector3(0, 0.025f, 0),
                MaterialOverride = cover,
            });
            for (var slot = 0; slot < 8; slot++)
                manhole.AddChild(new MeshInstance3D
                {
                    Mesh = new BoxMesh { Size = new Vector3(0.018f, 0.004f, 0.22f) },
                    Position = new Vector3(0, 0.046f, 0),
                    Rotation = new Vector3(0, slot * Mathf.Pi / 8f, 0),
                    MaterialOverride = iron,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                });
        }

        for (var distance = 8f; distance < length - 5f; distance += 12f)
        {
            var (position, tangent) = AtDistance(path, distance);
            var side = ((int)(distance / 12f) % 2 == 0) ? -1f : 1f;
            Count("빗물받이", distance, side);
            var drain = new Node3D
            {
                Position = new Vector3(position.X, world.HeightAt(position.X, position.Y) + 0.185f, position.Y)
                    + new Vector3(-tangent.Y, 0, tangent.X) * side * 3.83f,
                Rotation = new Vector3(0, Mathf.Atan2(tangent.X, tangent.Y), 0),
            };
            root.AddChild(drain);
            drain.AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(0.44f, 0.025f, 0.68f) },
                MaterialOverride = iron,
            });
            for (var slot = 0; slot < 6; slot++)
                drain.AddChild(new MeshInstance3D
                {
                    Mesh = new BoxMesh { Size = new Vector3(0.36f, 0.008f, 0.025f) },
                    Position = new Vector3(0, 0.018f, (slot - 2.5f) * 0.095f),
                    MaterialOverride = cover,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                });
        }
    }

    static void BuildFrontage(Node3D root, World world, IReadOnlyList<Vector2> path)
    {
        // 양쪽 모두 건물 전면이 이어진다. 폭·틈·소품은 시드로 달라져 같은 간격으로 되풀이되지 않는다.
        var length = PathLength(path);
        var slot = 0;
        foreach (var side in new[] { 1f, -1f })
        {
            var distance = side > 0 ? 6.5f : 4.2f;
            var lastCar = -99f;
            while (distance < length - 6f)
            {
                var seed = slot + 1;
                var width = 5.6f + Hash(seed, 1) * 1.6f;
                var gap = 0.35f + Hash(seed, 2) * 1.1f;
                var center = distance + width / 2f;
                if (center > length - 5f) break;
                var (point, tangent) = AtDistance(path, center);
                var normal = new Vector2(-tangent.Y, tangent.X);
                // 건물번호: 길 시작에서 떨어진 거리에 따라, 왼쪽은 홀수·오른쪽은 짝수
                var number = 2 * (int)(center / 5f) + (side > 0 ? 2 : 1);
                var (pilotis, depth) = AddBuilding(root, world, point + normal * side * FrontageOffset, tangent, side, seed, width, number);
                Count("건물", center, side);
                if (pilotis)
                {
                    AddCar(root, world, point + normal * side * (FrontageOffset + depth / 2f), normal * side, seed + 20);
                    Count("필로티 주차", center, side);
                    if (Style.TrashBags && Hash(seed, 19) < 0.7f)
                    {
                        StreetDetail.ParkingDrum(root, world, point + normal * side * 4.55f + tangent * (width * 0.3f));
                        Count("주차금지 드럼", center, side);
                    }
                }
                var bagsAt = center + width / 2f - 0.3f;
                if (Style.TrashBags && Hash(seed, 21) < 0.45f)
                {
                    var (bags, bagsTangent) = AtDistance(path, bagsAt);
                    StreetDetail.TrashBags(root, world, bags + new Vector2(-bagsTangent.Y, bagsTangent.X) * side * 4.6f, seed);
                    Count("종량제 봉투", bagsAt, side);
                }
                if (Style.Styrofoam && !pilotis && Hash(seed, 22) < 0.4f)
                {
                    var gardenAt = center + width * 0.22f;
                    var (garden, gardenTangent) = AtDistance(path, gardenAt);
                    StreetDetail.StyrofoamGarden(root, world, garden + new Vector2(-gardenTangent.Y, gardenTangent.X) * side * 5.3f, gardenTangent, seed);
                    Count("스티로폼 텃밭", gardenAt, side);
                }
                if (Style.Scooter && Hash(seed, 23) < 0.25f)
                {
                    var scooterAt = center - width * 0.1f;
                    var (scooter, scooterTangent) = AtDistance(path, scooterAt);
                    var scooterNormal = new Vector2(-scooterTangent.Y, scooterTangent.X);
                    StreetDetail.Scooter(root, world, scooter + scooterNormal * side * 4.35f, scooterNormal * side + scooterTangent * 0.4f, seed);
                    Count("배달 오토바이", scooterAt, side);
                }

                if (Hash(seed, 3) < 0.5f)
                {
                    var (entrance, entranceTangent) = AtDistance(path, center - width * 0.27f);
                    AddTactilePaving(root, world, entrance, entranceTangent, side);
                    Count("점자블록", center - width * 0.27f, side);
                }
                var (door, doorTangent) = AtDistance(path, center - width * 0.27f);
                AddDoorPots(root, world, door + new Vector2(-doorTangent.Y, doorTangent.X) * side * 5.25f, doorTangent, seed);
                Count("문 앞 화분", center - width * 0.27f, side);

                RegionalProps(root, world, path, center, width, side, seed);

                var roll = Hash(seed, 4);
                var propAt = center + (Hash(seed, 5) - 0.5f) * width * 0.6f;
                var (prop, propTangent) = AtDistance(path, propAt);
                var propNormal = new Vector2(-propTangent.Y, propTangent.X);
                if (roll < 0.4f)
                {
                    AddStreetBin(root, world, prop + propNormal * side * 4.25f, propTangent, seed);
                    Count("쓰레기통", propAt, side);
                }
                else
                {
                    AddPlanter(root, world, prop + propNormal * side * 5.05f, propTangent, side, seed);
                    Count("화단", propAt, side);
                }
                if (Hash(seed, 6) < 0.55f && center - lastCar > 9f)
                {
                    AddCar(root, world, point + normal * side * 2.35f, tangent, seed + 10);
                    Count("주차된 차", center, side);
                    lastCar = center;
                }
                distance += width + gap;
                slot++;
            }
        }
    }

    /// <summary>나라마다 길가에서 거리뷰로 먼저 눈에 띄는 것: 일본 자판기, 미국 소화전·우편함</summary>
    static void RegionalProps(Node3D root, World world, IReadOnlyList<Vector2> path, float center, float width, float side, int seed)
    {
        if (Style.VendingMachine && Hash(seed, 24) < 0.45f)
        {
            var at = center + width * 0.32f;
            var (p, t) = AtDistance(path, at);
            var n = new Vector2(-t.Y, t.X);
            StreetDetail.VendingMachine(root, world, p + n * side * 5.2f, -n * side, seed);
            Count("자판기", at, side);
        }
        if (Style.Hydrant && Hash(seed, 25) < 0.4f)
        {
            var at = center - width * 0.4f;
            var (p, t) = AtDistance(path, at);
            StreetDetail.Hydrant(root, world, p + new Vector2(-t.Y, t.X) * side * 3.65f);
            Count("소화전", at, side);
        }
        if (Style.CafeTerrace && Hash(seed, 27) < 0.6f)
        {
            var at = center - width * 0.15f;
            var (p, t) = AtDistance(path, at);
            StreetDetail.CafeTerrace(root, world, p + new Vector2(-t.Y, t.X) * side * 4.45f, t, seed);
            Count("카페 테라스", at, side);
        }
        if (Style.Mailbox && Hash(seed, 26) < 0.35f)
        {
            var at = center + width * 0.1f;
            var (p, t) = AtDistance(path, at);
            var n = new Vector2(-t.Y, t.X);
            StreetDetail.Mailbox(root, world, p + n * side * 3.7f, -n * side);
            Count("우편함", at, side);
        }
    }

    /// <summary>가게 문 옆에 내놓은 크기가 제각각인 화분들</summary>
    static void AddDoorPots(Node3D root, World world, Vector2 center, Vector2 tangent, int seed)
    {
        var clay = Look.Painted("painted_concrete", 0.5f, Hash(seed, 7) < 0.5f
            ? new Color(0.55f, 0.36f, 0.26f)
            : new Color(0.42f, 0.43f, 0.41f));
        clay.Roughness = 0.86f;
        var leaves = Look.Scanned("leafy_grass", 0.5f, new Color(0.62f, 0.76f, 0.5f));
        var count = 2 + (int)(Hash(seed, 8) * 2.99f);
        for (var i = 0; i < count; i++)
        {
            var radius = 0.13f + Hash(seed, 20 + i) * 0.12f;
            var height = radius * (1.3f + Hash(seed, 30 + i) * 0.6f);
            var p = center + tangent * (i * 0.42f + Hash(seed, 40 + i) * 0.1f);
            var baseY = world.HeightAt(p.X, p.Y) + 0.18f;
            var pot = new Node3D { Position = new Vector3(p.X, baseY, p.Y) };
            root.AddChild(pot);
            pot.AddChild(new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = radius, BottomRadius = radius * 0.78f, Height = height, RadialSegments = 12 },
                Position = new Vector3(0, height / 2f, 0),
                MaterialOverride = clay,
            });
            pot.AddChild(new MeshInstance3D
            {
                Mesh = new SphereMesh { Radius = radius * 1.15f, Height = radius * 1.7f, RadialSegments = 10, Rings = 6 },
                Position = new Vector3(0, height + radius * 0.55f, 0),
                Rotation = new Vector3(0, Hash(seed, 50 + i) * Mathf.Tau, 0),
                Scale = new Vector3(1f, 0.8f + Hash(seed, 60 + i) * 0.5f, 0.9f),
                MaterialOverride = leaves,
            });
        }
    }

    static void AddTactilePaving(Node3D root, World world, Vector2 center, Vector2 tangent, float side)
    {
        var normal = new Vector2(-tangent.Y, tangent.X);
        var tileCenter = center + normal * side * 4.02f;
        var tile = new Node3D
        {
            Position = new Vector3(tileCenter.X, world.HeightAt(tileCenter.X, tileCenter.Y) + 0.198f, tileCenter.Y),
            Rotation = new Vector3(0, Mathf.Atan2(tangent.X, tangent.Y), 0),
        };
        root.AddChild(tile);
        tile.AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(0.82f, 0.025f, 0.82f) },
            MaterialOverride = TactileTileMaterial,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
        for (var bar = 0; bar < 5; bar++)
            tile.AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(0.62f, 0.018f, 0.045f) },
                Position = new Vector3(0, 0.0215f, (bar - 2) * 0.14f),
                MaterialOverride = TactileBarMaterial,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            });
    }

    static void AddStreetBin(Node3D root, World world, Vector2 center, Vector2 tangent, int seed)
    {
        var bin = new Node3D
        {
            Name = $"StreetBin{seed}",
            Position = new Vector3(center.X, world.HeightAt(center.X, center.Y), center.Y),
            Rotation = new Vector3(0, Mathf.Atan2(tangent.X, tangent.Y), 0),
        };
        root.AddChild(bin);

        var body = Look.Painted("painted_concrete", 0.7f, Style.BinColor.Lightened(seed % 2 == 0 ? 0f : 0.08f));
        body.Roughness = 0.84f;
        var dark = new StandardMaterial3D { AlbedoColor = new Color(0.12f, 0.14f, 0.14f), Roughness = 0.8f };
        var reflector = new StandardMaterial3D { AlbedoColor = new Color(0.72f, 0.62f, 0.31f), Roughness = 0.72f };
        AddBox(bin, new Vector3(0.58f, 0.76f, 0.56f), new Vector3(0, 0.48f, 0), body);
        AddBox(bin, new Vector3(0.62f, 0.08f, 0.6f), new Vector3(0, 0.9f, 0), dark);
        AddBox(bin, new Vector3(0.4f, 0.035f, 0.42f), new Vector3(0, 0.95f, 0), body);
        AddBox(bin, new Vector3(0.5f, 0.045f, 0.018f), new Vector3(0, 0.58f, 0.29f), reflector);
        foreach (var x in new[] { -0.22f, 0.22f })
        {
            var wheel = new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = 0.09f, BottomRadius = 0.09f, Height = 0.055f, RadialSegments = 12 },
                Position = new Vector3(x, 0.105f, -0.2f),
                Rotation = new Vector3(0, 0, Mathf.Pi / 2),
                MaterialOverride = dark,
            };
            bin.AddChild(wheel);
        }
        AddBox(bin, new Vector3(0.035f, 0.25f, 0.035f), new Vector3(0.2f, 0.9f, -0.2f), dark);
    }

    static void BuildStreetLights(Node3D root, World world, IReadOnlyList<Vector2> path)
    {
        var metal = new StandardMaterial3D { AlbedoColor = new Color(0.25f, 0.28f, 0.28f), Metallic = 0.72f, Roughness = 0.4f };
        var diffuser = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.82f, 0.78f, 0.65f),
            EmissionEnabled = true,
            Emission = new Color(0.8f, 0.67f, 0.42f),
            EmissionEnergyMultiplier = 0.28f,
            Roughness = 0.5f,
        };
        var length = PathLength(path);
        var lamp = 0;
        for (var distance = 3.5f; distance < length - 3f; distance += 12.5f + Hash(lamp, 13) * 3f, lamp++)
        {
            var (point, tangent) = AtDistance(path, distance);
            var normal = new Vector2(-tangent.Y, tangent.X);
            var side = lamp % 2 == 0 ? -1f : 1f;
            Count("가로등", distance, side);
            var at = point + normal * side * 5.2f;
            var ground = world.HeightAt(at.X, at.Y);
            var pole = new Node3D
            {
                Position = new Vector3(at.X, ground, at.Y),
                Rotation = new Vector3(0, Mathf.Atan2(tangent.X, tangent.Y), 0),
            };
            root.AddChild(pole);
            pole.AddChild(new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = 0.045f, BottomRadius = 0.075f, Height = 5.4f, RadialSegments = 10 },
                Position = new Vector3(0, 2.7f, 0),
                MaterialOverride = metal,
            });
            pole.AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(0.09f, 0.08f, 1.15f) },
                Position = new Vector3(side * 0.5f, 5.25f, 0),
                MaterialOverride = metal,
            });
            pole.AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(0.28f, 0.09f, 0.42f) },
                Position = new Vector3(side * 1.02f, 5.2f, 0),
                MaterialOverride = diffuser,
            });
            var bollard = new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = 0.12f, BottomRadius = 0.14f, Height = 0.88f, RadialSegments = 12 },
                Position = new Vector3(-side * 0.6f, 0.44f, 0.9f),
                MaterialOverride = metal,
            };
            pole.AddChild(bollard);
        }
    }

    /// <summary>
    /// 한국 골목의 콘크리트 전주: 위에는 애자와 고압선 3가닥, 그 아래 4.6–5.6 m 에 통신선 다발이 처져 지나간다.
    /// 가운데 전주에는 주상변압기와 감아 둔 통신선 뭉치, 발판 볼트, 전주 번호판, 붙였다 뜯긴 전단지.
    /// 전선은 모두 한 메시로 합쳐 그리기 호출을 늘리지 않는다.
    /// </summary>
    static void BuildUtilityLines(Node3D root, World world, IReadOnlyList<Vector2> path)
    {
        var length = PathLength(path);
        if (length < 32f || Style.Pole == PoleKind.None) return; // 프랑스 마을: 전선은 땅속

        var wood = Style.Pole == PoleKind.UsWood;
        var concrete = wood
            ? Look.Scanned("weathered_planks", 1.4f, new Color(0.5f, 0.4f, 0.31f))
            : Look.Scanned("concrete_wall_008", 1.2f, new Color(0.86f, 0.85f, 0.81f));
        var steel = new StandardMaterial3D { AlbedoColor = new Color(0.4f, 0.41f, 0.4f), Metallic = 0.6f, Roughness = 0.55f };
        var ceramic = new StandardMaterial3D { AlbedoColor = new Color(0.72f, 0.69f, 0.58f), Roughness = 0.36f };
        var wire = new StandardMaterial3D { AlbedoColor = new Color(0.07f, 0.075f, 0.075f), Metallic = 0.3f, Roughness = 0.55f };
        var transformerGrey = new StandardMaterial3D { AlbedoColor = new Color(0.58f, 0.6f, 0.6f), Metallic = 0.35f, Roughness = 0.5f };
        var plate = new StandardMaterial3D { AlbedoColor = new Color(0.9f, 0.9f, 0.86f), Roughness = 0.5f };

        const float side = 1f;
        const float poleOffset = 4.95f;
        const float crossarmHeight = 7.6f;
        var poleDistances = new[] { 8f, length * 0.5f + 1.3f, length - 8f };
        var power = new List<Vector3[]>(poleDistances.Length);
        var telecom = new List<Vector3[]>(poleDistances.Length);
        for (var index = 0; index < poleDistances.Length; index++)
        {
            var distance = poleDistances[index];
            var (point, tangent) = AtDistance(path, distance);
            Count("전봇대", distance, side);
            var normal = new Vector2(-tangent.Y, tangent.X) * side;
            var groundPoint = point + normal * poleOffset;
            var pole = new Node3D
            {
                Name = "StreetUtilityPole",
                Position = new Vector3(groundPoint.X, world.HeightAt(groundPoint.X, groundPoint.Y), groundPoint.Y),
                Rotation = new Vector3(0, Mathf.Atan2(tangent.X, tangent.Y), 0),
            };
            root.AddChild(pole);
            Vector3 World(Vector3 local) => pole.Position + local.Rotated(Vector3.Up, pole.Rotation.Y);

            pole.AddChild(new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = 0.12f, BottomRadius = 0.19f, Height = 8.6f, RadialSegments = 14 },
                Position = new Vector3(0, 4.3f, 0),
                MaterialOverride = concrete,
            });
            // 가로대: 건물 쪽으로 걸리지 않게 길 쪽으로 치우친다. 미국은 나무 가로대
            AddBox(pole, new Vector3(wood ? 2.0f : 1.5f, wood ? 0.1f : 0.1f, wood ? 0.09f : 0.1f), new Vector3(wood ? 0.45f : 0.2f, crossarmHeight, 0), wood ? concrete : steel);
            if (Style.Pole is PoleKind.KoreaConcrete or PoleKind.JapanConcrete) StreetDetail.PoleStripes(pole, Style.Pole);
            if (Style.Pole == PoleKind.KoreaConcrete)
                pole.AddChild(new MeshInstance3D
                {
                    Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 0.05f, Height = 0.7f, RadialSegments = 8 },
                    Position = new Vector3(0, 8.95f, 0),
                    MaterialOverride = steel,
                }); // 꼭대기의 뾰족한 침
            var ends = new Vector3[3];
            for (var conductor = 0; conductor < 3; conductor++)
            {
                var x = (wood ? 0.45f : 0.2f) + (conductor - 1) * (wood ? 0.85f : 0.62f);
                pole.AddChild(new MeshInstance3D
                {
                    Mesh = new CylinderMesh { TopRadius = 0.07f, BottomRadius = 0.09f, Height = 0.2f, RadialSegments = 10 },
                    Position = new Vector3(x, crossarmHeight + 0.15f, 0),
                    MaterialOverride = ceramic,
                });
                ends[conductor] = World(new Vector3(x, crossarmHeight + 0.27f, 0));
            }
            power.Add(ends);

            // 통신선 고정 밴드와 다발 (높이·간격이 제각각)
            var lines = new Vector3[5];
            for (var line = 0; line < lines.Length; line++)
            {
                var y = 4.65f + line * 0.21f + Hash(index * 7 + line, 210) * 0.08f;
                var x = (line % 2 == 0 ? -0.2f : 0.2f);
                lines[line] = World(new Vector3(x, y, 0));
            }
            AddBox(pole, new Vector3(0.5f, 0.06f, 0.06f), new Vector3(0, 4.7f, 0), steel);
            AddBox(pole, new Vector3(0.5f, 0.06f, 0.06f), new Vector3(0, 5.3f, 0), steel);
            telecom.Add(lines);

            // 발판 볼트 (2 m 위부터 엇갈리게), 전주 번호판, 전단지
            for (var step = 0; step < 10; step++)
            {
                var y = 2.1f + step * 0.45f;
                var r = Mathf.Lerp(0.18f, 0.13f, y / 8.6f);
                var a = step % 2 == 0 ? 0.6f : -0.6f;
                pole.AddChild(new MeshInstance3D
                {
                    Mesh = new CylinderMesh { TopRadius = 0.012f, BottomRadius = 0.012f, Height = 0.22f, RadialSegments = 6 },
                    Position = new Vector3(Mathf.Sin(a) * (r + 0.08f), y, -Mathf.Cos(a) * (r + 0.08f)),
                    Rotation = new Vector3(Mathf.Pi / 2, a, 0),
                    MaterialOverride = steel,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                });
            }
            AddBox(pole, new Vector3(0.2f, 0.28f, 0.01f), new Vector3(0, 2.0f, -0.185f), plate, new Vector3(0, Mathf.Pi, 0));
            var tag = Look.Tag($"{Style.RoadName}\n{index + 1:00}", 0.0012f, Look.Stamp);
            tag.Position = new Vector3(0, 2.0f, -0.195f);
            tag.Rotation = new Vector3(0, Mathf.Pi, 0);
            pole.AddChild(tag);
            for (var flyer = 0; flyer < 3; flyer++)
            {
                var seed = index * 5 + flyer;
                var a = Mathf.Pi + (Hash(seed, 220) - 0.5f) * 1.6f;
                var y = 1.35f + Hash(seed, 221) * 0.7f;
                var torn = Hash(seed, 222);
                pole.AddChild(new MeshInstance3D
                {
                    Mesh = new BoxMesh { Size = new Vector3(0.2f, 0.27f * (0.5f + torn * 0.5f), 0.004f) },
                    Position = new Vector3(Mathf.Sin(a) * 0.185f, y, Mathf.Cos(a) * 0.185f),
                    Rotation = new Vector3(0, a, (Hash(seed, 223) - 0.5f) * 0.2f),
                    MaterialOverride = new StandardMaterial3D
                    {
                        AlbedoColor = torn < 0.4f ? new Color(0.92f, 0.88f, 0.6f) : new Color(0.9f, 0.9f, 0.87f),
                        Roughness = 0.85f,
                    },
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                });
            }

            if (index == 1)
            {
                // 주상변압기: 한국은 나란히 두 대, 일본은 위아래로 쌓고, 미국은 한 대. 그리고 감아 둔 통신선 뭉치
                var cans = Style.Pole switch
                {
                    PoleKind.JapanConcrete => new[] { new Vector2(0, 6.0f), new Vector2(0, 6.95f) },
                    PoleKind.UsWood => new[] { new Vector2(0, 6.4f) },
                    _ => new[] { new Vector2(-0.36f, 6.35f), new Vector2(0.36f, 6.35f) },
                };
                foreach (var can in cans)
                {
                    pole.AddChild(new MeshInstance3D
                    {
                        Mesh = new CylinderMesh { TopRadius = 0.24f, BottomRadius = 0.24f, Height = 0.85f, RadialSegments = 16 },
                        Position = new Vector3(can.X, can.Y, -0.3f),
                        MaterialOverride = transformerGrey,
                    });
                    pole.AddChild(new MeshInstance3D
                    {
                        Mesh = new CylinderMesh { TopRadius = 0.04f, BottomRadius = 0.05f, Height = 0.18f, RadialSegments = 8 },
                        Position = new Vector3(can.X, can.Y + 0.52f, -0.3f),
                        MaterialOverride = ceramic,
                    });
                }
                AddBox(pole, new Vector3(1.0f, 0.08f, 0.08f), new Vector3(0, 5.88f, -0.2f), steel);
                pole.AddChild(new MeshInstance3D
                {
                    Mesh = new TorusMesh { InnerRadius = 0.2f, OuterRadius = 0.27f, Rings = 16, RingSegments = 6 },
                    Position = new Vector3(0.05f, 4.25f, 0.26f),
                    Rotation = new Vector3(Mathf.Pi / 2, 0, 0.2f),
                    MaterialOverride = wire,
                });
            }
        }

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        for (var span = 0; span < power.Count - 1; span++)
        {
            for (var conductor = 0; conductor < 3; conductor++)
                AddCable(st, power[span][conductor], power[span + 1][conductor], 0.55f + conductor * 0.05f, 0.012f);
            for (var line = 0; line < (wood ? 2 : 5); line++)
                AddCable(st, telecom[span][line], telecom[span + 1][line], 0.55f + Hash(span * 5 + line, 230) * 0.6f, 0.009f + Hash(span * 5 + line, 231) * 0.01f);
        }
        // 전주에서 건물로 들어가는 인입선
        for (var drop = 0; drop < power.Count; drop++)
        {
            var from = telecom[drop][drop % 5];
            var (point, tangent) = AtDistance(path, poleDistances[drop] + 3f);
            var normal = new Vector2(-tangent.Y, tangent.X) * side;
            var wall = point + normal * (FrontageOffset + 0.02f);
            AddCable(st, from, new Vector3(wall.X, from.Y - 1.2f, wall.Y), 0.25f, 0.008f);
        }
        root.AddChild(new MeshInstance3D { Mesh = st.Commit(), MaterialOverride = wire, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
    }

    /// <summary>처진 전선 한 가닥을 육각 관으로 SurfaceTool 에 더한다 (포물선 처짐)</summary>
    static void AddCable(SurfaceTool st, Vector3 from, Vector3 to, float sag, float radius)
    {
        const int segments = 20, sides = 5;
        var points = new Vector3[segments + 1];
        for (var i = 0; i <= segments; i++)
        {
            var t = (float)i / segments;
            points[i] = from.Lerp(to, t) + Vector3.Down * (4f * sag * t * (1f - t));
        }
        for (var i = 0; i < segments; i++)
        {
            var axis = (points[i + 1] - points[i]).Normalized();
            var u = axis.Cross(Vector3.Up).Normalized();
            if (u.LengthSquared() < 0.5f) u = Vector3.Right;
            var v = u.Cross(axis);
            for (var k = 0; k < sides; k++)
            {
                var a0 = Mathf.Tau * k / sides;
                var a1 = Mathf.Tau * (k + 1) / sides;
                var n0 = u * Mathf.Cos(a0) + v * Mathf.Sin(a0);
                var n1 = u * Mathf.Cos(a1) + v * Mathf.Sin(a1);
                var p00 = points[i] + n0 * radius;
                var p01 = points[i] + n1 * radius;
                var p10 = points[i + 1] + n0 * radius;
                var p11 = points[i + 1] + n1 * radius;
                CableTri(st, p00, p10, p11, n0, n0, n1);
                CableTri(st, p00, p11, p01, n0, n1, n1);
            }
        }
    }

    static void CableTri(SurfaceTool st, Vector3 a, Vector3 b, Vector3 c, Vector3 na, Vector3 nb, Vector3 nc)
    {
        var outward = (na + nb + nc).Normalized();
        if ((c - a).Cross(b - a).Dot(outward) < 0) { (b, c) = (c, b); (nb, nc) = (nc, nb); }
        st.SetNormal(na); st.AddVertex(a);
        st.SetNormal(nb); st.AddVertex(b);
        st.SetNormal(nc); st.AddVertex(c);
    }

    /// <summary>
    /// 한국 주택가의 2–4층 다세대·상가주택. 외벽은 적벽돌·타일·도장 중 하나, 1층은 가게·필로티 주차장·대문 중 하나.
    /// 반환: 필로티이면 true (1층에 차를 세울 수 있다), 건물 깊이.
    /// </summary>
    static (bool Pilotis, float Depth) AddBuilding(Node3D root, World world, Vector2 front, Vector2 tangent, float side, int seed, float width, int number)
    {
        var depth = 4.4f + Hash(seed, 9) * 0.9f;
        var center = front + new Vector2(-tangent.Y, tangent.X) * side * depth / 2f;
        var floors = Style.MinFloors + (int)(Hash(seed, 10) * (Style.MaxFloors - Style.MinFloors + 0.99f));
        const float floorHeight = 2.8f;
        var height = floors * floorHeight + 0.35f;
        var groundRoll = Hash(seed, 15);
        var pilotis = Style.Pilotis && floors >= 3 && groundRoll < 0.35f;
        var shop = !pilotis && groundRoll < 0.72f;
        var yaw = Mathf.Atan2(-side * -tangent.Y, -side * tangent.X);
        var baseY = world.HeightAt(center.X, center.Y);
        var building = new StaticBody3D
        {
            Name = $"StreetBuilding{seed}",
            Position = new Vector3(center.X, baseY + height * 0.5f, center.Y),
            Rotation = new Vector3(0, yaw, 0),
            CollisionLayer = PartBody.LayerWorld,
            CollisionMask = 0,
        };
        root.AddChild(building);

        var facade = Style.Facade(Hash(seed, 14));
        var wall = Look.Scanned(facade.Texture, facade.Tile, facade.Tint.Lightened((Hash(seed, 12) - 0.5f) * 0.12f));
        var trim = new StandardMaterial3D { AlbedoColor = new Color(0.74f, 0.72f, 0.67f), Roughness = 0.78f };
        var bottom = -height / 2f;
        var frontZ = depth / 2f + 0.045f;

        if (pilotis)
        {
            // 필로티: 1층은 기둥만 남은 주차장, 위층이 그 위에 얹힌다
            var upper = height - floorHeight;
            building.AddChild(new CollisionShape3D { Position = new Vector3(0, bottom + floorHeight + upper / 2f, 0), Shape = new BoxShape3D { Size = new Vector3(width, upper, depth) } });
            building.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(width, upper, depth) }, Position = new Vector3(0, bottom + floorHeight + upper / 2f, 0), MaterialOverride = wall });
            var concrete = Look.Scanned("concrete_wall_008", 2f, new Color(0.82f, 0.81f, 0.78f));
            foreach (var cx in new[] { -1f, 1f })
            foreach (var cz in new[] { -1f, 1f })
            {
                var at = new Vector3(cx * (width / 2f - 0.2f), bottom + floorHeight / 2f, cz * (depth / 2f - 0.2f));
                building.AddChild(new CollisionShape3D { Position = at, Shape = new BoxShape3D { Size = new Vector3(0.38f, floorHeight, 0.38f) } });
                AddBox(building, new Vector3(0.38f, floorHeight, 0.38f), at, concrete);
            }
            AddBox(building, new Vector3(width - 0.2f, 0.06f, depth - 0.2f), new Vector3(0, bottom + floorHeight - 0.03f, 0), new StandardMaterial3D { AlbedoColor = new Color(0.36f, 0.36f, 0.34f), Roughness = 0.9f });
            AddBox(building, new Vector3(width - 0.4f, floorHeight, 0.1f), new Vector3(0, bottom + floorHeight / 2f, -depth / 2f + 0.06f), concrete);
            AddBox(building, new Vector3(width - 0.3f, 0.04f, depth - 0.3f), new Vector3(0, bottom + 0.02f, 0), concrete);
            // 천장 형광등 한 줄
            AddBox(building, new Vector3(1.2f, 0.05f, 0.1f), new Vector3(0, bottom + floorHeight - 0.09f, 0), new StandardMaterial3D
            {
                AlbedoColor = new Color(0.95f, 0.95f, 0.92f), EmissionEnabled = true, Emission = new Color(0.9f, 0.92f, 0.95f), EmissionEnergyMultiplier = 0.6f,
            });
        }
        else
        {
            building.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(width, height, depth) } });
            building.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(width, height, depth) }, MaterialOverride = wall });
        }

        var glass = new StandardMaterial3D { AlbedoColor = new Color(0.2f, 0.24f, 0.27f), Metallic = 0.1f, Roughness = 0.08f, MetallicSpecular = 0.7f };
        var aluminium = new StandardMaterial3D { AlbedoColor = new Color(0.7f, 0.71f, 0.7f), Metallic = 0.6f, Roughness = 0.38f };
        var curtain = new StandardMaterial3D { AlbedoColor = new Color(0.62f, 0.58f, 0.5f), Roughness = 0.9f };

        // 위층 창: 2짝 미닫이 알루미늄 창, 일부는 방범창, 층 사이 띠, 실외기
        var columns = width > 6.4f ? 3 : 2;
        for (var floor = 1; floor < floors; floor++)
        {
            var y = bottom + floorHeight * floor + 1.35f;
            for (var col = 0; col < columns; col++)
            {
                var x = (col - (columns - 1) / 2f) * width / columns;
                var windowWidth = 1.25f + Hash(seed, 20 + col) * 0.45f;
                const float windowHeight = 1.2f;
                AddBox(building, new Vector3(windowWidth, windowHeight, 0.06f), new Vector3(x, y, frontZ - 0.02f), glass);
                AddBox(building, new Vector3(windowWidth + 0.08f, 0.05f, 0.08f), new Vector3(x, y + windowHeight / 2f, frontZ), aluminium);
                AddBox(building, new Vector3(windowWidth + 0.08f, 0.05f, 0.08f), new Vector3(x, y - windowHeight / 2f, frontZ), aluminium);
                foreach (var ex in new[] { -windowWidth / 2f, 0f, windowWidth / 2f })
                    AddBox(building, new Vector3(0.05f, windowHeight, 0.08f), new Vector3(x + ex, y, frontZ), aluminium);
                AddBox(building, new Vector3(windowWidth + 0.24f, 0.07f, 0.16f), new Vector3(x, y - windowHeight / 2f - 0.06f, frontZ + 0.04f), trim);
                if (Hash(seed, 30 + floor * 4 + col) < 0.55f)
                    AddBox(building, new Vector3(windowWidth * (0.3f + Hash(seed, 40 + floor * 4 + col) * 0.4f), windowHeight - 0.1f, 0.02f),
                        new Vector3(x - windowWidth * 0.2f, y, frontZ - 0.06f), curtain);
                if (Style.Grilles && floor == 1 && Hash(seed, 50 + col) < 0.7f)
                    StreetDetail.Grille(building, x, y, frontZ, windowWidth, windowHeight);
                if (Style.Shutters)
                    StreetDetail.Shutters(building, x, y, frontZ, windowWidth, windowHeight, seed + floor * 3 + col);
            }
            AddBox(building, new Vector3(width + 0.04f, 0.12f, 0.06f), new Vector3(0, bottom + floorHeight * floor - 0.04f, frontZ + 0.01f), trim);
            if (Style.AcUnits && Hash(seed, 60 + floor) < 0.65f)
            {
                var acx = ((int)(Hash(seed, 61 + floor) * (columns - 1)) + 0.5f - (columns - 1) / 2f) * width / columns;
                StreetDetail.AcUnit(building, acx, bottom + floorHeight * floor + 0.75f, frontZ, seed + floor);
            }
        }

        // 1층
        if (shop)
        {
            var shopWidth = width * 0.7f;
            AddBox(building, new Vector3(shopWidth, 2.3f, 0.06f), new Vector3(-width * 0.08f, bottom + 1.2f, frontZ - 0.01f), glass);
            for (var m = 0; m <= 4; m++)
                AddBox(building, new Vector3(0.06f, 2.34f, 0.09f), new Vector3(-width * 0.08f - shopWidth / 2f + shopWidth * m / 4f, bottom + 1.2f, frontZ + 0.01f), aluminium);
            AddBox(building, new Vector3(shopWidth, 0.06f, 0.09f), new Vector3(-width * 0.08f, bottom + 2.35f, frontZ + 0.01f), aluminium);
            // 진열대·상자처럼 안이 비어 보이지 않게
            AddBox(building, new Vector3(shopWidth * 0.8f, 0.9f, 0.5f), new Vector3(-width * 0.08f, bottom + 0.45f, frontZ - 0.45f), curtain);
            var awning = new StandardMaterial3D { AlbedoColor = new Color(0.43f, 0.24f, 0.18f), Roughness = 0.82f };
            var stripe = new StandardMaterial3D { AlbedoColor = Hash(seed, 16) < 0.5f ? new Color(0.72f, 0.34f, 0.22f) : new Color(0.81f, 0.77f, 0.65f), Roughness = 0.88f };
            if (Hash(seed, 17) < 0.6f)
                for (var panel = 0; panel < 7; panel++)
                    AddBox(building, new Vector3(shopWidth / 7f, 0.04f, 0.95f),
                        new Vector3(-width * 0.08f + (panel - 3) * shopWidth / 7f, bottom + 2.42f, frontZ + 0.5f),
                        panel % 2 == 0 ? awning : stripe, new Vector3(0.22f, 0, 0));
            StreetDetail.ShopSigns(building, width, height, frontZ, seed, Style);
            if (Style.PharmacyCross && Hash(seed, 13) < 0.4f)
                StreetDetail.PharmacyCross(building, new Vector3(width / 2f - 0.6f, bottom + 3.3f, frontZ + 0.5f));
        }
        else if (!pilotis)
        {
            // 주택 대문과 방범창 친 1층 창
            var gate = new StandardMaterial3D { AlbedoColor = new Color(0.3f, 0.2f, 0.15f), Metallic = 0.4f, Roughness = 0.55f };
            AddBox(building, new Vector3(1.1f, 2.1f, 0.08f), new Vector3(-width * 0.28f, bottom + 1.05f, frontZ + 0.01f), gate);
            AddBox(building, new Vector3(1.3f, 0.12f, 0.5f), new Vector3(-width * 0.28f, bottom + 2.25f, frontZ + 0.24f), trim);
            AddBox(building, new Vector3(0.05f, 0.05f, 0.03f), new Vector3(-width * 0.28f + 0.42f, bottom + 1.05f, frontZ + 0.06f), aluminium);
            AddBox(building, new Vector3(1.3f, 1.0f, 0.06f), new Vector3(width * 0.18f, bottom + 1.5f, frontZ - 0.02f), glass);
            StreetDetail.Grille(building, width * 0.18f, bottom + 1.5f, frontZ, 1.3f, 1.0f);
        }

        // 지붕 슬래브, 옥상, 빗물받이 홈통, 가스관, 번호판
        AddBox(building, new Vector3(width + 0.12f, 0.18f, depth + 0.12f), new Vector3(0, height / 2f + 0.08f, 0), trim);
        if (Style.PitchedRoofs)
            StreetDetail.PitchedRoof(building, width, depth, height, seed, Style.RoofColor);
        else
            StreetDetail.Rooftop(building, width, depth, height, seed, wall, trim);
        var pipe = new StandardMaterial3D { AlbedoColor = new Color(0.62f, 0.63f, 0.6f), Roughness = 0.6f };
        var downX = (Hash(seed, 18) < 0.5f ? -1f : 1f) * (width / 2f - 0.12f);
        AddBox(building, new Vector3(0.09f, height + 0.6f, 0.09f), new Vector3(downX, 0.3f, frontZ + 0.08f), pipe);
        if (Style.GasPipes)
            StreetDetail.GasPipe(building, width, height, frontZ, floorHeight, floors, floorHeight, seed);
        StreetDetail.NumberPlate(building, new Vector3(width / 2f - 0.5f, bottom + 2.1f, frontZ + 0.03f), number, Style.RoadName, Style.NumberPlate);
        return (pilotis, depth);
    }

    static void AddPlanter(Node3D root, World world, Vector2 center, Vector2 tangent, float direction, int seed)
    {
        var planter = new StaticBody3D
        {
            Name = $"Planter{seed}",
            Position = new Vector3(center.X, world.HeightAt(center.X, center.Y) + 0.28f, center.Y),
            Rotation = new Vector3(0, Mathf.Atan2(tangent.X, tangent.Y), 0),
            CollisionLayer = PartBody.LayerWorld,
            CollisionMask = 0,
        };
        var box = new BoxMesh { Size = new Vector3(1.0f, 0.45f, 0.72f) };
        var concrete = Look.Painted("painted_concrete", 0.8f, new Color(0.48f, 0.47f, 0.43f));
        var soil = Look.Scanned("forrest_ground_01", 0.5f, new Color(0.72f, 0.67f, 0.54f));
        var leaves = Look.Scanned("leafy_grass", 0.65f, new Color(0.7f, 0.82f, 0.57f));
        planter.AddChild(new MeshInstance3D { Mesh = box, Position = new Vector3(0, 0.1f, 0), MaterialOverride = concrete });
        planter.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.86f, 0.055f, 0.58f) }, Position = new Vector3(0, 0.32f, 0), MaterialOverride = soil });
        for (var clump = 0; clump < 3; clump++)
            planter.AddChild(new MeshInstance3D
            {
                Mesh = new SphereMesh { Radius = 0.25f, Height = 0.46f, RadialSegments = 10, Rings = 6 },
                Position = new Vector3((clump - 1) * 0.25f, 0.48f + (clump % 2) * 0.04f, (clump % 2 == 0 ? -0.08f : 0.08f)),
                Scale = new Vector3(1, 0.8f, 1),
                MaterialOverride = leaves,
            });
        root.AddChild(planter);
    }

    internal static void AddBox(Node3D parent, Vector3 size, Vector3 position, Material material, Vector3? rotation = null)
    {
        parent.AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = size },
            Position = position,
            Rotation = rotation ?? Vector3.Zero,
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
    }

    static void AddCar(Node3D root, World world, Vector2 center, Vector2 tangent, int seed)
    {
        var widthScale = Style.CarLength < 1f ? 0.88f : 1f; // 일본 경차는 짧고 좁다, 미국 차는 길다
        var car = new StaticBody3D
        {
            Name = $"ParkedCar{seed}",
            Position = new Vector3(center.X, world.HeightAt(center.X, center.Y), center.Y),
            Rotation = new Vector3(0, Mathf.Atan2(tangent.X, tangent.Y), 0),
            CollisionLayer = PartBody.LayerWorld,
            CollisionMask = 0,
        };
        car.AddChild(new CollisionShape3D
        {
            Position = new Vector3(0, 0.72f, 0),
            Shape = new BoxShape3D { Size = new Vector3(1.78f * widthScale, 1.35f, 4.25f * Style.CarLength) },
        });
        root.AddChild(car);
        // 물리 몸체는 축척하지 않고 보이는 부분만 늘이거나 줄인다
        var visual = new Node3D { Scale = new Vector3(widthScale, 1f, Style.CarLength) };
        car.AddChild(visual);

        var paint = Look.Painted("metal_plate", 0.55f, (seed % 3) switch
        {
            0 => new Color(0.68f, 0.69f, 0.66f),
            1 => new Color(0.18f, 0.23f, 0.28f),
            _ => new Color(0.72f, 0.71f, 0.66f),
        });
        paint.Metallic = 0.22f;
        paint.Roughness = 0.48f;
        paint.NormalScale = 0.045f;
        // 옆모습 단면을 밀어 낸 차체: 보닛 → 경사진 앞유리 → 지붕 → 뒷유리 → 트렁크
        visual.AddChild(StreetDetail.Extrude(
        [
            new(2.12f, 0.3f), new(2.16f, 0.58f), new(2.06f, 0.76f), new(1.55f, 0.86f), new(0.98f, 0.93f),
            new(-1.58f, 0.96f), new(-2.02f, 0.93f), new(-2.15f, 0.72f), new(-2.12f, 0.3f),
        ], 1.76f, paint));
        visual.AddChild(StreetDetail.Extrude(
        [
            new(0.98f, 0.92f), new(0.2f, 1.38f), new(-0.05f, 1.44f), new(-1.1f, 1.44f), new(-1.3f, 1.38f), new(-1.62f, 0.95f),
        ], 1.5f, paint));

        var glass = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.12f, 0.15f, 0.17f),
            Metallic = 0.1f,
            Roughness = 0.06f,
        };
        visual.AddChild(StreetDetail.SlopePanel(new Vector2(0.98f, 0.92f), new Vector2(0.2f, 1.38f), 1.36f, glass));
        visual.AddChild(StreetDetail.SlopePanel(new Vector2(-1.62f, 0.95f), new Vector2(-1.3f, 1.38f), 1.3f, glass));
        // 옆 창: 기둥(A·B·C)을 남기고 단면보다 조금 바깥에
        foreach (var x in new[] { -0.755f, 0.755f })
        {
            visual.AddChild(StreetDetail.Extrude([new(0.82f, 0.99f), new(0.2f, 1.35f), new(-0.42f, 1.36f), new(-0.42f, 0.99f)], 0.012f, glass, x));
            visual.AddChild(StreetDetail.Extrude([new(-0.52f, 0.99f), new(-0.52f, 1.36f), new(-1.08f, 1.36f), new(-1.42f, 0.99f)], 0.012f, glass, x));
        }
        // 바퀴 집: 바퀴 둘레의 어두운 테
        var arch = new StandardMaterial3D { AlbedoColor = new Color(0.05f, 0.05f, 0.05f), Roughness = 0.9f };
        foreach (var x in new[] { -0.885f, 0.885f })
        foreach (var z in new[] { -1.27f, 1.27f })
            visual.AddChild(new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = 0.41f, BottomRadius = 0.41f, Height = 0.01f, RadialSegments = 18 },
                Position = new Vector3(x, 0.38f, z),
                Rotation = new Vector3(0, 0, Mathf.Pi / 2),
                MaterialOverride = arch,
            });

        var bumper = new StandardMaterial3D { AlbedoColor = new Color(0.19f, 0.2f, 0.2f), Roughness = 0.72f };
        AddBox(visual, new Vector3(1.72f, 0.16f, 0.12f), new Vector3(0, 0.48f, 2.12f), bumper);
        AddBox(visual, new Vector3(1.72f, 0.16f, 0.12f), new Vector3(0, 0.48f, -2.12f), bumper);
        AddBox(visual, new Vector3(0.78f, 0.18f, 0.045f), new Vector3(0, 0.69f, 2.15f), bumper);
        AddBox(visual, new Vector3(0.58f, 0.2f, 0.035f), new Vector3(0, 0.69f, 2.205f), bumper);
        var plate = new StandardMaterial3D { AlbedoColor = new Color(0.82f, 0.83f, 0.78f), Roughness = 0.72f };
        AddBox(visual, new Vector3(0.44f, 0.12f, 0.025f), new Vector3(0, 0.69f, 2.23f), plate);
        AddBox(visual, new Vector3(0.44f, 0.12f, 0.025f), new Vector3(0, 0.69f, -2.19f), plate);
        for (var slat = 0; slat < 4; slat++)
            AddBox(visual, new Vector3(0.48f - slat * 0.035f, 0.018f, 0.02f),
                new Vector3(0, 0.64f + slat * 0.035f, 2.226f), bumper);
        var trim = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.46f, 0.47f, 0.45f),
            Metallic = 0.68f,
            Roughness = 0.42f,
        };
        for (var x = -1; x <= 1; x += 2)
        {
            AddBox(visual, new Vector3(0.025f, 0.035f, 0.16f), new Vector3(x * 0.885f, 0.86f, -0.1f), trim);
            AddBox(visual, new Vector3(0.025f, 0.035f, 0.16f), new Vector3(x * 0.885f, 0.86f, 0.5f), trim);
            AddBox(visual, new Vector3(0.2f, 0.11f, 0.06f), new Vector3(x * 0.96f, 1.02f, 0.82f), paint);
            AddBox(visual, new Vector3(0.05f, 0.04f, 0.05f), new Vector3(x * 0.87f, 0.98f, 0.84f), trim);
        }
        var headlamp = new StandardMaterial3D { AlbedoColor = new Color(0.88f, 0.82f, 0.65f), Roughness = 0.3f };
        var taillamp = new StandardMaterial3D { AlbedoColor = new Color(0.48f, 0.08f, 0.05f), Roughness = 0.35f };
        for (var x = -1; x <= 1; x += 2)
        {
            AddBox(visual, new Vector3(0.32f, 0.14f, 0.04f), new Vector3(x * 0.58f, 0.73f, 2.14f), headlamp);
            AddBox(visual, new Vector3(0.26f, 0.18f, 0.04f), new Vector3(x * 0.61f, 0.73f, -2.14f), taillamp);
        }

        var rubber = new StandardMaterial3D { AlbedoColor = new Color(0.035f, 0.04f, 0.04f), Roughness = 0.88f };
        var hub = new StandardMaterial3D { AlbedoColor = new Color(0.48f, 0.49f, 0.47f), Metallic = 0.72f, Roughness = 0.34f };
        for (var x = -1; x <= 1; x += 2)
        for (var z = -1; z <= 1; z += 2)
        {
            var wheel = new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = 0.34f, BottomRadius = 0.34f, Height = 0.22f, RadialSegments = 16 },
                Position = new Vector3(x * 0.88f, 0.38f, z * 1.27f),
                Rotation = new Vector3(0, 0, Mathf.Pi / 2),
                MaterialOverride = rubber,
            };
            visual.AddChild(wheel);
            visual.AddChild(new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = 0.16f, BottomRadius = 0.16f, Height = 0.045f, RadialSegments = 16 },
                Position = new Vector3(x * 0.99f, 0.38f, z * 1.27f),
                Rotation = new Vector3(0, 0, Mathf.Pi / 2),
                MaterialOverride = hub,
            });
        }
    }
}
