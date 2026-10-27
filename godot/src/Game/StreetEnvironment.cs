using Godot;

namespace DollyForge.Game;

/// <summary>기존 작업장 동선을 따라 만든 한국형 저층 상가 골목.</summary>
internal static class StreetEnvironment
{
    static readonly Color[] FacadeColors =
    [
        new(0.78f, 0.74f, 0.67f),
        new(0.68f, 0.69f, 0.66f),
        new(0.72f, 0.63f, 0.55f),
        new(0.66f, 0.70f, 0.69f),
    ];

    public static void Build(Node3D parent, World world, IReadOnlyList<Vector2> path, Material sidewalk)
    {
        var root = new Node3D { Name = "StreetEnvironment" };
        parent.AddChild(root);

        var asphalt = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.26f, 0.27f, 0.28f),
            Roughness = 0.96f,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        var concrete = Look.Painted("painted_concrete", 2f, new Color(0.55f, 0.54f, 0.51f));
        var paint = new StandardMaterial3D { AlbedoColor = new Color(0.82f, 0.81f, 0.75f), Roughness = 0.9f };
        BuildSurface(root, world, path, -3.2f, 3.2f, 0.18f, asphalt);
        BuildSurface(root, world, path, -4.9f, -3.2f, 0.18f, sidewalk);
        BuildSurface(root, world, path, 3.2f, 4.9f, 0.18f, sidewalk);
        BuildSurface(root, world, path, -3.38f, -3.18f, 0.25f, concrete);
        BuildSurface(root, world, path, 3.18f, 3.38f, 0.25f, concrete);
        AddRoadMarkings(root, world, path, paint);
        BuildFrontage(root, world, path);
        BuildStreetLights(root, world, path);
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
                st.AddVertex(new Vector3(p.X, world.HeightAt(p.X, p.Y) + lift, p.Y));
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

    static Vector2 Tangent(IReadOnlyList<Vector2> path, int i)
    {
        var before = path[Math.Max(i - 1, 0)];
        var after = path[Math.Min(i + 1, path.Count - 1)];
        return (after - before).Normalized();
    }

