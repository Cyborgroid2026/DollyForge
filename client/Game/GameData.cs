namespace DollyForge.Client.Game;

/// <summary>게임 규칙 상수. Rust 서버(server/src/data.rs)와 값이 같아야 한다.</summary>
public enum Phase : byte { Daily = 0, Research = 1, Outing = 2, Ending = 3, Finished = 4 }

public sealed record Station(uint Id, string Label, float X, float Z);

public sealed record Project(string Name, ushort[] Cost, string Effect);

public sealed record PlayerState(
    ushort Day, ushort Years, Phase Phase, byte Tasks, float Battery, float Bio, ushort Memory,
    ushort[] Inv, byte Collected, byte Research, byte HaramAge, bool HaramAlive, byte Gender)
{
    public bool HasTask(int bit) => (Tasks & (1 << bit)) != 0;
}

public static class GameData
{
    public const int CollectGoal = 3;

    public static readonly Station[] Stations =
    [
        new(1, "충전 독", -12f, -2.5f),
        new(2, "영양 식탁", -7f, -2.5f),
        new(3, "윤활유 캔", -10f, -2.8f),
        new(4, "기억 일지", -7f, 2.5f),
        new(5, "정비 침대", -11.8f, 2f),
        new(6, "인공 동면 포드", -12.3f, 0f),
        new(7, "굿즈 진열장", -9.5f, 3.2f),
        new(11, "연구 칠판", 12.8f, 0f),
        new(12, "시료 분석기", 11f, -2.5f),
        new(13, "작업대", 11f, 2.5f),
        new(14, "영원의 기둥", 7f, -2f),
        new(15, "유한의 기둥", 7f, 2f),
        new(50, "하람", -22f, -34f),
    ];

    public static readonly string[] ResourceNames = ["티타늄 원석", "석영", "구리", "약초"];

    public static readonly Project[] Projects =
    [
        new("진공관 증폭기", [0, 2, 1, 0], "외출 시 자원 위치에 광선이 보입니다."),
        new("태엽 보조 전지", [1, 0, 2, 0], "배터리 소모가 절반으로 줄어듭니다."),
        new("생체 배양액", [0, 0, 0, 3], "밤마다 생체 안정도 하락이 절반이 됩니다."),
        new("양자 아날로그 통신기", [2, 2, 0, 0], "하람과 더 깊은 대화를 나눌 수 있습니다."),
        new("영생 프로토콜", [3, 3, 0, 2], "최종 연구. 선택의 순간이 옵니다."),
    ];

    public static string PhaseName(Phase p) => p switch
    {
        Phase.Daily => "일상",
        Phase.Research => "연구",
        Phase.Outing => "외출",
        Phase.Ending => "선택",
        _ => "완료",
    };

    public static string StationLabel(uint id) =>
        id >= 1000 ? "자원" : Stations.FirstOrDefault(s => s.Id == id)?.Label ?? "";

    public static string CostText(ushort[] cost) => string.Join(", ",
        cost.Select((n, i) => (n, i)).Where(t => t.n > 0).Select(t => $"{ResourceNames[t.i]} {t.n}"));

    public static IReadOnlyList<string> TaskNames(PlayerState s) => s.Phase switch
    {
        Phase.Daily => ["충전 독에서 배터리 충전", "생체 조직 영양 섭취", "관절 윤활유 보충", "기억 일지 기록"],
        Phase.Research => ["연구 칠판 확인", "시료 분석기 가동", "작업대에서 연구 진행"],
        Phase.Outing =>
        [
            $"자원 {CollectGoal}개 수집 ({Math.Min((int)s.Collected, CollectGoal)}/{CollectGoal})",
            s.HaramAlive ? "하람 방문 (남서쪽 오두막)" : "하람의 묘 방문",
            "귀가 후 침대에서 정비 슬립",
        ],
        Phase.Ending => ["연구소의 두 기둥 중 하나 선택"],
        _ => [],
    };
}
