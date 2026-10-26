//! 게임 규칙 상수와 월드 배치. C# 쪽 GameData.cs 와 값이 같아야 한다.

pub const TICK_HZ: u32 = 10;
pub const WORLD_RADIUS: f32 = 60.0;
pub const FENCE_RADIUS: f32 = 18.0;
pub const GATE_HALF_WIDTH: f32 = 1.6;
pub const AOI_RADIUS: f32 = 80.0;
pub const MAX_SPEED: f32 = 7.0; // m/s, 달리기 + 지연 여유
pub const INTERACT_RANGE: f32 = 4.5; // 클라이언트 3.5m + 지연 여유

pub const SPAWN: (f32, f32, f32) = (-6.5, 0.0, 0.0);
pub const SPAWN_YAW: f32 = std::f32::consts::FRAC_PI_2;

pub const CRYO_YEARS: u16 = 10;
pub const HARAM_START_AGE: u8 = 72;
pub const HARAM_DEATH_AGE: u8 = 90;
pub const BIO_START: f32 = 70.0;
pub const BIO_DECAY: f32 = 16.0;
pub const BIO_DECAY_CULTURE: f32 = 8.0;
pub const BIO_MEAL: f32 = 6.0;
pub const BATTERY_PER_M_INSIDE: f32 = 0.05;
pub const BATTERY_PER_M_OUTSIDE: f32 = 0.2;
pub const COLLECT_GOAL: u8 = 3;

pub const NODE_TARGET: usize = 18;
pub const NODE_RESPAWN_SECS: f32 = 4.0;
pub const NODE_ID_BASE: u32 = 1000;

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
#[repr(u8)]
pub enum Phase {
    Daily = 0,
    Research = 1,
    Outing = 2,
    Ending = 3,
    Finished = 4,
}

impl Phase {
    pub fn from_u8(v: u8) -> Phase {
        match v {
            1 => Phase::Research,
            2 => Phase::Outing,
            3 => Phase::Ending,
            4 => Phase::Finished,
            _ => Phase::Daily,
        }
    }
    /// 이 단계의 과제 개수 (과제는 비트마스크로 저장)
    pub fn task_count(self) -> u8 {
        match self {
            Phase::Daily => 4,
            Phase::Research | Phase::Outing => 3,
            Phase::Ending => 1,
            Phase::Finished => 0,
        }
    }
}

// 과제 비트 (단계별)
pub mod task {
    pub const CHARGE: u8 = 0;
    pub const EAT: u8 = 1;
    pub const OIL: u8 = 2;
    pub const JOURNAL: u8 = 3;
    pub const BOARD: u8 = 0;
    pub const ANALYZE: u8 = 1;
    pub const BENCH: u8 = 2;
    pub const COLLECT: u8 = 0;
    pub const VISIT: u8 = 1;
    pub const SLEEP: u8 = 2;
}

// 상호작용 대상 ID. 1000 이상은 자원 노드.
pub mod target {
    pub const CHARGE_DOCK: u32 = 1;
    pub const FOOD_TABLE: u32 = 2;
    pub const OIL_CAN: u32 = 3;
    pub const JOURNAL: u32 = 4;
    pub const BED: u32 = 5;
    pub const CRYO_POD: u32 = 6;
    pub const GOODS_SHELF: u32 = 7;
    pub const BLACKBOARD: u32 = 11;
    pub const ANALYZER: u32 = 12;
    pub const WORKBENCH: u32 = 13;
    pub const PILLAR_ETERNAL: u32 = 14;
    pub const PILLAR_MORTAL: u32 = 15;
    pub const HARAM: u32 = 50;
}

pub fn station_pos(id: u32) -> Option<(f32, f32)> {
    use target::*;
    Some(match id {
        CHARGE_DOCK => (-12.0, -2.5),
        FOOD_TABLE => (-7.0, -2.5),
        OIL_CAN => (-10.0, -2.8),
        JOURNAL => (-7.0, 2.5),
        BED => (-11.8, 2.0),
        CRYO_POD => (-12.3, 0.0),
        GOODS_SHELF => (-9.5, 3.2),
        BLACKBOARD => (12.8, 0.0),
        ANALYZER => (11.0, -2.5),
        WORKBENCH => (11.0, 2.5),
        PILLAR_ETERNAL => (7.0, -2.0),
        PILLAR_MORTAL => (7.0, 2.0),
        HARAM => (-22.0, -34.0),
        _ => return None,
    })
}

// 자원: 0 티타늄, 1 석영, 2 구리, 3 약초
pub const RESOURCE_NAMES: [&str; 4] = ["티타늄 원석", "석영", "구리", "약초"];
pub const RESOURCE_WEIGHTS: [u32; 4] = [3, 3, 2, 3];
pub const START_INVENTORY: [u16; 4] = [1, 2, 1, 0];

pub struct Project {
    pub name: &'static str,
    pub cost: [u16; 4],
    pub effect: &'static str,
}

pub const PROJECTS: [Project; 5] = [
    Project { name: "진공관 증폭기", cost: [0, 2, 1, 0], effect: "외출 시 자원 위치에 광선이 보입니다." },
    Project { name: "태엽 보조 전지", cost: [1, 0, 2, 0], effect: "배터리 소모가 절반으로 줄어듭니다." },
    Project { name: "생체 배양액", cost: [0, 0, 0, 3], effect: "밤마다 생체 안정도 하락이 절반이 됩니다." },
    Project { name: "양자 아날로그 통신기", cost: [2, 2, 0, 0], effect: "하람과 더 깊은 대화를 나눌 수 있습니다." },
    Project { name: "영생 프로토콜", cost: [3, 3, 0, 2], effect: "최종 연구. 선택의 순간이 옵니다." },
];

pub const P_SPRING: u8 = 2;
pub const P_CULTURE: u8 = 3;
pub const P_RADIO: u8 = 4;

pub fn cost_text(cost: &[u16; 4]) -> String {
    cost.iter()
        .enumerate()
        .filter(|(_, &n)| n > 0)
        .map(|(i, n)| format!("{} {}", RESOURCE_NAMES[i], n))
        .collect::<Vec<_>>()
        .join(", ")
}

pub const HARAM_LINES: [&str; 5] = [
    "돌리 왔구나. 오늘도 관절에서 태엽 소리가 곱네.",
    "저 언덕 너머 석영은 비 온 뒤에 더 잘 보인단다.",
    "우리 때는 화면이란 게 있었지. 지금은 바늘과 톱니뿐이야. 난 이게 더 좋아.",
    "네 얼굴은 없어도, 목소리로 기분은 다 알 수 있어.",
    "구리는 바위 근처에 많아. 배터리 조심해서 다니렴.",
];

pub const HARAM_DEEP_LINES: [&str; 3] = [
    "영원히 산다는 건, 작별 인사를 영원히 한다는 뜻이란다.",
    "동면에서 깨어나면 세상은 너를 기다려 주지 않아. 그래도 돌아와 줄 거지?",
    "기억이 남아 있는 한, 사람은 끝나지 않는다고 생각해.",
];

pub const HARAM_LETTER: &str = "\u{201c}돌리에게. 네가 깨어났을 때 내가 없더라도, 이 마을의 나무들은 너를 기억할 거야. \
영원을 택하든 끝을 택하든, 그건 네 몫이란다. — 하람\u{201d}";

pub const GOODS_TEXT: &str = "굿즈 진열장: 진공관 램프 · 태엽 회중시계 · 티타늄 카드지갑. \
게임 속 아날로그 소품은 실물 굿즈로 출시될 예정입니다.";
