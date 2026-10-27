using System.Numerics;

namespace DollyForge.Core;

/// <summary>사이보그 몸 모듈의 부위</summary>
public enum BodySlot { Pelvis, Torso, Head, UpperArmL, UpperArmR, ForearmL, ForearmR, HandL, HandR, ThighL, ThighR, ShinL, ShinR, FootL, FootR }

/// <summary>
/// 팔다리 모듈 종류. 실제 구동기 선택처럼 득실이 있다:
/// 표준, 고출력(HD: 토크 ×1.8, 질량 ×1.25, 값 ×2.2), 경량(LITE: 토크 그대로, 질량 ×0.7, 값 ×1.6 — 박육 티타늄 격자).
/// </summary>
public enum ModuleVariant { Standard, HeavyDuty, Lite }

/// <summary>몸 구성: 부위별 모듈 종류와 몸에 찬 기기들. 통신망 서버에 계정별로 저장된다.</summary>
public sealed record BodyConfig(byte Gender, Dictionary<BodySlot, ModuleVariant> Modules, List<string> Worn)
{
    public static BodyConfig Default(byte gender) => new(gender, [], []);
    public ModuleVariant VariantAt(BodySlot slot) => Modules.GetValueOrDefault(slot, ModuleVariant.Standard);

    /// <summary>서버에 저장하는 형태: 표준이 아닌 모듈의 품번</summary>
    public List<string> ModuleSkus() =>
        [.. Modules.Where(m => m.Value != ModuleVariant.Standard).OrderBy(m => m.Key).Select(m => Cyborg.Module(Cyborg.Of(Gender), m.Key, m.Value).Sku!)];

    public static BodyConfig FromSkus(byte gender, IEnumerable<string> modules, IEnumerable<string> worn)
    {
        var map = new Dictionary<BodySlot, ModuleVariant>();
        foreach (var sku in modules)
        {
            var def = Catalog.Get(sku).Build()!;
            if (Cyborg.SlotOf(def) is { } slot && Cyborg.GenderOf(def) == gender) map[slot] = Cyborg.VariantOf(def);
        }
        return new(gender, map, [.. worn]);
    }
}

/// <summary>몸 구성에서 계산한 능력: 질량, 두 팔로 들 수 있는 힘, 제자리 점프 이륙 속도</summary>
public sealed record BodyCapabilities(float MassKg, float LiftN, float JumpSpeed, float KneeTorque, float ElbowTorque)
{
    public float JumpHeight => JumpSpeed * JumpSpeed / (2 * 9.81f);
}

