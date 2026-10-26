namespace DollyForge.Core;

/// <summary>
/// 현실에서 실제로 살 수 있는 원자재·부품 카탈로그. 사양은 시판 표준품 기준이고,
/// 가격은 2026년 국내 소매가를 대략 반영한 원(₩) 단위 값이다.
/// 길이 재단품은 미터당 가격 × 길이 + 재단비로 계산한다.
/// </summary>
public sealed record CatalogItem(
    string Sku,
    string Category,
    string NameKo,
    string SpecKo,
    int PriceKrw,
    string UnitKo)
{
    /// <summary>재단 주문 가능 여부와 길이 범위 (mm). 가격은 PriceKrw 가 미터당.</summary>
    public (float Min, float Max, int CutFeeKrw)? CutToLength { get; init; }
    /// <summary>판재처럼 가로×세로로 재단 (mm). 가격은 PriceKrw 가 kg 당.</summary>
    public (float MinW, float MaxW, float MinD, float MaxD, int CutFeeKrw)? CutToSize { get; init; }
    /// <summary>물리적 부품으로 배송되는 품목의 형상 생성기. 소모품이면 null.</summary>
    public Func<float, float, PartDef>? Make { get; init; }
    /// <summary>소모품 묶음 수량 (볼트 세트 등). 0 이면 물리 부품.</summary>
    public int PackQty { get; init; }

    public bool IsConsumable => PackQty > 0;

    /// <summary>주문 단가. length·width·depth 는 mm.</summary>
    public int UnitPrice(float lengthMm = 0, float widthMm = 0)
    {
        if (CutToLength is { } c) return (int)Math.Round(PriceKrw * lengthMm / 1000f) + c.CutFeeKrw;
        if (CutToSize is { } s && Make is not null)
            return (int)Math.Round(PriceKrw * Make(lengthMm, widthMm).Mass) + s.CutFeeKrw;
        return PriceKrw;
    }

    public PartDef? Build(float lengthMm = 0, float widthMm = 0)
    {
        if (Make is null) return null;
        if (CutToLength is { } c && (lengthMm < c.Min || lengthMm > c.Max))
            throw new ArgumentOutOfRangeException(nameof(lengthMm), $"{NameKo} 재단 길이는 {c.Min}–{c.Max} mm 입니다.");
        if (CutToSize is { } s && (lengthMm < s.MinW || lengthMm > s.MaxW || widthMm < s.MinD || widthMm > s.MaxD))
            throw new ArgumentOutOfRangeException(nameof(lengthMm), $"{NameKo} 재단 크기는 {s.MinW}–{s.MaxW} × {s.MinD}–{s.MaxD} mm 입니다.");
        return Make(lengthMm, widthMm) with { Sku = Sku, PriceKrw = UnitPrice(lengthMm, widthMm) };
    }
}

public static class Catalog
{
    static PartDef Def(string id, string name, Shape shape, MaterialDef mat, string desc, float? mass = null, ActuatorSpec? act = null) =>
        new(id, name, shape, mat, desc, mass, act);

    static CatalogItem Profile(string sku, int size, float kgPerM, int pricePerM) => new(
        sku, "알루미늄 프로파일", $"알루미늄 프로파일 {size}{size}", $"{size}×{size} mm, 6063-T5, 슬롯 6 mm, {kgPerM} kg/m",
        pricePerM, "m")
    {
        CutToLength = (50, 3000, 1000),
        Make = (l, _) => Def($"{sku.ToLowerInvariant()}_{l:0}", $"알루미늄 프로파일 {size}{size} × {l:0} mm",
            Shape.Box(l / 1000f, size / 1000f, size / 1000f), Materials.Aluminium, "T슬롯 프로파일. T너트 볼트로 체결한다.",
            kgPerM * l / 1000f) with { Role = PartRole.Profile },
    };

