//! 존중 규칙 (0.5): 자리 → 구역, 구역 × 행동 → 권한. 모든 편집 요청은 여기를 지난다.
//!   집 골격·이웃 집터·길·시골 공용지는 형태가 정해져 있고, 밖(야생)만 자유롭다.
//!   내 마당은 내가 부은 것만 다시 팔 수 있다 (own_placed).

use crate::content::{Layout, Pack, FLOOR_Y};

#[derive(Clone, Copy, Debug, PartialEq)]
pub enum Action {
    Dig,
    Place,
}

impl Action {
    pub fn key(self) -> &'static str {
        match self {
            Action::Dig => "dig",
            Action::Place => "place",
        }
    }
    fn verb(self) -> &'static str {
        match self {
            Action::Dig => "파기",
            Action::Place => "놓기·붓기",
        }
    }
}

pub struct Verdict {
    pub ok: bool,
    pub zone: String,
    pub rule: String,
    pub msg: String,
}

fn smooth(a: f32, b: f32, x: f32) -> f32 {
    let t = ((x - a) / (b - a)).clamp(0.0, 1.0);
    t * t * (3.0 - 2.0 * t)
}

/// C# Terrain.Wildness 와 같은 식 (같은 world.json 값)
pub fn wildness(l: &Layout, x: f32, z: f32) -> f32 {
    let (cx, cz) = l.settled_center;
    let (rx, rz) = l.settled_radius;
    let e = ((x - cx).powi(2) / (rx * rx) + (z - cz).powi(2) / (rz * rz)).sqrt();
    smooth(l.settled_edge.0, l.settled_edge.1, e)
}

/// 자리의 구역 id (집 번호가 있으면 함께). 집 안의 방 공간(바깥벽 안쪽, 바닥 위 · 천장 아래)은 집 안이고, 벽·바닥·지붕 자체는 골격이다
pub fn zone_at(l: &Layout, p: [f32; 3]) -> (&'static str, Option<usize>) {
    let [x, y, z] = p;
    let (fw, fd) = l.footprint;
    const WALL: f32 = 0.12;
    for (i, hx) in l.house_x.iter().enumerate() {
        let ox = hx + l.origin_dx;
        if x >= ox && x <= ox + fw && z >= 0.0 && z <= fd {
            let room = x > ox + WALL && x < ox + fw - WALL && z > WALL && z < fd - WALL && y > FLOOR_Y + 0.03 && y < l.wall_height - 0.03;
            let own = i == l.own_house;
            return (
                match (own, room) {
                    (true, true) => "home_interior",
                    (true, false) => "home_shell",
                    _ => "neighbour_home",
                },
                Some(i),
            );
        }
    }
    for (i, hx) in l.house_x.iter().enumerate() {
        if (x - hx).abs() < l.lot_x_half && z >= l.lot_z.0 && z <= l.lot_z.1 {
            return (if i == l.own_house { "home_interior" } else { "neighbour_home" }, Some(i));
        }
    }
    if (z - l.road_z).abs() < l.road_half + 0.6 {
        return ("city", None);
    }
    if x.abs() < l.field_x_half && z >= l.field_z.0 && z <= l.field_z.1 {
        return ("rural_common", None);
    }
    if wildness(l, x, z) >= 0.5 {
        ("wild", None)
    } else {
        ("rural_common", None)
    }
}

/// own_placed 판정에 쓰는 물음: 이 자리가 이 사람이 부은 흙 안인가
pub trait PlacedBy {
    fn placed_by(&self, player: &str, p: [f32; 3]) -> bool;
}

pub fn judge(pack: &Pack, player: &str, action: Action, p: [f32; 3], placed: &impl PlacedBy) -> Verdict {
    let l = &pack.layout;
    let (zone, house) = zone_at(l, p);
    let z = &pack.zones[zone];
    let rule = z.rules.get(action.key()).cloned().unwrap_or_else(|| "deny".into());
    // 한 사람 놀이(v0.1): 내 집(own_house)의 주인은 이 접속자
    let owner = house == Some(l.own_house);
    let (ok, why) = match rule.as_str() {
        "allow" => (true, "누구나 할 수 있다"),
        "owner" | "invited" => (owner, if owner { "주인이라 할 수 있다" } else { "주인과 초대받은 이만 할 수 있다" }),
        "own_placed" => {
            let mine = owner && placed.placed_by(player, p);
            (mine, if mine { "내가 부은 흙이라 다시 팔 수 있다" } else { "내가 놓거나 부은 것만 다시 팔 수 있다 (집과 마당의 형태는 정해져 있다)" })
        }
        "anchors" => (false, "규격 고정점에만 할 수 있다"),
        "public" => (false, "공용 시설만 쓸 수 있다"),
        _ => (false, "아무도 하지 않는다 (서로의 자리를 존중한다)"),
    };
    Verdict { ok, zone: zone.into(), rule: rule.clone(), msg: format!("{}: {} — {}", z.name, action.verb(), why) }
}

#[cfg(test)]
mod tests {
    use super::*;

    struct Nothing;
    impl PlacedBy for Nothing {
        fn placed_by(&self, _: &str, _: [f32; 3]) -> bool {
            false
        }
    }

    fn pack() -> Pack {
        Pack::load(&crate::content::default_dir()).expect("콘텐츠 팩")
    }

    #[test]
    fn respect_rules_follow_the_zone_table() {
        let p = pack();
        let dig = |x: f32, z: f32| judge(&p, "local", Action::Dig, [x, 0.0, z], &Nothing);
        assert_eq!(dig(0.0, 3.0).zone, "home_shell");
        assert!(!dig(0.0, 3.0).ok, "집 골격은 부술 수 없다");
        assert_eq!(dig(18.0, -4.0).zone, "neighbour_home");
        assert!(!dig(18.0, -4.0).ok, "이웃 마당은 파지 않는다");
        assert!(!dig(0.0, -10.5).ok, "길은 파지 않는다");
        assert!(!dig(0.0, -50.0).ok, "밭(시골 공용지)은 파지 않는다");
        assert!(!dig(2.0, -4.0).ok, "내 마당 잔디는 내가 부은 것이 아니다");
        let wild = dig(0.0, 80.0);
        assert_eq!(wild.zone, "wild");
        assert!(wild.ok, "밖(야생)은 자유롭다");
        assert!(judge(&p, "local", Action::Place, [2.0, 0.0, -4.0], &Nothing).ok, "내 마당에는 놓을 수 있다");
        let room = judge(&p, "local", Action::Place, [-3.8, 0.4, 2.5], &Nothing);
        assert_eq!(room.zone, "home_interior", "침실 바닥 위는 집 안");
        assert!(room.ok, "내 방에는 가구를 놓을 수 있다");
        assert!(!dig(-3.8, 2.5).ok, "침실 바닥은 골격이라 파지 않는다");
        assert!(!judge(&p, "local", Action::Place, [18.0, 0.4, 3.0], &Nothing).ok, "이웃 집 안에는 놓지 않는다");
    }
}
