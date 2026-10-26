using System.Numerics;

namespace DollyForge.Core;

/// <summary>구동기 사양 (예: 태엽 모터). 토크 N·m, 속도 rad/s, 저장 에너지 J.</summary>
public sealed record ActuatorSpec(float MaxTorque, float MaxSpeed, float EnergyJ);

/// <summary>제작 가능한 부품 정의. 질량은 기본적으로 재료 밀도 × 형상 부피로 계산된다.</summary>
public sealed record PartDef(
    string Id,
    string NameKo,
    Shape Shape,
    MaterialDef Material,
    string DescriptionKo,
    float? MassOverride = null,
    ActuatorSpec? Actuator = null)
{
    public float Mass => MassOverride ?? Material.Density * Shape.Volume;
    public Vector3 Inertia => Shape.PrincipalInertia(Mass);

    /// <summary>카탈로그 품번 (구매한 부품일 때)</summary>
    public string? Sku { get; init; }
    /// <summary>구매 단가 (원). 길이 재단품은 재단비 포함.</summary>
    public int PriceKrw { get; init; }
    public PartRole Role { get; init; } = PartRole.Structure;
    /// <summary>축 지름 mm (축·모터 출력축)</summary>
    public float ShaftMm { get; init; }
    /// <summary>구멍 지름 mm (바퀴 허브·베어링 내경)</summary>
    public float BoreMm { get; init; }
    public ElectricalSpec? Electrical { get; init; }
}

public enum PartRole { Structure, Profile, Shaft, Bearing, Wheel, Motor, Battery }

/// <summary>전기 사양: 정격 전압 V, 배터리 용량 Wh, 모터 정격 전류 A</summary>
public sealed record ElectricalSpec(float Volts, float CapacityWh = 0, float RatedCurrentA = 0);

public sealed class PartInstance(int id, PartDef def, Pose pose)
{
    public int Id { get; } = id;
    public PartDef Def { get; } = def;
    /// <summary>월드 자세 (게임 좌표: Y-up, 미터)</summary>
    public Pose Pose { get; set; } = pose;
    /// <summary>URDF 링크·USD 프림 이름. USD 식별자 규칙([A-Za-z_][A-Za-z0-9_]*)을 따른다.</summary>
    public string LinkName => new string($"{Def.Id}_{Id}".Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray());
}

/// <summary>
/// Fusion 360 의 관절 종류에 대응한다. 관절 프레임: 원점 = Pivot, X축 = Axis, Y축 = Secondary.
/// </summary>
public enum JointKind
{
    /// <summary>Rigid — 용접·볼트 고정 (자유도 0)</summary>
    Fixed,
    /// <summary>Revolute — 각도 제한이 있는 회전 (경첩)</summary>
    Revolute,
    /// <summary>Revolute (무제한) — 차축</summary>
    Continuous,
    /// <summary>Slider — X축 직선 이동 (레일)</summary>
    Prismatic,
    /// <summary>Cylindrical — X축 회전 + X축 이동 (피스톤 로드)</summary>
    Cylindrical,
    /// <summary>Pin-Slot — X축 회전 + Y축 이동</summary>
    PinSlot,
    /// <summary>Planar — X축이 법선인 평면 위 이동 2 + X축 회전 1</summary>
    Planar,
    /// <summary>Ball — 구면 회전 3</summary>
    Ball,
}

public enum DofKind { Rotate, Translate }

/// <summary>관절 프레임 기준 자유도 하나</summary>
public readonly record struct Dof(DofKind Kind, Vector3 Axis);

