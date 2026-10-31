//! 세계 상태 (서버 권한): 지형 편집(둥근 CSG 구), 흩어진 물건(Rapier 강체), 들고 있는 것.
//!   편집은 8 m 섹션마다 파일로 저장한다 (data/sections/sx_sz.json). 물건과 사람 상태도 저장한다.
//!   땅은 엔진이 보낸 높이장(heightfield)에 편집을 반영해 Rapier 충돌체로 쓰고, 집은 상자, 나무 줄기는 캡슐이다.
//!   흙의 양은 보존된다: 한 번 파면 흙 포대 하나가 생기고, 포대 하나를 부으면 같은 부피가 지형에 더해진다.

use crate::content::Pack;
use crate::zones::{self, Action, PlacedBy, Verdict};
use rapier3d::parry::utils::Array2;
use rapier3d::prelude::*;
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use std::collections::{BTreeMap, HashMap};
use std::fs;
use std::path::PathBuf;

pub const SECTION: f32 = 8.0;

#[derive(Clone, Serialize, Deserialize)]
pub struct Edit {
    pub id: u64,
    pub op: String, // "sub" (파기) | "add" (붓기)
    pub p: [f32; 3],
    pub r: f32,
    pub mat: String,
    pub owner: String,
}

#[derive(Clone, Serialize, Deserialize)]
struct SavedBody {
    id: u32,
    item: String,
    owner: String,
    p: [f32; 3],
    q: [f32; 4],
}

struct Body {
    item: String,
    owner: String,
    handle: RigidBodyHandle,
    half: [f32; 3],
    r: f32,
    awake_sent: bool,
}

struct Ground {
    origin: (f32, f32),
    step: f32,
    n: usize,
    h: Vec<f32>, // [z * n + x]
    collider: Option<ColliderHandle>,
}

pub struct World {
    pub pack: Pack,
    dir: PathBuf,
    sections: BTreeMap<(i32, i32), Vec<Edit>>,
    next_edit: u64,
    phys: PhysicsWorld,
    bodies: BTreeMap<u32, Body>,
    next_body: u32,
    pub held: HashMap<String, String>,
    ground: Option<Ground>,
    statics: Vec<ColliderHandle>,
    dirty_bodies: bool,
}

fn section_of(p: [f32; 3]) -> (i32, i32) {
    ((p[0] / SECTION).floor() as i32, (p[2] / SECTION).floor() as i32)
}

impl PlacedBy for World {
    fn placed_by(&self, player: &str, p: [f32; 3]) -> bool {
        let (sx, sz) = section_of(p);
        for a in -1..=1 {
            for b in -1..=1 {
                for e in self.sections.get(&(sx + a, sz + b)).into_iter().flatten() {
                    let d = ((e.p[0] - p[0]).powi(2) + (e.p[1] - p[1]).powi(2) + (e.p[2] - p[2]).powi(2)).sqrt();
                    if e.op == "add" && e.owner == player && d < e.r * 1.6 {
                        return true;
                    }
                }
            }
        }
        false
    }
}

impl World {
    pub fn open(pack: Pack, dir: PathBuf) -> World {
        let _ = fs::create_dir_all(dir.join("sections"));
        let mut w = World {
            pack,
            dir,
            sections: BTreeMap::new(),
            next_edit: 1,
            phys: PhysicsWorld::new(),
            bodies: BTreeMap::new(),
            next_body: 1,
            held: HashMap::new(),
            ground: None,
            statics: vec![],
            dirty_bodies: false,
        };
        // 섹션 파일들
        if let Ok(rd) = fs::read_dir(w.dir.join("sections")) {
            for f in rd.flatten() {
                let Ok(text) = fs::read_to_string(f.path()) else { continue };
                let Ok(edits) = serde_json::from_str::<Vec<Edit>>(&text) else { continue };
                for e in edits {
                    w.next_edit = w.next_edit.max(e.id + 1);
                    w.sections.entry(section_of(e.p)).or_default().push(e);
                }
            }
        }
        if let Ok(text) = fs::read_to_string(w.dir.join("players.json")) {
            w.held = serde_json::from_str(&text).unwrap_or_default();
        }
        w.add_houses();
        // 물건은 땅이 생긴 뒤(엔진의 hello) 다시 놓는다
        w
    }

    pub fn edit_count(&self) -> usize {
        self.sections.values().map(|v| v.len()).sum()
    }

