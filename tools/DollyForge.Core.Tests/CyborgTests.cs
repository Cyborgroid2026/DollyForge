using System.Numerics;
using DollyForge.Core;
using Xunit;

namespace DollyForge.Core.Tests;

public class CyborgTests
{
    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)1)]
    public void BodyHasHumanMassAndProportions(byte gender)
    {
        var b = Cyborg.Of(gender);
        var body = Cyborg.Assemble(b, Vector3.Zero);
        Assert.Equal(17, body.Parts.Count);   // 분절 15 + 배양 장기 코어 + 대사 지원 유닛
        Assert.Equal(16, body.Joints.Count);
        Assert.Single(body.Parts, p => p.Def.Role == PartRole.BioCore);
        Assert.Single(body.Parts, p => p.Def.Role == PartRole.LifeSupport);
        // 분절 질량 합 ≈ 체중 (Winter 비율 합 ≈ 1)
        Assert.InRange(body.TotalMass, b.Mass * 0.97f, b.Mass * 1.03f);
        // 머리 꼭대기 ≈ 키
        var head = body.Parts.First(p => Cyborg.SlotOf(p.Def) == BodySlot.Head);
        var top = head.Pose.Position.Y + head.Def.Shape.Radius;
        Assert.InRange(top, b.Height * 0.93f, b.Height * 1.07f);
        // 무게중심은 사람처럼 골반 근처 (키의 약 55 %)
        var com = body.ComputeMassProperties().CenterOfMass.Y / b.Height;
        Assert.InRange(com, 0.5f, 0.62f);
    }

    [Fact]
    public void JointsHaveHumanLimitsAndActuators()
    {
        var body = Cyborg.Assemble(Cyborg.Male, Vector3.Zero);
        var knee = body.Joints.First(j => Cyborg.SlotOf(body.Part(j.Child).Def) == BodySlot.ShinL);
        Assert.Equal(JointKind.Revolute, knee.Kind);
        Assert.True(knee.Upper <= 0 && knee.Lower < -2, "무릎은 한 방향으로만 접힌다");
        Assert.Equal(120, knee.Effort);
        var hip = body.Joints.First(j => Cyborg.SlotOf(body.Part(j.Child).Def) == BodySlot.ThighR);
        Assert.Equal(JointKind.Ball, hip.Kind);
        Assert.All(body.Joints, j => Assert.Equal(1, j.Consumed[Cyborg.Connector]));
    }

    [Fact]
    public void ModulesOnlyAttachAnatomically()
    {
        Assert.True(Cyborg.CanAttach(BodySlot.ForearmL, BodySlot.UpperArmL).Ok);
        Assert.False(Cyborg.CanAttach(BodySlot.ForearmL, BodySlot.ThighL).Ok);
        Assert.False(Cyborg.CanAttach(BodySlot.Head, BodySlot.Pelvis).Ok);
    }

    [Fact]
    public void BodyExportsAsRobotWithFictionalNeuralCore()
    {
        var body = Cyborg.Assemble(Cyborg.Female, Vector3.Zero);
        var urdf = Export.Urdf(body, body.Parts.First(p => Cyborg.SlotOf(p.Def) == BodySlot.Pelvis).Id);
        Assert.Contains("가상 부품", urdf);           // 신경 중추
        Assert.Contains("type=\"revolute\"", urdf);   // 무릎·팔꿈치
        Assert.Contains("_dof", urdf);                 // 3축 관절은 중간 링크 사슬
        var dir = Environment.GetEnvironmentVariable("DOLLY_EXPORT_DIR");
        if (dir is not null)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "cyborg_female.urdf"), urdf);
            File.WriteAllText(Path.Combine(dir, "cyborg_female.usda"), Export.Usda(body, body.Parts[0].Id));
        }
    }

    [Fact]
    public void WearablesClipOnLikeWatchesAndHeadphones()
    {
        var b = Cyborg.Female;
        var hand = Cyborg.Module(b, BodySlot.HandL);
        var head = Cyborg.Module(b, BodySlot.Head);
        var jet = Catalog.Get("ATE-JT-250").Build()!;
        var hs = Catalog.Get("ATE-HS-G").Build()!;
        Assert.True(Rules.Check(JoinMethod.Wear, hand, jet).Ok);
        Assert.False(Rules.Check(JoinMethod.Wear, Cyborg.Module(b, BodySlot.ThighL), jet).Ok);
        Assert.True(Rules.Check(JoinMethod.Wear, head, hs).Ok);
        Assert.False(Rules.Check(JoinMethod.Wear, hand, hs).Ok);
        Assert.Empty(Rules.Check(JoinMethod.Wear, hand, jet).Consumes); // 공구·소모품 없이
    }

    [Fact]
    public void RealThrustersCannotLiftABodyButFictionalOnesCan()
    {
        // 실제: 마이크로 제트 터빈 250 N × 2 = 500 N < 60 kg 몸 + 5.2 kg 의 무게 640 N
        var real = Enumerable.Repeat(Catalog.Get("ATE-JT-250").Build()!, 2);
        Assert.True(Cyborg.LiftAcceleration(Cyborg.Female.Mass, real) < 0);
        // 가상: 양자 추력기 700 N × 2 = 1400 N > 61.8 kg 의 무게 606 N
        var quantum = Enumerable.Repeat(Catalog.Get("ATE-QW-1").Build()!, 2);
        Assert.True(Cyborg.LiftAcceleration(Cyborg.Female.Mass, quantum) > 5);
        Assert.True(Catalog.Get("ATE-QW-1").Build()!.Fictional);
    }
}
