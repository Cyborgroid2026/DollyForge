using System.Numerics;

namespace DollyForge.Core;

/// <summary>사이보그 몸 모듈의 부위</summary>
public enum BodySlot { Pelvis, Torso, Head, UpperArmL, UpperArmR, ForearmL, ForearmR, HandL, HandR, ThighL, ThighR, ShinL, ShinR, FootL, FootR }

/// <summary>
/// 전신 사이보그: 수술로 사람을 사이보그로 옮겨 조립한 몸. 몸 전체가 모듈로 되어 있어
/// 로봇처럼 계속 떼고 붙이며 개조할 수 있다 (같은 부품·관절·URDF 체계).
///
/// 치수와 질량은 실제 사람 인체 측정 자료(Winter 의 분절 비율)를 따른다:
/// 키 H 에 대한 분절 길이, 체중 M 에 대한 분절 질량. 관절 가동 범위는 사람의 정상 범위,
/// 구동기 토크는 사람 보행·일상 동작에 필요한 최대 관절 토크 수준이다.
/// 머리 모듈의 "신경 중추"(뇌·척수 생명 유지 + 신경 접속)는 현재 기술로 불가능하므로 가상 부품으로 표시한다.
/// </summary>
public static class Cyborg
{
    public const string Connector = "MC-MODULE-LINK";

    public sealed record BodyType(byte Gender, string NameKo, float Height, float Mass, float ShoulderWidth, float HipWidth);

    public static readonly BodyType Female = new(0, "여성형", 1.66f, 60f, 0.37f, 0.35f);
    public static readonly BodyType Male = new(1, "남성형", 1.78f, 76f, 0.46f, 0.33f);
    public static BodyType Of(byte gender) => gender == 1 ? Male : Female;

    /// <summary>분절: Winter (2009) 분절 질량 비율과 길이 비율</summary>
    sealed record Segment(BodySlot Slot, string NameKo, float MassFrac, Func<BodyType, Shape> Shape,
        JointKind Joint, float Lower, float Upper, float Torque, float Speed, string ActuatorKo);

    static readonly Segment[] Segments =
    [
        new(BodySlot.Pelvis, "골반 프레임", 0.142f, b => Core.Shape.Box(b.HipWidth, 0.18f * b.Height / 1.7f, 0.2f), JointKind.Fixed, 0, 0, 0, 0, "기준 몸체"),
        new(BodySlot.Torso, "흉부 프레임", 0.355f, b => Core.Shape.Box(b.ShoulderWidth * 0.85f, 0.288f * b.Height, 0.22f), JointKind.Ball, -0.5f, 0.5f, 180, 3, "허리 3축 구동기"),
        new(BodySlot.Head, "머리 모듈 (신경 중추)", 0.081f, b => Core.Shape.Sphere(0.105f * b.Height / 1.7f), JointKind.Ball, -1.0f, 1.0f, 20, 6, "목 3축 구동기"),
        new(BodySlot.UpperArmL, "왼 상완", 0.028f, b => Core.Shape.Cylinder(0.045f, 0.186f * b.Height), JointKind.Ball, -2.6f, 2.6f, 60, 6, "어깨 3축 구동기"),
        new(BodySlot.UpperArmR, "오른 상완", 0.028f, b => Core.Shape.Cylinder(0.045f, 0.186f * b.Height), JointKind.Ball, -2.6f, 2.6f, 60, 6, "어깨 3축 구동기"),
        new(BodySlot.ForearmL, "왼 전완", 0.016f, b => Core.Shape.Cylinder(0.037f, 0.146f * b.Height), JointKind.Revolute, 0, 2.5f, 40, 8, "팔꿈치 구동기"),
        new(BodySlot.ForearmR, "오른 전완", 0.016f, b => Core.Shape.Cylinder(0.037f, 0.146f * b.Height), JointKind.Revolute, 0, 2.5f, 40, 8, "팔꿈치 구동기"),
        new(BodySlot.HandL, "왼손", 0.006f, b => Core.Shape.Box(0.03f, 0.108f * b.Height, 0.085f), JointKind.Revolute, -1.2f, 1.2f, 10, 10, "손목 구동기"),
        new(BodySlot.HandR, "오른손", 0.006f, b => Core.Shape.Box(0.03f, 0.108f * b.Height, 0.085f), JointKind.Revolute, -1.2f, 1.2f, 10, 10, "손목 구동기"),
        new(BodySlot.ThighL, "왼 대퇴", 0.100f, b => Core.Shape.Cylinder(0.075f, 0.245f * b.Height), JointKind.Ball, -1.6f, 2.0f, 150, 6, "고관절 3축 구동기"),
        new(BodySlot.ThighR, "오른 대퇴", 0.100f, b => Core.Shape.Cylinder(0.075f, 0.245f * b.Height), JointKind.Ball, -1.6f, 2.0f, 150, 6, "고관절 3축 구동기"),
        new(BodySlot.ShinL, "왼 하퇴", 0.0465f, b => Core.Shape.Cylinder(0.05f, 0.246f * b.Height), JointKind.Revolute, -2.4f, 0, 120, 8, "무릎 구동기"),
        new(BodySlot.ShinR, "오른 하퇴", 0.0465f, b => Core.Shape.Cylinder(0.05f, 0.246f * b.Height), JointKind.Revolute, -2.4f, 0, 120, 8, "무릎 구동기"),
        new(BodySlot.FootL, "왼발", 0.0145f, b => Core.Shape.Box(0.09f, 0.039f * b.Height, 0.152f * b.Height), JointKind.Revolute, -0.8f, 0.5f, 100, 6, "발목 구동기"),
        new(BodySlot.FootR, "오른발", 0.0145f, b => Core.Shape.Box(0.09f, 0.039f * b.Height, 0.152f * b.Height), JointKind.Revolute, -0.8f, 0.5f, 100, 6, "발목 구동기"),
    ];

