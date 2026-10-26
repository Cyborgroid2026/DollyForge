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
        Assert.Equal(MathF.Round(120 * 76f / 60f), knee.Effort); // 남성형 구동기는 몸무게 비례로 크다
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

    [Fact]
    public void ModuleVariantsTradeTorqueMassAndPrice()
    {
        var b = Cyborg.Female;
        var std = Cyborg.Module(b, BodySlot.ShinL);
        var hd = Cyborg.Module(b, BodySlot.ShinL, ModuleVariant.HeavyDuty);
        var lite = Cyborg.Module(b, BodySlot.ShinL, ModuleVariant.Lite);
        Assert.Equal("CY-F-SHINL-HD", hd.Sku);
        Assert.Equal(BodySlot.ShinL, Cyborg.SlotOf(hd));
        Assert.Equal(ModuleVariant.Lite, Cyborg.VariantOf(lite));
        Assert.Equal((byte)0, Cyborg.GenderOf(lite));
        Assert.Equal(std.Actuator!.MaxTorque * 1.8f, hd.Actuator!.MaxTorque, 3);
        Assert.True(hd.Mass > std.Mass && lite.Mass < std.Mass);
        Assert.True(hd.PriceKrw > lite.PriceKrw && lite.PriceKrw > std.PriceKrw);
        Assert.Throws<ArgumentException>(() => Cyborg.Module(b, BodySlot.Head, ModuleVariant.HeavyDuty));
        // 카탈로그에서 산 모듈 = 같은 정의
        Assert.Equal(hd.Mass, Catalog.Get("CY-F-SHINL-HD").Build()!.Mass);
    }

    [Fact]
    public void CapabilitiesFollowFromModules()
    {
        var std = Cyborg.Capabilities(BodyConfig.Default(0));
        Assert.Equal(Cyborg.Female.Mass, std.MassKg, 1);
        Assert.InRange(std.JumpHeight, 0.3f, 0.7f);    // 사람 제자리 점프 수준
        Assert.InRange(std.LiftN, 200f, 450f);    // 팔꿈치로 약 25 kg

        var legs = BodyConfig.Default(0) with
        {
            Modules = new() { [BodySlot.ShinL] = ModuleVariant.HeavyDuty, [BodySlot.ShinR] = ModuleVariant.HeavyDuty },
        };
        var hd = Cyborg.Capabilities(legs);
        Assert.True(hd.JumpHeight > std.JumpHeight * 1.3f);
        Assert.InRange(Cyborg.Capabilities(BodyConfig.Default(1)).JumpHeight, 0.3f, 0.7f); // 남성형도 사람 수준
        Assert.True(hd.MassKg > std.MassKg);

        // 찬 기기도 몸 질량에 들어간다
        var worn = BodyConfig.Default(0) with { Worn = ["ATE-HS-G"] };
        Assert.True(Cyborg.Capabilities(worn).MassKg > std.MassKg);

        // 구성대로 조립하면 관절 토크도 따라간다
        var body = Cyborg.Assemble(legs, Vector3.Zero);
        var knee = body.Joints.First(j => body.Part(j.Child).Def.Sku == "CY-F-SHINL-HD");
        Assert.Equal(hd.KneeTorque, knee.Effort, 3);
    }

    [Fact]
    public void BodyConfigRoundTripsThroughServerPacket()
    {
        var config = BodyConfig.Default(1) with
        {
            Modules = new() { [BodySlot.ThighR] = ModuleVariant.Lite, [BodySlot.ShinL] = ModuleVariant.HeavyDuty },
            Worn = ["ATE-HS-G"],
        };
        Assert.Equal(["CY-M-THIGHR-LITE", "CY-M-SHINL-HD"], config.ModuleSkus());
        var packet = DollyForge.Core.Net.Packets.SaveBody(config.ModuleSkus(), config.Worn);
        packet[0] = DollyForge.Core.Net.Op.BodyData; // 서버는 같은 형식으로 돌려준다
        var back = (DollyForge.Core.Net.BodyData)DollyForge.Core.Net.Packets.Parse(packet)!;
        var restored = BodyConfig.FromSkus(1, back.Modules, back.Worn);
        Assert.Equal(ModuleVariant.Lite, restored.VariantAt(BodySlot.ThighR));
        Assert.Equal(ModuleVariant.HeavyDuty, restored.VariantAt(BodySlot.ShinL));
        Assert.Equal(ModuleVariant.Standard, restored.VariantAt(BodySlot.ShinR));
        Assert.Equal(["ATE-HS-G"], restored.Worn);
    }

    [Fact]
    public void WornGearIsPartOfTheExportedBody()
    {
        var config = BodyConfig.Default(0) with { Worn = ["ATE-QW-1", "ATE-QW-1", "ATE-HS-G"] };
        var body = Cyborg.Assemble(config, Vector3.Zero);
        Assert.Equal(17 + 3, body.Parts.Count);
        var (tree, loops, disconnected) = body.SpanningTree(body.DefaultRoot());
        Assert.Equal(19, tree.Count);
        Assert.Empty(loops);
        Assert.Empty(disconnected);
        Assert.Contains("ATE-QW-1", Export.Bom(body).Markdown);
        Assert.Equal(2 + 3, body.Joints.Count(j => j.Kind == JointKind.Fixed)); // 장기 코어·MSU + 찬 기기 3개
        Assert.Throws<ArgumentException>(() => Cyborg.Assemble(config with { Worn = ["MT-NEMA17"] }, Vector3.Zero));
    }
}