    static void AddRoadMarkings(Node3D root, World world, IReadOnlyList<Vector2> path, Material paint)
    {
        var walked = 0f;
        var nextDash = 5f;
        for (var i = 1; i < path.Count; i++)
        {
            walked += path[i].DistanceTo(path[i - 1]);
            if (walked < nextDash) continue;
            nextDash += 5f;
            var tangent = Tangent(path, i);
            var position = path[i];
            root.AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(0.1f, 0.012f, 1.6f) },
                MaterialOverride = paint,
                Position = new Vector3(position.X, world.HeightAt(position.X, position.Y) + 0.2f, position.Y),
                Rotation = new Vector3(0, Mathf.Atan2(tangent.X, tangent.Y), 0),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            });
        }
    }

    static void BuildFrontage(Node3D root, World world, IReadOnlyList<Vector2> path)
    {
        var walked = 0f;
        var nextBuilding = 9.5f;
        var slot = 0;
        for (var i = 1; i < path.Count - 6; i++)
        {
            walked += path[i].DistanceTo(path[i - 1]);
            if (walked < nextBuilding) continue;
            nextBuilding += 8.5f;
            var tangent = Tangent(path, i);
            var normal = new Vector2(-tangent.Y, tangent.X);
            var side = slot % 2 == 0 ? 1f : -1f;
            var near = path[i] + normal * side * 7.8f;
            AddBuilding(root, world, near, tangent, side, slot + 1);
            if (slot % 2 == 1)
                AddCar(root, world, path[i] + normal * side * 3.8f, tangent, slot + 10);
            if (slot % 3 == 0)
                AddPlanter(root, world, path[i] + normal * side * 5.4f, tangent, side == 1f ? 1 : -1, slot);
            slot++;
        }
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
        for (var i = 12; i < path.Count - 8; i += 26)
        {
            var tangent = Tangent(path, i);
            var normal = new Vector2(-tangent.Y, tangent.X);
            var side = i % 2 == 0 ? 1f : -1f;
            var at = path[i] + normal * side * 5.2f;
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

    static void AddBuilding(Node3D root, World world, Vector2 center, Vector2 tangent, float side, int seed)
    {
        var width = 6.2f + seed % 3 * 0.45f;
        var depth = 4.6f + seed % 2 * 0.5f;
        var floors = 2 + seed % 2;
        var floorHeight = 2.9f;
        var height = floors * floorHeight + 0.35f;
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
        building.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(width, height, depth) } });
        root.AddChild(building);

        var plaster = Look.Painted("white_plaster_02", 3f, FacadeColors[seed % FacadeColors.Length]);
        var body = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(width, height, depth) }, MaterialOverride = plaster };
        building.AddChild(body);

        var frontZ = depth / 2f + 0.045f;
        var glass = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.28f, 0.32f, 0.34f),
            Metallic = 0.08f,
            Roughness = 0.28f,
        };
        var frame = new StandardMaterial3D { AlbedoColor = new Color(0.72f, 0.70f, 0.64f), Roughness = 0.75f };
        var awning = new StandardMaterial3D { AlbedoColor = new Color(0.43f, 0.24f, 0.18f), Roughness = 0.82f };
        for (var floor = 0; floor < floors; floor++)
        {
            var y = -height / 2f + floorHeight * (floor + 0.5f);
            for (var col = 0; col < 3; col++)
            {
                var x = (col - 1) * width * 0.29f;
                var windowWidth = col == 1 ? 1.2f : 1.0f;
                AddBox(building, new Vector3(windowWidth, 1.25f, 0.09f), new Vector3(x, y, frontZ), glass);
                AddBox(building, new Vector3(windowWidth + 0.12f, 0.07f, 0.12f), new Vector3(x, y + 0.66f, frontZ + 0.045f), frame);
                AddBox(building, new Vector3(0.07f, 1.32f, 0.12f), new Vector3(x - windowWidth / 2f, y, frontZ + 0.045f), frame);
                AddBox(building, new Vector3(0.07f, 1.32f, 0.12f), new Vector3(x + windowWidth / 2f, y, frontZ + 0.045f), frame);
            }
        }

        var storefront = new StandardMaterial3D { AlbedoColor = new Color(0.24f, 0.27f, 0.27f), Roughness = 0.55f };
        AddBox(building, new Vector3(1.15f, 2.0f, 0.1f), new Vector3(-width * 0.27f, -height / 2f + 1.25f, frontZ + 0.02f), storefront);
        AddBox(building, new Vector3(width * 0.6f, 0.38f, 0.42f), new Vector3(0, -height / 2f + 2.7f, frontZ + 0.16f), frame);
        AddBox(building, new Vector3(width + 0.15f, 0.18f, depth + 0.15f), new Vector3(0, height / 2f + 0.08f, 0), frame);
        AddBox(building, new Vector3(width * 0.82f, 0.16f, 0.72f), new Vector3(0, -height / 2f + 1.8f, frontZ + 0.2f), awning);

        var ac = Look.Scanned("metal_plate", 0.35f, new Color(0.67f, 0.68f, 0.66f), 0.5f);
        for (var col = 0; col < 2; col++)
            AddBox(building, new Vector3(0.72f, 0.42f, 0.38f), new Vector3((col == 0 ? -1 : 1) * width * 0.26f, -height / 2f + 1.98f, depth / 2f + 0.24f), ac);

        var sign = Look.Tag(seed % 2 == 0 ? "동네 상점" : "생활 수리", 0.0015f, Look.Stamp);
        sign.Position = new Vector3(0, -height / 2f + 2.75f, frontZ + 0.38f);
        sign.Modulate = new Color(0.14f, 0.16f, 0.16f);
        building.AddChild(sign);

        AddBox(building, new Vector3(width * 0.82f, height * 0.72f, 0.12f), new Vector3(0, -0.12f, -depth / 2f - 0.08f), new StandardMaterial3D { AlbedoColor = new Color(0.86f, 0.84f, 0.77f), Roughness = 0.88f });
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
        var soil = new StandardMaterial3D { AlbedoColor = new Color(0.32f, 0.28f, 0.22f), Roughness = 0.92f };
        var leaves = new StandardMaterial3D { AlbedoColor = new Color(0.28f, 0.45f, 0.26f), Roughness = 0.9f };
        planter.AddChild(new MeshInstance3D { Mesh = box, Position = new Vector3(0, 0.1f, 0), MaterialOverride = soil });
        planter.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.54f, 0.48f, 0.54f) }, Position = new Vector3(direction * 0.12f, 0.42f, 0.04f), MaterialOverride = leaves });
        root.AddChild(planter);
    }

    static void AddBox(Node3D parent, Vector3 size, Vector3 position, Material material)
    {
        parent.AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = size },
            Position = position,
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
    }

    static void AddCar(Node3D root, World world, Vector2 center, Vector2 tangent, int seed)
    {
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
            Shape = new BoxShape3D { Size = new Vector3(1.78f, 1.35f, 4.25f) },
        });
        root.AddChild(car);

        var paint = new StandardMaterial3D
        {
            AlbedoColor = (seed % 3) switch
            {
                0 => new Color(0.68f, 0.69f, 0.66f),
                1 => new Color(0.18f, 0.23f, 0.28f),
                _ => new Color(0.72f, 0.71f, 0.66f),
            },
            Metallic = 0.18f,
            Roughness = 0.42f,
        };
        AddBox(car, new Vector3(1.78f, 0.62f, 4.25f), new Vector3(0, 0.66f, 0), paint);
        AddBox(car, new Vector3(1.42f, 0.58f, 1.85f), new Vector3(0, 1.23f, -0.12f),
            new StandardMaterial3D { AlbedoColor = new Color(0.24f, 0.29f, 0.31f), Metallic = 0.06f, Roughness = 0.3f });

        var rubber = new StandardMaterial3D { AlbedoColor = new Color(0.035f, 0.04f, 0.04f), Roughness = 0.88f };
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
            car.AddChild(wheel);
        }
    }
}