/// <summary>
/// 두 부품을 잇는 관절. Pivot·Axis·Secondary 는 월드 좌표.
/// Effort 는 N·m (회전) 또는 N (직선), Velocity 는 rad/s 또는 m/s.
/// </summary>
public sealed class JointInstance(int id, JointKind kind, int parent, int child, Vector3 pivot, Vector3 axis)
{
    /// <summary>관절 종류별 자유도 (관절 프레임 기준)</summary>
    public IReadOnlyList<Dof> Dofs => Kind switch
    {
        JointKind.Fixed => [],
        JointKind.Revolute or JointKind.Continuous => [new(DofKind.Rotate, Vector3.UnitX)],
        JointKind.Prismatic => [new(DofKind.Translate, Vector3.UnitX)],
        JointKind.Cylindrical => [new(DofKind.Translate, Vector3.UnitX), new(DofKind.Rotate, Vector3.UnitX)],
        JointKind.PinSlot => [new(DofKind.Translate, Vector3.UnitY), new(DofKind.Rotate, Vector3.UnitX)],
        JointKind.Planar => [new(DofKind.Translate, Vector3.UnitY), new(DofKind.Translate, Vector3.UnitZ), new(DofKind.Rotate, Vector3.UnitX)],
        _ => [new(DofKind.Rotate, Vector3.UnitX), new(DofKind.Rotate, Vector3.UnitY), new(DofKind.Rotate, Vector3.UnitZ)],
    };

    /// <summary>두 번째 기준 방향 (Pin-Slot 의 슬롯 방향 등). Axis 와 직교화된다.</summary>
    public Vector3 Secondary { get; set; } = Vector3.Zero;

    /// <summary>월드 기준 관절 프레임 (X = Axis, Y = Secondary 직교화)</summary>
    public Quaternion FrameRotation
    {
        get
        {
            var x = Axis;
            var hint = Secondary.LengthSquared() > 1e-6f ? Secondary : (MathF.Abs(x.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitZ);
            var z = Vector3.Normalize(Vector3.Cross(x, hint));
            var y = Vector3.Cross(z, x);
            return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(new Matrix4x4(
                x.X, x.Y, x.Z, 0,
                y.X, y.Y, y.Z, 0,
                z.X, z.Y, z.Z, 0,
                0, 0, 0, 1)));
        }
    }

    public Pose Frame => new(Pivot, FrameRotation);

    public int Id { get; } = id;
    public JointKind Kind { get; } = kind;
    public int Parent { get; set; } = parent;
    public int Child { get; set; } = child;
    /// <summary>월드 기준 관절 위치. 기기가 움직이면 게임 쪽에서 갱신한다.</summary>
    public Vector3 Pivot { get; set; } = pivot;
    private Vector3 _axis = Vector3.Normalize(axis);
    public Vector3 Axis { get => _axis; set => _axis = Vector3.Normalize(value); }
    public float Lower { get; init; } = -MathF.PI;
    public float Upper { get; init; } = MathF.PI;
    public float Effort { get; set; } = 0;
    public float Velocity { get; set; } = 0;
    /// <summary>체결에 쓰인 소모품 (품번 → 수량). BOM 에 들어간다.</summary>
    public Dictionary<string, int> Consumed { get; init; } = new();

    /// <summary>이 관절을 구동하는 구동기 부품 id (없으면 수동)</summary>
    public int? DrivenBy { get; set; }
    public string Name => $"{Kind.ToString().ToLowerInvariant()}_{Id}";
}

public sealed class Assembly(string name)
{
    public string Name { get; set; } = name;
    public List<PartInstance> Parts { get; } = [];
    public List<JointInstance> Joints { get; } = [];

    private int _nextPart = 1, _nextJoint = 1;

    public PartInstance AddPart(PartDef def, Pose pose)
    {
        var p = new PartInstance(_nextPart++, def, pose);
        Parts.Add(p);
        return p;
    }

    public JointInstance AddJoint(JointKind kind, PartInstance parent, PartInstance child, Vector3 pivot, Vector3 axis,
        float lower = -MathF.PI, float upper = MathF.PI, Vector3? secondary = null)
    {
        var j = new JointInstance(_nextJoint++, kind, parent.Id, child.Id, pivot, axis)
        {
            Lower = lower, Upper = upper, Secondary = secondary ?? Vector3.Zero,
        };
        Joints.Add(j);
        return j;
    }

    public PartInstance Part(int id) => Parts.First(p => p.Id == id);

    public float TotalMass => Parts.Sum(p => p.Def.Mass);

