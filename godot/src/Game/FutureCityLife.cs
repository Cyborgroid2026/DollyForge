using Godot;

namespace DollyForge.Game;

/// <summary>
/// 레트로퓨처 도시의 생활감. Atomic Heart 풍 참고 이미지에서 되풀이되는 요소를 이 세계의 말로 옮긴다
/// (그대로 베끼지 않고 형태 언어만: 흰 에나멜 곡면, 진홍, 놋쇠, 둥근 창, 유리).
///  · 떠 있는 플랫폼 아래에 매달린 원통 주거 포드와 진홍 등 구슬
///  · 하늘을 천천히 도는 구형 정비 드론 (적대 로봇이 아니라 도시의 일꾼)
///  · 늑골 뼈대의 유리 거품 돔 온실 군락
///  · 하늘로 손을 뻗은 거대한 사이보그 기념상 (얼굴 없는 몸 그대로)
///  · 정류장과 작업장 앞마당의 진홍 풍선 다발
/// </summary>
public partial class FutureCity
{
    readonly List<(Node3D Node, Vector3 Center, float Radius, float Speed, float Phase)> _drones = new();
    readonly List<(Node3D Node, float Phase)> _balloons = new();

    static readonly StandardMaterial3D Lantern = new()
    {
        AlbedoColor = new Color(0.75f, 0.06f, 0.05f), Roughness = 0.12f, ClearcoatEnabled = true, Clearcoat = 1,
        EmissionEnabled = true, Emission = new Color(1f, 0.2f, 0.1f), EmissionEnergyMultiplier = 0.8f,
    };
    static readonly StandardMaterial3D WarmWindow = new()
    {
        AlbedoColor = new Color(1f, 0.85f, 0.6f), EmissionEnabled = true, Emission = new Color(1f, 0.72f, 0.4f), EmissionEnergyMultiplier = 2.5f,
    };
    static readonly StandardMaterial3D Glass = new()
    {
        AlbedoColor = new Color(0.82f, 0.94f, 0.9f, 0.16f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        Roughness = 0.04f, Metallic = 0.2f, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };
    static readonly StandardMaterial3D Leafy = new() { AlbedoColor = new Color(0.24f, 0.42f, 0.18f), Roughness = 0.9f };
    static readonly StandardMaterial3D BalloonSkin = new()
    {
        AlbedoColor = new Color(0.78f, 0.07f, 0.06f), Roughness = 0.2f, ClearcoatEnabled = true, Clearcoat = 0.8f, ClearcoatRoughness = 0.1f,
    };
    static readonly StandardMaterial3D Twine = new() { AlbedoColor = new Color(0.92f, 0.9f, 0.86f), Roughness = 0.8f };

    static MeshInstance3D Put(Node3D parent, Mesh mesh, Material mat, Vector3 at, Vector3? rotation = null)
    {
        var m = new MeshInstance3D { Mesh = mesh, MaterialOverride = mat, Position = at, Rotation = rotation ?? Vector3.Zero };
        parent.AddChild(m);
        return m;
    }

    /// <summary>a 에서 b 까지 가는 막대 (줄·대롱)</summary>
    static void Rod(Node3D parent, Vector3 a, Vector3 b, float radius, Material mat)
    {
        var d = b - a;
        var basis = d.Normalized().Dot(Vector3.Up) > 0.999f ? Basis.Identity : new Basis(new Quaternion(Vector3.Up, d.Normalized()));
        parent.AddChild(new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = radius, BottomRadius = radius, Height = d.Length(), RadialSegments = 6 },
            MaterialOverride = mat,
            Transform = new Transform3D(basis, (a + b) / 2),
        });
    }

    /// <summary>플랫폼 밑면에서 놋쇠 대롱으로 내려온 원통 포드. 둥근 창이 둘러 있고 아래에 진홍 등 구슬.</summary>
    void BuildHangingPods(Node3D plat, float r)
    {
        var count = _rng.RandiRange(5, 7);
        for (var k = 0; k < count; k++)
        {
            var a = k * Mathf.Tau / count + Jitter(0.25f);
            var anchor = new Vector3(Mathf.Cos(a) * r * 0.62f, -9, Mathf.Sin(a) * r * 0.62f);
            var drop = _rng.RandfRange(9, 24);
            var podR = _rng.RandfRange(3f, 4.6f);
            var podH = _rng.RandfRange(5f, 7.5f);
            var top = anchor + new Vector3(0, -drop, 0);
            Rod(plat, anchor, top, 0.35f, Brass);
            var pod = new Node3D { Position = top + new Vector3(0, -podH / 2, 0) };
            plat.AddChild(pod);
            Put(pod, new CylinderMesh { TopRadius = podR, BottomRadius = podR * 0.92f, Height = podH, RadialSegments = 32 }, Enamel, Vector3.Zero);
            Put(pod, new SphereMesh { Radius = podR * 0.92f, Height = podR * 1.1f, RadialSegments = 32, Rings = 12 }, Enamel, new Vector3(0, -podH / 2, 0));
            Put(pod, new TorusMesh { InnerRadius = podR * 0.99f, OuterRadius = podR * 1.06f, Rings = 48, RingSegments = 8 }, Crimson, new Vector3(0, podH / 2 - 0.5f, 0));
            var windows = (int)(podR * 3);
            for (var w = 0; w < windows; w++)
            {
                var wa = w * Mathf.Tau / windows;
                var at = new Vector3(Mathf.Cos(wa) * podR * 0.97f, 0.3f, Mathf.Sin(wa) * podR * 0.97f);
                var face = new Vector3(Mathf.Pi / 2, -wa + Mathf.Pi / 2, 0);
                Put(pod, new TorusMesh { InnerRadius = 0.55f, OuterRadius = 0.7f, Rings = 16, RingSegments = 6 }, Brass, at, face);
                if (_rng.Randf() < 0.6f)
                    Put(pod, new CylinderMesh { TopRadius = 0.56f, BottomRadius = 0.56f, Height = 0.05f, RadialSegments = 12 }, WarmWindow, at, face);
            }
            var lanternY = -podH / 2 - podR * 0.55f - 2.5f;
            Rod(pod, new Vector3(0, -podH / 2 - podR * 0.5f, 0), new Vector3(0, lanternY, 0), 0.08f, Brass);
            Put(pod, new SphereMesh { Radius = 1.4f, Height = 2.8f }, Lantern, new Vector3(0, lanternY - 1.2f, 0));
        }
    }

    /// <summary>
    /// 구형 정비 드론 떼: 흰 에나멜 공에 놋쇠 띠와 진홍 눈 하나. 도시 위를 저마다의 원을 그리며 천천히 돌고,
    /// 몇 대는 작업장 위 하늘까지 내려온다.
    /// </summary>
    void BuildDrones()
    {
        Node3D Drone(float size)
        {
            var d = new Node3D();
            Put(d, new SphereMesh { Radius = size, Height = size * 2, RadialSegments = 24, Rings = 12 }, Enamel, Vector3.Zero);
            Put(d, new TorusMesh { InnerRadius = size * 0.98f, OuterRadius = size * 1.08f, Rings = 32, RingSegments = 6 }, Brass, Vector3.Zero);
            Put(d, new SphereMesh { Radius = size * 0.22f, Height = size * 0.44f }, Lantern, new Vector3(0, 0, size * 0.95f));
            Put(d, new CylinderMesh { TopRadius = size * 0.35f, BottomRadius = size * 0.5f, Height = size * 0.3f, RadialSegments = 16 }, Crimson, new Vector3(0, -size * 1.0f, 0));
            AddChild(d);
            return d;
        }
        for (var i = 0; i < 34; i++)
        {
            var a = _rng.RandfRange(-Mathf.Pi * 0.95f, -Mathf.Pi * 0.05f);
            var dist = _rng.RandfRange(260, 760);
            var center = new Vector3(Mathf.Cos(a) * dist, _rng.RandfRange(55, 230), Mathf.Sin(a) * dist);
            _drones.Add((Drone(_rng.RandfRange(1.6f, 3.2f)), center, _rng.RandfRange(15, 70), _rng.RandfRange(0.02f, 0.06f) * (_rng.Randf() < 0.5f ? 1 : -1), _rng.Randf() * Mathf.Tau));
        }
        foreach (var c in new[] { new Vector3(-30, 32, -25), new Vector3(35, 40, -55), new Vector3(-55, 46, 30) })
            _drones.Add((Drone(0.9f), c, _rng.RandfRange(10, 20), 0.05f, _rng.Randf() * Mathf.Tau));
    }

    /// <summary>늑골 뼈대의 유리 거품 돔 온실 군락. 돔끼리는 유리 대롱으로 이어진다.</summary>
    void BuildGreenhouses(Vector3 at)
    {
        var site = new Node3D { Position = at };
        AddChild(site);
        var domes = new List<(Vector3 P, float R)>();
        for (var i = 0; i < 6; i++)
        {
            var r = i == 0 ? 24f : _rng.RandfRange(10, 17);
            var p = i == 0 ? Vector3.Zero : new Vector3(Mathf.Cos(i * 1.15f) * (26 + r), 0, Mathf.Sin(i * 1.15f) * (24 + r) * 0.8f);
            domes.Add((p, r));
            var dome = new Node3D { Position = p };
            site.AddChild(dome);
            Put(dome, new SphereMesh { Radius = r, Height = r, IsHemisphere = true, RadialSegments = 48, Rings = 16 }, Glass, Vector3.Zero);
            // 자오선 늑골: 정확한 등간격 (인공미)
            for (var m = 0; m < 8; m++)
                Put(dome, new TorusMesh { InnerRadius = r * 0.995f, OuterRadius = r * 1.012f, Rings = 64, RingSegments = 4 }, Enamel, Vector3.Zero, new Vector3(Mathf.Pi / 2, m * Mathf.Pi / 8, 0));
            foreach (var lat in new[] { 0.3f, 0.62f, 0.9f })
                Put(dome, new TorusMesh { InnerRadius = r * Mathf.Cos(lat) * 0.995f, OuterRadius = r * Mathf.Cos(lat) * 1.012f, Rings = 64, RingSegments = 4 }, Enamel, new Vector3(0, r * Mathf.Sin(lat), 0));
            Put(dome, new CylinderMesh { TopRadius = r * 1.02f, BottomRadius = r * 1.05f, Height = 1.2f, RadialSegments = 48 }, Crimson, new Vector3(0, 0.2f, 0));
            // 안쪽 수풀 (자연미): 크기·자리 제각각
            for (var b = 0; b < (int)(r * 0.8f); b++)
            {
                var s = _rng.RandfRange(1.5f, r * 0.28f);
                var bp = new Vector3(Jitter(r * 0.6f), s * 0.6f, Jitter(r * 0.6f));
                Put(dome, new SphereMesh { Radius = s, Height = s * 1.6f, RadialSegments = 12, Rings = 6 }, Leafy, bp);
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

    /// <summary>
    /// 기념상: 얼굴 없는 사이보그 몸을 그대로 키워 흰 돌로 깎았다. 한 팔을 하늘로 뻗고 남쪽(정류장)을 본다.
    /// 계단식 콘크리트 받침에 진홍 띠.
    /// </summary>
    void BuildMonument(Vector3 at, float height)
    {
        var root = new Node3D { Position = at };
        AddChild(root);
        var y = 0f;
        foreach (var (w, h) in new[] { (34f, 4f), (26f, 5f), (18f, 7f) })
        {
            Put(root, new BoxMesh { Size = new Vector3(w, h, w) }, FacadeMat, new Vector3(0, y + h / 2, 0));
            y += h;
        }
        Put(root, new BoxMesh { Size = new Vector3(18.4f, 1.6f, 18.4f) }, Crimson, new Vector3(0, y - 2.2f, 0));

        var statue = CyborgBody.Build(0);
        statue.RaiseArm(1, -2.75f);
        var k = height / statue.Height;
        statue.Scale = Vector3.One * k;
        statue.Position = new Vector3(0, y, 0);
        root.AddChild(statue);
        var stone = Look.Painted("white_plaster_02", 3f, new Color(0.93f, 0.92f, 0.88f));
        foreach (var m in statue.FindChildren("*", nameof(MeshInstance3D), recursive: true, owned: false).OfType<MeshInstance3D>())
            m.MaterialOverride = stone;
    }

    /// <summary>정류장 양 끝과 작업장 앞마당 모서리에 묶인 진홍 풍선 다발. 바람에 살짝 흔들린다.</summary>
    void BuildBalloons()
    {
        var platform = Site.PlatformTop;
        var spots = new List<Vector3>
        {
            new(Site.StationX0 + 0.5f, platform, Site.PlatformZ0 + 0.4f),
            new(Site.StationX1 - 0.5f, platform, Site.PlatformZ0 + 0.4f),
        };
        foreach (var x in new[] { Site.Apron.Position.X + 0.4f, Site.Apron.End.X - 0.4f })
        {
            var z = Site.Apron.End.Y - 0.4f;
            spots.Add(new Vector3(x, World.HeightAt(x, z), z));
        }
        foreach (var p in spots)
        {
            var post = new Node3D { Position = p };
            AddChild(post);
            Put(post, new CylinderMesh { TopRadius = 0.025f, BottomRadius = 0.035f, Height = 1.1f, RadialSegments = 8 }, Brass, new Vector3(0, 0.55f, 0));
            var bunch = new Node3D { Position = new Vector3(0, 1.1f, 0) };
            post.AddChild(bunch);
            for (var i = 0; i < 7; i++)
            {
                var top = new Vector3(Jitter(0.45f), _rng.RandfRange(1.3f, 2.1f), Jitter(0.45f));
                Rod(bunch, Vector3.Zero, top, 0.003f, Twine);
                var s = _rng.RandfRange(0.9f, 1.1f);
                Put(bunch, new SphereMesh { Radius = 0.21f * s, Height = 0.52f * s, RadialSegments = 16, Rings = 10 }, BalloonSkin, top + new Vector3(0, 0.25f * s, 0));
            }
            _balloons.Add((bunch, _rng.Randf() * Mathf.Tau));
        }
    }

    void AnimateLife(float dt)
    {
        foreach (var (node, center, radius, speed, phase) in _drones)
        {
            var a = phase + _time * speed;
            node.Position = center + new Vector3(Mathf.Cos(a) * radius, Mathf.Sin(_time * 0.4f + phase) * 1.5f, Mathf.Sin(a) * radius);
            node.Rotation = new Vector3(0, -a + (speed > 0 ? 0 : Mathf.Pi), 0);
        }
        foreach (var (node, phase) in _balloons)
            node.Rotation = new Vector3(Mathf.Sin(_time * 0.9f + phase) * 0.08f, 0, Mathf.Sin(_time * 0.7f + phase * 1.7f) * 0.1f);
    }
}