    /// <summary>모듈을 어디에 붙일 수 있는가 (해부학적 연결): 자식 부위 → 부모 부위</summary>
    public static readonly IReadOnlyDictionary<BodySlot, BodySlot> ParentOf = new Dictionary<BodySlot, BodySlot>
    {
        [BodySlot.Torso] = BodySlot.Pelvis, [BodySlot.Head] = BodySlot.Torso,
        [BodySlot.UpperArmL] = BodySlot.Torso, [BodySlot.UpperArmR] = BodySlot.Torso,
        [BodySlot.ForearmL] = BodySlot.UpperArmL, [BodySlot.ForearmR] = BodySlot.UpperArmR,
        [BodySlot.HandL] = BodySlot.ForearmL, [BodySlot.HandR] = BodySlot.ForearmR,
        [BodySlot.ThighL] = BodySlot.Pelvis, [BodySlot.ThighR] = BodySlot.Pelvis,
        [BodySlot.ShinL] = BodySlot.ThighL, [BodySlot.ShinR] = BodySlot.ThighR,
        [BodySlot.FootL] = BodySlot.ShinL, [BodySlot.FootR] = BodySlot.ShinR,
    };

    /// <summary>모듈 부품 정의. 질량은 실제 분절 질량, 구동기는 그 분절을 매다는 관절의 것.</summary>
    public static PartDef Module(BodyType body, BodySlot slot)
    {
        var seg = Segments.First(s => s.Slot == slot);
        var head = slot == BodySlot.Head;
        return new PartDef(
            $"cy_{(body.Gender == 1 ? "m" : "f")}_{slot.ToString().ToLowerInvariant()}",
            $"{body.NameKo} {seg.NameKo}",
            seg.Shape(body),
            Materials.Titanium,
            $"전신 사이보그 모듈. {seg.ActuatorKo}" + (seg.Torque > 0 ? $" (최대 {seg.Torque} N·m)" : "") + ". 모듈 커넥터로 떼고 붙인다.",
            body.Mass * seg.MassFrac,
            seg.Torque > 0 ? new ActuatorSpec(seg.Torque, seg.Speed, 0) : null)
        {
            Sku = $"CY-{(body.Gender == 1 ? "M" : "F")}-{slot.ToString().ToUpperInvariant()}",
            Role = PartRole.Structure,
            PriceKrw = ModulePrice(seg),
            Electrical = seg.Torque > 0 ? new ElectricalSpec(48, RatedCurrentA: seg.Torque / 10f) : null,
            Fictional = head,
            RealEquivalentKo = head ? "현실 대체 없음 — 뇌 이식·신경 접속은 현재 불가능. 원격 조종 헤드(카메라·IMU·제어기)로 대체" : null,
        };
    }

    // 실제 로봇 관절 구동기 가격대를 토크로 근사 (예: 고관절급 150 N·m ≈ 수백만 원)
    static int ModulePrice(Segment seg) => seg.Torque switch
    {
        0 => 1_200_000,
        _ => (int)(seg.Torque * 18_000 + (seg.Joint == JointKind.Ball ? 900_000 : 300_000)),
    };

    public static BodySlot? SlotOf(PartDef def) =>
        def.Sku is { } sku && sku.StartsWith("CY-") && Enum.TryParse<BodySlot>(sku[5..], true, out var s) ? s : null;

