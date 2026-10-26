namespace DollyForge.Core;

/// <summary>사람이 실제로 쓰는 체결·결합 방법. 각 방법은 관절 종류 하나로 이어진다.</summary>
public enum JoinMethod { Weld, Bolt, Screw, Axle, Hinge, Slide, Ball }

/// <summary>결합 가능 여부, 이유, 소모되는 체결 부품, 모터 구동 여부</summary>
public sealed record JoinCheck(bool Ok, string ReasonKo, IReadOnlyDictionary<string, int> Consumes, JointKind Kind, bool Driven = false);

/// <summary>
/// 현실에서 가능한 결합만 허용한다.
/// 용접은 같은 금속끼리, 프로파일은 T너트, 목재는 나사, 축은 지름이 맞는 구멍에만 들어간다.
/// </summary>
public static class Rules
{
    static readonly IReadOnlyDictionary<string, int> None = new Dictionary<string, int>();

    static bool IsMetal(MaterialDef m) => m.Id is "steel_s235" or "cast_iron" or "al6061_t6" or "ti6al4v" or "brass_c260" or "copper";
    static bool IsWood(MaterialDef m) => m.Id is "pine" or "oak";
    static string WeldFamily(MaterialDef m) => m.Id is "al6061_t6" ? "al" : m.Id is "steel_s235" ? "steel" : m.Id is "ti6al4v" ? "ti" : "";

    public static JoinCheck Check(JoinMethod method, PartDef a, PartDef b)
    {
        static JoinCheck No(string why, JointKind k) => new(false, why, None, k);
        var delicate = new[] { PartRole.Bearing, PartRole.Motor, PartRole.Battery, PartRole.Controller, PartRole.Sequencer, PartRole.Display };
        switch (method)
        {
            case JoinMethod.Weld:
            {
                if (delicate.Contains(a.Role) || delicate.Contains(b.Role))
                    return No("베어링·모터·배터리·제어 기기는 용접하면 망가집니다. 볼트로 고정하세요.", JointKind.Fixed);
                var fa = WeldFamily(a.Material);
                if (fa == "" || fa != WeldFamily(b.Material))
                    return No($"{a.Material.NameKo}와(과) {b.Material.NameKo}는 용접할 수 없습니다. 같은 금속끼리만 용접됩니다.", JointKind.Fixed);
                return new(true, "용접", None, JointKind.Fixed);
            }
            case JoinMethod.Bolt:
            {
                if (IsWood(a.Material) && IsWood(b.Material)) return No("목재끼리는 목재 나사로 체결하세요.", JointKind.Fixed);
                var sku = a.Role == PartRole.Profile || b.Role == PartRole.Profile ? "FS-TNUT-M5" : "FS-BOLT-M8";
                return new(true, sku == "FS-TNUT-M5" ? "T너트 볼트 체결" : "M8 볼트 체결", new Dictionary<string, int> { [sku] = 2 }, JointKind.Fixed);
            }
            case JoinMethod.Screw:
                if (!IsWood(a.Material) && !IsWood(b.Material)) return No("목재 나사는 한쪽이 목재여야 합니다.", JointKind.Fixed);
                return new(true, "목재 나사 체결", new Dictionary<string, int> { ["FS-WOOD-4X40"] = 4 }, JointKind.Fixed);
            case JoinMethod.Axle:
            {
                var (shaft, hole) = a.ShaftMm > 0 ? (a, b) : (b, a);
                if (shaft.ShaftMm <= 0) return No("회전 관절에는 축(샤프트·모터 출력축)이 필요합니다.", JointKind.Continuous);
                if (hole.BoreMm <= 0) return No($"{hole.NameKo}에는 축 구멍이 없습니다. 베어링이나 바퀴에 끼우세요.", JointKind.Continuous);
                if (MathF.Abs(shaft.ShaftMm - hole.BoreMm) > 0.05f)
                    return No($"축 지름 {shaft.ShaftMm} mm 와 구멍 지름 {hole.BoreMm} mm 가 맞지 않습니다.", JointKind.Continuous);
                var driven = shaft.Role == PartRole.Motor;
                return new(true, driven ? "모터 직결" : "축 결합", None, JointKind.Continuous, driven);
            }
            case JoinMethod.Hinge:
                return new(true, "경첩", new Dictionary<string, int> { ["MC-HINGE-75"] = 1 }, JointKind.Revolute);
            case JoinMethod.Slide:
            {
                var shaft = a.Role == PartRole.Shaft ? a : b.Role == PartRole.Shaft ? b : null;
                if (shaft is null || MathF.Abs(shaft.ShaftMm - 8) > 0.05f)
                    return No("LM8UU 리니어 부싱은 Ø8 리니어 샤프트 위에서만 미끄러집니다.", JointKind.Prismatic);
                return new(true, "리니어 부싱", new Dictionary<string, int> { ["MC-LM8UU"] = 1 }, JointKind.Prismatic);
            }
            default:
                return new(true, "로드엔드 볼조인트", new Dictionary<string, int> { ["MC-RODEND-M8"] = 1 }, JointKind.Ball);
        }
    }
}
