using Godot;
using DollyForge.Core;

namespace DollyForge.Game;

/// <summary>
/// 월드 안의 부품 하나. 질량·관성·마찰·반발은 코어 정의(실제 재료 물성)에서 온다.
/// 원기둥은 코어 관례대로 로컬 Z 축이 원기둥 축이다 (Godot 원기둥은 Y 축이라 자식 노드를 돌려 맞춘다).
/// </summary>
public partial class PartBody : RigidBody3D
{
    public const uint LayerWorld = 1, LayerParts = 2, LayerPlayer = 4;

    public PartInstance Part { get; private set; } = null!;
    public CollisionShape3D ShapeNode { get; private set; } = null!;

    /// <summary>배터리 잔량 (J). 배터리가 아니면 0.</summary>
    public double EnergyJ { get; set; }

    public static PartBody Create(PartInstance part)
    {
        var def = part.Def;
        var body = new PartBody
        {
            Part = part,
            Name = part.LinkName,
            Mass = def.Mass,
            CenterOfMassMode = CenterOfMassModeEnum.Custom,
            CenterOfMass = Vector3.Zero,
            Inertia = def.Inertia.G(),
            PhysicsMaterialOverride = new PhysicsMaterial
            {
                Friction = def.Material.StaticFriction,
                Bounce = def.Material.Restitution,
            },
            ContinuousCd = def.Mass < 0.5f,
            LinearDampMode = DampMode.Replace,
            LinearDamp = 0.01f,   // 공기 저항 수준
            AngularDampMode = DampMode.Replace,
            AngularDamp = 0.02f,
            CollisionLayer = LayerParts,
            CollisionMask = LayerWorld | LayerParts | LayerPlayer,
            Transform = part.Pose.Xform(),
        };
        if (def.Electrical is { CapacityWh: > 0 } e) body.EnergyJ = e.CapacityWh * 3600;

        var s = def.Shape;
        Shape3D shape;
        Mesh mesh;
        var align = Basis.Identity;
        switch (s.Kind)
        {
            case ShapeKind.Box:
                shape = new BoxShape3D { Size = s.Size.G() };
                mesh = new BoxMesh { Size = s.Size.G() };
                break;
            case ShapeKind.Cylinder:
                shape = new CylinderShape3D { Radius = s.Radius, Height = s.Length };
                mesh = new CylinderMesh { TopRadius = s.Radius, BottomRadius = s.Radius, Height = s.Length, RadialSegments = 48, Rings = 1 };
                align = new Basis(Vector3.Right, Mathf.Pi / 2); // Godot 원기둥 Y축 → 코어 Z축
                break;
            default:
                shape = new SphereShape3D { Radius = s.Radius };
                mesh = new SphereMesh { Radius = s.Radius, Height = s.Radius * 2, RadialSegments = 32, Rings = 16 };
                break;
        }
        body.ShapeNode = new CollisionShape3D { Shape = shape, Transform = new Transform3D(align, Vector3.Zero) };
        body.AddChild(body.ShapeNode);
        body.AddChild(new MeshInstance3D
        {
            Mesh = mesh,
            MaterialOverride = Look.For(def.Material),
            Transform = new Transform3D(align, Vector3.Zero),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.On,
        });
        if (def.Role == PartRole.Motor) AddShaftStub(body, def);
        return body;
    }

    /// <summary>모터 출력축 표시 (형상·물리에는 포함되지 않는 시각 요소)</summary>
    static void AddShaftStub(PartBody body, PartDef def)
    {
        var len = 0.015f;
        var stub = new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = def.ShaftMm / 2000f, BottomRadius = def.ShaftMm / 2000f, Height = len },
            MaterialOverride = Look.For(Materials.Steel),
        };
        var half = def.Shape.Kind == ShapeKind.Cylinder ? def.Shape.Length / 2 : def.Shape.Size.Z / 2;
        stub.Transform = new Transform3D(new Basis(Vector3.Right, Mathf.Pi / 2), new Vector3(0, 0, half + len / 2));
        body.AddChild(stub);
    }

    /// <summary>
    /// 접촉 판정용으로 조금 키운 같은 형상 (월드 변환 포함). 두 부품이 맞닿아 있는지 확인할 때 쓴다.
    /// </summary>
    public (Shape3D Shape, Transform3D Xform) Grown(float margin)
    {
        var s = Part.Def.Shape;
        Shape3D shape = s.Kind switch
        {
            ShapeKind.Box => new BoxShape3D { Size = s.Size.G() + Vector3.One * margin * 2 },
            ShapeKind.Cylinder => new CylinderShape3D { Radius = s.Radius + margin, Height = s.Length + margin * 2 },
            _ => new SphereShape3D { Radius = s.Radius + margin },
        };
        return (shape, GlobalTransform * ShapeNode.Transform);
    }

    /// <summary>부품의 주요 축(월드). 원기둥이면 원기둥 축, 상자면 가장 긴 변 방향.</summary>
    public Vector3 PrincipalAxis()
    {
        var s = Part.Def.Shape;
        var b = GlobalBasis.Orthonormalized();
        if (s.Kind == ShapeKind.Cylinder) return b.Z;
        if (s.Kind == ShapeKind.Box)
        {
            var v = s.Size;
            return v.X >= v.Y && v.X >= v.Z ? b.X : v.Y >= v.Z ? b.Y : b.Z;
        }
        return b.Y;
    }

    public override void _PhysicsProcess(double delta) => Part.Pose = GlobalTransform.Pose();
}