    /// <summary>
    /// 선 자세의 전신 사이보그를 모듈 조립품으로 만든다 (게임 좌표: Y-up, 정면 -Z, 발바닥 높이 = feet.Y).
    /// 모든 관절은 실제 가동 범위와 구동기 토크를 갖는다.
    /// </summary>
    public static Assembly Assemble(BodyType b, Vector3 feet)
    {
        var a = new Assembly($"Cyborg {b.NameKo}");
        var H = b.Height;
        var parts = new Dictionary<BodySlot, PartInstance>();
        var footH = 0.039f * H;
        var shinL = 0.246f * H;
        var thighL = 0.245f * H;
        var hipY = feet.Y + footH + shinL + thighL;
        var pelvisH = 0.18f * H / 1.7f;
        var torsoH = 0.288f * H;
        var shoulderY = hipY + pelvisH / 2 + torsoH;
        var up = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2); // 원기둥 축(Z)을 세로로

        PartInstance Put(BodySlot slot, Vector3 at, Quaternion rot) => parts[slot] = a.AddPart(Module(b, slot), new Pose(at, rot));
        Put(BodySlot.Pelvis, new(feet.X, hipY, feet.Z), Quaternion.Identity);
        Put(BodySlot.Torso, new(feet.X, hipY + pelvisH / 2 + torsoH / 2, feet.Z), Quaternion.Identity);
        Put(BodySlot.Head, new(feet.X, shoulderY + 0.05f * H + 0.105f * H / 1.7f, feet.Z), Quaternion.Identity);
        foreach (var (side, ua, fa, hd, th, sh, ft) in new[]
                 {
                     (-1f, BodySlot.UpperArmL, BodySlot.ForearmL, BodySlot.HandL, BodySlot.ThighL, BodySlot.ShinL, BodySlot.FootL),
                     (1f, BodySlot.UpperArmR, BodySlot.ForearmR, BodySlot.HandR, BodySlot.ThighR, BodySlot.ShinR, BodySlot.FootR),
                 })
        {
            var sx = feet.X + side * (b.ShoulderWidth / 2 + 0.03f);
            Put(ua, new(sx, shoulderY - 0.186f * H / 2, feet.Z), up);
            Put(fa, new(sx, shoulderY - 0.186f * H - 0.146f * H / 2, feet.Z), up);
            Put(hd, new(sx, shoulderY - 0.186f * H - 0.146f * H - 0.108f * H / 2, feet.Z), Quaternion.Identity);
            var hx = feet.X + side * b.HipWidth / 4;
            Put(th, new(hx, hipY - thighL / 2, feet.Z), up);
            Put(sh, new(hx, hipY - thighL - shinL / 2, feet.Z), up);
            Put(ft, new(hx, feet.Y + footH / 2, feet.Z - 0.152f * H * 0.3f), Quaternion.Identity);
        }

        // 관절: 자식 모듈의 몸 쪽 끝(근위단)에 둔다. 굽힘 축은 좌우(X) — 무릎은 뒤로, 팔꿈치는 앞으로만 접힌다.
        foreach (var (child, parent) in ParentOf)
        {
            var seg = Segments.First(s => s.Slot == child);
            var c = parts[child];
            var top = c.Def.Shape.Kind == ShapeKind.Cylinder ? c.Def.Shape.Length / 2 : c.Def.Shape.Kind == ShapeKind.Box ? c.Def.Shape.Size.Y / 2 : c.Def.Shape.Radius;
            var pivot = c.Pose.Position + Vector3.UnitY * top;
            Attach(a, parts[parent], c, pivot, seg);
        }
        return a;
    }

    static void Attach(Assembly a, PartInstance parent, PartInstance child, Vector3 pivot, Segment seg)
    {
        var j = a.AddJoint(seg.Joint, parent, child, pivot, Vector3.UnitX, seg.Lower, seg.Upper);
        j.Effort = seg.Torque;
        j.Velocity = seg.Speed;
        j.Consumed[Connector] = 1;
    }

    /// <summary>
    /// 개조: 같은 부위의 다른 모듈로 바꾼다 (예: 표준 대퇴 → 강화 대퇴). 관절은 그대로 두고 부품만 교체한다.
    /// </summary>
    public static (bool Ok, string MessageKo) CanAttach(BodySlot child, BodySlot parent) =>
        ParentOf.TryGetValue(child, out var p) && p == parent
            ? (true, "모듈 커넥터 연결")
            : (false, $"{child} 모듈은 {(ParentOf.TryGetValue(child, out var q) ? q : BodySlot.Pelvis)} 에만 붙습니다.");
}
