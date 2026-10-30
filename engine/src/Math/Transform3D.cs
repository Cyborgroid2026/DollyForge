using System.Numerics;

namespace DollyForge;

/// <summary>
/// Godot의 Transform3D 대체 (회전+이동, 열 벡터 규약).
/// Basis 열벡터 X,Y,Z 는 각 축이 가리키는 월드 방향이다.
/// </summary>
public readonly struct Transform3D
{
    public readonly Vector3 X, Y, Z, Origin;

    public Transform3D(Vector3 x, Vector3 y, Vector3 z, Vector3 origin)
        => (X, Y, Z, Origin) = (x, y, z, origin);

    public static readonly Transform3D Identity = new(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, Vector3.Zero);

    // 방향 벡터 변환 (이동 없음)
    public Vector3 BasisMul(Vector3 v) => X * v.X + Y * v.Y + Z * v.Z;

    // 점 변환 (이동 포함)
    public static Vector3 operator*(Transform3D t, Vector3 p)
        => t.X * p.X + t.Y * p.Y + t.Z * p.Z + t.Origin;

    // 변환 합성 (A * B)(p) = A(B(p))
    public static Transform3D operator*(Transform3D a, Transform3D b)
        => new(a.BasisMul(b.X), a.BasisMul(b.Y), a.BasisMul(b.Z),
               a.BasisMul(b.Origin) + a.Origin);

    // 역변환 (순수 회전+이동이라 전치가 역행렬)
    public Transform3D AffineInverse()
    {
        var rx = new Vector3(X.X, Y.X, Z.X);
        var ry = new Vector3(X.Y, Y.Y, Z.Y);
        var rz = new Vector3(X.Z, Y.Z, Z.Z);
        var inv = new Transform3D(rx, ry, rz, Vector3.Zero);
        return new Transform3D(rx, ry, rz, -(inv.BasisMul(Origin)));
    }

    // 쿼터니언 추출
    public Quaternion GetRotationQuaternion()
        => Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(
            new Matrix4x4(
                X.X, Y.X, Z.X, 0,
                X.Y, Y.Y, Z.Y, 0,
                X.Z, Y.Z, Z.Z, 0,
                0,   0,   0,   1)));

    // 축-각도로 순수 회전 변환 생성
    public static Transform3D FromAxisAngle(Vector3 axis, float angle)
    {
        var q = Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), angle);
        return new(
            Vector3.Transform(Vector3.UnitX, q),
            Vector3.Transform(Vector3.UnitY, q),
            Vector3.Transform(Vector3.UnitZ, q),
            Vector3.Zero);
    }

    // 이동만 있는 변환 생성
    public static Transform3D FromTranslation(Vector3 t)
        => new(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, t);

    // 회전+이동 변환 생성
    public static Transform3D From(Transform3D rotation, Vector3 origin)
        => new(rotation.X, rotation.Y, rotation.Z, origin);

    // 오일러(YXZ) → 쿼터니언
    public static Quaternion EulerToQuat(Vector3 euler)
    {
        var qx = Quaternion.CreateFromAxisAngle(Vector3.UnitX, euler.X);
        var qy = Quaternion.CreateFromAxisAngle(Vector3.UnitY, euler.Y);
        var qz = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, euler.Z);
        return Quaternion.Normalize(qy * qx * qz);
    }

    // 4×4 열우선 행렬 (OpenGL용)
    public Matrix4x4 ToMatrix4x4()
        => new(X.X, X.Y, X.Z, 0,
               Y.X, Y.Y, Y.Z, 0,
               Z.X, Z.Y, Z.Z, 0,
               Origin.X, Origin.Y, Origin.Z, 1);
}
