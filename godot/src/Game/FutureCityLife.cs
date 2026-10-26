using Godot;

namespace DollyForge.Game;

/// <summary>
/// 레트로퓨처 도시의 생활감. Atomic Heart 풍 참고 이미지에서 되풀이되는 요소를 이 세계의 말로 옮긴다
/// (그대로 베끼지 않고 형태 언어만: 흰 에나멜 곡면, 진홍, 놋쇠, 둥근 창, 유리).
///  · 떠 있는 플랫폼 아래에 매달린 원통 주거 포드와 진홍 등 구슬
///  · 늑골 뼈대의 유리 거품 돔 온실 군락
/// </summary>
public partial class FutureCity
{

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

}
