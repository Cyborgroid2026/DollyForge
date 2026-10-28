using Godot;

namespace DollyForge.Game;

/// <summary>발소리: 잡음을 걸러 만든 한 발짝 (중심 주파수, 날카로움, 감쇠 시간, 울림)</summary>
public sealed record StepSound(float CenterHz, float Q, float DecayMs, float RingHz, float Gain);

/// <summary>
/// 길·벽·바닥의 표면. 거리뷰 사진의 "질감"을 물리 세계로 옮긴 것으로, 다섯 감각이 모두 여기서 나온다.
///   촉각: 마찰(신발 밑창 고무 기준), 반발, 표면 요철 깊이, 열 유출률(만졌을 때 차고 따뜻한 정도)
///   청각: 그 위를 디딜 때의 발소리
///   후각: 표면 자체가 내는 냄새 (뜨거운 아스팔트의 타르, 풀)
///   미각(접촉 화학)은 손에 쥔 재료에서 (TasteOf)
/// 값의 근거는 기획서 "다섯 감각과 질감" 표.
/// </summary>
public sealed record Surface(
    string Id,
    string Name,
    float Friction,
    float Bounce,
    float TextureDepthMm,
    float Effusivity,
    StepSound Step,
    string? Smell = null)
{
    public const string MetaKey = "surface";

    // 마찰: 고무–마른 아스팔트 정지 0.9·운동 0.5–0.8, 고무–콘크리트 0.6–0.85, 신발–나무 0.3–0.6.
    // 열 유출률 J·s^-½·K^-1·m^-2: 나무 401, 고무 571, 유리 1433, 화강암 2750, 스테인리스 10184 (측정값).
    // 콘크리트 1571·벽돌 970 은 k·ρ·c 로 계산, 아스팔트 1260 은 보통 물성(k 0.75, ρ 2300, c 920)으로 계산.
    public static readonly Surface Asphalt = new("asphalt", "아스팔트", 0.75f, 0.15f, 0.9f, 1260, new(900, 1.2f, 70, 0, 0.55f), "햇볕 받은 타르 냄새");
    public static readonly Surface Paint = new("road_paint", "도로 도색", 0.55f, 0.15f, 0.2f, 1260, new(1300, 1.5f, 55, 0, 0.5f));
    public static readonly Surface Paver = new("concrete_paver", "보도블록", 0.72f, 0.25f, 0.4f, 1571, new(2200, 2.2f, 45, 0, 0.6f));
    public static readonly Surface Concrete = new("concrete", "콘크리트", 0.7f, 0.25f, 0.3f, 1571, new(2000, 2f, 50, 0, 0.6f));
    public static readonly Surface Tactile = new("tactile_paving", "점자블록", 0.8f, 0.2f, 5f, 600, new(1600, 1.6f, 60, 0, 0.6f));
    public static readonly Surface Steel = new("cast_iron_cover", "주철 맨홀·빗물받이", 0.5f, 0.35f, 2.5f, 10184, new(700, 6f, 260, 820, 0.7f));
    public static readonly Surface Brick = new("clay_brick", "벽돌", 0.7f, 0.15f, 1.5f, 970, new(1500, 1.8f, 55, 0, 0.55f));
    public static readonly Surface Plaster = new("plaster", "미장 벽", 0.6f, 0.1f, 0.6f, 900, new(1200, 1.5f, 50, 0, 0.5f));
    public static readonly Surface PaintedSteel = new("painted_steel", "도장 강판", 0.45f, 0.4f, 0.05f, 10184, new(600, 5f, 200, 540, 0.65f));
    public static readonly Surface Plastic = new("plastic", "플라스틱", 0.5f, 0.35f, 0.1f, 514, new(1700, 3f, 60, 0, 0.5f));
    public static readonly Surface Wood = new("wood", "나무", 0.45f, 0.3f, 0.8f, 401, new(380, 2.5f, 90, 260, 0.6f));
    public static readonly Surface Grass = new("grass", "풀밭", 0.4f, 0.05f, 25f, 800, new(3500, 0.6f, 140, 0, 0.3f), "풀 냄새");

    public static readonly Surface[] All = [Asphalt, Paint, Paver, Concrete, Tactile, Steel, Brick, Plaster, PaintedSteel, Plastic, Wood, Grass];

    static readonly Dictionary<string, PhysicsMaterial> Physics = [];

    /// <summary>이 표면의 물리 재질 (Jolt 가 접촉마다 마찰·반발을 섞는다)</summary>
    public PhysicsMaterial PhysicsMaterial()
    {
        if (!Physics.TryGetValue(Id, out var m))
            Physics[Id] = m = new PhysicsMaterial { Friction = Friction, Bounce = Bounce };
        return m;
    }

    /// <summary>충돌체에 표면을 붙인다: 물리 재질과 감각용 표시</summary>
    public T Apply<T>(T body) where T : StaticBody3D
    {
        body.PhysicsMaterialOverride = PhysicsMaterial();
        body.SetMeta(MetaKey, Id);
        return body;
    }

    public static Surface? Of(GodotObject? collider) =>
        collider is not null && collider.HasMeta(MetaKey) ? ById(collider.GetMeta(MetaKey).AsString()) : null;

    public static Surface? ById(string id) => All.FirstOrDefault(s => s.Id == id);

    /// <summary>
    /// 만진 순간 손끝이 느끼는 온도 (°C). 두 물체가 닿으면 접촉면은 열 유출률로 가중한 평균 온도가 된다.
    /// 손끝 감각부는 사람 피부처럼 33 °C·열 유출률 1500 으로 둔다.
    /// </summary>
    public float ContactTemperature(float ambientC) => (1500f * 33f + Effusivity * ambientC) / (1500f + Effusivity);

    /// <summary>표면 결을 말로: 요철 깊이 (mm)</summary>
    public string TextureWord => TextureDepthMm switch
    {
        < 0.1f => "매끈함",
        < 0.5f => "고운 결",
        < 1.2f => "까끌까끌함",
        < 4f => "울퉁불퉁함",
        _ => "돌기·덩어리",
    };

    public string TemperatureWord(float ambientC) => ContactTemperature(ambientC) switch
    {
        < 22f => "차가움",
        < 26f => "서늘함",
        < 29f => "미지근함",
        _ => "따뜻함",
    };

    public string GripWord => Friction switch
    {
        < 0.45f => "미끄러움",
        < 0.6f => "조금 미끄러움",
        _ => "단단히 잡힘",
    };

    /// <summary>
    /// 미각 = 접촉 화학 감각. 얼굴 없는 사이보그는 혀가 없지만 손끝 감각부가 닿은 재료의 이온·휘발 성분을 읽는다.
    /// </summary>
    public static string TasteOf(string materialId) => materialId switch
    {
        "steel_s235" or "cast_iron" => "쇠 맛 (철 이온, 피 같은 금속성)",
        "copper" or "brass_c260" => "쓰고 시큼한 구리 맛",
        "ti6al4v" => "거의 없음 (티타늄 산화막)",
        "al6061_t6" => "약한 금속성",
        "pine" => "송진의 쓴맛",
        "oak" => "떫은 타닌 맛",
        "natural_rubber" => "고무의 쓴맛",
        "soda_lime_glass" or "quartz" => "무미",
        "concrete_block" => "알칼리성 쓴맛",
        "clay_brick" or "soil" => "흙 맛",
        _ => "알 수 없음",
    };
}