/// <summary>
/// Android Cyborg (안드로이드 사이보그): 사람과 로봇이 합쳐진 몸.
///  · Cyborg — 생체 쪽. 사람의 몸을 로봇에 옮겨 넣어 자유롭게 움직이게 하는 것이 목적이며,
///    로봇이라기보다 줄기세포·장기 배양체(배양 장기 코어)와 신경 중추다.
///  · Android Tool Environment (ATE) — 기계 쪽. 대사 지원 유닛이 물리·대사 활동을 보장하고,
///    모듈식 몸이 로봇으로서 기능한다. 비행 같은 기능은 시계·헤드폰처럼 몸에 차는 기기로 더한다.
/// 몸 전체가 모듈이라 로봇처럼 계속 떼고 붙이며 개조할 수 있다 (같은 부품·관절·URDF 체계).
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

    public static readonly BodyType Female = new(0, "여성형", 1.80f, 60f, 0.37f, 0.35f);
    public static readonly BodyType Male = new(1, "남성형", 1.86f, 76f, 0.46f, 0.33f);
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

    public const float OrganCoreKg = 3.5f;
    public const float LifeSupportKg = 2.4f;

    /// <summary>배양 장기 코어: 줄기세포에서 기른 장기 배양체 (Cyborg 의 생체 쪽). 현재 기술로는 불가능 → 가상 부품.</summary>
    public static PartDef OrganCore(BodyType body) => new(
        $"cy_{(body.Gender == 1 ? "m" : "f")}_organs", $"{body.NameKo} 배양 장기 코어", Shape.Cylinder(0.07f, 0.2f), Materials.Titanium,
        "줄기세포·장기 배양체. 사람 몸에서 옮겨 온 생체 부분이며 대사 지원 유닛이 살려 둔다.", OrganCoreKg)
    {
        Sku = $"CY-{(body.Gender == 1 ? "M" : "F")}-ORGANS", Role = PartRole.BioCore, PriceKrw = 0, Fictional = true,
        RealEquivalentKo = "현실 대체 없음 — 장기 오가노이드는 연구 단계 (수 mm 크기)",
    };

    /// <summary>모듈 부품 정의. 질량은 실제 분절 질량, 구동기는 그 분절을 매다는 관절의 것.</summary>
    /// <summary>바꿔 끼울 수 있는 팔다리 부위</summary>
    public static readonly BodySlot[] Upgradable =
    [
        BodySlot.UpperArmL, BodySlot.UpperArmR, BodySlot.ForearmL, BodySlot.ForearmR,
        BodySlot.ThighL, BodySlot.ThighR, BodySlot.ShinL, BodySlot.ShinR,
    ];

    static (float Torque, float Mass, float Price, string Sku, string Ko) Tune(ModuleVariant v) => v switch
    {
        ModuleVariant.HeavyDuty => (1.8f, 1.25f, 2.2f, "-HD", " (고출력 HD)"),
        ModuleVariant.Lite => (1.0f, 0.7f, 1.6f, "-LITE", " (경량 LITE)"),
        _ => (1f, 1f, 1f, "", ""),
    };

    public static PartDef Module(BodyType body, BodySlot slot, ModuleVariant variant = ModuleVariant.Standard)
    {
        var seg = Segments.First(s => s.Slot == slot);
        var head = slot == BodySlot.Head;
        if (variant != ModuleVariant.Standard && !Upgradable.Contains(slot))
            throw new ArgumentException($"{slot} 모듈에는 {variant} 종류가 없습니다.");
        var t = Tune(variant);
        // 흉부 프레임의 질량에서 안에 든 배양 장기 코어·대사 지원 유닛을 뺀다 (몸 전체 = 실제 체중)
        var mass = (body.Mass * seg.MassFrac - (slot == BodySlot.Torso ? OrganCoreKg + LifeSupportKg : 0)) * t.Mass;
        // 구동기는 몸무게에 맞춰 고른다: 남성형(76 kg)은 여성형(60 kg)보다 정격 토크가 76/60 배
        var torque = MathF.Round(seg.Torque * t.Torque * body.Mass / Female.Mass);
        return new PartDef(
            $"cy_{(body.Gender == 1 ? "m" : "f")}_{slot.ToString().ToLowerInvariant()}{t.Sku.ToLowerInvariant()}",
            $"{body.NameKo} {seg.NameKo}{t.Ko}",
            seg.Shape(body),
            Materials.Titanium,
            $"전신 사이보그 모듈. {seg.ActuatorKo}" + (torque > 0 ? $" (최대 {torque:0} N·m)" : "") + ". 모듈 커넥터로 떼고 붙인다.",
            mass,
            torque > 0 ? new ActuatorSpec(torque, seg.Speed, 0) : null)
        {
            Sku = $"CY-{(body.Gender == 1 ? "M" : "F")}-{slot.ToString().ToUpperInvariant()}{t.Sku}",
            Role = PartRole.Structure,
            PriceKrw = (int)(ModulePrice(seg) * t.Price),
            Electrical = torque > 0 ? new ElectricalSpec(48, RatedCurrentA: torque / 10f) : null,
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

    /// <summary>품번 "CY-F-SHINL-HD" → 부위 ShinL</summary>
    public static BodySlot? SlotOf(PartDef def) =>
        def.Sku is { } sku && sku.StartsWith("CY-") && sku.Split('-') is { Length: >= 3 } p && Enum.TryParse<BodySlot>(p[2], true, out var s) ? s : null;

    public static ModuleVariant VariantOf(PartDef def) =>
        def.Sku?.EndsWith("-HD") == true ? ModuleVariant.HeavyDuty : def.Sku?.EndsWith("-LITE") == true ? ModuleVariant.Lite : ModuleVariant.Standard;

    public static byte? GenderOf(PartDef def) => def.Sku is { } sku && sku.StartsWith("CY-") ? (byte)(sku[3] == 'M' ? 1 : 0) : null;

    /// <summary>카탈로그에 올라가는 몸 모듈 (팔다리 × 종류 × 체형)</summary>
    public static IEnumerable<PartDef> PurchasableModules() =>
        from body in new[] { Female, Male }
        from slot in Upgradable
        from v in Enum.GetValues<ModuleVariant>()
        select Module(body, slot, v);

    /// <summary>
    /// 몸 능력 (단순화한 생체역학):
    ///  · 들기 — 두 팔의 팔꿈치 토크 ÷ (전완 + 손 절반) 지레 팔.
    ///  · 점프 — 두 다리의 (엉덩이 + 무릎) 토크 ÷ 유효 지레(다리 길이의 40 %)로 내는 다리 신전력이
    ///    다리 길이 40 % 를 미는 동안 몸을 가속한다. 표준 체형에서 이륙 약 3.4 m/s (높이 약 0.6 m).
    /// </summary>
    public static BodyCapabilities Capabilities(BodyConfig config)
    {
        var b = Of(config.Gender);
        var mass = Assemble(config, Vector3.Zero).TotalMass; // 찬 기기 포함
        float Torque(BodySlot slot) => Module(b, slot, config.VariantAt(slot)).Actuator?.MaxTorque ?? 0;
        var forearm = 0.146f * b.Height + 0.108f * b.Height / 2;
        var lift = (Torque(BodySlot.ForearmL) + Torque(BodySlot.ForearmR)) / forearm;
        var push = 0.4f * (0.245f + 0.246f) * b.Height;
        var legForce = (Torque(BodySlot.ThighL) + Torque(BodySlot.ThighR) + Torque(BodySlot.ShinL) + Torque(BodySlot.ShinR)) / push;
        var accel = MathF.Max(legForce / mass - 9.81f, 0);
        return new BodyCapabilities(mass, lift, MathF.Sqrt(2 * accel * push), Torque(BodySlot.ShinL), Torque(BodySlot.ForearmL));
    }

    /// <summary>
    /// 선 자세의 전신 사이보그를 모듈 조립품으로 만든다 (게임 좌표: Y-up, 정면 -Z, 발바닥 높이 = feet.Y).
    /// 모든 관절은 실제 가동 범위와 구동기 토크를 갖는다.
    /// </summary>
    public static Assembly Assemble(BodyType b, Vector3 feet) => Assemble(BodyConfig.Default(b.Gender), feet);

    public static Assembly Assemble(BodyConfig config, Vector3 feet)
    {
        var b = Of(config.Gender);
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

        PartInstance Put(BodySlot slot, Vector3 at, Quaternion rot) => parts[slot] = a.AddPart(Module(b, slot, config.VariantAt(slot)), new Pose(at, rot));
        Put(BodySlot.Pelvis, new(feet.X, hipY, feet.Z), Quaternion.Identity);
        var torso = Put(BodySlot.Torso, new(feet.X, hipY + pelvisH / 2 + torsoH / 2, feet.Z), Quaternion.Identity);
        var headRadius = 0.105f * H / 1.7f;
        Put(BodySlot.Head, new(feet.X, shoulderY + 0.0055f * H + headRadius, feet.Z), Quaternion.Identity);
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

        // 흉부 안: 생체 쪽(배양 장기 코어)과 ATE 의 대사 지원 유닛. 그 질량만큼 흉부 프레임은 가볍다.
        var organs = a.AddPart(OrganCore(b), new Pose(torso.Pose.Position + new Vector3(0, 0.03f, 0), Quaternion.Identity));
        var msu = a.AddPart(Catalog.Get("ATE-MSU-2").Build()!, new Pose(torso.Pose.Position + new Vector3(0, -0.1f, 0.02f), Quaternion.Identity));
        a.AddJoint(JointKind.Fixed, torso, organs, organs.Pose.Position, Vector3.UnitX).Consumed[Connector] = 1;
        a.AddJoint(JointKind.Fixed, torso, msu, msu.Pose.Position, Vector3.UnitX).Consumed[Connector] = 1;

        // 관절: 자식 모듈의 몸 쪽 끝(근위단)에 둔다. 굽힘 축은 좌우(X) — 무릎은 뒤로, 팔꿈치는 앞으로만 접힌다.
        foreach (var (child, parent) in ParentOf)
        {
            var seg = Segments.First(s => s.Slot == child);
            var c = parts[child];
            var top = c.Def.Shape.Kind == ShapeKind.Cylinder ? c.Def.Shape.Length / 2 : c.Def.Shape.Kind == ShapeKind.Box ? c.Def.Shape.Size.Y / 2 : c.Def.Shape.Radius;
            var pivot = c.Pose.Position + Vector3.UnitY * top;
            Attach(a, parts[parent], c, pivot, seg);
        }

        // 몸에 찬 Android Tool 기기: 손목형은 왼쪽부터 전완 끝 바깥쪽에, 머리형은 머리 위에 고정 (커넥터 없이 찬다)
        var wrists = new Queue<(BodySlot Slot, float Side)>([(BodySlot.ForearmL, -1f), (BodySlot.ForearmR, 1f)]);
        foreach (var sku in config.Worn)
        {
            var gear = Catalog.Get(sku).Build()!;
            (PartInstance on, Vector3 at) = gear.Wear switch
            {
                WearSlot.Wrist when wrists.TryDequeue(out var w) =>
                    (parts[w.Slot], parts[w.Slot].Pose.Position + new Vector3(w.Side * 0.06f, -0.146f * H * 0.35f, 0)),
                WearSlot.Head => (parts[BodySlot.Head], parts[BodySlot.Head].Pose.Position + new Vector3(0, 0.07f * H / 1.7f, 0)),
                _ => throw new ArgumentException($"{gear.NameKo} 은(는) 몸에 찰 수 없습니다."),
            };
            var g = a.AddPart(gear, new Pose(at, Quaternion.Identity));
            a.AddJoint(JointKind.Fixed, on, g, at, Vector3.UnitX);
        }
        return a;
    }

    static void Attach(Assembly a, PartInstance parent, PartInstance child, Vector3 pivot, Segment seg)
    {
        var j = a.AddJoint(seg.Joint, parent, child, pivot, Vector3.UnitX, seg.Lower, seg.Upper);
        j.Effort = child.Def.Actuator?.MaxTorque ?? seg.Torque;
        j.Velocity = seg.Speed;
        j.Consumed[Connector] = 1;
    }

    /// <summary>
    /// 비행: 몸에 찬 추력기의 합력으로 낼 수 있는 수직 가속도 (m/s², 중력 포함).
    /// 0 보다 커야 떠오른다. 실제 마이크로 제트 터빈 둘(500 N)로는 사람 하나를 띄우지 못한다.
    /// </summary>
    public static float LiftAcceleration(float bodyMassKg, IEnumerable<PartDef> worn)
    {
        var gear = worn.ToList();
        var mass = bodyMassKg + gear.Sum(g => g.Mass);
        return gear.Sum(g => g.ThrustN) / mass - 9.81f;
    }

    /// <summary>
    /// 개조: 같은 부위의 다른 모듈로 바꾼다 (예: 표준 대퇴 → 강화 대퇴). 관절은 그대로 두고 부품만 교체한다.
    /// </summary>
    public static (bool Ok, string MessageKo) CanAttach(BodySlot child, BodySlot parent) =>
        ParentOf.TryGetValue(child, out var p) && p == parent
            ? (true, "모듈 커넥터 연결")
            : (false, $"{child} 모듈은 {(ParentOf.TryGetValue(child, out var q) ? q : BodySlot.Pelvis)} 에만 붙습니다.");
}