    static CatalogItem RoundBar(string sku, string name, float d, MaterialDef mat, int pricePerM, string spec) => new(
        sku, "축·환봉", name, spec, pricePerM, "m")
    {
        CutToLength = (30, 2000, 1000),
        Make = (l, _) => Def($"{sku.ToLowerInvariant()}_{l:0}", $"{name} × {l:0} mm", Shape.Cylinder(d / 2000f, l / 1000f), mat,
            "원형 축. 같은 지름 구멍의 바퀴·베어링과 회전 관절을 이룬다.") with { Role = PartRole.Shaft, ShaftMm = d },
    };

    static CatalogItem Plate(string sku, string name, MaterialDef mat, float t, int pricePerKg) => new(
        sku, "판재", name, $"두께 {t} mm, {mat.NameKo}, 레이저 재단", pricePerKg, "kg")
    {
        CutToSize = (20, 1200, 20, 1200, 5000),
        Make = (w, d) => Def($"{sku.ToLowerInvariant()}_{w:0}x{d:0}", $"{name} {w:0}×{d:0} mm", Shape.Box(w / 1000f, t / 1000f, d / 1000f), mat,
            "재단 판재. 볼트·용접으로 체결한다."),
    };

    public static readonly IReadOnlyList<CatalogItem> Items =
    [
        // ---- 원자재 (재단) ----
        Profile("AL-2020", 20, 0.49f, 6000),
        Profile("AL-4040", 40, 1.55f, 17000),
        Plate("PL-SS400-3", "강판 SS400 3t", Materials.Steel, 3, 2800),
        Plate("PL-AL6061-5", "알루미늄 판 6061 5t", Materials.Aluminium, 5, 14000),
        Plate("PL-PLY-12", "자작 합판 12t", Materials.Pine, 12, 4000),
        RoundBar("SH-SUJ2-8", "리니어 샤프트 Ø8", 8, Materials.Steel, 9000, "SUJ2 고주파 열처리 연마봉, 공차 g6"),
        RoundBar("SH-SUJ2-12", "리니어 샤프트 Ø12", 12, Materials.Steel, 15000, "SUJ2 고주파 열처리 연마봉, 공차 g6"),
        RoundBar("SH-S45C-20", "S45C 환봉 Ø20", 20, Materials.Steel, 12000, "기계구조용 탄소강 S45C"),
        new("WD-2X4", "목재", "구조목 2×4", "38×89 mm 소나무 구조목 (SPF)", 2800, "m")
        {
            CutToLength = (100, 3600, 500),
            Make = (l, _) => Def($"wd-2x4_{l:0}", $"구조목 2×4 × {l:0} mm", Shape.Box(l / 1000f, 0.038f, 0.089f), Materials.Pine,
                "목재 각재. 목재 나사로 체결한다."),
        },

        // ---- 베어링 ----
        new("BRG-608ZZ", "베어링", "볼베어링 608ZZ", "내경 8 × 외경 22 × 폭 7 mm, 양면 철실드", 1200, "개")
        {
            Make = (_, _) => Def("brg-608zz", "볼베어링 608ZZ", Shape.Cylinder(0.011f, 0.007f), Materials.Steel,
                "구조물에 고정하고 8 mm 축을 끼운다.", 0.012f) with { Role = PartRole.Bearing, BoreMm = 8 },
        },
        new("BRG-6001ZZ", "베어링", "볼베어링 6001ZZ", "내경 12 × 외경 28 × 폭 8 mm", 2000, "개")
        {
            Make = (_, _) => Def("brg-6001zz", "볼베어링 6001ZZ", Shape.Cylinder(0.014f, 0.008f), Materials.Steel,
                "구조물에 고정하고 12 mm 축을 끼운다.", 0.022f) with { Role = PartRole.Bearing, BoreMm = 12 },
        },
        new("BRG-UCP204", "베어링", "필로우 블록 UCP204", "내경 20 mm, 주철 하우징, 볼트 구멍 2개", 12000, "개")
        {
            Make = (_, _) => Def("brg-ucp204", "필로우 블록 UCP204", Shape.Box(0.127f, 0.065f, 0.038f), Materials.CastIron,
                "바닥에 볼트로 고정하고 20 mm 축을 받친다.", 0.9f) with { Role = PartRole.Bearing, BoreMm = 20 },
        },

        // ---- 바퀴 ----
        new("WH-65-D6", "바퀴", "고무 바퀴 65 mm (6 mm D축 허브)", "지름 65 × 폭 26 mm, 기어드 모터 축 직결", 3500, "개")
        {
            Make = (_, _) => Def("wh-65-d6", "고무 바퀴 65 mm", Shape.Cylinder(0.0325f, 0.026f), Materials.Rubber,
                "6 mm 모터 축에 직결.", 0.045f) with { Role = PartRole.Wheel, BoreMm = 6 },
        },
        new("WH-100-B8", "바퀴", "우레탄 바퀴 100 mm (608 베어링 내장)", "지름 100 × 폭 30 mm, 내경 8 mm", 6500, "개")
        {
            Make = (_, _) => Def("wh-100-b8", "우레탄 바퀴 100 mm", Shape.Cylinder(0.05f, 0.03f), Materials.Rubber,
                "8 mm 축에 끼운다.", 0.18f) with { Role = PartRole.Wheel, BoreMm = 8 },
        },
        new("WH-260-B20", "바퀴", "공기압 타이어 260 mm (내경 20 mm)", "3.00-4 타이어, 베어링 내장", 24000, "개")
        {
            Make = (_, _) => Def("wh-260-b20", "공기압 타이어 260 mm", Shape.Cylinder(0.13f, 0.085f), Materials.Rubber,
                "20 mm 축에 끼운다.", 2.1f) with { Role = PartRole.Wheel, BoreMm = 20 },
        },

        // ---- 구동 ----
        new("MT-JGB37-100", "모터", "DC 기어드 모터 12V 100RPM", "JGB37-520, 출력축 6 mm D컷, 정격 0.45 N·m, 정격 1.0 A", 16000, "개")
        {
            Make = (_, _) => Def("mt-jgb37-100", "DC 기어드 모터 12V 100RPM", Shape.Cylinder(0.0185f, 0.072f), Materials.Steel,
                "구조물에 고정하고, 6 mm 허브 바퀴나 커플링을 출력축에 단다.", 0.19f,
                new ActuatorSpec(MaxTorque: 0.45f, MaxSpeed: 100 * MathF.Tau / 60, EnergyJ: 0))
                with { Role = PartRole.Motor, ShaftMm = 6, Electrical = new ElectricalSpec(12, RatedCurrentA: 1.0f) },
        },
        new("MT-NEMA17", "모터", "스테퍼 모터 NEMA17", "42×42×40 mm, 유지 토크 0.45 N·m, 축 5 mm, 1.5 A", 15000, "개")
        {
            Make = (_, _) => Def("mt-nema17", "스테퍼 모터 NEMA17", Shape.Box(0.042f, 0.042f, 0.040f), Materials.Steel,
                "정밀 회전. 5 mm 축.", 0.28f, new ActuatorSpec(0.45f, 15f, 0))
                with { Role = PartRole.Motor, ShaftMm = 5, Electrical = new ElectricalSpec(12, RatedCurrentA: 1.5f) },
        },

        // ---- 전원 ----
        new("BT-SLA-12-7", "전원", "납축전지 12V 7Ah", "151×94×65 mm, 84 Wh", 28000, "개")
        {
            Make = (_, _) => Def("bt-sla-12-7", "납축전지 12V 7Ah", Shape.Box(0.151f, 0.094f, 0.065f), Materials.Steel,
                "같은 기기 안의 12 V 모터에 전력을 공급한다.", 2.1f)
                with { Role = PartRole.Battery, Electrical = new ElectricalSpec(12, CapacityWh: 84) },
        },
        new("BT-LFP-12-10", "전원", "리튬인산철 배터리 12.8V 10Ah", "151×98×65 mm, 128 Wh", 85000, "개")
        {
            Make = (_, _) => Def("bt-lfp-12-10", "리튬인산철 배터리 12.8V 10Ah", Shape.Box(0.151f, 0.098f, 0.065f), Materials.Aluminium,
                "가볍고 오래 간다.", 1.3f)
                with { Role = PartRole.Battery, Electrical = new ElectricalSpec(12.8f, CapacityWh: 128) },
        },

        // ---- 체결·기구 소모품 ----
        new("FS-TNUT-M5", "체결", "T너트 + 볼트 M5 세트 (2020용)", "20세트 묶음", 6000, "묶음") { PackQty = 20 },
        new("FS-BOLT-M8", "체결", "육각볼트·너트 M8 세트", "10세트 묶음, 강도 8.8", 3000, "묶음") { PackQty = 10 },
        new("FS-WOOD-4X40", "체결", "목재 나사 4×40", "50개 묶음", 2500, "묶음") { PackQty = 50 },
        new("MC-HINGE-75", "기구", "강철 경첩 75 mm", "개당, 나사 포함", 2000, "개") { PackQty = 1 },
        new("MC-LM8UU", "기구", "리니어 부싱 LM8UU", "8 mm 축용, 개당", 1500, "개") { PackQty = 1 },
        new("MC-RODEND-M8", "기구", "로드엔드 볼조인트 M8", "개당", 4000, "개") { PackQty = 1 },
    ];

