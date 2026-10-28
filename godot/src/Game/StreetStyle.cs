using Godot;

namespace DollyForge.Game;

public enum LinePattern { None, Dashed, LongDashed, Solid, Double, DoubleYellowKerb }
public enum PoleKind { None, KoreaConcrete, JapanConcrete, UsWood, PlainConcrete }
public enum BollardKind { None, KoreaYellowFlex, FranceWhite }
public enum RoadSignKind { KoreaPlate, JapanPlate, UsGreenBlade, FranceWallPlate, UkWallPlate, BrazilPlate }

/// <summary>
/// 거리뷰로 읽히는 나라별 단서를 한 묶음으로. 공통 기준(뷰 3 m, 길가 밀도, 사진의 기하·노출)은 모든 양식에 같고,
/// 여기에는 나라마다 달라서 거리뷰 한 장만 보고도 나라를 알아보게 하는 것만 둔다.
/// 근거는 기획서 "전 세계 거리뷰 단서" 표 (GeoGuessr 커뮤니티 정리 + 규격 자료).
/// </summary>
public sealed record StreetStyle(
    string Id,
    string Name,
    bool LeftHandTraffic,
    Color CenterColor,
    LinePattern Center,
    LinePattern Edge,
    PoleKind Pole,
    BollardKind Bollard,
    RoadSignKind RoadSign,
    string RoadName,
    string[] ShopNames,
    bool VerticalSigns,
    (string Texture, float Tile, Color Tint, float Weight)[] Facades,
    int MinFloors,
    int MaxFloors,
    bool PitchedRoofs,
    Color RoofColor,
    bool Pilotis,
    bool GasPipes,
    bool Grilles,
    bool AcUnits,
    bool Shutters,
    Color NumberPlate,
    bool Mirror,
    bool SpeedHump,
    bool TrashBags,
    bool Scooter,
    bool Styrofoam,
    bool VendingMachine,
    bool Hydrant,
    bool Mailbox,
    bool PharmacyCross,
    bool CafeTerrace,
    bool PostBox,
    Color BinColor,
    float CarLength)
{
    public static readonly StreetStyle Korea = new(
        "kr", "한국 주택가 골목", false,
        new Color(0.86f, 0.68f, 0.16f), LinePattern.Solid, LinePattern.Solid,
        PoleKind.KoreaConcrete, BollardKind.KoreaYellowFlex, RoadSignKind.KoreaPlate,
        "들꽃길", ["세탁소", "철물점", "부동산", "분식", "미용실", "수선", "슈퍼", "인쇄", "꽃집", "열쇠"], true,
        [("red_brick_03", 1.5f, new Color(0.92f, 0.86f, 0.82f), 0.45f), ("grey_tiles", 1.1f, new Color(0.96f, 0.9f, 0.8f), 0.25f),
         ("beige_wall_001", 2.4f, new Color(0.86f, 0.82f, 0.74f), 0.3f)],
        2, 4, false, default, true, true, true, true, false, new Color(0.07f, 0.2f, 0.47f),
        true, true, true, true, true, false, false, false, false, false, false, new Color(0.27f, 0.37f, 0.32f), 1f);

    public static readonly StreetStyle Japan = new(
        "jp", "일본 주택가", true,
        new Color(0.9f, 0.9f, 0.86f), LinePattern.Dashed, LinePattern.Solid,
        PoleKind.JapanConcrete, BollardKind.None, RoadSignKind.JapanPlate,
        "花通り", ["クリーニング", "薬局", "酒店", "理容", "喫茶", "不動産", "米店", "書店"], true,
        [("beige_wall_001", 2.2f, new Color(0.9f, 0.88f, 0.82f), 0.45f), ("grey_tiles", 0.9f, new Color(0.82f, 0.8f, 0.76f), 0.3f),
         ("concrete_wall_008", 1.6f, new Color(0.85f, 0.84f, 0.8f), 0.25f)],
        2, 3, true, new Color(0.24f, 0.27f, 0.3f), false, false, true, true, false, new Color(0.1f, 0.3f, 0.55f),
        true, false, false, true, false, true, false, false, false, false, false, new Color(0.3f, 0.45f, 0.62f), 0.82f);

    public static readonly StreetStyle France = new(
        "fr", "프랑스 마을 거리", false,
        new Color(0.92f, 0.92f, 0.9f), LinePattern.Dashed, LinePattern.LongDashed,
        PoleKind.None, BollardKind.FranceWhite, RoadSignKind.FranceWallPlate,
        "Rue des Fleurs", ["BOULANGERIE", "PHARMACIE", "CAFÉ", "TABAC", "COIFFURE", "FLEURISTE", "BOUCHERIE"], false,
        [("beige_wall_001", 2.6f, new Color(0.93f, 0.88f, 0.78f), 0.6f), ("concrete_wall_008", 2f, new Color(0.9f, 0.86f, 0.78f), 0.4f)],
        2, 4, true, new Color(0.36f, 0.38f, 0.4f), false, false, false, false, true, new Color(0.08f, 0.22f, 0.5f),
        false, false, false, false, false, false, false, false, true, true, false, new Color(0.2f, 0.42f, 0.25f), 0.95f);

    public static readonly StreetStyle UnitedStates = new(
        "us", "미국 소도시 중심가", false,
        new Color(0.9f, 0.72f, 0.14f), LinePattern.Double, LinePattern.Solid,
        PoleKind.UsWood, BollardKind.None, RoadSignKind.UsGreenBlade,
        "Wildflower St", ["DINER", "HARDWARE", "BARBER", "LAUNDROMAT", "PHARMACY", "BAKERY", "BOOKS"], false,
        [("red_brick_03", 1.5f, new Color(0.95f, 0.85f, 0.78f), 0.7f), ("beige_wall_001", 2.4f, new Color(0.9f, 0.87f, 0.8f), 0.3f)],
        2, 3, false, default, false, false, false, false, false, default,
        false, false, false, false, false, false, true, true, false, false, false, new Color(0.2f, 0.24f, 0.26f), 1.12f);

    public static readonly StreetStyle UnitedKingdom = new(
        "uk", "영국 테라스 거리", true,
        new Color(0.92f, 0.92f, 0.9f), LinePattern.Dashed, LinePattern.DoubleYellowKerb,
        PoleKind.None, BollardKind.None, RoadSignKind.UkWallPlate,
        "Wildflower Lane", ["NEWSAGENT", "CHEMIST", "FISH & CHIPS", "BARBERS", "BAKERY", "OFF LICENCE"], false,
        [("red_brick_03", 1.4f, new Color(0.88f, 0.8f, 0.76f), 0.75f), ("beige_wall_001", 2.4f, new Color(0.93f, 0.92f, 0.88f), 0.25f)],
        2, 3, true, new Color(0.3f, 0.32f, 0.36f), false, false, false, false, false, new Color(0.95f, 0.95f, 0.93f),
        false, false, false, false, false, false, false, false, false, false, true, new Color(0.12f, 0.13f, 0.13f), 0.95f);

    public static readonly StreetStyle Brazil = new(
        "br", "브라질 주택가", false,
        new Color(0.9f, 0.72f, 0.14f), LinePattern.Dashed, LinePattern.Solid,
        PoleKind.PlainConcrete, BollardKind.None, RoadSignKind.BrazilPlate,
        "Rua das Flores", ["PADARIA", "FARMÁCIA", "AÇOUGUE", "BAR", "MERCADO", "LANCHONETE"], false,
        [("beige_wall_001", 2.4f, new Color(0.95f, 0.82f, 0.45f), 0.25f), ("beige_wall_001", 2.4f, new Color(0.6f, 0.78f, 0.86f), 0.2f),
         ("beige_wall_001", 2.4f, new Color(0.92f, 0.66f, 0.66f), 0.15f), ("beige_wall_001", 2.4f, new Color(0.7f, 0.85f, 0.62f), 0.15f),
         ("concrete_wall_008", 1.8f, new Color(0.88f, 0.86f, 0.82f), 0.25f)],
        1, 3, false, default, false, false, true, true, false, new Color(0.1f, 0.25f, 0.55f),
        false, false, false, true, false, false, false, false, false, false, false, new Color(0.24f, 0.3f, 0.26f), 0.95f);

    public static readonly StreetStyle[] All = [Korea, Japan, France, UnitedStates, UnitedKingdom, Brazil];

    public static StreetStyle ById(string id) => All.FirstOrDefault(s => s.Id == id) ?? Korea;

    /// <summary>시드에 따라 외벽 재질 하나 (비율은 Weight)</summary>
    public (string Texture, float Tile, Color Tint) Facade(float roll)
    {
        var total = Facades.Sum(f => f.Weight);
        var at = roll * total;
        foreach (var f in Facades)
        {
            if (at < f.Weight) return (f.Texture, f.Tile, f.Tint);
            at -= f.Weight;
        }
        var last = Facades[^1];
        return (last.Texture, last.Tile, last.Tint);
    }
}
