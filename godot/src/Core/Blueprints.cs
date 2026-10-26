using System.Numerics;

namespace DollyForge.Core;

/// <summary>검증된 조립 예제. 테스트와 게임이 같은 정의를 쓴다.</summary>
public static class Blueprints
{
    /// <summary>
    /// 카탈로그에서 실제로 사서 만든 2륜 차동 구동 로봇.
    /// 알루미늄 판 220×200 5t 차체 아래에 DC 기어드 모터 2개를 좌우 대칭(출력축이 바깥)으로 볼트 고정하고
    /// 65 mm 바퀴를 축에 직결, 뒤쪽은 설치 높이 51 mm 볼 캐스터, 위에는 납축전지.
    /// 치수: 바퀴 중심 = 모터 축 = 32.5 mm, 판 아래면 = 51 mm (모터 윗면·캐스터 높이와 일치).
    /// 양쪽 모터를 같은 극성으로 배선하면 제자리에서 돌고, 한쪽 극성을 바꾸면 직진한다 (실제와 같다).
    /// </summary>
    public static Assembly TwoWheelRobot(ShopAccount shop, Vector3 at)
    {
        var (ok, msg, parts) = shop.Purchase([
            new(Catalog.Get("PL-AL6061-5"), 1, 220, 200),
            new(Catalog.Get("MT-JGB37-100"), 2),
            new(Catalog.Get("WH-65-D6"), 2),
            new(Catalog.Get("WH-CASTER-51"), 1),
            new(Catalog.Get("BT-SLA-12-7"), 1),
            new(Catalog.Get("FS-BOLT-M8"), 1),
        ]);
        if (!ok) throw new InvalidOperationException(msg);
        PartDef Take(string sku) { var p = parts.First(d => d.Sku == sku); parts.Remove(p); return p; }

        var a = new Assembly("Two Wheel Robot");
        var chassis = a.AddPart(Take("PL-AL6061-5"), new Pose(at + new Vector3(0, 0.0535f, 0), Quaternion.Identity));
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
            // 원기둥 축(로컬 Z)이 바깥(±X)을 향하도록: 출력축이 바퀴 쪽
            var outward = Quaternion.CreateFromAxisAngle(Vector3.UnitY, sx * MathF.PI / 2);
            var motor = a.AddPart(Take("MT-JGB37-100"), new Pose(at + new Vector3(sx * 0.085f, 0.0325f, -0.06f), outward));
            Join(JoinMethod.Bolt, chassis, motor, motor.Pose.Position, Vector3.UnitX);
            // 모터 하우징 끝(0.085 + 0.036) + 틈 3 mm + 바퀴 반폭 13 mm = 0.137 m
            var wheel = a.AddPart(Take("WH-65-D6"), new Pose(at + new Vector3(sx * 0.137f, 0.0325f, -0.06f), outward));
            Join(JoinMethod.Axle, motor, wheel, wheel.Pose.Position, sx * Vector3.UnitX);
        }
        var caster = a.AddPart(Take("WH-CASTER-51"), new Pose(at + new Vector3(0, 0.0255f, 0.075f), Quaternion.Identity));
        Join(JoinMethod.Bolt, chassis, caster, caster.Pose.Position, Vector3.UnitX);
        var battery = a.AddPart(Take("BT-SLA-12-7"), new Pose(at + new Vector3(0, 0.103f, 0.01f), Quaternion.Identity));
        Join(JoinMethod.Bolt, chassis, battery, battery.Pose.Position, Vector3.UnitX);
        return a;
    }
}
