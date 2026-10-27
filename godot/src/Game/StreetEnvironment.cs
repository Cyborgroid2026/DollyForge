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

    public static void Build(Node3D parent, World world, IReadOnlyList<Vector2> path, Material sidewalk)
    {
        var root = new Node3D { Name = "StreetEnvironment" };
        parent.AddChild(root);

        var asphalt = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/asphalt.gdshader") };
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

    static void AddRoadMarkings(Node3D root, World world, IReadOnlyList<Vector2> path)
    {
        var edgePaint = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.82f, 0.8f, 0.71f),
            Roughness = 0.92f,
        };
        var centerPaint = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.78f, 0.62f, 0.28f),
            Roughness = 0.92f,
        };
        BuildSurface(root, world, path, -2.83f, -2.72f, 0.205f, edgePaint);
        BuildSurface(root, world, path, 2.72f, 2.83f, 0.205f, edgePaint);

        var length = PathLength(path);
        for (var distance = 4f; distance < length - 1f; distance += 8f)
        {
            var (position, tangent) = AtDistance(path, distance);
            root.AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(0.11f, 0.012f, 2.2f) },
                MaterialOverride = centerPaint,
                Position = new Vector3(position.X, world.HeightAt(position.X, position.Y) + 0.205f, position.Y),
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
        for (var distance = 15f; distance < length - 10f; distance += 30f)
        {
            var (position, _) = AtDistance(path, distance);
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
            AddTactilePaving(root, world, path[i], tangent, side);
            if (slot % 2 == 0)
                AddStreetBin(root, world, path[i] + normal * side * 4.25f, tangent, slot);
            if (slot % 2 == 1)
                AddCar(root, world, path[i] + normal * side * 3.8f, tangent, slot + 10);
            if (slot % 3 == 0)
                AddPlanter(root, world, path[i] + normal * side * 5.4f, tangent, side == 1f ? 1 : -1, slot);
            slot++;
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

        var body = Look.Painted("painted_concrete", 0.7f, seed % 2 == 0
            ? new Color(0.27f, 0.37f, 0.32f)
            : new Color(0.28f, 0.34f, 0.4f));
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

    static void BuildUtilityLines(Node3D root, World world, IReadOnlyList<Vector2> path)
    {
        var length = PathLength(path);
        if (length < 32f) return;

        var poleMetal = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.28f, 0.29f, 0.27f),
            Metallic = 0.38f,
            Roughness = 0.72f,
        };
        var ceramic = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.72f, 0.69f, 0.58f),
            Roughness = 0.36f,
        };
        var wire = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.085f, 0.09f, 0.09f),
            Metallic = 0.68f,
            Roughness = 0.46f,
        };

        const float side = 1f;
        const float poleOffset = 6.1f;
        const float crossarmHeight = 6.2f;
        var poleDistances = new[] { 8f, length - 8f };
        var wireEnds = new List<Vector3[]>(poleDistances.Length);
        foreach (var distance in poleDistances)
        {
            var (point, tangent) = AtDistance(path, distance);
            var normal = new Vector2(-tangent.Y, tangent.X) * side;
            var groundPoint = point + normal * poleOffset;
            var ground = world.HeightAt(groundPoint.X, groundPoint.Y);
            var pole = new Node3D
            {
                Name = "StreetUtilityPole",
                Position = new Vector3(groundPoint.X, ground, groundPoint.Y),
                Rotation = new Vector3(0, Mathf.Atan2(tangent.X, tangent.Y), 0),
            };
            root.AddChild(pole);
            pole.AddChild(new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = 0.13f, BottomRadius = 0.2f, Height = 7.1f, RadialSegments = 12 },
                Position = new Vector3(0, 3.55f, 0),
                MaterialOverride = poleMetal,
            });
            pole.AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(1.7f, 0.12f, 0.14f) },
                Position = new Vector3(0, crossarmHeight, 0),
                MaterialOverride = poleMetal,
            });
            var ends = new Vector3[3];
            for (var conductor = 0; conductor < ends.Length; conductor++)
            {
                var x = (conductor - 1) * 0.56f;
                pole.AddChild(new MeshInstance3D
                {
                    Mesh = new CylinderMesh { TopRadius = 0.095f, BottomRadius = 0.11f, Height = 0.22f, RadialSegments = 10 },
                    Position = new Vector3(x, crossarmHeight + 0.17f, 0),
                    MaterialOverride = ceramic,
                });
                pole.AddChild(new MeshInstance3D
                {
                    Mesh = new CylinderMesh { TopRadius = 0.012f, BottomRadius = 0.012f, Height = 0.08f, RadialSegments = 8 },
                    Position = new Vector3(x, crossarmHeight + 0.32f, 0),
                    MaterialOverride = poleMetal,
                });
                ends[conductor] = pole.Position + new Vector3(x, crossarmHeight + 0.36f, 0).Rotated(Vector3.Up, pole.Rotation.Y);
            }
            wireEnds.Add(ends);
        }

        var spanCount = Math.Min(wireEnds.Count - 1, 1);
        var cableMesh = new CylinderMesh { TopRadius = 0.018f, BottomRadius = 0.018f, RadialSegments = 6 };
        for (var span = 0; span < spanCount; span++)
        for (var conductor = 0; conductor < 3; conductor++)
        {
            var from = wireEnds[span][conductor];
            var to = wireEnds[span + 1][conductor];
            const int segments = 18;
            var previous = from;
            for (var segment = 1; segment <= segments; segment++)
            {
                var t = (float)segment / segments;
                var current = from.Lerp(to, t) + Vector3.Down * (2.4f * t * (1f - t));
                var delta = current - previous;
                var cable = new MeshInstance3D
                {
                    Mesh = (CylinderMesh)cableMesh.Duplicate(),
                    Position = (previous + current) * 0.5f,
                    Rotation = new Basis(new Quaternion(Vector3.Up, delta.Normalized())).GetEuler(),
                    MaterialOverride = wire,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                };
                ((CylinderMesh)cable.Mesh).Height = delta.Length();
                root.AddChild(cable);
                previous = current;
            }
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

        var plaster = Look.Scanned("white_plaster_02", 3f, FacadeColors[seed % FacadeColors.Length]);
        var body = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(width, height, depth) }, MaterialOverride = plaster };
        building.AddChild(body);

        var frontZ = depth / 2f + 0.045f;
        var glass = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.28f, 0.32f, 0.34f),
            Metallic = 0.08f,
            Roughness = 0.28f,
        };
        var interior = new StandardMaterial3D { AlbedoColor = new Color(0.18f, 0.2f, 0.2f), Roughness = 0.96f };
        var blind = new StandardMaterial3D { AlbedoColor = new Color(0.48f, 0.45f, 0.38f), Roughness = 0.88f };
        var frame = new StandardMaterial3D { AlbedoColor = new Color(0.72f, 0.70f, 0.64f), Roughness = 0.75f };
        var awning = new StandardMaterial3D { AlbedoColor = new Color(0.43f, 0.24f, 0.18f), Roughness = 0.82f };
        for (var floor = 0; floor < floors; floor++)
        {
            var y = -height / 2f + floorHeight * (floor + 0.5f);
            for (var col = 0; col < 3; col++)
            {
                var x = (col - 1) * width * 0.29f;
                var windowWidth = col == 1 ? 1.2f : 1.0f;
                AddBox(building, new Vector3(windowWidth - 0.12f, 1.12f, 0.035f),
                    new Vector3(x, y, frontZ - 0.055f), interior);
                AddBox(building, new Vector3(windowWidth, 1.25f, 0.09f), new Vector3(x, y, frontZ), glass);
                AddBox(building, new Vector3(windowWidth + 0.12f, 0.07f, 0.12f), new Vector3(x, y + 0.66f, frontZ + 0.045f), frame);
                AddBox(building, new Vector3(0.07f, 1.32f, 0.12f), new Vector3(x - windowWidth / 2f, y, frontZ + 0.045f), frame);
                AddBox(building, new Vector3(0.07f, 1.32f, 0.12f), new Vector3(x + windowWidth / 2f, y, frontZ + 0.045f), frame);
                AddBox(building, new Vector3(windowWidth + 0.12f, 0.065f, 0.16f),
                    new Vector3(x, y - 0.66f, frontZ + 0.045f), frame);
                if ((seed + floor + col) % 3 != 0)
                    for (var slat = 0; slat < 4; slat++)
                        AddBox(building, new Vector3(windowWidth - 0.16f, 0.025f, 0.018f),
                            new Vector3(x, y - 0.28f + slat * 0.12f, frontZ - 0.026f), blind);
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

        var addressPlate = Look.Scanned("metal_plate", 0.24f, new Color(0.23f, 0.25f, 0.25f), 0.56f);
        AddBox(building, new Vector3(0.52f, 0.28f, 0.055f),
            new Vector3(width / 2f - 0.48f, -height / 2f + 2.42f, frontZ + 0.16f), addressPlate);
        var address = Look.Tag($"{seed + 101} 번", 0.0012f, Look.Stamp);
        address.Position = new Vector3(width / 2f - 0.48f, -height / 2f + 2.42f, frontZ + 0.2f);
        address.Modulate = new Color(0.9f, 0.84f, 0.64f);
        building.AddChild(address);

        AddBox(building, new Vector3(width * 0.82f, height * 0.72f, 0.12f), new Vector3(0, -0.12f, -depth / 2f - 0.08f), new StandardMaterial3D { AlbedoColor = new Color(0.86f, 0.84f, 0.77f), Roughness = 0.88f });
        AddFacadeDetails(building, width, depth, height, frontZ, seed, frame, awning);
    }

    static void AddFacadeDetails(Node3D building, float width, float depth, float height, float frontZ, int seed, Material frame, Material awning)
    {
        var pipe = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.34f, 0.36f, 0.35f),
            Metallic = 0.42f,
            Roughness = 0.68f,
        };
        var downpipeHeight = height - 0.55f;
        foreach (var side in new[] { -1f, 1f })
        {
            AddBox(building, new Vector3(0.07f, downpipeHeight, 0.07f),
                new Vector3(side * (width / 2f - 0.18f), -0.04f, frontZ + 0.12f), pipe);
            AddBox(building, new Vector3(0.34f, 0.055f, 0.08f),
                new Vector3(side * (width / 2f - 0.18f), -height / 2f + 0.34f, frontZ + 0.14f), pipe);
        }

        var stripe = new StandardMaterial3D
        {
            AlbedoColor = seed % 2 == 0 ? new Color(0.72f, 0.34f, 0.22f) : new Color(0.81f, 0.77f, 0.65f),
            Roughness = 0.88f,
        };
        for (var panel = 0; panel < 7; panel++)
            AddBox(building, new Vector3(width * 0.82f / 7f, 0.025f, 0.72f),
                new Vector3((panel - 3) * width * 0.82f / 7f, -height / 2f + 1.89f, frontZ + 0.2f),
                panel % 2 == 0 ? awning : stripe);

        var serviceBox = Look.Scanned("metal_plate", 0.55f, new Color(0.48f, 0.49f, 0.46f), 0.52f);
        AddBox(building, new Vector3(0.48f, 0.62f, 0.24f),
            new Vector3(width / 2f - 0.55f, -height / 2f + 0.72f, frontZ + 0.12f), serviceBox);
        AddBox(building, new Vector3(0.32f, 0.035f, 0.27f),
            new Vector3(width / 2f - 0.55f, -height / 2f + 0.72f, frontZ + 0.25f), pipe);
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

    static void AddBox(Node3D parent, Vector3 size, Vector3 position, Material material, Vector3? rotation = null)
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

        var paint = Look.Painted("metal_plate", 0.55f, (seed % 3) switch
        {
            0 => new Color(0.68f, 0.69f, 0.66f),
            1 => new Color(0.18f, 0.23f, 0.28f),
            _ => new Color(0.72f, 0.71f, 0.66f),
        });
        paint.Metallic = 0.22f;
        paint.Roughness = 0.48f;
        paint.NormalScale = 0.045f;
        AddBox(car, new Vector3(1.78f, 0.62f, 4.25f), new Vector3(0, 0.66f, 0), paint);
        AddBox(car, new Vector3(1.68f, 0.12f, 1.05f), new Vector3(0, 1.0f, 1.05f), paint);
        AddBox(car, new Vector3(1.68f, 0.1f, 0.68f), new Vector3(0, 0.99f, -1.48f), paint);
        AddBox(car, new Vector3(1.32f, 0.14f, 1.5f), new Vector3(0, 1.43f, -0.15f), paint);

        var glass = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.25f, 0.31f, 0.34f),
            Metallic = 0.08f,
            Roughness = 0.28f,
        };
        var window = new BoxMesh { Size = new Vector3(1.27f, 0.38f, 0.035f) };
        AddBox(car, new Vector3(0.035f, 0.38f, 0.57f), new Vector3(-0.74f, 1.19f, -0.45f), glass);
        AddBox(car, new Vector3(0.035f, 0.38f, 0.57f), new Vector3(-0.74f, 1.19f, 0.22f), glass);
        AddBox(car, new Vector3(0.035f, 0.38f, 0.57f), new Vector3(0.74f, 1.19f, -0.45f), glass);
        AddBox(car, new Vector3(0.035f, 0.38f, 0.57f), new Vector3(0.74f, 1.19f, 0.22f), glass);
        AddBox(car, window.Size, new Vector3(0, 1.19f, -0.91f), glass);
        AddBox(car, window.Size, new Vector3(0, 1.19f, 0.68f), glass,
            new Vector3(-0.48f, 0, 0));

        var bumper = new StandardMaterial3D { AlbedoColor = new Color(0.19f, 0.2f, 0.2f), Roughness = 0.72f };
        AddBox(car, new Vector3(1.72f, 0.16f, 0.12f), new Vector3(0, 0.48f, 2.12f), bumper);
        AddBox(car, new Vector3(1.72f, 0.16f, 0.12f), new Vector3(0, 0.48f, -2.12f), bumper);
        AddBox(car, new Vector3(0.78f, 0.18f, 0.045f), new Vector3(0, 0.69f, 2.15f), bumper);
        AddBox(car, new Vector3(0.58f, 0.2f, 0.035f), new Vector3(0, 0.69f, 2.205f), bumper);
        var plate = new StandardMaterial3D { AlbedoColor = new Color(0.82f, 0.83f, 0.78f), Roughness = 0.72f };
        AddBox(car, new Vector3(0.44f, 0.12f, 0.025f), new Vector3(0, 0.69f, 2.23f), plate);
        AddBox(car, new Vector3(0.44f, 0.12f, 0.025f), new Vector3(0, 0.69f, -2.19f), plate);
        for (var slat = 0; slat < 4; slat++)
            AddBox(car, new Vector3(0.48f - slat * 0.035f, 0.018f, 0.02f),
                new Vector3(0, 0.64f + slat * 0.035f, 2.226f), bumper);
        var trim = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.46f, 0.47f, 0.45f),
            Metallic = 0.68f,
            Roughness = 0.42f,
        };
        for (var x = -1; x <= 1; x += 2)
        {
            AddBox(car, new Vector3(0.16f, 0.045f, 0.025f), new Vector3(x * 0.52f, 1.0f, -0.02f), trim);
            AddBox(car, new Vector3(0.16f, 0.045f, 0.025f), new Vector3(x * 0.52f, 1.0f, 0.48f), trim);
            AddBox(car, new Vector3(0.18f, 0.07f, 0.13f), new Vector3(x * 0.82f, 1.28f, 0.62f), trim);
            AddBox(car, new Vector3(0.08f, 0.06f, 0.16f), new Vector3(x * 0.88f, 1.2f, 0.62f), paint);
        }
        var headlamp = new StandardMaterial3D { AlbedoColor = new Color(0.88f, 0.82f, 0.65f), Roughness = 0.3f };
        var taillamp = new StandardMaterial3D { AlbedoColor = new Color(0.48f, 0.08f, 0.05f), Roughness = 0.35f };
        for (var x = -1; x <= 1; x += 2)
        {
            AddBox(car, new Vector3(0.32f, 0.14f, 0.04f), new Vector3(x * 0.58f, 0.73f, 2.14f), headlamp);
            AddBox(car, new Vector3(0.26f, 0.18f, 0.04f), new Vector3(x * 0.61f, 0.73f, -2.14f), taillamp);
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
            car.AddChild(wheel);
            car.AddChild(new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = 0.16f, BottomRadius = 0.16f, Height = 0.045f, RadialSegments = 16 },
                Position = new Vector3(x * 0.99f, 0.38f, z * 1.27f),
                Rotation = new Vector3(0, 0, Mathf.Pi / 2),
                MaterialOverride = hub,
            });
        }
    }
}
