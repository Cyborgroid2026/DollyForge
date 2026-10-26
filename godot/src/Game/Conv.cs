using N = System.Numerics;

namespace DollyForge.Game;

/// <summary>코어(System.Numerics) ↔ Godot 수학 타입 변환. 둘 다 게임 좌표(Y-up, 미터)다.</summary>
public static class Conv
{
    public static Godot.Vector3 G(this N.Vector3 v) => new(v.X, v.Y, v.Z);
    public static N.Vector3 N(this Godot.Vector3 v) => new(v.X, v.Y, v.Z);
    public static Godot.Quaternion G(this N.Quaternion q) => new(q.X, q.Y, q.Z, q.W);
    public static N.Quaternion N(this Godot.Quaternion q) => new(q.X, q.Y, q.Z, q.W);

    public static Core.Pose Pose(this Godot.Transform3D t) =>
        new(t.Origin.N(), t.Basis.Orthonormalized().GetRotationQuaternion().N());

    public static Godot.Transform3D Xform(this Core.Pose p) =>
        new(new Godot.Basis(p.Rotation.G()), p.Position.G());
}
