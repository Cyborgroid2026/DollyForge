using Godot;

namespace DollyForge.Game;

/// <summary>
/// 자연미: 통제된 계산이 아니라 예측할 수 없는 무작위성과 불완전함으로 생명력을 만든다.
///  · 풀 — 잎마다 높이·폭·기울기·휨·색이 다르고, 노이즈가 정한 군락으로 자란다. 일부는 마른 잎.
///  · 바람 — 돌풍 세기가 시간에 따라 노이즈로 변해 같은 주기가 되풀이되지 않는다. 풀·가지가 함께 흔들린다.
///  · 들꽃 — 풀숲 사이에 불규칙하게, 꽃잎 수·모양도 송이마다 조금씩 다르다.
///  · 연못 — 깊이에 따라 짙어지는 물, 굴절과 반사, 서로 다른 방향의 물결, 물가의 갈대.
///  · 공기 — 햇빛 속을 떠다니는 꽃가루와 먼지.
/// </summary>
public partial class Nature : Node3D
{
    public World World { get; set; } = null!;
    readonly RandomNumberGenerator _rng = new() { Seed = 4242 };
    readonly FastNoiseLite _gust = new() { Seed = 99, Frequency = 0.07f, FractalOctaves = 3 };
    readonly FastNoiseLite _edgeNoise = new() { Seed = 73, Frequency = 0.018f, FractalOctaves = 2 };
    GpuParticles3D? _pollen;
    float _time;

    const float Extent = 78f, Cell = 16f;

    public override void _Ready()
    {
        BuildGrass();
        BuildFlowers();
        BuildPond();
        BuildPollen();
    }

    bool OpenGround(float x, float z, out float h)
    {
        h = World.HeightAt(x, z);
        if (!Site.Clear(x, z)) return false; // 작업장·마당·정류장·선로·오솔길·연못
        var slope = new Vector2(World.HeightAt(x + 0.5f, z) - World.HeightAt(x - 0.5f, z), World.HeightAt(x, z + 0.5f) - World.HeightAt(x, z - 0.5f)).Length();
        return slope < 0.7f; // 가파른 비탈엔 풀이 덜 붙는다
    }

    float EdgeDensity(float x, float z)
    {
        var radius = new Vector2(x, z).Length();
        var fadeStart = 51f + _edgeNoise.GetNoise2D(x, z) * 7f;
        return 1f - Mathf.SmoothStep(fadeStart, Extent, radius);
    }

    // ------------------------------------------------------------------ 풀