    fn saved_bodies(&self) -> Vec<SavedBody> {
        fs::read_to_string(self.dir.join("bodies.json")).ok().and_then(|t| serde_json::from_str(&t).ok()).unwrap_or_default()
    }

    fn save_section(&self, key: (i32, i32)) {
        let path = self.dir.join("sections").join(format!("{}_{}.json", key.0, key.1));
        let edits = self.sections.get(&key).cloned().unwrap_or_default();
        if let Err(e) = fs::write(&path, serde_json::to_string(&edits).unwrap_or_default()) {
            eprintln!("[world] 섹션 저장 실패 {}: {e}", path.display());
        }
    }

    fn save_players(&self) {
        let _ = fs::write(self.dir.join("players.json"), serde_json::to_string(&self.held).unwrap_or_default());
    }

    pub fn save_bodies(&mut self) {
        if !self.dirty_bodies {
            return;
        }
        let list: Vec<SavedBody> = self
            .bodies
            .iter()
            .filter_map(|(id, b)| {
                let rb = self.phys.bodies.get(b.handle)?;
                let t = rb.translation();
                let q = rb.rotation();
                Some(SavedBody { id: *id, item: b.item.clone(), owner: b.owner.clone(), p: [t.x, t.y, t.z], q: [q.x, q.y, q.z, q.w] })
            })
            .collect();
        let _ = fs::write(self.dir.join("bodies.json"), serde_json::to_string(&list).unwrap_or_default());
        self.dirty_bodies = false;
    }

    // ─── 정적 충돌체: 집 · 땅 · 나무 줄기 ─────────────────────────────────────

    fn add_houses(&mut self) {
        let l = &self.pack.layout;
        let (fw, fd) = l.footprint;
        let colliders: Vec<Collider> = l
            .house_x
            .iter()
            .map(|hx| {
                let hy = l.wall_height * 0.5 + 0.1;
                ColliderBuilder::cuboid(fw * 0.5, hy, fd * 0.5).translation(Vector::new(hx + l.origin_dx + fw * 0.5, hy, fd * 0.5)).friction(0.6).build()
            })
            .collect();
        for c in colliders {
            self.statics.push(self.phys.colliders.insert(c));
        }
    }

    /// 엔진이 보낸 땅 높이장 + 나무 줄기. 지금까지의 편집을 높이장에 반영해 충돌체로 만든다
    pub fn set_ground(&mut self, g: &Value, trees: &Value) {
        let n = g["n"].as_u64().unwrap_or(0) as usize;
        let h: Vec<f32> = g["h"].as_array().into_iter().flatten().map(|v| v.as_f64().unwrap_or(0.0) as f32).collect();
        if n < 2 || h.len() != n * n {
            eprintln!("[world] 높이장이 올바르지 않습니다 (n={n}, {}개)", h.len());
            return;
        }
        let origin = (g["origin"][0].as_f64().unwrap_or(0.0) as f32, g["origin"][1].as_f64().unwrap_or(0.0) as f32);
        let step = g["step"].as_f64().unwrap_or(1.0) as f32;
        self.ground = Some(Ground { origin, step, n, h, collider: None });
        let all: Vec<Edit> = self.sections.values().flatten().cloned().collect();
        for e in &all {
            self.carve_ground(e);
        }
        self.rebuild_ground();
        for t in trees.as_array().into_iter().flatten() {
            let v: Vec<f32> = t.as_array().into_iter().flatten().map(|x| x.as_f64().unwrap_or(0.0) as f32).collect();
            if v.len() < 5 {
                continue;
            }
            let (r, top) = (v[3], v[4]);
            let c = ColliderBuilder::capsule_y(top * 0.5, r).translation(Vector::new(v[0], v[1] + top * 0.5, v[2])).friction(0.5).build();
            self.statics.push(self.phys.colliders.insert(c));
        }
        // 저장된 물건을 다시 놓는다 (처음 한 번)
        if self.bodies.is_empty() {
            for s in self.saved_bodies() {
                self.next_body = self.next_body.max(s.id + 1);
                self.spawn(s.id, &s.item, &s.owner, s.p, s.q, [0.0; 3]);
            }
        }
        println!("[world] 땅 {n}×{n} (간격 {step} m), 나무 줄기 {}개, 물건 {}개", trees.as_array().map_or(0, |a| a.len()), self.bodies.len());
    }

