using System.Numerics;
using DollyForge.Core;

namespace DollyForge.Core.Tests;

/// <summary>검증용 실제 조립품들</summary>
public static class Samples
{
    /// <summary>
    /// 태엽 구동 수레: 강철 판 차체 + 강철 축 2개(고정) + 고무 바퀴 4개(연속 회전) + 태엽 모터(뒷바퀴 구동).
    /// 게임 좌표: Y-up, 전방 -Z.
    /// </summary>
    public static Assembly Cart()
    {
        var a = new Assembly("Spring Cart");
        var chassis = a.AddPart(Templates.Plate.Make(Materials.Steel, new Dictionary<string, float> { ["w"] = 600, ["d"] = 900, ["t"] = 6 }),
            new Pose(new(0, 0.3f, 0), Quaternion.Identity));
        var axleDef = Templates.Shaft.Make(Materials.Steel, new Dictionary<string, float> { ["d"] = 25, ["l"] = 760 });
        var wheelDef = Templates.Wheel.Make(Materials.Rubber, new Dictionary<string, float> { ["d"] = 400, ["w"] = 60 });
        // 원기둥 축은 로컬 Z → 월드 X 방향으로 눕힌다
        var sideways = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2);
        var motor = a.AddPart(Templates.SpringMotor.Make(Materials.Steel), new Pose(new(0, 0.366f, 0.3f), Quaternion.Identity));
        a.AddJoint(JointKind.Fixed, chassis, motor, motor.Pose.Position, Vector3.UnitX);
        foreach (var z in new[] { -0.35f, 0.35f })
        {
            var axle = a.AddPart(axleDef, new Pose(new(0, 0.2f, z), sideways));
            a.AddJoint(JointKind.Fixed, chassis, axle, axle.Pose.Position, Vector3.UnitX);
            foreach (var x in new[] { -0.35f, 0.35f })
            {
                var wheel = a.AddPart(wheelDef, new Pose(new(x, 0.2f, z), sideways));
                var j = a.AddJoint(JointKind.Continuous, axle, wheel, wheel.Pose.Position, Vector3.UnitX);
                if (z > 0)
                {
                    j.DrivenBy = motor.Id;
                    j.Effort = motor.Def.Actuator!.MaxTorque / 2;
                    j.Velocity = motor.Def.Actuator.MaxSpeed;
                }
            }
        }
        return a;
    }

    /// <summary>Fusion 360 의 모든 관절 종류를 한 번씩 쓰는 기구 + 닫힌 고리 하나</summary>
    public static Assembly Mechanisms()
    {
        var a = new Assembly("Joint Showcase");
        var basePlate = a.AddPart(Templates.Plate.Make(Materials.Aluminium, new Dictionary<string, float> { ["w"] = 1000, ["d"] = 1000, ["t"] = 10 }),
            new Pose(Vector3.Zero, Quaternion.Identity));
        PartInstance Bar(Vector3 at) => a.AddPart(Templates.Bar.Make(Materials.Aluminium, new Dictionary<string, float> { ["l"] = 300, ["a"] = 20 }), new Pose(at, Quaternion.Identity));
        var hinge = Bar(new(-0.3f, 0.1f, -0.3f));
        a.AddJoint(JointKind.Revolute, basePlate, hinge, new(-0.45f, 0.1f, -0.3f), Vector3.UnitZ, -1.2f, 1.2f);
        var slider = Bar(new(0.3f, 0.05f, -0.3f));
        a.AddJoint(JointKind.Prismatic, basePlate, slider, slider.Pose.Position, Vector3.UnitX, -0.2f, 0.2f);
        var piston = a.AddPart(Templates.Shaft.Make(Materials.Steel, new Dictionary<string, float> { ["d"] = 20, ["l"] = 300 }),
            new Pose(new(-0.3f, 0.2f, 0.3f), Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2)));
        a.AddJoint(JointKind.Cylindrical, basePlate, piston, piston.Pose.Position, Vector3.UnitY, -0.1f, 0.1f);
        var pin = Bar(new(0.3f, 0.05f, 0.3f));
        a.AddJoint(JointKind.PinSlot, basePlate, pin, pin.Pose.Position, Vector3.UnitY, -0.1f, 0.1f).GetType();
        var puck = a.AddPart(Templates.Disc.Make(Materials.Brass), new Pose(new(0, 0.02f, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2)));
        a.AddJoint(JointKind.Planar, basePlate, puck, puck.Pose.Position, Vector3.UnitY);
        var ball = a.AddPart(Templates.Ball.Make(Materials.Steel), new Pose(new(0, 0.3f, -0.45f), Quaternion.Identity));
        a.AddJoint(JointKind.Ball, hinge, ball, ball.Pose.Position, Vector3.UnitY);
        // 닫힌 고리: 공을 받침판에도 고정 → URDF 에서는 생략, USDA 에는 포함
        a.AddJoint(JointKind.Fixed, basePlate, ball, ball.Pose.Position, Vector3.UnitY);
        return a;
    }
}
