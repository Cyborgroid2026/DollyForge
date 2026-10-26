using System.Numerics;

namespace DollyForge.Core;

/// <summary>검증된 조립 예제. 테스트와 게임이 같은 정의를 쓴다.</summary>
public static class Blueprints
{
    /// <summary>
    /// 카탈로그에서 실제로 사서 만든 2륜 구동 로봇 (예산 ₩200,000):
    /// 알루미늄 판 300×200 5t 차체, DC 기어드 모터 2개(M8 볼트 고정) + 65 mm 바퀴 직결,
    /// 뒤쪽에 608 베어링 2개로 받친 Ø8 축 + 100 mm 우레탄 바퀴, 납축전지(볼트 고정).
    /// </summary>
    public static Assembly TwoWheelRobot(ShopAccount shop, Vector3 at)
    {
        var (ok, msg, parts) = shop.Purchase([
            new(Catalog.Get("PL-AL6061-5"), 1, 300, 200),
            new(Catalog.Get("MT-JGB37-100"), 2),
            new(Catalog.Get("WH-65-D6"), 2),
            new(Catalog.Get("SH-SUJ2-8"), 1, 120),
            new(Catalog.Get("BRG-608ZZ"), 2),
            new(Catalog.Get("WH-100-B8"), 1),
            new(Catalog.Get("BT-SLA-12-7"), 1),
            new(Catalog.Get("FS-BOLT-M8"), 1),
        ]);
        if (!ok) throw new InvalidOperationException(msg);
        PartDef Take(string sku) { var p = parts.First(d => d.Sku == sku); parts.Remove(p); return p; }

        var a = new Assembly("Two Wheel Robot");
        var side = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2); // 원기둥 축(로컬 Z) → 월드 X
        var chassis = a.AddPart(Take("PL-AL6061-5"), new Pose(at + new Vector3(0, 0.06f, 0), Quaternion.Identity));
        void Join(JoinMethod m, PartInstance x, PartInstance y, Vector3 pivot, Vector3 axis)
        {
            var c = Rules.Check(m, x.Def, y.Def);
            if (!c.Ok) throw new InvalidOperationException(c.ReasonKo);
            if (!shop.Has(c.Consumes)) throw new InvalidOperationException("체결 부품 부족");
            shop.Consume(c.Consumes);
            var j = a.AddJoint(c.Kind, x, y, pivot, axis);
            foreach (var (k, v) in c.Consumes) j.Consumed[k] = v;
            if (c.Driven)
            {
                var motor = x.Def.Role == PartRole.Motor ? x : y;
                j.DrivenBy = motor.Id;
                j.Effort = motor.Def.Actuator!.MaxTorque;
                j.Velocity = motor.Def.Actuator.MaxSpeed;
            }
        }
        foreach (var sx in new[] { -1f, 1f })
        {
            var motor = a.AddPart(Take("MT-JGB37-100"), new Pose(at + new Vector3(sx * 0.085f, 0.04f, -0.06f), side));
            Join(JoinMethod.Bolt, chassis, motor, motor.Pose.Position, Vector3.UnitX);
            var wheel = a.AddPart(Take("WH-65-D6"), new Pose(at + new Vector3(sx * 0.137f, 0.0325f, -0.06f), side));
            Join(JoinMethod.Axle, motor, wheel, wheel.Pose.Position, Vector3.UnitX);
        }
        var axle = a.AddPart(Take("SH-SUJ2-8"), new Pose(at + new Vector3(0, 0.05f, 0.08f), side));
        foreach (var sx in new[] { -1f, 1f })
        {
            var brg = a.AddPart(Take("BRG-608ZZ"), new Pose(at + new Vector3(sx * 0.045f, 0.05f, 0.08f), side));
            Join(JoinMethod.Bolt, chassis, brg, brg.Pose.Position, Vector3.UnitX);
            Join(JoinMethod.Axle, axle, brg, brg.Pose.Position, Vector3.UnitX);
        }
        var caster = a.AddPart(Take("WH-100-B8"), new Pose(at + new Vector3(0, 0.05f, 0.08f), side));
        Join(JoinMethod.Axle, axle, caster, caster.Pose.Position, Vector3.UnitX);
        var battery = a.AddPart(Take("BT-SLA-12-7"), new Pose(at + new Vector3(0, 0.1095f, 0.01f), Quaternion.Identity));
        Join(JoinMethod.Bolt, chassis, battery, battery.Pose.Position, Vector3.UnitX);
        return a;
    }
}