    fn rebuild_ground(&mut self) {
        let Some(g) = self.ground.as_mut() else { return };
        if let Some(c) = g.collider.take() {
            self.phys.remove_collider(c);
        }
        // parry 높이장: 행 = z, 열 = x, 열 우선 저장 (data[i + j * nrows])
        let n = g.n;
        let mut data = vec![0.0f32; n * n];
        for z in 0..n {
            for x in 0..n {
                data[z + x * n] = g.h[z * n + x];
            }
        }
        let size = g.step * (n - 1) as f32;
        let mat = self.pack.materials.get("soil");
        let c = ColliderBuilder::heightfield(Array2::new(n, n, data), Vector::new(size, 1.0, size))
            .translation(Vector::new(g.origin.0 + size * 0.5, 0.0, g.origin.1 + size * 0.5))
            .friction(mat.map_or(0.6, |m| m.mu_s))
            .restitution(mat.map_or(0.05, |m| m.restitution))
            .build();
        g.collider = Some(self.phys.colliders.insert(c));
        // 땅이 바뀌면 그 위의 물건이 다시 떨어질 수 있다
        self.phys.wake_up_all(true);
    }

    /// 편집 구를 높이장 기둥에 반영한다 (높이장은 동굴을 못 담으니 위에서 본 겉면만)
    fn carve_ground(&mut self, e: &Edit) {
        let Some(g) = self.ground.as_mut() else { return };
        let n = g.n as i32;
        let (i0, i1) = (((e.p[0] - e.r - g.origin.0) / g.step).floor() as i32, ((e.p[0] + e.r - g.origin.0) / g.step).ceil() as i32);
        let (k0, k1) = (((e.p[2] - e.r - g.origin.1) / g.step).floor() as i32, ((e.p[2] + e.r - g.origin.1) / g.step).ceil() as i32);
        for k in k0.max(0)..=k1.min(n - 1) {
            for i in i0.max(0)..=i1.min(n - 1) {
                let x = g.origin.0 + i as f32 * g.step;
                let z = g.origin.1 + k as f32 * g.step;
                let d2 = (x - e.p[0]).powi(2) + (z - e.p[2]).powi(2);
                if d2 >= e.r * e.r {
                    continue;
                }
                let dz = (e.r * e.r - d2).sqrt();
                let h = &mut g.h[(k * n + i) as usize];
                if e.op == "sub" && *h > e.p[1] - dz && *h < e.p[1] + dz {
                    *h = e.p[1] - dz;
                } else if e.op == "add" && *h >= e.p[1] - dz {
                    *h = h.max(e.p[1] + dz);
                }
            }
        }
    }

    // ─── 물건 (강체) ────────────────────────────────────────────────────

    /// 아이템을 실제 치수·질량·재료 마찰로 강체로 만든다 (둥근 모서리 = 아이템 edge)
    fn spawn(&mut self, id: u32, item: &str, owner: &str, p: [f32; 3], q: [f32; 4], v: [f32; 3]) -> Value {
        let it = self.pack.items.get(item);
        let mm = it.map(|i| i.mm.clone()).unwrap_or_else(|| vec![250.0, 250.0, 250.0]);
        // 가장 얇은 치수를 높이(y)로: 바닥에 눕는다
        let mut d: Vec<f32> = mm.iter().map(|x| x / 1000.0).collect();
        d.resize(3, 0.1);
        d.sort_by(|a, b| b.partial_cmp(a).unwrap());
        let half = [d[0] * 0.5, d[2] * 0.5, d[1] * 0.5];
        let r = (it.map_or(10.0, |i| i.edge_mm) / 1000.0).min(half[1] * 0.9);
        let mass = self.pack.item_mass(item);
        let mat = it.and_then(|i| self.pack.materials.get(&i.mat));
        let rb = RigidBodyBuilder::dynamic()
            .translation(Vector::new(p[0], p[1], p[2]))
            .linvel(Vector::new(v[0], v[1], v[2]))
            .ccd_enabled(true)
            .build();
        let h = self.phys.bodies.insert(rb);
        if let Some(b) = self.phys.bodies.get_mut(h) {
            b.set_rotation(Rotation::from_xyzw(q[0], q[1], q[2], q[3]).normalize(), true);
        }
        let col = ColliderBuilder::round_cuboid(half[0] - r, half[1] - r, half[2] - r, r)
            .mass(mass)
            .friction(mat.map_or(0.6, |m| m.mu_s))
            .restitution(mat.map_or(0.05, |m| m.restitution))
            .build();
        self.phys.colliders.insert_with_parent(col, h, &mut self.phys.bodies);
        self.bodies.insert(id, Body { item: item.into(), owner: owner.into(), handle: h, half, r, awake_sent: false });
        self.dirty_bodies = true;
        self.body_json(id).unwrap_or(Value::Null)
    }

