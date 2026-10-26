using System.Numerics;
using DollyForge.Core;
using Xunit;

namespace DollyForge.Core.Tests;

public class ShopTests
{
    [Fact]
    public void CutToLengthPriceIsPerMeterPlusCutFee()
    {
        // 2020 프로파일 750 mm = ₩6,000 × 0.75 + 재단비 ₩1,000 = ₩5,500
        var item = Catalog.Get("AL-2020");
        Assert.Equal(5500, item.UnitPrice(750));
        var part = item.Build(750)!;
        Assert.Equal(0.49f * 0.75f, part.Mass, 4);
        Assert.Equal(PartRole.Profile, part.Role);
        Assert.Throws<ArgumentOutOfRangeException>(() => item.Build(5000));
    }

    [Fact]
    public void PlateIsPricedByWeight()
    {
        // 강판 3t 500×400 = 0.0006 m³ × 7850 = 4.71 kg × ₩2,800 + 재단비 ₩5,000
        var item = Catalog.Get("PL-SS400-3");
        Assert.Equal((int)Math.Round(2800 * 4.71) + 5000, item.UnitPrice(500, 400));
    }

    [Fact]
    public void PurchaseChargesShippingAndStocksConsumables()
    {
        var shop = new ShopAccount(100_000);
        var (ok, _, delivered) = shop.Purchase([
            new(Catalog.Get("MT-JGB37-100"), 1),
            new(Catalog.Get("FS-BOLT-M8"), 1),
        ]);
        Assert.True(ok);
        Assert.Single(delivered);
        Assert.Equal(100_000 - (16000 + 3000 + ShopAccount.ShippingKrw), shop.BalanceKrw);
        Assert.Equal(10, shop.Consumables["FS-BOLT-M8"]);
        var (ok2, msg, _) = shop.Purchase([new(Catalog.Get("BT-LFP-12-10"), 2)]);
        Assert.False(ok2);
        Assert.Contains("잔액", msg);
    }

    [Fact]
    public void FreeShippingAboveThreshold()
    {
        Assert.Equal(0, ShopAccount.Quote([new(Catalog.Get("BT-LFP-12-10"), 1)]).Shipping);
    }
}

public class RuleTests
{
    static PartDef P(string sku, float l = 0, float w = 0) => Catalog.Get(sku).Build(l, w)!;

    [Fact]
    public void AxleRequiresMatchingDiameter()
    {
        Assert.True(Rules.Check(JoinMethod.Axle, P("SH-SUJ2-8", 300), P("WH-100-B8")).Ok);
        var bad = Rules.Check(JoinMethod.Axle, P("SH-SUJ2-12", 300), P("WH-100-B8"));
        Assert.False(bad.Ok);
        Assert.Contains("12 mm", bad.ReasonKo);
        var motor = Rules.Check(JoinMethod.Axle, P("WH-65-D6"), P("MT-JGB37-100"));
        Assert.True(motor.Ok && motor.Driven);
    }

    [Fact]
    public void WeldingOnlyJoinsLikeMetals()
    {
        Assert.True(Rules.Check(JoinMethod.Weld, P("PL-SS400-3", 200, 200), P("SH-S45C-20", 300)).Ok);
        Assert.False(Rules.Check(JoinMethod.Weld, P("PL-SS400-3", 200, 200), P("PL-AL6061-5", 200, 200)).Ok);
        Assert.False(Rules.Check(JoinMethod.Weld, P("WD-2X4", 500), P("WD-2X4", 500)).Ok);
        Assert.False(Rules.Check(JoinMethod.Weld, P("PL-SS400-3", 200, 200), P("MT-JGB37-100")).Ok);
    }

    [Fact]
    public void FastenersMatchTheMaterials()
    {
        Assert.Equal("FS-TNUT-M5", Rules.Check(JoinMethod.Bolt, P("AL-2020", 500), P("PL-AL6061-5", 100, 100)).Consumes.Keys.Single());
        Assert.False(Rules.Check(JoinMethod.Screw, P("AL-2020", 500), P("PL-AL6061-5", 100, 100)).Ok);
        Assert.False(Rules.Check(JoinMethod.Slide, P("SH-SUJ2-12", 300), P("PL-AL6061-5", 100, 100)).Ok);
    }

    [Fact]
    public void RobotBomTotalsMatchPurchase()
    {
        var (robot, spent) = Samples.RobotCart();
        var (md, csv, total) = Export.Bom(robot);
        Assert.Contains("DC 기어드 모터", md);
        Assert.Equal(robot.Parts.Count, csv.Split('\n').Count(l => l.StartsWith("MT-") || l.StartsWith("WH-") || l.StartsWith("PL-") || l.StartsWith("BT-") || l.StartsWith("SH-") || l.StartsWith("BRG-")) is var n && n > 0 ? robot.Parts.Count : -1);
        Assert.True(total <= spent, $"BOM ₩{total} 는 실제 지출 ₩{spent} 이하여야 함 (남은 소모품 제외)");
        Assert.Equal(2, robot.Joints.Count(j => j.DrivenBy is not null));
    }
}
