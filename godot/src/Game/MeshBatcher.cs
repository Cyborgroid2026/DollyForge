using Godot;

namespace DollyForge.Game;

/// <summary>
/// 움직이지 않는 장식 메시를 재질별로 하나로 합친다. 골목은 상자·원통 수천 개로 지어지는데,
/// 하나씩 그리면 그리기 호출이 수천 번이 된다. 같은 재질(속성이 같으면 다른 인스턴스여도 같다)·같은 그림자 설정·
/// 같은 정점 형식끼리 묶어 한 메시로 만든다. 글자판(Label3D)과 충돌 모양은 건드리지 않는다.
/// </summary>
internal static class MeshBatcher
{
    /// <summary>root 아래의 메시를 합치고, 합친 원래 메시 수와 합친 뒤 메시 수를 돌려준다</summary>
    public static (int Before, int After) Merge(Node3D root)
    {
        var groups = new Dictionary<string, (Material Material, GeometryInstance3D.ShadowCastingSetting Shadow, SurfaceTool Tool)>();
        var merged = new List<MeshInstance3D>();
        foreach (var mesh in Descendants(root))
        {
            if (mesh.Mesh is not { } source || mesh.MaterialOverride is not { } material || !mesh.Visible) continue;
            var transform = Relative(root, mesh);
            var materialKey = Key(material);
            // 골목의 메시는 모두 삼각형 목록이다 (기본 도형과 SurfaceTool). 정점 형식은 들어 있는 배열로 판단한다.
            for (var surface = 0; surface < source.GetSurfaceCount(); surface++)
            {
                var arrays = source.SurfaceGetArrays(surface);
                var format = string.Concat(Enumerable.Range(0, arrays.Count).Select(i => arrays[i].VariantType == Variant.Type.Nil ? '0' : '1'));
                var key = $"{materialKey}|{mesh.CastShadow}|{format}";
                if (!groups.TryGetValue(key, out var group))
                {
                    var tool = new SurfaceTool();
                    tool.Begin(Mesh.PrimitiveType.Triangles);
                    group = (material, mesh.CastShadow, tool);
                    groups[key] = group;
                }
                group.Tool.AppendFrom(source, surface, transform);
            }
            merged.Add(mesh);
        }
        foreach (var mesh in merged)
        {
            mesh.GetParent().RemoveChild(mesh);
            mesh.QueueFree();
        }
        foreach (var group in groups.Values)
            root.AddChild(new MeshInstance3D { Name = "Batched", Mesh = group.Tool.Commit(), MaterialOverride = group.Material, CastShadow = group.Shadow });
        return (merged.Count, groups.Count);
    }

    static IEnumerable<MeshInstance3D> Descendants(Node node)
    {
        foreach (var child in node.GetChildren())
        {
            if (child is MeshInstance3D mesh) yield return mesh;
            foreach (var inner in Descendants(child)) yield return inner;
        }
    }

    /// <summary>root 기준 변환 (트리에 들어가기 전에도 부모를 따라 올라가며 곱한다)</summary>
    static Transform3D Relative(Node3D root, Node3D node)
    {
        var transform = node.Transform;
        for (var parent = node.GetParent(); parent is not null && parent != root; parent = parent.GetParent())
            if (parent is Node3D p) transform = p.Transform * transform;
        return transform;
    }

    /// <summary>속성이 같은 StandardMaterial3D 는 같은 열쇠. 그 밖의 재질은 인스턴스마다 따로</summary>
    static string Key(Material material)
    {
        if (material is not StandardMaterial3D m) return $"#{material.GetInstanceId()}";
        static string Rid(Texture2D? t) => t is null ? "-" : t.GetRid().Id.ToString();
        return string.Join(',',
            m.AlbedoColor, Rid(m.AlbedoTexture), m.Roughness, Rid(m.RoughnessTexture), m.Metallic, m.MetallicSpecular,
            m.NormalEnabled, Rid(m.NormalTexture), m.NormalScale,
            m.EmissionEnabled, m.Emission, m.EmissionEnergyMultiplier,
            m.Uv1Triplanar, m.Uv1WorldTriplanar, m.Uv1Scale, m.CullMode, m.Transparency, m.RimEnabled, m.Rim, m.TextureFilter);
    }
}
