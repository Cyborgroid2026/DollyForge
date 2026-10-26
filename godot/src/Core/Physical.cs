using System.Numerics;

namespace DollyForge.Core;

/// <summary>
/// 실제 재료 물성. 값은 대표적인 공학 수치(상온)다.
/// 밀도 kg/m³, 탄성계수 GPa, 항복강도 MPa, 마찰계수는 강철 상대면 기준 근사치.
/// </summary>
public sealed record MaterialDef(
    string Id,
    string NameKo,
    float Density,
    float YoungGPa,
    float YieldMPa,
    float StaticFriction,
    float DynamicFriction,
    float Restitution,
    Vector4 Color);

public static class Materials
{
    public static readonly MaterialDef Steel = new("steel_s235", "구조용 강철 S235", 7850, 200, 235, 0.74f, 0.57f, 0.3f, new(0.55f, 0.57f, 0.60f, 1));
    public static readonly MaterialDef CastIron = new("cast_iron", "주철", 7200, 110, 200, 1.1f, 0.15f, 0.2f, new(0.30f, 0.31f, 0.33f, 1));
    public static readonly MaterialDef Copper = new("copper", "구리 (연질)", 8960, 117, 70, 0.53f, 0.36f, 0.3f, new(0.80f, 0.46f, 0.30f, 1));
    public static readonly MaterialDef Brass = new("brass_c260", "황동 C260", 8530, 110, 200, 0.51f, 0.44f, 0.3f, new(0.82f, 0.70f, 0.38f, 1));
    public static readonly MaterialDef Titanium = new("ti6al4v", "티타늄 Ti-6Al-4V", 4430, 114, 880, 0.36f, 0.31f, 0.3f, new(0.70f, 0.71f, 0.72f, 1));
    public static readonly MaterialDef Aluminium = new("al6061_t6", "알루미늄 6061-T6", 2700, 69, 276, 0.61f, 0.47f, 0.3f, new(0.80f, 0.81f, 0.83f, 1));
    public static readonly MaterialDef Pine = new("pine", "소나무 목재", 500, 10, 40, 0.5f, 0.4f, 0.4f, new(0.72f, 0.56f, 0.38f, 1));
    public static readonly MaterialDef Oak = new("oak", "참나무 목재", 750, 12, 50, 0.6f, 0.48f, 0.4f, new(0.55f, 0.40f, 0.25f, 1));
    public static readonly MaterialDef Rubber = new("natural_rubber", "천연고무", 1100, 0.05f, 15, 1.0f, 0.8f, 0.75f, new(0.08f, 0.08f, 0.08f, 1));
    public static readonly MaterialDef Glass = new("soda_lime_glass", "소다석회 유리", 2500, 72, 50, 0.9f, 0.4f, 0.5f, new(0.85f, 0.92f, 0.95f, 0.35f));
    public static readonly MaterialDef Concrete = new("concrete_block", "콘크리트 블록", 2000, 30, 3, 0.7f, 0.6f, 0.1f, new(0.62f, 0.61f, 0.58f, 1));
    public static readonly MaterialDef Brick = new("clay_brick", "점토 벽돌", 1900, 15, 3, 0.7f, 0.6f, 0.1f, new(0.62f, 0.30f, 0.22f, 1));
    public static readonly MaterialDef Granite = new("granite", "화강암", 2700, 50, 130, 0.7f, 0.6f, 0.2f, new(0.55f, 0.54f, 0.52f, 1));
    public static readonly MaterialDef Quartz = new("quartz", "석영", 2650, 72, 50, 0.6f, 0.5f, 0.3f, new(0.92f, 0.94f, 0.96f, 1));
    public static readonly MaterialDef Soil = new("soil", "흙 (다져진 표토)", 1500, 0.05f, 0, 0.6f, 0.5f, 0.05f, new(0.40f, 0.30f, 0.22f, 1));
    public static readonly MaterialDef IronOre = new("iron_ore", "철광석 (적철석)", 5000, 0, 0, 0.7f, 0.6f, 0.2f, new(0.45f, 0.25f, 0.20f, 1));
    public static readonly MaterialDef CopperOre = new("copper_ore", "구리광석 (황동석)", 4200, 0, 0, 0.7f, 0.6f, 0.2f, new(0.55f, 0.48f, 0.25f, 1));

    public static readonly IReadOnlyDictionary<string, MaterialDef> All = new[]
    {
        Steel, CastIron, Copper, Brass, Titanium, Aluminium, Pine, Oak, Rubber, Glass,
        Concrete, Brick, Granite, Quartz, Soil, IronOre, CopperOre,
    }.ToDictionary(m => m.Id);
}

public enum ShapeKind { Box, Cylinder, Sphere }

/// <summary>
/// 기본 형상. 좌표계는 부품 로컬 프레임.
/// Box: Size = (x, y, z) 변 길이. Cylinder: Size.X = 반지름, Size.Y = 길이, 축은 로컬 Z (URDF·USD 관례).
/// Sphere: Size.X = 반지름.
/// </summary>
public readonly record struct Shape(ShapeKind Kind, Vector3 Size)
{
    public static Shape Box(float x, float y, float z) => new(ShapeKind.Box, new(x, y, z));
    public static Shape Cylinder(float radius, float length) => new(ShapeKind.Cylinder, new(radius, length, 0));
    public static Shape Sphere(float radius) => new(ShapeKind.Sphere, new(radius, 0, 0));

    public float Radius => Size.X;
    public float Length => Size.Y;

    public float Volume => Kind switch
    {
        ShapeKind.Box => Size.X * Size.Y * Size.Z,
        ShapeKind.Cylinder => MathF.PI * Radius * Radius * Length,
        _ => 4f / 3f * MathF.PI * Radius * Radius * Radius,
    };

    /// <summary>질량 중심에 대한 주관성 모멘트 (로컬 축 = 주축), kg·m²</summary>
    public Vector3 PrincipalInertia(float mass) => Kind switch
    {
        ShapeKind.Box => new(
            mass / 12f * (Size.Y * Size.Y + Size.Z * Size.Z),
            mass / 12f * (Size.X * Size.X + Size.Z * Size.Z),
            mass / 12f * (Size.X * Size.X + Size.Y * Size.Y)),
        ShapeKind.Cylinder => new(
            mass / 12f * (3 * Radius * Radius + Length * Length),
            mass / 12f * (3 * Radius * Radius + Length * Length),
            mass / 2f * Radius * Radius),
        _ => new Vector3(2f / 5f * mass * Radius * Radius),
    };
}

/// <summary>강체 자세: 위치(m)와 회전.</summary>
public readonly record struct Pose(Vector3 Position, Quaternion Rotation)
{
    public static readonly Pose Identity = new(Vector3.Zero, Quaternion.Identity);

    public Pose Inverse()
    {
        var inv = Quaternion.Inverse(Rotation);
        return new(Vector3.Transform(-Position, inv), inv);
    }

    /// <summary>this ∘ other (other 를 this 프레임 안에서 표현한 것을 월드로)</summary>
    public Pose Compose(Pose other) =>
        new(Position + Vector3.Transform(other.Position, Rotation), Quaternion.Normalize(Rotation * other.Rotation));

    public Vector3 TransformPoint(Vector3 p) => Position + Vector3.Transform(p, Rotation);
    public Vector3 TransformDirection(Vector3 d) => Vector3.Transform(d, Rotation);
}