    public static CatalogItem Get(string sku) => Items.First(i => i.Sku == sku);
}

/// <summary>주문 한 줄. 재단품은 길이(·폭) mm.</summary>
public sealed record OrderLine(CatalogItem Item, int Qty, float LengthMm = 0, float WidthMm = 0)
{
    public int Price => Item.UnitPrice(LengthMm, WidthMm) * Qty;
}

/// <summary>예산과 소모품 재고. 물리 부품은 배송 상자로 월드에 나타난다.</summary>
public sealed class ShopAccount(long balanceKrw)
{
    public const int ShippingKrw = 3500;
    public const int FreeShippingOverKrw = 50000;

    public long BalanceKrw { get; private set; } = balanceKrw;
    public Dictionary<string, int> Consumables { get; } = new();
    public List<string> Ledger { get; } = [];

    public static (int Subtotal, int Shipping, int Total) Quote(IEnumerable<OrderLine> lines)
    {
        var subtotal = lines.Sum(l => l.Price);
        var shipping = subtotal == 0 || subtotal >= FreeShippingOverKrw ? 0 : ShippingKrw;
        return (subtotal, shipping, subtotal + shipping);
    }

    /// <summary>결제. 성공하면 배송될 물리 부품 목록을 돌려주고 소모품은 바로 재고에 더한다.</summary>
    public (bool Ok, string MessageKo, List<PartDef> Delivered) Purchase(IReadOnlyList<OrderLine> lines)
    {
        var (subtotal, shipping, total) = Quote(lines);
        if (lines.Count == 0) return (false, "주문할 품목이 없습니다.", []);
        if (total > BalanceKrw) return (false, $"잔액이 부족합니다. (필요 ₩{total:N0}, 잔액 ₩{BalanceKrw:N0})", []);
        var delivered = new List<PartDef>();
        foreach (var l in lines)
        {
            if (l.Item.IsConsumable)
                Consumables[l.Item.Sku] = Consumables.GetValueOrDefault(l.Item.Sku) + l.Item.PackQty * l.Qty;
            else
                for (var i = 0; i < l.Qty; i++) delivered.Add(l.Item.Build(l.LengthMm, l.WidthMm)!);
        }
        BalanceKrw -= total;
        Ledger.Add($"주문 ₩{total:N0} (상품 ₩{subtotal:N0} + 배송 ₩{shipping:N0}): " +
                   string.Join(", ", lines.Select(l => $"{l.Item.NameKo}×{l.Qty}")));
        return (true, $"결제 완료 ₩{total:N0}. 잔액 ₩{BalanceKrw:N0}", delivered);
    }

    public bool Has(IReadOnlyDictionary<string, int> needs) => needs.All(n => Consumables.GetValueOrDefault(n.Key) >= n.Value);

    public void Consume(IReadOnlyDictionary<string, int> needs)
    {
        foreach (var (sku, qty) in needs) Consumables[sku] -= qty;
    }
}