    /// <summary>풀 한 포기: 잎 10~14장, 잎마다 다른 높이·폭·기울기·휨·색 (일부는 마른 잎)</summary>
    ArrayMesh Clump(float scale = 1)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        var blades = _rng.RandiRange(16, 24);
        var v = 0;
        for (var b = 0; b < blades; b++)
        {
            var r = Mathf.Sqrt(_rng.Randf()) * 0.24f;
            var a = _rng.RandfRange(0, Mathf.Tau);
            var root = new Vector3(Mathf.Cos(a) * r, 0, Mathf.Sin(a) * r);
            var height = _rng.RandfRange(0.3f, 0.8f) * scale;
            var width = _rng.RandfRange(0.014f, 0.03f);
            var lean = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * _rng.RandfRange(0.05f, 0.45f) + new Vector3(_rng.RandfRange(-0.1f, 0.1f), 0, _rng.RandfRange(-0.1f, 0.1f));
            var face = _rng.RandfRange(0, Mathf.Tau);
            var side = new Vector3(Mathf.Cos(face), 0, Mathf.Sin(face));
            var dry = _rng.Randf() < 0.08f;
            var tint = dry ? new Color(1.35f, 1.15f, 0.55f) : new Color(_rng.RandfRange(0.85f, 1.1f), _rng.RandfRange(0.9f, 1.15f), _rng.RandfRange(0.8f, 1.05f));
            var normal = (Vector3.Up * 0.6f + side.Cross(Vector3.Up).Normalized() * 0.4f).Normalized();
            const int segs = 4;
            for (var k = 0; k <= segs; k++)
            {
                var t = (float)k / segs;
                var center = root + Vector3.Up * height * t + lean * height * t * t;
                var half = width * (1 - t * 0.92f) / 2;
                foreach (var s in new[] { -1, 1 })
                {
                    st.SetColor(tint);
                    st.SetUV(new Vector2(s < 0 ? 0 : 1, t));
                    st.SetNormal(normal);
                    st.AddVertex(center + side * half * s);
                }
            }
            for (var k = 0; k < segs; k++)
            {
                int i0 = v + k * 2, i1 = i0 + 1, i2 = i0 + 2, i3 = i0 + 3;
                st.AddIndex(i0); st.AddIndex(i2); st.AddIndex(i1);
                st.AddIndex(i1); st.AddIndex(i2); st.AddIndex(i3);
            }
            v += (segs + 1) * 2;
        }
        return st.Commit();
    }

    static NoiseTexture2D WindNoise(int seed) =>
        new() { Width = 256, Height = 256, Seamless = true, Noise = new FastNoiseLite { Seed = seed, Frequency = 0.02f } };

    void BuildGrass()
    {
        var mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/grass.gdshader") };
        mat.SetShaderParameter("wind_noise", WindNoise(3));
        var variants = new[] { Clump(), Clump(), Clump(), Clump() };
        var patch = new FastNoiseLite { Seed = 17, Frequency = 0.035f, FractalOctaves = 3 };
        var total = 0;
        for (var cx = -Extent; cx < Extent; cx += Cell)
        for (var cz = -Extent; cz < Extent; cz += Cell)
        {
            var perVariant = variants.Select(_ => new List<Transform3D>()).ToArray();
            for (var x = cx; x < cx + Cell; x += 0.5f)
            for (var z = cz; z < cz + Cell; z += 0.5f)
            {
                var px = x + _rng.RandfRange(-0.45f, 0.45f);
                var pz = z + _rng.RandfRange(-0.45f, 0.45f);
                if (!OpenGround(px, pz, out _)) continue;
                if (_rng.Randf() > EdgeDensity(px, pz)) continue;
                if (patch.GetNoise2D(px, pz) < -0.05f + _rng.Randf() * 0.2f) continue; // 군락 사이 빈 땅
                var h = World.HeightAt(px, pz);
                var s = _rng.RandfRange(0.7f, 1.35f) * (0.85f + patch.GetNoise2D(px * 3, pz * 3) * 0.3f);
                var basis = new Basis(Vector3.Up, _rng.RandfRange(0, Mathf.Tau)).Scaled(new Vector3(s, s * _rng.RandfRange(0.8f, 1.25f), s));
                perVariant[_rng.RandiRange(0, variants.Length - 1)].Add(new Transform3D(basis, new Vector3(px, h - 0.01f, pz)));
            }
            for (var i = 0; i < variants.Length; i++)
            {
                if (perVariant[i].Count == 0) continue;
                var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = variants[i], InstanceCount = perVariant[i].Count };
                for (var k = 0; k < perVariant[i].Count; k++) mm.SetInstanceTransform(k, perVariant[i][k]);
                total += perVariant[i].Count;
                AddChild(new MultiMeshInstance3D
                {
                    Multimesh = mm, MaterialOverride = mat,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                    VisibilityRangeEnd = 75, VisibilityRangeEndMargin = 10,
                    VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Self,
                });
            }
        }
        GD.Print($"[nature] 풀 {total} 포기");
    }

    // ------------------------------------------------------------------ 들꽃

    /// <summary>꽃잎 그림: 꽃잎 수·기울기가 송이 종류마다 다르고, 가운데는 노란 꽃밥</summary>
    Texture2D FlowerTexture(Color petal)
    {
        const int size = 64;
        var img = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        var petals = _rng.RandiRange(5, 7);
        var tilt = _rng.Randf() * Mathf.Tau;
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var p = new Vector2(x - size / 2f + 0.5f, y - size / 2f + 0.5f) / (size / 2f);
            var r = p.Length();
            var a = Mathf.Atan2(p.Y, p.X) + tilt;
            var edge = 0.55f + 0.4f * Mathf.Abs(Mathf.Cos(a * petals / 2f));
            var c = new Color(0, 0, 0, 0);
            if (r < 0.2f) c = new Color(0.95f, 0.75f, 0.15f);
            else if (r < edge) c = petal.Lerp(Colors.White, (1 - r) * 0.35f);
            img.SetPixel(x, y, c);
        }
        img.GenerateMipmaps();
        return ImageTexture.CreateFromImage(img);
    }

    void BuildFlowers()
    {
        var colors = new[] { new Color(0.95f, 0.95f, 0.92f), new Color(0.98f, 0.82f, 0.2f), new Color(0.62f, 0.45f, 0.85f) };
        var meadow = new FastNoiseLite { Seed = 31, Frequency = 0.05f };
        foreach (var color in colors)
        {
            var mat = new StandardMaterial3D
            {
                AlbedoTexture = FlowerTexture(color), Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor, AlphaScissorThreshold = 0.5f,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled, Roughness = 0.7f, BillboardMode = BaseMaterial3D.BillboardModeEnum.FixedY,
                BillboardKeepScale = true, BacklightEnabled = true, Backlight = color * 0.4f,
            };
            var spots = new List<Transform3D>();
            for (var i = 0; i < 2200 && spots.Count < 700; i++)
            {
                var x = _rng.RandfRange(-Extent, Extent);
                var z = _rng.RandfRange(-Extent, Extent);
                if (!OpenGround(x, z, out var h) || meadow.GetNoise2D(x, z) < 0.15f) continue;
                if (_rng.Randf() > EdgeDensity(x, z)) continue;
                var s = _rng.RandfRange(0.05f, 0.09f);
                spots.Add(new Transform3D(Basis.Identity.Scaled(Vector3.One * s), new Vector3(x, h + _rng.RandfRange(0.18f, 0.42f), z)));
            }
            var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = new QuadMesh { Size = Vector2.One }, InstanceCount = spots.Count };
            for (var i = 0; i < spots.Count; i++) mm.SetInstanceTransform(i, spots[i]);
            AddChild(new MultiMeshInstance3D { Multimesh = mm, MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, VisibilityRangeEnd = 60 });
        }
    }

    // ------------------------------------------------------------------ 연못

    void BuildPond()
    {
        // 수면 높이: 연못 둘레 지형의 가장 낮은 곳보다 조금 아래 (물이 넘치지 않게)
        var c = World.PondCenter;
        var rim = float.MaxValue;
        for (var a = 0f; a < Mathf.Tau; a += 0.1f)
            rim = Mathf.Min(rim, World.HeightAt(c.X + Mathf.Cos(a) * (World.PondRadius + 2.5f), c.Y + Mathf.Sin(a) * (World.PondRadius + 2.5f)));
        var level = rim - 0.25f;
        var mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/water.gdshader") };
        NoiseTexture2D Ripple(int seed, float freq) => new()
        {
            Width = 512, Height = 512, Seamless = true, AsNormalMap = true, BumpStrength = 6,
            Noise = new FastNoiseLite { Seed = seed, Frequency = freq, FractalOctaves = 3 },
        };
        mat.SetShaderParameter("ripple_a", Ripple(51, 0.03f));
        mat.SetShaderParameter("ripple_b", Ripple(52, 0.06f));
        AddChild(new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = Vector2.One * (World.PondRadius * 2 + 8), SubdivideWidth = 8, SubdivideDepth = 8 },
            MaterialOverride = mat, Position = new Vector3(c.X, level, c.Y), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
        // 물가의 갈대 무더기 (불규칙한 경계)
        var reeds = new List<Transform3D>();
        for (var i = 0; i < 260; i++)
        {
            var a = _rng.RandfRange(0, Mathf.Tau);
            var r = World.PondRadius + _rng.RandfRange(-0.5f, 2.5f);
            var x = c.X + Mathf.Cos(a) * r;
            var z = c.Y + Mathf.Sin(a) * r;
            var h = World.HeightAt(x, z);
            if (h < level - 0.3f) continue;
            var s = _rng.RandfRange(0.8f, 1.3f);
            reeds.Add(new Transform3D(new Basis(Vector3.Up, _rng.RandfRange(0, Mathf.Tau)).Scaled(new Vector3(s, s, s)), new Vector3(x, Mathf.Max(h, level) - 0.02f, z)));
        }
        var reedMat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/grass.gdshader") };
        reedMat.SetShaderParameter("wind_noise", WindNoise(8));
        reedMat.SetShaderParameter("root_color", new Color(0.18f, 0.2f, 0.08f));
        reedMat.SetShaderParameter("tip_color", new Color(0.62f, 0.58f, 0.32f));
        var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = Clump(2.6f), InstanceCount = reeds.Count };
        for (var i = 0; i < reeds.Count; i++) mm.SetInstanceTransform(i, reeds[i]);
        AddChild(new MultiMeshInstance3D { Multimesh = mm, MaterialOverride = reedMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.On });
    }

    // ------------------------------------------------------------------ 공기

    void BuildPollen()
    {
        var process = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
            EmissionBoxExtents = new Vector3(14, 4, 14),
            Gravity = new Vector3(0, -0.015f, 0),
            InitialVelocityMin = 0.02f,
            InitialVelocityMax = 0.12f,
            Spread = 180,
            TurbulenceEnabled = true,
            TurbulenceNoiseStrength = 0.6f,
            TurbulenceNoiseScale = 6,
            ScaleMin = 0.5f,
            ScaleMax = 1.4f,
        };
        var dot = new QuadMesh { Size = Vector2.One * 0.01f };
        dot.Material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
            AlbedoColor = new Color(1f, 0.97f, 0.85f, 0.6f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        };
        _pollen = new GpuParticles3D
        {
            Amount = 700, Lifetime = 14, Preprocess = 14, ProcessMaterial = process, DrawPass1 = dot,
            VisibilityAabb = new Aabb(new Vector3(-20, -6, -20), new Vector3(40, 12, 40)), LocalCoords = false,
        };
        AddChild(_pollen);
    }

    public override void _Process(double delta)
    {
        _time += (float)delta;
        // 돌풍: 노이즈로 변하는 세기 (0.15–0.95), 같은 주기가 되풀이되지 않는다
        var gust = Mathf.Clamp(0.5f + _gust.GetNoise1D(_time) * 0.8f, 0.15f, 0.95f);
        RenderingServer.GlobalShaderParameterSet("wind_strength", gust);
        var direction = new Vector2(0.9f, 0.35f).Rotated(_gust.GetNoise1D(_time * 0.08f) * 0.22f).Normalized();
        RenderingServer.GlobalShaderParameterSet("wind_direction", direction);
        if (_pollen is not null && GetViewport().GetCamera3D() is { } cam)
            _pollen.GlobalPosition = cam.GlobalPosition;
    }
}
