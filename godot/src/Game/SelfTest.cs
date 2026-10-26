using Godot;
using DollyForge.Core;

namespace DollyForge.Game;

/// <summary>
/// 실제 엔진(Jolt 물리) 안에서 핵심 흐름을 자동으로 확인한다: 구매 → 축 스냅 결합 → 전기 구동 → 내보내기.
/// 실행: godot --path godot -- --selftest   (결과를 출력하고 종료 코드 0/1 로 끝난다)
/// </summary>
public partial class SelfTest : Node
{
    public Devices Devices { get; set; } = null!;
    int _failures;
    double _t;
    int _step;
    PartBody? _robotBase, _motorL;
    Vector3 _start;
    double _startEnergy;
    PartBody? _battery;

    void Check(bool ok, string what)
    {
        GD.Print($"{(ok ? "✔" : "✘")} {what}");
        if (!ok) _failures++;
    }

    public override void _Ready()
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

        // 2. 축 스냅: 바퀴를 모터 축 근처(축선에서 3 cm 벗어난 곳)에 두고 결합
        var motor = Devices.Spawn(parts[0], new Transform3D(new Basis(Vector3.Up, Mathf.Pi / 2), new Vector3(8, 0.5f, 8)));
        var wheel = Devices.Spawn(parts[1], new Transform3D(Basis.Identity, new Vector3(8.06f, 0.53f, 8.02f)));
        motor.Freeze = true;
        var (joined, jmsg) = Devices.Join(JoinMethod.Axle, motor, wheel, wheel.GlobalPosition);
        var onAxis = (wheel.GlobalPosition - motor.GlobalPosition).Cross(motor.PrincipalAxis()).Length();
        var aligned = Mathf.Abs(wheel.GlobalBasis.Z.Normalized().Dot(motor.PrincipalAxis()));
        Check(joined && onAxis < 0.002f && aligned > 0.999f, $"축 스냅 결합: {jmsg} (축선 거리 {onAxis * 1000:0.0} mm)");
        var bad = Devices.Join(JoinMethod.Axle, motor, Devices.Spawn(Catalog.Get("WH-100-B8").Build()!, new Transform3D(Basis.Identity, new Vector3(7.9f, 0.5f, 8))), Vector3.Zero);
        Check(!bad.Ok, $"지름이 다른 바퀴 거부: {bad.Message}");

        // 3. 견본 로봇을 마당 바닥에 내려놓는다 (작업대 견본과 별개)
        var robot = Blueprints.TwoWheelRobot(new ShopAccount(1_000_000), new System.Numerics.Vector3(-4, 0.02f, 10));
        var before2 = Devices.Bodies.Count();
        Devices.Adopt(robot);
        var mine = Devices.Bodies.Skip(before2).ToList();
        _robotBase = mine.First(b => b.Part.Def.Sku == "PL-AL6061-5");
        _motorL = mine.First(b => b.Part.Def.Role == PartRole.Motor);
        _battery = mine.First(b => b.Part.Def.Role == PartRole.Battery);
    }

    public override void _PhysicsProcess(double delta)
    {
        _t += delta;
        switch (_step)
        {
            case 0 when _t > 1.0: // 내려앉을 시간
                _start = _robotBase!.GlobalPosition;
                _startEnergy = _battery!.EnergyJ;
                GD.Print("  " + Devices.TogglePower(_robotBase));
                _step = 1;
                _t = 0;
                break;
            case 1 when _t > 2.0:
            {
                // 같은 배선 + 좌우 대칭 장착 → 제자리 회전 (실제 차동 구동과 같다)
                var moved = (_robotBase!.GlobalPosition - _start) with { Y = 0 };
                var yawRate = Mathf.Abs(_robotBase.AngularVelocity.Y);
                Check(moved.Length() < 0.25f && yawRate > 0.5f, $"같은 배선: 제자리 회전 (이동 {moved.Length():0.00} m, 회전 {yawRate:0.0} rad/s)");
                GD.Print("  " + Devices.FlipWiring(_motorL!));
                _start = _robotBase.GlobalPosition;
                _step = 2;
                _t = 0;
                break;
            }
            case 2 when _t > 3.0:
            {
                var moved = (_robotBase!.GlobalPosition - _start) with { Y = 0 };
                var speed = moved.Length() / 3.0f;
                // 이론 최고 속도 = 100 RPM × 0.0325 m = 0.34 m/s (가속·미끄럼 고려 0.15–0.4)
                Check(speed is > 0.15f and < 0.4f, $"한쪽 배선 반전: 직진 {moved.Length():0.00} m / 3 s = {speed:0.00} m/s (이론 0.34 m/s)");
                var used = _startEnergy - _battery!.EnergyJ;
                Check(used > 0, $"배터리 소모 {used:0} J (5초 동안)");
                GD.Print("  " + Devices.TogglePower(_robotBase));

                // 4. 내보내기
                var result = Devices.ExportDevice(_robotBase);
                Check(result.StartsWith("내보냄"), result);
                Check(Devices.Properties(_robotBase).Contains("부품 10개"), Devices.Properties(_robotBase));
                GD.Print(_failures == 0 ? "SELFTEST PASS" : $"SELFTEST FAIL ({_failures})");
                GetTree().Quit(_failures == 0 ? 0 : 1);
                _step = 3;
                break;
            }
        }
    }
}
