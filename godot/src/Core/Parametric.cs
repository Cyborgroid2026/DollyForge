using System.Globalization;

namespace DollyForge.Core;

/// <summary>매개변수 하나. 값은 mm (Fusion 360 기본 단위).</summary>
public sealed record ParamSpec(string Key, string NameKo, float Default, float Min, float Max);

/// <summary>
/// 매개변수형 부품 템플릿 (Fusion 360 의 Parameters 와 같은 개념).
/// 치수와 재료를 정하면 형상·질량·관성이 계산된 PartDef 가 만들어진다.
/// </summary>
public sealed record PartTemplate(
    string Id,
    string NameKo,
    string DescriptionKo,
    MaterialDef[] Materials,
    ParamSpec[] Params,
    Func<Func<string, float>, Shape> Build,
    ActuatorSpec? Actuator = null,
    float? MassOverrideKg = null)
{
    public IReadOnlyDictionary<string, float> Defaults => Params.ToDictionary(p => p.Key, p => p.Default);

    public PartDef Make(MaterialDef material, IReadOnlyDictionary<string, float>? values = null)
    {
        if (!Materials.Contains(material))
            throw new ArgumentException($"{NameKo} 은(는) {material.NameKo} 로 만들 수 없습니다.");
        var v = new Dictionary<string, float>();
        foreach (var p in Params)
        {
            var x = values is not null && values.TryGetValue(p.Key, out var given) ? given : p.Default;
            if (x < p.Min || x > p.Max)
                throw new ArgumentOutOfRangeException(p.Key, $"{p.NameKo} {x} mm 는 허용 범위 {p.Min}–{p.Max} mm 밖입니다.");
            v[p.Key] = x;
        }
        float mm(string key) => v[key] / 1000f;
        var shape = Build(mm);
        var dims = string.Join("×", Params.Select(p => v[p.Key].ToString("0.#", CultureInfo.InvariantCulture)));
        var id = $"{Id}_{material.Id}_{dims.Replace('×', 'x').Replace('.', 'p')}";
        return new PartDef(id, $"{material.NameKo} {NameKo} {dims} mm", shape, material, DescriptionKo, MassOverrideKg, Actuator);
    }
}

public static class Templates
{
    static readonly MaterialDef[] Metals = [Core.Materials.Steel, Core.Materials.Aluminium, Core.Materials.Titanium, Core.Materials.Brass, Core.Materials.Copper];
    static readonly MaterialDef[] Woods = [Core.Materials.Pine, Core.Materials.Oak];

    public static readonly PartTemplate Plate = new(
        "plate", "판재", "평판. 프레임 바닥·덮개·브래킷.",
        [.. Metals, .. Woods],
        [new("w", "가로", 500, 20, 2000), new("d", "세로", 500, 20, 2000), new("t", "두께", 5, 1, 50)],
        mm => Shape.Box(mm("w"), mm("t"), mm("d")));

    public static readonly PartTemplate Bar = new(
        "bar", "각봉", "사각 단면 봉. 프레임·기둥·레버.",
        [.. Metals, .. Woods],
        [new("l", "길이", 1000, 20, 4000), new("a", "단면 한 변", 30, 5, 200)],
        mm => Shape.Box(mm("l"), mm("a"), mm("a")));

    public static readonly PartTemplate Shaft = new(
        "shaft", "축", "원형 단면 축. 차축·핀·피스톤 로드.",
        Metals,
        [new("d", "지름", 20, 3, 200), new("l", "길이", 600, 10, 3000)],
        mm => Shape.Cylinder(mm("d") / 2, mm("l")));

    public static readonly PartTemplate Wheel = new(
        "wheel", "바퀴", "원판형 바퀴. 축에 회전 관절로 단다.",
        [Core.Materials.Pine, Core.Materials.Oak, Core.Materials.Rubber, Core.Materials.Steel, Core.Materials.Aluminium],
        [new("d", "지름", 500, 50, 2000), new("w", "폭", 60, 10, 400)],
        mm => Shape.Cylinder(mm("d") / 2, mm("w")));

    public static readonly PartTemplate Disc = new(
        "disc", "원판·기어 블랭크", "원판. 기어·플라이휠·풀리의 소재.",
        Metals,
        [new("d", "지름", 120, 10, 1000), new("t", "두께", 20, 2, 200)],
        mm => Shape.Cylinder(mm("d") / 2, mm("t")));

    public static readonly PartTemplate Block = new(
        "block", "블록", "건축용 블록. 벽·기초·평형추.",
        [Core.Materials.Concrete, Core.Materials.Brick, Core.Materials.Granite, .. Woods, Core.Materials.Steel],
        [new("w", "가로", 390, 50, 1000), new("h", "높이", 190, 50, 1000), new("d", "깊이", 190, 50, 1000)],
        mm => Shape.Box(mm("w"), mm("h"), mm("d")));

    public static readonly PartTemplate Ball = new(
        "ball", "구", "구. 베어링 볼·평형추.",
        [.. Metals, Core.Materials.Rubber, Core.Materials.Granite],
        [new("d", "지름", 100, 5, 1000)],
        mm => Shape.Sphere(mm("d") / 2));

    /// <summary>
    /// 태엽 모터 (완제품 기기). 실제 대형 태엽 스프링 수준: 정격 토크 12 N·m, 무부하 3 rad/s, 저장 에너지 2 kJ.
    /// 질량은 하우징·스프링·기어를 합친 값.
    /// </summary>
    public static readonly PartTemplate SpringMotor = new(
        "spring_motor", "태엽 모터", "감아 둔 강철 태엽으로 관절을 구동한다. 감기: 상호작용 키를 누르고 있기.",
        [Core.Materials.Steel],
        [new("w", "가로", 160, 160, 160), new("h", "높이", 120, 120, 120), new("d", "깊이", 160, 160, 160)],
        mm => Shape.Box(mm("w"), mm("h"), mm("d")),
        new ActuatorSpec(MaxTorque: 12, MaxSpeed: 3, EnergyJ: 2000),
        MassOverrideKg: 6.5f);

    public static readonly IReadOnlyList<PartTemplate> All = [Plate, Bar, Shaft, Wheel, Disc, Block, Ball, SpringMotor];
}