    fn body_json(&self, id: u32) -> Option<Value> {
        let b = self.bodies.get(&id)?;
        let rb = self.phys.bodies.get(b.handle)?;
        let t = rb.translation();
        let q = rb.rotation();
        Some(json!({"id": id, "item": b.item, "owner": b.owner, "half": b.half, "r": b.r, "p": [t.x, t.y, t.z], "q": [q.x, q.y, q.z, q.w]}))
    }

    pub fn welcome(&self, player: &str) -> Value {
        let edits: Vec<&Edit> = self.sections.values().flatten().collect();
        let bodies: Vec<Value> = self.bodies.keys().filter_map(|id| self.body_json(*id)).collect();
        json!({"t": "welcome", "edits": edits, "bodies": bodies, "held": self.held.get(player)})
    }

    // ─── 요청 ───────────────────────────────────────────────────────────

    fn deny(v: &Verdict, action: &str, p: [f32; 3]) -> Vec<Value> {
        vec![json!({"t": "deny", "action": action, "zone": v.zone, "rule": v.rule, "msg": v.msg, "p": p})]
    }

    fn add_edit(&mut self, op: &str, p: [f32; 3], r: f32, mat: &str, owner: &str) -> Edit {
        let e = Edit { id: self.next_edit, op: op.into(), p, r, mat: mat.into(), owner: owner.into() };
        self.next_edit += 1;
        let key = section_of(p);
        self.sections.entry(key).or_default().push(e.clone());
        self.save_section(key);
        self.carve_ground(&e);
        self.rebuild_ground();
        e
    }

    /// 삽 한 번 부피의 구 반지름
    fn tool_radius(&self) -> f32 {
        (3.0 * self.pack.layout.dig_volume / (4.0 * std::f32::consts::PI)).cbrt()
    }