    /// <summary>
    /// URDF 는 트리 구조만 허용한다. root 에서 너비 우선으로 신장 트리를 만들고,
    /// 필요하면 관절의 부모/자식을 뒤집는다. 고리를 만드는 관절은 Loops 로 따로 돌려준다.
    /// 연결되지 않은 부품은 Disconnected 로 돌려준다.
    /// </summary>
    public (List<JointInstance> Tree, List<JointInstance> Loops, List<PartInstance> Disconnected) SpanningTree(int rootId)
    {
        var visited = new HashSet<int> { rootId };
        var tree = new List<JointInstance>();
        var queue = new Queue<int>([rootId]);
        var used = new HashSet<int>();
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            foreach (var j in Joints)
            {
                if (used.Contains(j.Id)) continue;
                int other;
                if (j.Parent == cur) other = j.Child;
                else if (j.Child == cur) other = j.Parent;
                else continue;
                if (visited.Contains(other)) continue;
                if (j.Child == cur) (j.Parent, j.Child) = (j.Child, j.Parent);
                visited.Add(other);
                used.Add(j.Id);
                tree.Add(j);
                queue.Enqueue(other);
            }
        }
        var loops = Joints.Where(j => !used.Contains(j.Id)).ToList();
        var disconnected = Parts.Where(p => !visited.Contains(p.Id)).ToList();
        return (tree, loops, disconnected);
    }

    /// <summary>관절로 이어진 부품 묶음(기기 하나)의 부품 id 들</summary>
    public HashSet<int> ConnectedTo(int partId)
    {
        var seen = new HashSet<int> { partId };
        var stack = new Stack<int>([partId]);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            foreach (var j in Joints)
            {
                var other = j.Parent == cur ? j.Child : j.Child == cur ? j.Parent : -1;
                if (other >= 0 && seen.Add(other)) stack.Push(other);
            }
        }
        return seen;
    }

    /// <summary>부품 일부와 그 사이 관절만으로 새 조립품을 만든다 (내보내기용). 부품 id 는 새로 매긴다.</summary>
    public Assembly Extract(IEnumerable<int> partIds, string name)
    {
        var ids = partIds.ToHashSet();
        var sub = new Assembly(name);
        var map = new Dictionary<int, PartInstance>();
        foreach (var p in Parts.Where(p => ids.Contains(p.Id)).OrderBy(p => p.Id))
            map[p.Id] = sub.AddPart(p.Def, p.Pose);
        foreach (var j in Joints.Where(j => ids.Contains(j.Parent) && ids.Contains(j.Child)))
        {
            var nj = sub.AddJoint(j.Kind, map[j.Parent], map[j.Child], j.Pivot, j.Axis, j.Lower, j.Upper, j.Secondary);
            nj.Effort = j.Effort;
            nj.Velocity = j.Velocity;
            foreach (var (k, v) in j.Consumed) nj.Consumed[k] = v;
            if (j.DrivenBy is { } m && map.TryGetValue(m, out var motor)) nj.DrivenBy = motor.Id;
        }
        return sub;
    }

    public void RemovePart(int id)
    {
        Parts.RemoveAll(p => p.Id == id);
        Joints.RemoveAll(j => j.Parent == id || j.Child == id);
    }

    /// <summary>가장 무거운 부품을 기본 몸체(root)로 삼는다.</summary>
    public int DefaultRoot() => Parts.OrderByDescending(p => p.Def.Mass).First().Id;

    /// <summary>
    /// 조립품 물리 특성 (Fusion 360 의 Properties): 총질량, 질량 중심, 질량 중심 기준 관성 텐서(월드 축).
    /// 각 부품의 주관성 모멘트를 월드 축으로 회전하고 평행축 정리로 합친다.
    /// </summary>
    public MassProperties ComputeMassProperties()
    {
        var mass = TotalMass;
        if (mass <= 0) return new(0, Vector3.Zero, new double[3, 3]);
        var com = Parts.Aggregate(Vector3.Zero, (acc, p) => acc + p.Pose.Position * p.Def.Mass) / mass;
        var I = new double[3, 3];
        foreach (var p in Parts)
        {
            var R = Matrix4x4.CreateFromQuaternion(p.Pose.Rotation);
            var d = p.Def.Inertia;
            double[,] r = { { R.M11, R.M21, R.M31 }, { R.M12, R.M22, R.M32 }, { R.M13, R.M23, R.M33 } };
            double[] diag = { d.X, d.Y, d.Z };
            var off = p.Pose.Position - com;
            double[] o = { off.X, off.Y, off.Z };
            for (var a = 0; a < 3; a++)
            for (var b = 0; b < 3; b++)
            {
                double rot = 0;
                for (var k = 0; k < 3; k++) rot += r[a, k] * diag[k] * r[b, k];
                var parallel = p.Def.Mass * ((a == b ? Vector3.Dot(off, off) : 0) - o[a] * o[b]);
                I[a, b] += rot + parallel;
            }
        }
        return new(mass, com, I);
    }
}

