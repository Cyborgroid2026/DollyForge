using Godot;

namespace DollyForge.Game;

/// <summary>
/// 지평선의 도시와 산: 실제 자연·도시 사진(위키미디어 공용 품질 사진)에서 되풀이되는 모습을 따른다.
///  · 도시는 높이·폭이 제각각인 직육면체 건물 숲이다. 도심은 높은 유리·콘크리트 빌딩, 바깥으로 갈수록 낮아지고
///    외곽은 경사 지붕 집. 격자 사이 빈칸은 거리와 공원(나무)이 된다. 건물마다 외벽색이 조금씩 다르다.
///  · 지평선은 평평하지 않다: 숲 덮인 언덕 능선과 그 뒤의 산이 겹겹이 푸른 안개 속으로 물러난다.
///  · 도시 앞 들판에는 늑골 뼈대의 유리 온실 군락 (식물원).
/// 좌표: -Z 가 북쪽(도시), 시작 지점은 원점 근처.
/// </summary>
public partial class FutureCity
{
    static readonly StandardMaterial3D Glass = new()
    {
        AlbedoColor = new Color(0.82f, 0.94f, 0.9f, 0.16f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        Roughness = 0.04f, Metallic = 0.2f, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };
    static readonly StandardMaterial3D Leafy = new() { AlbedoColor = new Color(0.24f, 0.42f, 0.18f), Roughness = 0.9f };
    static readonly StandardMaterial3D Concrete = new() { AlbedoColor = new Color(0.62f, 0.62f, 0.6f), Roughness = 0.9f };

    static MeshInstance3D Put(Node3D parent, Mesh mesh, Material mat, Vector3 at, Vector3? rotation = null)
    {
        var m = new MeshInstance3D { Mesh = mesh, MaterialOverride = mat, Position = at, Rotation = rotation ?? Vector3.Zero };
        parent.AddChild(m);
        return m;
    }

    /// <summary>a 에서 b 까지 가는 막대 (온실 사이 유리 통로)</summary>
    static void Rod(Node3D parent, Vector3 a, Vector3 b, float radius, Material mat)
    {
        var d = b - a;
        var basis = d.Normalized().Dot(Vector3.Up) > 0.999f ? Basis.Identity : new Basis(new Quaternion(Vector3.Up, d.Normalized()));
        parent.AddChild(new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = radius, BottomRadius = radius, Height = d.Length(), RadialSegments = 12 },
            MaterialOverride = mat,
            Transform = new Transform3D(basis, (a + b) / 2),
        });
    }

    // ------------------------------------------------------------------ 도시

    enum FacadeKind { Curtain, Concrete, Brick, Plaster }

    /// <summary>
    /// 외벽 한 칸(폭 3 m × 층고 3.6 m)을 64×64 텍스처로 그린다. 월드 기준 삼면 투영이라 어떤 건물에도 같은 층 높이로 입혀지고,
    /// 건물마다의 색 편차는 인스턴스 색이 곱해진다.
    /// </summary>
    StandardMaterial3D FacadeMaterial(FacadeKind kind)
    {
        const int n = 64;
        var img = Image.CreateEmpty(n, n, true, Image.Format.Rgb8);
        var dark = new Color(0.12f, 0.15f, 0.18f);
        for (var y = 0; y < n; y++)
        for (var x = 0; x < n; x++)
        {
            Color c;
            switch (kind)
            {
                case FacadeKind.Curtain: // 통유리: 가는 멀리언, 층마다 어두운 스팬드럴 띠
                    var mullion = x % 32 < 2 || y < 2;
                    var spandrel = y >= 2 && y < 12;
                    c = mullion ? new Color(0.55f, 0.57f, 0.6f) : spandrel ? new Color(0.2f, 0.23f, 0.26f) : new Color(0.32f, 0.4f, 0.47f);
                    break;
                default: // 벽에 뚫린 창: 창 둘레 문틀, 유리는 반사 편차
                    var wall = kind switch
                    {
                        FacadeKind.Concrete => new Color(0.64f, 0.62f, 0.58f),
                        FacadeKind.Brick => new Color(0.55f, 0.3f, 0.22f),
                        _ => new Color(0.84f, 0.77f, 0.64f), // 황토·베이지 회벽
                    };
                    var inWindow = x >= 14 && x < 50 && y >= 18 && y < 54;
                    var frame = x >= 12 && x < 52 && y >= 16 && y < 56 && !inWindow;
                    c = inWindow ? dark * (0.85f + (x >= 32 ? 0.25f : 0f)) : frame ? wall.Darkened(0.25f) : wall * (0.94f + _rng.Randf() * 0.06f);
                    if (kind == FacadeKind.Brick && !inWindow && !frame && (y % 6 == 0 || (x + (y / 6 % 2) * 6) % 12 == 0)) c = c.Darkened(0.2f); // 줄눈
                    break;
            }
            img.SetPixel(x, y, c);
        }
        img.GenerateMipmaps();
        return new StandardMaterial3D
        {
            AlbedoTexture = ImageTexture.CreateFromImage(img),
            VertexColorUseAsAlbedo = true,
            Metallic = kind == FacadeKind.Curtain ? 0.55f : 0.0f,
            Roughness = kind == FacadeKind.Curtain ? 0.12f : 0.85f,
            Uv1Triplanar = true, Uv1WorldTriplanar = true, Uv1Scale = new Vector3(1 / 3f, 1 / 3.6f, 1 / 3f),
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
        };
    }

    /// <summary>
    /// 북쪽 지평선(거리 460–1000 m)의 도시. 34 m 격자에 건물을 앉히고(빈칸은 거리·공원), 도심일수록 높게 한다.
    /// </summary>
    void BuildCity()
    {
        var bodies = new Dictionary<FacadeKind, List<(Transform3D X, Color C)>>();
        foreach (var f in Enum.GetValues<FacadeKind>()) bodies[f] = new();
        var roofs = new List<(Transform3D X, Color C)>();    // 평지붕 덮개와 옥상 기계실
        var pitched = new List<(Transform3D X, Color C)>();  // 외곽 집의 경사 지붕
        var trees = new List<(Transform3D X, Color C)>();
        var resonance = new Vector2(60, -950);
        const float cell = 34f, ground = -0.6f;

        for (var gx = -900f; gx <= 900f; gx += cell)
        for (var gz = -1000f; gz <= -400f; gz += cell)
        {
            var p = new Vector2(gx + Jitter(5), gz + Jitter(5));
            var r = p.Length();
            if (r < 460 || r > 1000 || p.DistanceTo(resonance) < 60) continue;
            // 도심: 북쪽 720 m 근처가 가장 높고 촘촘하다
            var downtown = Mathf.Exp(-(p.X * p.X) / (260f * 260f) - (p.Y + 720) * (p.Y + 720) / (170f * 170f));
            var spread = Mathf.Clamp(1 - Mathf.Abs(p.X) / 950f, 0, 1);
            if (_rng.Randf() > 0.35f + 0.6f * spread)
            {
                if (_rng.Randf() < 0.5f) AddPark(trees, p, cell); // 빈 칸의 절반은 공원
                continue;
            }
            var w = _rng.RandfRange(14, 30);
            var d = _rng.RandfRange(14, 30);
            var h = downtown > 0.25f
                ? 40 + 220 * downtown * Mathf.Pow(_rng.Randf(), 0.7f)
                : _rng.Randf() < 0.35f + 0.4f * spread ? _rng.RandfRange(14, 45) : _rng.RandfRange(7, 13);
            var basis = new Basis(Vector3.Up, Jitter(0.06f));
            if (h < 14)
            {
                // 외곽의 집: 회벽·벽돌, 경사 지붕 (기와·슬레이트)
                var kind = _rng.Randf() < 0.6f ? FacadeKind.Plaster : FacadeKind.Brick;
                bodies[kind].Add((new Transform3D(basis.Scaled(new Vector3(w * 0.6f, h, d * 0.6f)), new Vector3(p.X, ground + h / 2, p.Y)), Tint(kind)));
                var roofColor = _rng.Randf() < 0.6f ? new Color(0.55f, 0.27f, 0.2f) : new Color(0.3f, 0.31f, 0.33f);
                pitched.Add((new Transform3D(basis.Scaled(new Vector3(w * 0.66f, h * 0.45f, d * 0.66f)), new Vector3(p.X, ground + h + h * 0.225f, p.Y)), roofColor * (0.9f + _rng.Randf() * 0.2f)));
            }
            else
            {
                // 재질 비율은 도시 사진 9,262장의 건물 색 분포를 따른다: 따뜻한 벽돌·황토 49 %, 밝은 회백 12 %,
                // 어두운 회색 12 %, 차가운 유리 8 % — 유리 빌딩은 도심의 높은 건물에 몰린다
                var kind = h > 90 ? (_rng.Randf() < 0.5f ? FacadeKind.Curtain : FacadeKind.Concrete)
                    : _rng.Randf() switch { < 0.1f => FacadeKind.Curtain, < 0.35f => FacadeKind.Concrete, < 0.65f => FacadeKind.Brick, _ => FacadeKind.Plaster };
                bodies[kind].Add((new Transform3D(basis.Scaled(new Vector3(w, h, d)), new Vector3(p.X, ground + h / 2, p.Y)), Tint(kind)));
                roofs.Add((new Transform3D(basis.Scaled(new Vector3(w + 0.6f, 0.8f, d + 0.6f)), new Vector3(p.X, ground + h + 0.4f, p.Y)), new Color(0.5f, 0.5f, 0.5f)));
                if (h > 30) // 옥상 기계실·물탱크: 높이가 들쭉날쭉한 스카이라인
                {
                    var mh = _rng.RandfRange(3, 8);
                    roofs.Add((new Transform3D(basis.Scaled(new Vector3(w * _rng.RandfRange(0.25f, 0.5f), mh, d * _rng.RandfRange(0.25f, 0.5f))),
                        new Vector3(p.X + Jitter(w * 0.2f), ground + h + 0.8f + mh / 2, p.Y + Jitter(d * 0.2f))), new Color(0.55f, 0.55f, 0.53f)));
                }
                if (downtown > 0.25f && h > 120 && _rng.Randf() < 0.5f) // 높은 빌딩 일부는 위가 좁아지는 셋백
                {
                    var h2 = h * _rng.RandfRange(0.15f, 0.3f);
                    bodies[kind].Add((new Transform3D(basis.Scaled(new Vector3(w * 0.65f, h2, d * 0.65f)), new Vector3(p.X, ground + h + h2 / 2, p.Y)), Tint(kind)));
                }
            }
            if (_rng.Randf() < 0.4f) AddPark(trees, p + new Vector2(cell * 0.45f, 0), cell * 0.3f); // 가로수
        }

        foreach (var (kind, list) in bodies) Batch(new BoxMesh(), list, FacadeMaterial(kind));
        Batch(new BoxMesh(), roofs, new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 0.9f });
        Batch(new PrismMesh { LeftToRight = 0.5f }, pitched, new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 0.85f });
        Batch(new SphereMesh { Radius = 1, Height = 2, RadialSegments = 10, Rings = 6 }, trees, new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 0.95f });
        // 도시 바닥: 거리와 광장의 포장면
        AddChild(new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(1900, 620) }, Position = new Vector3(0, -0.55f, -710),
            MaterialOverride = Look.Scanned("concrete_pavers", 6f, new Color(0.62f, 0.62f, 0.6f)),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
    }

    Color Tint(FacadeKind kind) => kind switch
    {
        FacadeKind.Curtain => new Color(0.9f, 0.95f, 1f) * (0.85f + _rng.Randf() * 0.25f),
        FacadeKind.Brick => new Color(1f, 0.9f + _rng.Randf() * 0.15f, 0.85f + _rng.Randf() * 0.15f) * (0.8f + _rng.Randf() * 0.3f),
        _ => new Color(1f, 0.97f + _rng.Randf() * 0.03f, 0.9f + _rng.Randf() * 0.1f) * (0.85f + _rng.Randf() * 0.2f),
    };

    /// <summary>공원·가로수: 크기와 초록빛이 제각각인 나무 덩어리</summary>
    void AddPark(List<(Transform3D X, Color C)> trees, Vector2 at, float size)
    {
        var count = _rng.RandiRange(3, 9);
        for (var i = 0; i < count; i++)
        {
            var s = _rng.RandfRange(3, 7);
            var pos = new Vector3(at.X + Jitter(size * 0.4f), -0.6f + s * 1.1f, at.Y + Jitter(size * 0.4f));
            var green = new Color(0.16f + _rng.Randf() * 0.1f, 0.3f + _rng.Randf() * 0.15f, 0.12f + _rng.Randf() * 0.06f);
            trees.Add((new Transform3D(Basis.Identity.Scaled(new Vector3(s, s * 1.25f, s)), pos), green));
        }
    }

    void Batch(Mesh mesh, List<(Transform3D X, Color C)> xs, Material mat)
    {
        if (xs.Count == 0) return;
        var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, Mesh = mesh, InstanceCount = xs.Count };
        for (var i = 0; i < xs.Count; i++)
        {
            mm.SetInstanceTransform(i, xs[i].X);
            mm.SetInstanceColor(i, xs[i].C);
        }
        AddChild(new MultiMeshInstance3D { Multimesh = mm, MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
    }

    // ------------------------------------------------------------------ 산과 언덕

    /// <summary>
    /// 지평선을 두르는 두 겹의 능선: 앞은 숲 덮인 언덕(높이 40–120 m), 뒤는 산(150–380 m).
    /// 도시가 있는 북쪽은 도시 뒤로 물러나 있다. 높이는 노이즈라 같은 모양이 되풀이되지 않는다.
    /// </summary>
    void BuildHills()
    {
        var noise = new FastNoiseLite { Seed = 31, Frequency = 1f, FractalOctaves = 5, NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin };
        AddRidge(noise, 0, near: 760, far: 1300, height: (40, 120), color: new Color(0.2f, 0.3f, 0.16f));
        AddRidge(noise, 100, near: 1500, far: 2100, height: (150, 380), color: new Color(0.3f, 0.36f, 0.3f));
    }

    void AddRidge(FastNoiseLite noise, float seedOffset, float near, float far, (float Min, float Max) height, Color color)
    {
        const int segments = 720, radial = 8;
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        for (var i = 0; i <= segments; i++)
        {
            var a = i * Mathf.Tau / segments;
            var dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
            // 북쪽(도시 쪽, -Z)은 능선을 뒤로 민다
            var north = Mathf.Max(0, -dir.Y);
            var r0 = near + (far - near) * 0.6f * north * north;
            var peak = Mathf.Lerp(height.Min, height.Max, Mathf.Clamp(0.5f + noise.GetNoise2D(Mathf.Cos(a) * 3 + seedOffset, Mathf.Sin(a) * 3) * 0.9f, 0, 1));
            for (var j = 0; j <= radial; j++)
            {
                var t = (float)j / radial;
                var r = Mathf.Lerp(r0, far + 400, t);
                // 단면: 앞쪽은 완만하게 오르고 등성이를 지나 뒤로 이어진다
                var profile = Mathf.SmoothStep(0, 0.35f, t) * (1 - 0.3f * Mathf.SmoothStep(0.5f, 1, t));
                var detail = noise.GetNoise2D(Mathf.Cos(a) * 12 + seedOffset, Mathf.Sin(a) * 12 + t * 4) * 0.25f;
                st.SetColor(color * (0.85f + detail));
                st.AddVertex(new Vector3(dir.X * r, -0.6f + peak * profile * (1 + detail), dir.Y * r));
            }
        }
        for (var i = 0; i < segments; i++)
        for (var j = 0; j < radial; j++)
        {
            int a = i * (radial + 1) + j, b = a + 1, c = a + radial + 1, d = c + 1;
            st.AddIndex(a); st.AddIndex(b); st.AddIndex(c);
            st.AddIndex(b); st.AddIndex(d); st.AddIndex(c);
        }
        st.GenerateNormals();
        AddChild(new MeshInstance3D
        {
            Mesh = st.Commit(),
            MaterialOverride = new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 1, CullMode = BaseMaterial3D.CullModeEnum.Disabled },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
    }

    // ------------------------------------------------------------------ 식물원 온실

    /// <summary>늑골 뼈대의 유리 거품 돔 온실 군락. 돔끼리는 유리 대롱으로 이어진다.</summary>
    void BuildGreenhouses(Vector3 at)
    {
        var site = new Node3D { Position = at };
        AddChild(site);
        var frame = new StandardMaterial3D { AlbedoColor = new Color(0.93f, 0.93f, 0.9f), Roughness = 0.35f, Metallic = 0.4f };
        var domes = new List<(Vector3 P, float R)>();
        for (var i = 0; i < 6; i++)
        {
            var r = i == 0 ? 24f : _rng.RandfRange(10, 17);
            var p = i == 0 ? Vector3.Zero : new Vector3(Mathf.Cos(i * 1.15f) * (26 + r), 0, Mathf.Sin(i * 1.15f) * (24 + r) * 0.8f);
            domes.Add((p, r));
            var dome = new Node3D { Position = p };
            site.AddChild(dome);
            Put(dome, new SphereMesh { Radius = r, Height = r, IsHemisphere = true, RadialSegments = 48, Rings = 16 }, Glass, Vector3.Zero);
            // 자오선 늑골: 정확한 등간격
            for (var m = 0; m < 8; m++)
                Put(dome, new TorusMesh { InnerRadius = r * 0.995f, OuterRadius = r * 1.012f, Rings = 64, RingSegments = 4 }, frame, Vector3.Zero, new Vector3(Mathf.Pi / 2, m * Mathf.Pi / 8, 0));
            foreach (var lat in new[] { 0.3f, 0.62f, 0.9f })
                Put(dome, new TorusMesh { InnerRadius = r * Mathf.Cos(lat) * 0.995f, OuterRadius = r * Mathf.Cos(lat) * 1.012f, Rings = 64, RingSegments = 4 }, frame, new Vector3(0, r * Mathf.Sin(lat), 0));
            Put(dome, new CylinderMesh { TopRadius = r * 1.02f, BottomRadius = r * 1.05f, Height = 1.2f, RadialSegments = 48 }, Concrete, new Vector3(0, 0.2f, 0));
            // 안쪽 수풀: 크기·자리 제각각
            for (var b = 0; b < (int)(r * 0.8f); b++)
            {
                var s = _rng.RandfRange(1.5f, r * 0.28f);
                Put(dome, new SphereMesh { Radius = s, Height = s * 1.6f, RadialSegments = 12, Rings = 6 }, Leafy, new Vector3(Jitter(r * 0.6f), s * 0.6f, Jitter(r * 0.6f)));
            }
        }
        for (var i = 1; i < domes.Count; i++)
        {
            var a = domes[0].P + new Vector3(0, 3, 0);
            var b = domes[i].P + new Vector3(0, 3, 0);
            var d = (b - a).Normalized();
            Rod(site, a + d * domes[0].R * 0.9f, b - d * domes[i].R * 0.9f, 2.4f, Glass);
        }
    }
}
