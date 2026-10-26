using Godot;
using DollyForge.Core;

namespace DollyForge.Game;

/// <summary>
/// 실제 엔진(Jolt 물리) 안에서 핵심 흐름을 자동으로 확인한다:
/// 구매 → 축 스냅 결합 → 전기 구동(같은 배선 제자리 회전, 반전 직진) → 진공관 서보 손잡이 → 캠 드럼 프로그램 → 닉시관 → 내보내기.
/// 실행: godot --path godot -- --selftest   (결과를 출력하고 종료 코드 0/1 로 끝난다)
/// </summary>
public partial class SelfTest : Node
{
    public Devices Devices { get; set; } = null!;
    int _failures;

    void Check(bool ok, string what)
    {
        GD.Print($"{(ok ? "✔" : "✘")} {what}");
        if (!ok) _failures++;
    }

    async Task Wait(double seconds) =>
        await ToSignal(GetTree().CreateTimer(seconds, processAlways: true, processInPhysics: true), SceneTreeTimer.SignalName.Timeout);

    public override async void _Ready()
    {
        try { await Run(); }
        catch (Exception e) { Check(false, $"예외: {e.Message}"); }
        GD.Print(_failures == 0 ? "SELFTEST PASS" : $"SELFTEST FAIL ({_failures})");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    /// <summary>견본 로봇(+ 추가 기기)을 마당 바닥에 내려놓고, 왼쪽 모터 배선을 반전해 직진하게 한다.</summary>
    List<PartBody> PlaceRobot(System.Numerics.Vector3 at, params string[] extras)
    {
        var robot = Blueprints.TwoWheelRobot(new ShopAccount(1_000_000), at);
        var chassis = robot.Parts.First(p => p.Def.Sku == "PL-AL6061-5");
        var x = -0.07f;
        foreach (var sku in extras)
        {
            var def = Catalog.Get(sku).Build()!;
            var h = def.Shape.Kind == ShapeKind.Cylinder ? def.Shape.Radius : def.Shape.Size.Y / 2;
            var part = robot.AddPart(def, new Pose(chassis.Pose.Position + new System.Numerics.Vector3(x, 0.0025f + h, 0.07f), System.Numerics.Quaternion.Identity));
            robot.AddJoint(JointKind.Fixed, chassis, part, part.Pose.Position, System.Numerics.Vector3.UnitX);
            x += 0.07f;
        }
        var before = Devices.Bodies.Count();
        Devices.Adopt(robot);
        var mine = Devices.Bodies.Skip(before).ToList();
        return mine;
    }

    static PartBody Find(List<PartBody> parts, PartRole role) => parts.First(b => b.Part.Def.Role == role);
    static PartBody Chassis(List<PartBody> parts) => parts.First(b => b.Part.Def.Sku == "PL-AL6061-5");

    async Task Run()
    {
        // 1. 구매: 금액·소모품·배송 부품
        var shop = Devices.Shop;
        var before = shop.BalanceKrw;
        var (ok, msg, parts) = shop.Purchase([
            new(Catalog.Get("MT-JGB37-100"), 1),
            new(Catalog.Get("WH-65-D6"), 1),
            new(Catalog.Get("FS-BOLT-M8"), 1),
        ]);
        Check(ok && parts.Count == 2 && shop.Consumables["FS-BOLT-M8"] == 10, $"구매: {msg}");
        Check(before - shop.BalanceKrw == 16000 + 3500 + 3000 + ShopAccount.ShippingKrw, $"결제 금액 ₩{before - shop.BalanceKrw:N0}");

        // 2. 축 스냅: 바퀴를 모터 축 근처에 두고 결합
        var motor = Devices.Spawn(parts[0], new Transform3D(new Basis(Vector3.Up, Mathf.Pi / 2), new Vector3(8, 0.5f, 8)));
        var wheel = Devices.Spawn(parts[1], new Transform3D(Basis.Identity, new Vector3(8.06f, 0.53f, 8.02f)));
        motor.Freeze = true;
        var (joined, jmsg) = Devices.Join(JoinMethod.Axle, motor, wheel, wheel.GlobalPosition);
        var onAxis = (wheel.GlobalPosition - motor.GlobalPosition).Cross(motor.PrincipalAxis()).Length();
        Check(joined && onAxis < 0.002f, $"축 스냅 결합: {jmsg} (축선 거리 {onAxis * 1000:0.0} mm)");
        var bad = Devices.Join(JoinMethod.Axle, motor, Devices.Spawn(Catalog.Get("WH-100-B8").Build()!, new Transform3D(Basis.Identity, new Vector3(7.9f, 0.5f, 8))), Vector3.Zero);
        Check(!bad.Ok, $"지름이 다른 바퀴 거부: {bad.Message}");

        // 3. 견본 로봇: 같은 배선 → 제자리 회전, 한쪽 반전 → 직진
        var r1 = PlaceRobot(new(-4, 0.02f, 10));
        var c1 = Chassis(r1);
        var battery = Find(r1, PartRole.Battery);
        await Wait(1.0);
        var start = c1.GlobalPosition;
        var e0 = battery.EnergyJ;
        GD.Print("  " + Devices.TogglePower(c1));
        await Wait(2.0);
        var moved = (c1.GlobalPosition - start) with { Y = 0 };
        Check(moved.Length() < 0.25f && Mathf.Abs(c1.AngularVelocity.Y) > 0.5f,
            $"같은 배선: 제자리 회전 (이동 {moved.Length():0.00} m, 회전 {Mathf.Abs(c1.AngularVelocity.Y):0.0} rad/s)");
        GD.Print("  " + Devices.FlipWiring(r1.First(b => b.Part.Def.Role == PartRole.Motor)));
        start = c1.GlobalPosition;
        await Wait(3.0);
        var full = ((c1.GlobalPosition - start) with { Y = 0 }).Length() / 3f;
        Check(full is > 0.15f and < 0.4f, $"한쪽 배선 반전: 직진 {full:0.00} m/s (이론 0.34 m/s)");
        Check(e0 - battery.EnergyJ > 0, $"배터리 소모 {e0 - battery.EnergyJ:0} J");
        GD.Print("  " + Devices.TogglePower(c1));
        Check(Devices.ExportDevice(c1).StartsWith("내보냄"), "내보내기 (URDF·USDA·STL·BOM)");

        // 4. 진공관 서보 증폭기: 손잡이 50 % → 속도 절반
        var r2 = PlaceRobot(new(4, 0.02f, 10), "QA-SERVO-1", "QA-NIXIE-4");
        var c2 = Chassis(r2);
        var amp = Find(r2, PartRole.Controller);
        Devices.FlipWiring(r2.First(b => b.Part.Def.Role == PartRole.Motor));
        for (var i = 0; i < 5; i++) Devices.TurnKnob(amp, -0.1f);
        await Wait(1.0);
        Devices.TogglePower(c2);
        await Wait(1.0);
        start = c2.GlobalPosition;
        await Wait(3.0);
        var half = ((c2.GlobalPosition - start) with { Y = 0 }).Length() / 3f;
        Check(Mathf.Abs(Devices.Knob(amp.Part.Id) - 0.5f) < 1e-4 && half > full * 0.35f && half < full * 0.65f,
            $"서보 손잡이 50 %: {half:0.00} m/s (전속 {full:0.00} m/s 의 {half / full * 100:0} %)");
        var nixie = Find(r2, PartRole.Display);
        Check(nixie.Readout?.Text.Contains('V') == true, $"닉시관 표시: \"{nixie.Readout?.Text}\"");
        Devices.TogglePower(c2);

        // 5. 캠 드럼: 4초 전진 · 1초 정지 · 2초 후진 · 1초 정지 → 8초 동안 순전진은 약 2초어치, 도중에 실제로 뒤로 간다
        var r3 = PlaceRobot(new(0, 0.02f, 16), "QA-DRUM-8");
        var c3 = Chassis(r3);
        Devices.FlipWiring(r3.First(b => b.Part.Def.Role == PartRole.Motor));
        await Wait(1.0);
        Devices.TogglePower(c3);
        await Wait(0.5);
        var origin = c3.GlobalPosition;
        var heading = (-c3.GlobalBasis.Z) with { Y = 0 };
        float maxAlong = 0, minAfterMax = float.MaxValue;
        for (var t = 0; t < 32; t++)
        {
            await Wait(0.25);
            var along = (c3.GlobalPosition - origin).Dot(heading.Normalized());
            if (MathF.Abs(along) > MathF.Abs(maxAlong)) { maxAlong = along; minAfterMax = MathF.Abs(along); }
            minAfterMax = MathF.Min(minAfterMax, MathF.Abs(along));
        }
        var net = ((c3.GlobalPosition - origin) with { Y = 0 }).Length();
        var reversed = MathF.Abs(maxAlong) - minAfterMax > 0.2f || MathF.Abs(maxAlong) - net > 0.2f;
        Check(net > full * 1.0f && net < full * 3.5f && reversed,
            $"캠 드럼 8초: 순이동 {net:0.00} m (전속 8초면 {full * 8:0.0} m), 최대 {MathF.Abs(maxAlong):0.00} m 후 되돌아옴");
        Devices.TogglePower(c3);
    }
}