/// <summary>총질량 kg, 질량 중심 m (월드), 질량 중심 기준 관성 텐서 kg·m² (월드 축)</summary>
public readonly record struct MassProperties(float Mass, Vector3 CenterOfMass, double[,] Inertia);

/// <summary>
/// 게임 좌표(Godot: Y-up, -Z 전방) → ROS/USD 좌표(Z-up, +X 전방, +Y 왼쪽) 변환.
/// 회전 행렬 C: x_r = -z_g, y_r = -x_g, z_r = y_g (det = +1).
/// 프레임은 왼쪽에서 곱해 변환하므로(F_r = C ∘ F_g) 부품 로컬 치수와 상대 관절 자세는 바뀌지 않는다.
/// </summary>
public static class Frames
{
    public static readonly Quaternion GameToRos = Quaternion.CreateFromRotationMatrix(new Matrix4x4(
        0, -1, 0, 0,
        0, 0, 1, 0,
        -1, 0, 0, 0,
        0, 0, 0, 1));

    public static Vector3 ToRos(Vector3 v) => Vector3.Transform(v, GameToRos);

    public static Pose ToRos(Pose p) => new(ToRos(p.Position), Quaternion.Normalize(GameToRos * p.Rotation));

    /// <summary>
    /// URDF rpy (고정축 X→Y→Z, 즉 R = Rz(yaw)·Ry(pitch)·Rx(roll)).
    /// 회전 행렬에서 뽑고, pitch = ±90° (짐벌 잠금)일 때는 yaw = 0 으로 두고 roll 로 나머지를 표현한다.
    /// </summary>
    public static Vector3 ToRpy(Quaternion q)
    {
        q = Quaternion.Normalize(q);
        double w = q.W, x = q.X, y = q.Y, z = q.Z;
        double r00 = 1 - 2 * (y * y + z * z), r10 = 2 * (x * y + w * z), r20 = 2 * (x * z - w * y);
        double r21 = 2 * (y * z + w * x), r22 = 1 - 2 * (x * x + y * y);
        double r11 = 1 - 2 * (x * x + z * z), r12 = 2 * (y * z - w * x);
        if (Math.Abs(r20) < 1 - 1e-9)
        {
            var pitch = Math.Asin(-r20);
            return new((float)Math.Atan2(r21, r22), (float)pitch, (float)Math.Atan2(r10, r00));
        }
        var p = r20 < 0 ? Math.PI / 2 : -Math.PI / 2;
        return new((float)Math.Atan2(-r12, r11), (float)p, 0);
    }

    /// <summary>a 를 b 로 돌리는 최소 회전</summary>
    public static Quaternion FromTo(Vector3 a, Vector3 b)
    {
        a = Vector3.Normalize(a);
        b = Vector3.Normalize(b);
        var d = Vector3.Dot(a, b);
        if (d > 0.999999f) return Quaternion.Identity;
        if (d < -0.999999f)
        {
            var ortho = MathF.Abs(a.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(Vector3.Cross(a, ortho)), MathF.PI);
        }
        var c = Vector3.Cross(a, b);
        return Quaternion.Normalize(new Quaternion(c, 1 + d));
    }
}
