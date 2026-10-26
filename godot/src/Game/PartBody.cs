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

    /// <summary>닉시관·드럼 표시 글씨 (해당 기기만)</summary>
    public Label3D? Readout { get; private set; }
    /// <summary>켜지면 빛나는 진공관 필라멘트 (서보 증폭기)</summary>
    public StandardMaterial3D? Glow { get; private set; }

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
        if (def.Role is PartRole.Controller or PartRole.Display or PartRole.Sequencer) AddAnalogFace(body, def);
        if (def.Fictional) AddFictionalSheen(body, def);
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
    /// 양자 아날로그 기기의 얼굴: 진공관 두 개(서보 증폭기), 닉시관 네 개(계기), 캠 핀(드럼).
    /// 모두 시각 요소이며 물리 형상·질량은 부품 정의를 따른다.
    /// </summary>
    static void AddAnalogFace(PartBody body, PartDef def)
    {
        var glass = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.85f, 0.9f, 0.95f, 0.25f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            Roughness = 0.05f, Metallic = 0, RimEnabled = true, Rim = 0.4f,
        };
        var size = def.Shape.Size;
        switch (def.Role)
        {
            case PartRole.Controller:
            {
                body.Glow = new StandardMaterial3D { AlbedoColor = new Color(0.3f, 0.15f, 0.05f), EmissionEnabled = true, Emission = new Color(1f, 0.45f, 0.1f), EmissionEnergyMultiplier = 0 };
                foreach (var x in new[] { -0.03f, 0.03f })
                {
                    body.AddChild(new MeshInstance3D { Mesh = new CapsuleMesh { Radius = 0.013f, Height = 0.06f }, MaterialOverride = glass, Position = new Vector3(x, size.Y / 2 + 0.03f, -0.02f) });
                    body.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.004f, BottomRadius = 0.004f, Height = 0.03f }, MaterialOverride = body.Glow, Position = new Vector3(x, size.Y / 2 + 0.028f, -0.02f) });
                }
                body.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.012f, BottomRadius = 0.014f, Height = 0.015f }, MaterialOverride = Look.For(Materials.Brass), Position = new Vector3(0, size.Y / 2 + 0.008f, 0.03f) });
                body.Readout = MakeReadout(body, "출력 +100%", new Vector3(0, size.Y / 2 + 0.002f, 0.045f), flat: true, size: 0.00035f);
                break;
            }
            case PartRole.Display:
                for (var i = 0; i < 4; i++)
                    body.AddChild(new MeshInstance3D { Mesh = new CapsuleMesh { Radius = 0.0095f, Height = 0.04f }, MaterialOverride = glass, Position = new Vector3(-0.039f + i * 0.026f, size.Y / 2 + 0.02f, 0) });
                body.Readout = MakeReadout(body, "--.-", new Vector3(0, size.Y / 2 + 0.02f, 0.011f), flat: false, size: 0.0011f);
                body.Readout.Modulate = new Color(1f, 0.5f, 0.15f);
                break;
            case PartRole.Sequencer:
            {
                var peg = Look.For(Materials.Steel);
                for (var i = 0; i < 8; i++)
                {
                    var a = i * Mathf.Tau / 8;
                    body.AddChild(new MeshInstance3D
                    {
                        Mesh = new BoxMesh { Size = new Vector3(0.008f, 0.008f, 0.012f) }, MaterialOverride = peg,
                        Position = new Vector3(Mathf.Cos(a) * 0.041f, Mathf.Sin(a) * 0.041f, 0),
                    });
                }
                body.Readout = MakeReadout(body, "단 1/8", new Vector3(0, 0.06f, 0), flat: false, size: 0.0007f);
                break;
            }
        }
    }

    static Label3D MakeReadout(PartBody body, string text, Vector3 at, bool flat, float size)
    {
        var l = Look.Tag(text, size);
        l.Position = at;
        l.Shaded = false;
        l.Modulate = new Color(0.95f, 0.85f, 0.6f);
        if (flat) l.RotationDegrees = new Vector3(-90, 0, 0);
        else l.Billboard = BaseMaterial3D.BillboardModeEnum.Enabled;
        body.AddChild(l);
        return l;
    }

    /// <summary>가상 부품은 표면에 희미한 간섭무늬 빛을 둘러 현실 부품과 구별되게 한다.</summary>
    static void AddFictionalSheen(PartBody body, PartDef def)
    {
        var s = def.Shape;
        var mat = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.4f, 0.8f, 1f, 0.12f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            EmissionEnabled = true, Emission = new Color(0.3f, 0.7f, 1f), EmissionEnergyMultiplier = 0.6f,
            RimEnabled = true, Rim = 1, RimTint = 1, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        };
        Mesh shell = s.Kind == ShapeKind.Cylinder
            ? new CylinderMesh { TopRadius = s.Radius * 1.04f, BottomRadius = s.Radius * 1.04f, Height = s.Length * 1.02f }
            : new BoxMesh { Size = s.Size.G() * 1.04f };
        body.AddChild(new MeshInstance3D
        {
            Mesh = shell, MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Transform = new Transform3D(s.Kind == ShapeKind.Cylinder ? new Basis(Vector3.Right, Mathf.Pi / 2) : Basis.Identity, Vector3.Zero),
        });
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