    /// 돌려주는 것은 모든 접속자에게 보낼 메시지들 (거절은 요청한 이에게만 가도 되지만 v0.1 은 한 사람)
    pub fn request(&mut self, player: &str, m: &Value) -> Vec<Value> {
        let p3 = |v: &Value| -> [f32; 3] { [0, 1, 2].map(|i| v[i].as_f64().unwrap_or(0.0) as f32) };
        // 손이 닿는 곳만 (엔진이 보낸 눈 위치에서 world.json 의 edit_reach_m 안)
        if matches!(m["t"].as_str(), Some("dig" | "pour")) {
            let (p, from) = (p3(&m["p"]), p3(&m["from"]));
            let d = ((p[0] - from[0]).powi(2) + (p[1] - from[1]).powi(2) + (p[2] - from[2]).powi(2)).sqrt();
            if d > self.pack.layout.edit_reach + 0.3 {
                return vec![json!({"t": "deny", "action": m["t"], "msg": format!("손이 닿지 않는다 ({d:.1} m, {} m 까지)", self.pack.layout.edit_reach), "p": p})];
            }
        }
        match m["t"].as_str().unwrap_or_default() {
            "dig" => {
                let p = p3(&m["p"]);
                let v = zones::judge(&self.pack, player, Action::Dig, p, self);
                if !v.ok {
                    return Self::deny(&v, "dig", p);
                }
                let r = self.tool_radius();
                let e = self.add_edit("sub", p, r, "soil", player);
                // 판 흙은 포대 하나가 되어 구덩이 옆에 떨어진다 (실제 질량·마찰로 구른다)
                let id = self.next_body;
                self.next_body += 1;
                let yields = self.pack.layout.dig_yields.clone();
                let n = p3(&m["n"]);
                let at = [p[0] + n[0] * 0.5, p[1] + 0.45 + n[1] * 0.3, p[2] + n[2] * 0.5];
                let b = self.spawn(id, &yields, player, at, [0.0, 0.0, 0.0, 1.0], [n[0] * 0.8, 1.2, n[2] * 0.8]);
                vec![json!({"t": "edit", "edit": e, "msg": v.msg}), json!({"t": "body_add", "body": b})]
            }
            "pour" => {
                let p = p3(&m["p"]);
                if self.held.get(player).map(String::as_str) != Some(self.pack.layout.dig_yields.as_str()) {
                    return vec![json!({"t": "deny", "action": "place", "msg": "부을 흙 포대를 들고 있지 않다 (E 로 줍는다)", "p": p})];
                }
                let v = zones::judge(&self.pack, player, Action::Place, p, self);
                if !v.ok {
                    return Self::deny(&v, "place", p);
                }
                let r = self.tool_radius();
                let e = self.add_edit("add", p, r, "soil", player);
                self.held.remove(player);
                self.save_players();
                vec![json!({"t": "edit", "edit": e, "msg": v.msg}), json!({"t": "held", "item": Value::Null})]
            }
            "pickup" => {
                if let Some(h) = self.held.get(player) {
                    return vec![json!({"t": "deny", "action": "pickup", "msg": format!("이미 {}을(를) 들고 있다 (Q 로 내려놓는다)", self.item_name(h))})];
                }
                let p = p3(&m["p"]);
                let mut best: Option<(u32, f32)> = None;
                for (id, b) in &self.bodies {
                    let Some(rb) = self.phys.bodies.get(b.handle) else { continue };
                    let t = rb.translation();
                    let d = ((t.x - p[0]).powi(2) + (t.y - p[1]).powi(2) + (t.z - p[2]).powi(2)).sqrt();
                    if d < 1.8 && best.is_none_or(|(_, bd)| d < bd) {
                        best = Some((*id, d));
                    }
                }
                let Some((id, _)) = best else {
                    return vec![json!({"t": "deny", "action": "pickup", "msg": "가까이에 주울 물건이 없다"})];
                };
                let b = &self.bodies[&id];
                if b.owner != player {
                    return vec![json!({"t": "deny", "action": "pickup", "msg": "남이 놓은 물건은 그 사람 것이다 (존중)"})];
                }
                let mass = self.pack.item_mass(&b.item);
                if mass > self.pack.carry_limit_kg {
                    return vec![json!({"t": "deny", "action": "pickup", "msg": format!("{:.0} kg: 혼자 들 수 있는 {} kg 을 넘는다", mass, self.pack.carry_limit_kg)})];
                }
                let b = self.bodies.remove(&id).unwrap();
                self.phys.remove_body_with_colliders(b.handle, true);
                self.held.insert(player.into(), b.item.clone());
                self.save_players();
                self.dirty_bodies = true;
                vec![json!({"t": "body_del", "id": id}), json!({"t": "held", "item": b.item, "mass": mass, "msg": format!("{} ({:.1} kg) 을 들었다", self.item_name(&b.item), mass)})]
            }
            "drop" => {
                let Some(item) = self.held.remove(player) else {
                    return vec![json!({"t": "deny", "action": "drop", "msg": "들고 있는 것이 없다"})];
                };
                self.save_players();
                let id = self.next_body;
                self.next_body += 1;
                let b = self.spawn(id, &item, player, p3(&m["p"]), [0.0, 0.0, 0.0, 1.0], p3(&m["v"]));
                vec![json!({"t": "body_add", "body": b}), json!({"t": "held", "item": Value::Null})]
            }
            other => vec![json!({"t": "deny", "msg": format!("모르는 요청 {other}")})],
        }
    }

    fn item_name(&self, id: &str) -> String {
        self.pack.items.get(id).map_or(id.to_string(), |i| i.name.clone())
    }

    // ─── 시간 ───────────────────────────────────────────────────────────

    pub fn step(&mut self) {
        self.phys.step();
    }

    /// 움직이는 물건의 자세 [id, x, y, z, qx, qy, qz, qw] (멈춘 물건은 멈춘 순간 한 번만)
    pub fn moving(&mut self) -> Vec<Value> {
        let mut out = vec![];
        for (id, b) in self.bodies.iter_mut() {
            let Some(rb) = self.phys.bodies.get(b.handle) else { continue };
            let sleeping = rb.is_sleeping();
            if sleeping && b.awake_sent {
                continue;
            }
            b.awake_sent = sleeping;
            let t = rb.translation();
            let q = rb.rotation();
            out.push(json!([id, t.x, t.y, t.z, q.x, q.y, q.z, q.w]));
            self.dirty_bodies = true;
        }
        // 땅 밖으로 떨어진 물건은 치운다
        let lost: Vec<u32> = self
            .bodies
            .iter()
            .filter(|(_, b)| self.phys.bodies.get(b.handle).is_none_or(|rb| rb.translation().y < -60.0))
            .map(|(id, _)| *id)
            .collect();
        for id in lost {
            if let Some(b) = self.bodies.remove(&id) {
                self.phys.remove_body_with_colliders(b.handle, true);
            }
        }
        out
    }
}
