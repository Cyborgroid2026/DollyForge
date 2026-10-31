//! 세계 상태 (서버 권한): 지형 편집(둥근 CSG 구), 흩어진 물건(Rapier 강체), 들고 있는 것.
//!   편집은 8 m 섹션마다 파일로 저장한다 (data/sections/sx_sz.json). 물건과 사람 상태도 저장한다.
//!   땅은 엔진이 보낸 높이장(heightfield)에 편집을 반영해 Rapier 충돌체로 쓰고, 집은 엔진이 보낸 충돌 곡면(삼각형), 나무 줄기는 캡슐이다.
//!   흙의 양은 보존된다: 한 번 파면 흙 포대 하나가 생기고, 포대 하나를 부으면 같은 부피가 지형에 더해진다.
//! 물건은 조립체(M3)다: 부품(아이템 + 조립체 안 자세)들과 그 사이의 결합. 고정 결합(용접·볼트·나사)은 하나의 복합 강체로 합친다.

use crate::content::{Pack, FLOOR_Y};
use crate::zones::{self, Action, PlacedBy, Verdict};
use rapier3d::parry::math::Mat3;
use rapier3d::parry::query;
use rapier3d::parry::utils::Array2;
use rapier3d::prelude::*;
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use std::collections::{BTreeMap, HashMap};
use std::f32::consts::FRAC_PI_2;
use std::fs;
use std::path::PathBuf;

pub const SECTION: f32 = 8.0;
/// 손이 닿는 거리 (눈에서 물건 겉면까지, m)
const HAND_REACH: f32 = 2.6;

#[derive(Clone, Serialize, Deserialize)]
pub struct Edit {
    pub id: u64,
    pub op: String, // "sub" (파기) | "add" (붓기)
    pub p: [f32; 3],
    pub r: f32,
    pub mat: String,
    pub owner: String,
}

/// 조립체 안의 부품 하나: 아이템과 조립체 기준 자세
#[derive(Clone, Serialize, Deserialize)]
pub struct Part {
    pub item: String,
    pub p: [f32; 3],
    pub q: [f32; 4],
}

/// 두 부품 사이의 결합 (method = joining.json 의 id)
#[derive(Clone, Serialize, Deserialize)]
pub struct Joint {
    pub a: usize,
    pub b: usize,
    pub method: String,
}

#[derive(Clone, Serialize, Deserialize, Default)]
pub struct Shape {
    pub parts: Vec<Part>,
    #[serde(default)]
    pub joints: Vec<Joint>,
}

impl Shape {
    pub fn single(item: &str) -> Shape {
        Shape { parts: vec![Part { item: item.into(), p: [0.0; 3], q: [0.0, 0.0, 0.0, 1.0] }], joints: vec![] }
    }
    fn is_single(&self, item: &str) -> bool {
        self.parts.len() == 1 && self.parts[0].item == item
    }
}

#[derive(Clone, Serialize, Deserialize)]
struct SavedBody {
    id: u32,
    owner: String,
    p: [f32; 3],
    q: [f32; 4],
    #[serde(default)]
    shape: Option<Shape>,
    #[serde(default)]
    item: Option<String>, // v0.1 (M2) 저장 형식: 아이템 하나
}

struct Body {
    shape: Shape,
    owner: String,
    handle: RigidBodyHandle,
    awake_sent: bool,
}

struct Ground {
    origin: (f32, f32),
    step: f32,
    n: usize,
    h: Vec<f32>, // [z * n + x]
    collider: Option<ColliderHandle>,
}

/// 부품의 기하 (아이템 기준 좌표: 상자는 x = mm[0], z = mm[1], y = mm[2] 이고 가운데가 원점, 원기둥은 y 축)
#[derive(Clone, Copy)]
enum Geo {
    Box { half: Vector, r: f32 },
    Cyl { half_h: f32, rad: f32 },
    Ball { rad: f32 },
}

impl Geo {
    fn half(self) -> Vector {
        match self {
            Geo::Box { half, .. } => half,
            Geo::Cyl { half_h, rad } => Vector::new(rad, half_h, rad),
            Geo::Ball { rad } => Vector::splat(rad),
        }
    }
    /// shrink: 놓을 자리 검사에서 맞닿음은 겹침으로 치지 않게 줄인다
    fn shape(self, shrink: f32) -> SharedShape {
        match self {
            Geo::Box { half, r } => {
                let h = (half - Vector::splat(shrink)).max(Vector::splat(0.001));
                let r = r.min(h.min_element() * 0.9);
                SharedShape::round_cuboid(h.x - r, h.y - r, h.z - r, r)
            }
            Geo::Cyl { half_h, rad } => SharedShape::cylinder((half_h - shrink).max(0.001), (rad - shrink).max(0.001)),
            Geo::Ball { rad } => SharedShape::ball((rad - shrink).max(0.001)),
        }
    }
}

pub struct World {
    pub pack: Pack,
    dir: PathBuf,
    sections: BTreeMap<(i32, i32), Vec<Edit>>,
    next_edit: u64,
    phys: PhysicsWorld,
    bodies: BTreeMap<u32, Body>,
    next_body: u32,
    pub held: HashMap<String, Shape>,
    ground: Option<Ground>,
    statics: Vec<ColliderHandle>,
    houses: Vec<ColliderHandle>,
    dirty_bodies: bool,
}

fn section_of(p: [f32; 3]) -> (i32, i32) {
    ((p[0] / SECTION).floor() as i32, (p[2] / SECTION).floor() as i32)
}

fn v3(a: [f32; 3]) -> Vector {
    Vector::new(a[0], a[1], a[2])
}

fn pose_of(p: [f32; 3], q: [f32; 4]) -> Pose {
    Pose::from_parts(v3(p), Rotation::from_xyzw(q[0], q[1], q[2], q[3]).normalize())
}

fn arr_p(p: &Pose) -> [f32; 3] {
    p.translation.to_array()
}

fn arr_q(p: &Pose) -> [f32; 4] {
    p.rotation.to_array()
}

/// 가장 가까운 90° 단위 자세 (24가지 중 하나): 회전 행렬의 각 열을 가장 가까운 축으로 맞춘다
fn snap_rotation(q: Rotation) -> Rotation {
    let m = Mat3::from_quat(q);
    let mut cols = [m.x_axis, m.y_axis, m.z_axis];
    let mut used = [false; 3];
    let mut out = [Vector::ZERO; 3];
    // 축 성분이 가장 큰 열부터 차지한다 (두 열이 같은 축을 고르지 않게)
    let mut order: Vec<usize> = (0..3).collect();
    order.sort_by(|a, b| cols[*b].abs().max_element().partial_cmp(&cols[*a].abs().max_element()).unwrap());
    for &c in &order {
        let v = cols[c];
        let mut best = (0usize, -1.0f32);
        for k in 0..3 {
            if !used[k] && v[k].abs() > best.1 {
                best = (k, v[k].abs());
            }
        }
        used[best.0] = true;
        let mut a = Vector::ZERO;
        a[best.0] = v[best.0].signum();
        out[c] = a;
        cols[c] = a;
    }
    // 오른손 좌표계 유지
    if out[0].cross(out[1]).dot(out[2]) < 0.0 {
        out[2] = -out[2];
    }
    Rotation::from_mat3(&Mat3::from_cols(out[0], out[1], out[2])).normalize()
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
            houses: vec![],
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
        // 들고 있는 것 (M2 형식은 아이템 id 하나)
        if let Ok(text) = fs::read_to_string(w.dir.join("players.json")) {
            let raw: HashMap<String, Value> = serde_json::from_str(&text).unwrap_or_default();
            for (k, v) in raw {
                let s = match &v {
                    Value::String(item) => Some(Shape::single(item)),
                    _ => serde_json::from_value::<Shape>(v).ok(),
                };
                if let Some(s) = s {
                    w.held.insert(k, s);
                }
            }
        }
        w.add_house_boxes();
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
                let pos = self.phys.bodies.get(b.handle)?.position();
                Some(SavedBody { id: *id, owner: b.owner.clone(), p: arr_p(pos), q: arr_q(pos), shape: Some(b.shape.clone()), item: None })
            })
            .collect();
        let _ = fs::write(self.dir.join("bodies.json"), serde_json::to_string(&list).unwrap_or_default());
        self.dirty_bodies = false;
    }

    // ─── 정적 충돌체: 집 · 땅 · 나무 줄기 ─────────────────────────────────────

    /// 엔진이 집 곡면을 보내기 전의 대신: 집 덩어리 상자
    fn add_house_boxes(&mut self) {
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
            self.houses.push(self.phys.colliders.insert(c));
        }
    }

    /// 엔진이 보낸 집 충돌 모양 (화면과 같은 거리장): 골격은 삼각형 곡면, 붙박이 가구·문짝·유리는 상자 [cx, cy, cz, hx, hy, hz].
    /// 내 집은 가구까지, 이웃 집은 유리만 (집 기준 좌표, origins 로 옮긴다)
    pub fn set_houses(&mut self, h: &Value) {
        let floats = |v: &Value| -> Vec<f32> { v.as_array().into_iter().flatten().map(|x| x.as_f64().unwrap_or(0.0) as f32).collect() };
        let v = floats(&h["shell"]["v"]);
        let idx: Vec<u32> = h["shell"]["i"].as_array().into_iter().flatten().map(|x| x.as_u64().unwrap_or(0) as u32).collect();
        if v.len() < 9 || idx.len() < 3 {
            return;
        }
        let verts: Vec<Vector> = v.chunks_exact(3).map(|c| Vector::new(c[0], c[1], c[2])).collect();
        let tris: Vec<[u32; 3]> = idx.chunks_exact(3).map(|c| [c[0], c[1], c[2]]).collect();
        let boxes = |k: &str| -> Vec<Vec<f32>> { h[k].as_array().into_iter().flatten().map(floats).filter(|b| b.len() == 6).collect() };
        let (own, other) = (boxes("own"), boxes("other"));
        for c in self.houses.drain(..) {
            self.phys.remove_collider(c);
        }
        let own_i = self.pack.layout.own_house;
        for (i, o) in h["origins"].as_array().into_iter().flatten().enumerate() {
            let at = v3([0, 1, 2].map(|k| o[k].as_f64().unwrap_or(0.0) as f32));
            match ColliderBuilder::trimesh(verts.clone(), tris.clone()) {
                Ok(b) => self.houses.push(self.phys.colliders.insert(b.translation(at).friction(0.6).build())),
                Err(e) => eprintln!("[world] 집 {i} 충돌 곡면 오류: {e:?}"),
            }
            for b in if i == own_i { &own } else { &other } {
                let c = ColliderBuilder::cuboid(b[3].max(0.002), b[4].max(0.002), b[5].max(0.002)).translation(at + Vector::new(b[0], b[1], b[2])).friction(0.6).build();
                self.houses.push(self.phys.colliders.insert(c));
            }
        }
        println!("[world] 집 충돌: {}채 · 골격 삼각형 {} · 내 집 가구·유리 상자 {} · 이웃 유리 {}", h["origins"].as_array().map_or(0, |a| a.len()), tris.len(), own.len(), other.len());
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
        if let Some(old) = self.ground.take().and_then(|g| g.collider) {
            self.phys.remove_collider(old);
        }
        for c in self.statics.drain(..) {
            self.phys.remove_collider(c);
        }
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
        // 저장된 물건을 다시 놓는다 (처음 한 번), 처음 여는 세계면 내 집 가구와 처음 받은 부품
        if self.bodies.is_empty() {
            for s in self.saved_bodies() {
                self.next_body = self.next_body.max(s.id + 1);
                let shape = s.shape.or_else(|| s.item.as_deref().map(Shape::single)).unwrap_or_default();
                if !shape.parts.is_empty() {
                    self.insert_body(s.id, shape, &s.owner, pose_of(s.p, s.q), Vector::ZERO);
                }
            }
            self.place_starter();
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

    // ─── 부품과 조립체 (강체) ────────────────────────────────────────────

    fn geo(&self, item: &str) -> Geo {
        let Some(it) = self.pack.items.get(item) else { return Geo::Box { half: Vector::splat(0.125), r: 0.01 } };
        let m: Vec<f32> = it.mm.iter().map(|x| x / 1000.0).collect();
        match (it.shape.as_str(), m.as_slice()) {
            ("box", [x, z, y]) => {
                let half = Vector::new(x * 0.5, y * 0.5, z * 0.5);
                Geo::Box { half, r: (it.edge_mm / 1000.0).min(half.min_element() * 0.9) }
            }
            ("cyl", [d, h]) | ("tube", [d, _, h]) => Geo::Cyl { half_h: h * 0.5, rad: d * 0.5 },
            ("sphere", [d, ..]) => Geo::Ball { rad: d * 0.5 },
            _ => Geo::Box { half: Vector::splat(0.125), r: 0.01 },
        }
    }

    fn shape_mass(&self, s: &Shape) -> f32 {
        s.parts.iter().map(|p| self.pack.item_mass(&p.item)).sum()
    }

    fn shape_name(&self, s: &Shape) -> String {
        let first = s.parts.first().map(|p| self.item_name(&p.item)).unwrap_or_default();
        if s.parts.len() > 1 {
            format!("{first} 외 {}개 조립체", s.parts.len() - 1)
        } else {
            first
        }
    }

    /// 놓인 자리의 기본 자세: 가구는 서 있고, 판·봉·각재는 가장 얇은 치수가 위로 오게 눕는다
    fn rest_rotation(&self, item: &str) -> Rotation {
        let furniture = self.pack.items.get(item).is_some_and(|i| matches!(i.cat.as_str(), "home" | "workshop"));
        if furniture {
            return Rotation::IDENTITY;
        }
        match self.geo(item) {
            Geo::Box { half, .. } if half.x < half.y && half.x <= half.z => Rotation::from_rotation_z(FRAC_PI_2),
            Geo::Box { half, .. } if half.z < half.y && half.z < half.x => Rotation::from_rotation_x(FRAC_PI_2),
            Geo::Cyl { half_h, rad } if half_h > rad * 1.5 => Rotation::from_rotation_z(FRAC_PI_2),
            _ => Rotation::IDENTITY,
        }
    }

    /// 조립체를 자세 rot 로 돌렸을 때 아래로 내려간 깊이 (가운데에서 바닥까지)
    fn depth_below(&self, s: &Shape, rot: Rotation) -> f32 {
        let mut low = 0.0f32;
        for p in &s.parts {
            let lp = pose_of(p.p, p.q);
            let h = self.geo(&p.item).half();
            let c = rot * lp.translation;
            let r = Mat3::from_quat(rot * lp.rotation);
            let ext = r.x_axis.y.abs() * h.x + r.y_axis.y.abs() * h.y + r.z_axis.y.abs() * h.z;
            low = low.max(-(c.y - ext));
        }
        low
    }

    fn part_collider(&self, part: &Part, shrink: f32) -> Collider {
        let it = self.pack.items.get(&part.item);
        let mat = it.and_then(|i| self.pack.materials.get(&i.mat));
        ColliderBuilder::new(self.geo(&part.item).shape(shrink))
            .position(pose_of(part.p, part.q))
            .mass(self.pack.item_mass(&part.item))
            .friction(mat.map_or(0.6, |m| m.mu_s))
            .restitution(mat.map_or(0.05, |m| m.restitution))
            .build()
    }

    /// 조립체를 강체 하나로: 부품마다 실제 치수·질량·재료 마찰의 충돌체 (고정 결합은 내부 경계가 없는 복합 강체)
    fn insert_body(&mut self, id: u32, shape: Shape, owner: &str, pose: Pose, v: Vector) -> Value {
        let rb = RigidBodyBuilder::dynamic().pose(pose).linvel(v).ccd_enabled(true).build();
        let h = self.phys.bodies.insert(rb);
        for part in &shape.parts {
            let col = self.part_collider(part, 0.0);
            self.phys.colliders.insert_with_parent(col, h, &mut self.phys.bodies);
        }
        self.bodies.insert(id, Body { shape, owner: owner.into(), handle: h, awake_sent: false });
        self.dirty_bodies = true;
        self.body_json(id).unwrap_or(Value::Null)
    }

    fn remove_body(&mut self, id: u32) -> Option<Body> {
        let b = self.bodies.remove(&id)?;
        self.phys.remove_body_with_colliders(b.handle, true);
        self.dirty_bodies = true;
        Some(b)
    }

    fn new_id(&mut self) -> u32 {
        self.next_body += 1;
        self.next_body - 1
    }

    fn body_pose(&self, id: u32) -> Option<Pose> {
        Some(*self.phys.bodies.get(self.bodies.get(&id)?.handle)?.position())
    }

    fn body_json(&self, id: u32) -> Option<Value> {
        let b = self.bodies.get(&id)?;
        let pos = self.body_pose(id)?;
        Some(json!({"id": id, "owner": b.owner, "shape": b.shape, "mass": self.shape_mass(&b.shape), "p": arr_p(&pos), "q": arr_q(&pos)}))
    }

    pub fn welcome(&self, player: &str) -> Value {
        let edits: Vec<&Edit> = self.sections.values().flatten().collect();
        let bodies: Vec<Value> = self.bodies.keys().filter_map(|id| self.body_json(*id)).collect();
        json!({"t": "welcome", "edits": edits, "bodies": bodies, "held": self.held.get(player)})
    }

    /// 처음 여는 세계: 내 집의 옮길 수 있는 가구와 차고에 처음 받은 부품 (data/state.json 에 한 번만 했다고 적는다)
    fn place_starter(&mut self) {
        let state = self.dir.join("state.json");
        let mut st: Value = fs::read_to_string(&state).ok().and_then(|t| serde_json::from_str(&t).ok()).unwrap_or(json!({}));
        if st["starter"] == true {
            return;
        }
        let l = &self.pack.layout;
        let o = (l.house_x[l.own_house] + l.origin_dx, 0.0f32);
        let list: Vec<(String, (f32, f32), f32)> =
            l.movable.iter().map(|m| (m.item.clone(), m.at, m.yaw_deg)).chain(l.starter.iter().map(|s| (s.item.clone(), s.at, s.yaw_deg))).collect();
        for (item, at, yaw) in list {
            let s = Shape::single(&item);
            let rot = Rotation::from_rotation_y(yaw.to_radians()) * self.rest_rotation(&item);
            let y = FLOOR_Y + self.depth_below(&s, rot) + 0.002;
            let id = self.new_id();
            self.insert_body(id, s, "local", Pose::from_parts(Vector::new(o.0 + at.0, y, o.1 + at.1), rot), Vector::ZERO);
        }
        st["starter"] = json!(true);
        let _ = fs::write(&state, st.to_string());
        println!("[world] 처음 여는 세계: 내 집 가구 {}점 · 처음 받은 부품 {}점을 놓았다", self.pack.layout.movable.len(), self.pack.layout.starter.len());
    }

    /// 눈에서 조립체 겉면까지 거리
    fn reach_to(&self, id: u32, eye: Vector) -> f32 {
        let (Some(b), Some(pos)) = (self.bodies.get(&id), self.body_pose(id)) else { return f32::MAX };
        b.shape
            .parts
            .iter()
            .map(|p| {
                let pp = pos * pose_of(p.p, p.q);
                self.geo(&p.item).shape(0.0).distance_to_point(&pp, eye, true)
            })
            .fold(f32::MAX, f32::min)
    }

    // ─── 요청 ───────────────────────────────────────────────────────────

    fn deny(v: &Verdict, action: &str, p: [f32; 3]) -> Vec<Value> {
        vec![json!({"t": "deny", "action": action, "zone": v.zone, "rule": v.rule, "msg": v.msg, "p": p})]
    }

    fn deny_msg(action: &str, msg: String) -> Vec<Value> {
        vec![json!({"t": "deny", "action": action, "msg": msg})]
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

    fn held_msg(&self, player: &str, msg: Option<String>) -> Value {
        match self.held.get(player) {
            Some(s) => json!({"t": "held", "shape": s, "name": self.shape_name(s), "mass": self.shape_mass(s), "msg": msg}),
            None => json!({"t": "held", "shape": Value::Null, "msg": msg}),
        }
    }

    /// 돌려주는 것은 보낼 메시지들 (거절과 들기 상태는 요청한 이에게만, 세계의 변화는 모두에게: main.rs)
    pub fn request(&mut self, player: &str, m: &Value) -> Vec<Value> {
        let p3 = |v: &Value| -> [f32; 3] { [0, 1, 2].map(|i| v[i].as_f64().unwrap_or(0.0) as f32) };
        let q4 = |v: &Value| -> [f32; 4] {
            if v.as_array().is_some_and(|a| a.len() == 4) {
                [0, 1, 2, 3].map(|i| v[i].as_f64().unwrap_or(0.0) as f32)
            } else {
                [0.0, 0.0, 0.0, 1.0]
            }
        };
        let eye = v3(p3(&m["from"]));
        // 손이 닿는 곳만 (엔진이 보낸 눈 위치에서 world.json 의 edit_reach_m 안)
        if matches!(m["t"].as_str(), Some("dig" | "pour")) {
            let p = p3(&m["p"]);
            let d = v3(p).distance(eye);
            if d > self.pack.layout.edit_reach + 0.3 {
                return vec![json!({"t": "deny", "action": m["t"], "msg": format!("손이 닿지 않는다 ({d:.1} m, {} m 까지)", self.pack.layout.edit_reach), "p": p})];
            }
        }
        let body_id = m["id"].as_u64().map(|x| x as u32);
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
                let id = self.new_id();
                let yields = self.pack.layout.dig_yields.clone();
                let n = p3(&m["n"]);
                let at = [p[0] + n[0] * 0.5, p[1] + 0.45 + n[1] * 0.3, p[2] + n[2] * 0.5];
                let b = self.insert_body(id, Shape::single(&yields), player, pose_of(at, [0.0, 0.0, 0.0, 1.0]), Vector::new(n[0] * 0.8, 1.2, n[2] * 0.8));
                vec![json!({"t": "edit", "edit": e, "msg": v.msg}), json!({"t": "body_add", "body": b})]
            }
            "pour" => {
                let p = p3(&m["p"]);
                let sack = self.pack.layout.dig_yields.clone();
                if !self.held.get(player).is_some_and(|s| s.is_single(&sack)) {
                    return Self::deny_msg("place", "부을 흙 포대를 들고 있지 않다 (E 로 줍는다)".into());
                }
                let v = zones::judge(&self.pack, player, Action::Place, p, self);
                if !v.ok {
                    return Self::deny(&v, "place", p);
                }
                let r = self.tool_radius();
                let e = self.add_edit("add", p, r, "soil", player);
                self.held.remove(player);
                self.save_players();
                vec![json!({"t": "edit", "edit": e, "msg": v.msg}), self.held_msg(player, None)]
            }
            "pickup" => {
                if let Some(h) = self.held.get(player) {
                    return Self::deny_msg("pickup", format!("이미 {}을(를) 들고 있다 (Q 로 내려놓는다)", self.shape_name(h)));
                }
                // 겨냥한 물건, 없으면 발 가까이의 물건
                let id = match body_id {
                    Some(id) if self.bodies.contains_key(&id) => id,
                    _ => {
                        let p = v3(p3(&m["p"]));
                        let near = self.bodies.keys().map(|id| (*id, self.reach_to(*id, p))).filter(|(_, d)| *d < 1.2).min_by(|a, b| a.1.total_cmp(&b.1));
                        let Some((id, _)) = near else { return Self::deny_msg("pickup", "가까이에 주울 물건이 없다".into()) };
                        id
                    }
                };
                let d = self.reach_to(id, eye);
                if d > HAND_REACH {
                    return Self::deny_msg("pickup", format!("손이 닿지 않는다 ({d:.1} m)"));
                }
                let b = &self.bodies[&id];
                if b.owner != player {
                    return Self::deny_msg("pickup", "남이 놓은 물건은 그 사람 것이다 (존중)".into());
                }
                let mass = self.shape_mass(&b.shape);
                if mass > self.pack.carry_limit_kg {
                    return Self::deny_msg("pickup", format!("{:.1} kg: 혼자 들 수 있는 {} kg 을 넘는다", mass, self.pack.carry_limit_kg));
                }
                let b = self.remove_body(id).unwrap();
                let name = self.shape_name(&b.shape);
                self.held.insert(player.into(), b.shape);
                self.save_players();
                vec![json!({"t": "body_del", "id": id}), self.held_msg(player, Some(format!("{name} ({mass:.1} kg) 을 들었다")))]
            }
            "drop" => {
                let Some(shape) = self.held.get(player).cloned() else {
                    return Self::deny_msg("drop", "들고 있는 것이 없다".into());
                };
                let p = p3(&m["p"]);
                let v = zones::judge(&self.pack, player, Action::Place, p, self);
                if !v.ok {
                    return Self::deny(&v, "place", p);
                }
                let pose = pose_of(p, q4(&m["q"]));
                // 벽·땅·나무에 파묻히는 자리는 안 된다 (맞닿음은 괜찮다: 1 cm 줄인 모양으로 겹침 검사)
                for part in &shape.parts {
                    let pp = pose * pose_of(part.p, part.q);
                    let sh = self.geo(&part.item).shape(0.01);
                    if let Some((c, _)) = self.phys.intersect_shape(pp, sh.as_ref(), QueryFilter::only_fixed()).next() {
                        let what = if self.ground.as_ref().is_some_and(|g| g.collider == Some(c)) {
                            "땅"
                        } else if self.houses.contains(&c) {
                            "집"
                        } else {
                            "나무"
                        };
                        return Self::deny_msg("drop", format!("그 자리에는 들어가지 않는다 ({what}에 걸린다)"));
                    }
                }
                self.held.remove(player);
                self.save_players();
                let id = self.new_id();
                let b = self.insert_body(id, shape, player, pose, v3(p3(&m["v"])));
                vec![json!({"t": "body_add", "body": b}), self.held_msg(player, None)]
            }
            "join" => self.join(player, body_id, v3(p3(&m["p"])), eye),
            "split" => self.split(player, body_id, v3(p3(&m["p"])), eye),
            other => Self::deny_msg("?", format!("모르는 요청 {other}")),
        }
    }

    // ─── 결합 (M3): 맞닿은 두 조립체를 연결점 격자에 맞춰 하나로 ────────────────

    /// 겨냥한 물건과 맞닿은 내 물건 하나를 잇는다. 재료 계열로 방법이 정해지고(용접·나사·볼트), 상대는 90° 자세와 연결점 격자에 맞춰진다
    fn join(&mut self, player: &str, id: Option<u32>, aim: Vector, eye: Vector) -> Vec<Value> {
        let Some(a_id) = id.filter(|i| self.bodies.contains_key(i)) else {
            return Self::deny_msg("join", "이을 물건을 겨냥한다 (가운데 십자선)".into());
        };
        if self.reach_to(a_id, eye) > HAND_REACH {
            return Self::deny_msg("join", "손이 닿지 않는다".into());
        }
        if self.bodies[&a_id].owner != player {
            return Self::deny_msg("join", "남이 놓은 물건은 그 사람 것이다 (존중)".into());
        }
        let v = zones::judge(&self.pack, player, Action::Place, aim.to_array(), self);
        if !v.ok {
            return Self::deny(&v, "join", aim.to_array());
        }
        let a_pose = self.body_pose(a_id).unwrap();
        // 가장 가까이 맞닿은 부품 쌍 (조준점에 가까운 쌍을 먼저)
        let mut best: Option<(u32, usize, usize, f32)> = None;
        for (b_id, b) in &self.bodies {
            if *b_id == a_id || b.owner != player {
                continue;
            }
            let b_pose = self.body_pose(*b_id).unwrap();
            for (ai, ap) in self.bodies[&a_id].shape.parts.iter().enumerate() {
                let pa = a_pose * pose_of(ap.p, ap.q);
                let sa = self.geo(&ap.item).shape(0.0);
                for (bi, bp) in b.shape.parts.iter().enumerate() {
                    let pb = b_pose * pose_of(bp.p, bp.q);
                    let sb = self.geo(&bp.item).shape(0.0);
                    let Ok(d) = query::distance(&pa, sa.as_ref(), &pb, sb.as_ref()) else { continue };
                    if d.distance > self.pack.joining.contact {
                        continue;
                    }
                    let score = d.distance + 0.05 * pa.translation.lerp(pb.translation, 0.5).distance(aim);
                    if best.is_none_or(|x| score < x.3) {
                        best = Some((*b_id, ai, bi, score));
                    }
                }
            }
        }
        let Some((b_id, ai, bi, _)) = best else {
            return Self::deny_msg("join", format!("맞닿은 내 물건이 없다 ({} mm 안으로 붙여 놓는다)", (self.pack.joining.contact * 1000.0) as i32));
        };
        let a_shape = self.bodies[&a_id].shape.clone();
        let b_shape = self.bodies[&b_id].shape.clone();
        let (a_item, b_item) = (a_shape.parts[ai].item.clone(), b_shape.parts[bi].item.clone());
        let method = match self.pack.join_method(&a_item, &b_item) {
            Ok(m) => (m.id.clone(), m.name.clone(), m.consumes.clone()),
            Err(e) => return Self::deny_msg("join", e),
        };
        let b_pose = self.body_pose(b_id).unwrap();

        // 부품 a 기준으로 부품 b 의 자세: 90° 단위로 돌리고, 맞닿은 축은 면을 딱 붙이고, 나머지 두 축은 연결점 격자에
        let a_local = pose_of(a_shape.parts[ai].p, a_shape.parts[ai].q);
        let b_local = pose_of(b_shape.parts[bi].p, b_shape.parts[bi].q);
        let pa = a_pose * a_local;
        let pb = b_pose * b_local;
        let rel = pa.inverse() * pb;
        let rot = snap_rotation(rel.rotation);
        let ha = self.geo(&a_item).half();
        let hb0 = self.geo(&b_item).half();
        let rm = Mat3::from_quat(rot);
        let hb = Vector::new(
            rm.x_axis.x.abs() * hb0.x + rm.y_axis.x.abs() * hb0.y + rm.z_axis.x.abs() * hb0.z,
            rm.x_axis.y.abs() * hb0.x + rm.y_axis.y.abs() * hb0.y + rm.z_axis.y.abs() * hb0.z,
            rm.x_axis.z.abs() * hb0.x + rm.y_axis.z.abs() * hb0.y + rm.z_axis.z.abs() * hb0.z,
        );
        let mut c = rel.translation;
        // 맞닿은 축 = 두 상자가 가장 덜 겹치는(가장 벌어진) 축
        let gap = |k: usize| c[k].abs() - (ha[k] + hb[k]);
        let k = (0..3).max_by(|x, y| gap(*x).total_cmp(&gap(*y))).unwrap();
        c[k] = c[k].signum() * (ha[k] + hb[k]);
        let (grid, fine) = (self.pack.joining.grid, self.pack.joining.fine);
        for t in (0..3).filter(|t| *t != k) {
            let g = if ha[t] * 2.0 >= grid - 1e-4 && hb[t] * 2.0 >= grid - 1e-4 { grid } else { fine };
            // b 의 아래 모서리를 a 의 아래 모서리에서 격자 칸만큼: 한 칸이 b 크기를 넘으면 가운데끼리 맞춘다
            let lo = c[t] - hb[t] + ha[t];
            let snapped = (lo / g).round() * g;
            c[t] = snapped - ha[t] + hb[t];
            // 맞닿은 면 밖으로 나가 떨어지지 않게 (적어도 조금은 겹치게)
            let lim = ha[t] + hb[t] - 0.004;
            c[t] = c[t].clamp(-lim, lim);
        }
        let b_new = a_local * Pose::from_parts(c, rot); // 조립체 a 기준 부품 b 의 새 자세
        let b_frame = b_new * b_local.inverse(); // 조립체 a 기준 조립체 b 의 틀
        let base = a_shape.parts.len();
        let mut merged = a_shape.clone();
        for p in &b_shape.parts {
            let np = b_frame * pose_of(p.p, p.q);
            merged.parts.push(Part { item: p.item.clone(), p: arr_p(&np), q: arr_q(&np) });
        }
        for j in &b_shape.joints {
            merged.joints.push(Joint { a: j.a + base, b: j.b + base, method: j.method.clone() });
        }
        merged.joints.push(Joint { a: ai, b: base + bi, method: method.0.clone() });
        let mass = self.shape_mass(&merged);
        self.remove_body(a_id);
        self.remove_body(b_id);
        let id = self.new_id();
        let body = self.insert_body(id, merged, player, a_pose, Vector::ZERO);
        let used: Vec<String> = method.2.iter().map(|(c, n)| format!("{} {n}개", self.item_name(c))).collect();
        let msg = format!(
            "{}: {} + {} — 하나의 강체 ({mass:.1} kg){}",
            method.1,
            self.item_name(&a_item),
            self.item_name(&b_item),
            if used.is_empty() { " · 접합부가 곡면으로 녹아 이어졌다".to_string() } else { format!(" · {}", used.join(", ")) }
        );
        vec![json!({"t": "body_del", "id": a_id}), json!({"t": "body_del", "id": b_id}), json!({"t": "body_add", "body": body}), json!({"t": "note", "msg": msg})]
    }

    /// 조준점에 가장 가까운 풀 수 있는 결합 하나를 푼다 (볼트·나사). 용접은 녹여 붙인 것이라 풀 수 없다
    fn split(&mut self, player: &str, id: Option<u32>, aim: Vector, eye: Vector) -> Vec<Value> {
        let Some(id) = id.filter(|i| self.bodies.contains_key(i)) else {
            return Self::deny_msg("split", "풀 조립체를 겨냥한다".into());
        };
        if self.reach_to(id, eye) > HAND_REACH {
            return Self::deny_msg("split", "손이 닿지 않는다".into());
        }
        let b = &self.bodies[&id];
        if b.owner != player {
            return Self::deny_msg("split", "남이 만든 것은 분해하지 않는다 (존중)".into());
        }
        if b.shape.joints.is_empty() {
            return Self::deny_msg("split", "이음이 없는 부품 하나다".into());
        }
        let pose = self.body_pose(id).unwrap();
        let at = |j: &Joint| {
            let s = &b.shape.parts;
            (pose * v3(s[j.a].p)).lerp(pose * v3(s[j.b].p), 0.5).distance(aim)
        };
        let undo = |j: &Joint| self.pack.joining.methods.iter().find(|m| m.id == j.method).is_none_or(|m| m.undo);
        let pick = b.shape.joints.iter().enumerate().filter(|(_, j)| undo(j)).min_by(|x, y| at(x.1).total_cmp(&at(y.1))).map(|(i, _)| i);
        let Some(ji) = pick else {
            return Self::deny_msg("split", "용접은 녹여 붙인 것이라 풀 수 없다 (절단 공정은 다음 단계)".into());
        };
        let mut shape = b.shape.clone();
        let removed = shape.joints.remove(ji);
        // 남은 결합으로 이어진 무리마다 조립체 하나
        let n = shape.parts.len();
        let mut group: Vec<usize> = (0..n).collect();
        fn root(g: &mut [usize], i: usize) -> usize {
            let mut r = i;
            while g[r] != r {
                r = g[r];
            }
            g[i] = r;
            r
        }
        for j in &shape.joints {
            let (ra, rb) = (root(&mut group, j.a), root(&mut group, j.b));
            group[ra] = rb;
        }
        let roots: Vec<usize> = (0..n).map(|i| root(&mut group, i)).collect();
        let mut keys: Vec<usize> = roots.clone();
        keys.sort();
        keys.dedup();
        let method_name = self.pack.joining.methods.iter().find(|m| m.id == removed.method).map_or(removed.method.clone(), |m| m.name.clone());
        let (na, nb) = (self.item_name(&shape.parts[removed.a].item), self.item_name(&shape.parts[removed.b].item));
        if keys.len() == 1 {
            self.bodies.get_mut(&id).unwrap().shape = shape;
            self.dirty_bodies = true;
            let body = self.body_json(id).unwrap();
            return vec![json!({"t": "body_add", "body": body}), json!({"t": "note", "msg": format!("{method_name}을 풀었다: {na} — {nb} (다른 이음으로 아직 이어져 있다)")})];
        }
        self.remove_body(id);
        let mut out = vec![json!({"t": "body_del", "id": id})];
        for k in keys {
            let idx: Vec<usize> = (0..n).filter(|i| roots[*i] == k).collect();
            let remap: HashMap<usize, usize> = idx.iter().enumerate().map(|(new, old)| (*old, new)).collect();
            let parts = idx.iter().map(|i| shape.parts[*i].clone()).collect();
            let joints = shape
                .joints
                .iter()
                .filter(|j| remap.contains_key(&j.a))
                .map(|j| Joint { a: remap[&j.a], b: remap[&j.b], method: j.method.clone() })
                .collect();
            let nid = self.new_id();
            out.push(json!({"t": "body_add", "body": self.insert_body(nid, Shape { parts, joints }, player, pose, Vector::ZERO)}));
        }
        out.push(json!({"t": "note", "msg": format!("{method_name}을 풀었다: {na} | {nb}")}));
        out
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
            self.remove_body(id);
        }
        out
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn snapped_rotation_is_axis_aligned() {
        let q = Rotation::from_rotation_y(0.2) * Rotation::from_rotation_x(1.4);
        let s = snap_rotation(q);
        let m = Mat3::from_quat(s);
        for c in [m.x_axis, m.y_axis, m.z_axis] {
            assert!((c.abs().max_element() - 1.0).abs() < 1e-4, "축에 맞는다: {c:?}");
        }
        assert!(s.angle_between(Rotation::from_rotation_x(FRAC_PI_2)) < 0.01);
    }

    #[test]
    fn weld_two_bars_into_one_body_and_bolts_come_apart() {
        let pack = Pack::load(&crate::content::default_dir()).unwrap();
        let dir = std::env::temp_dir().join(format!("dolly-world-test-{}", std::process::id()));
        let _ = fs::remove_dir_all(&dir);
        let mut w = World::open(pack, dir.clone());
        let lay = Rotation::from_rotation_z(FRAC_PI_2);
        // 강철 봉 두 개를 T 자로 (조금 비뚤게, 1 cm 띄워)
        let a = w.insert_body(1, Shape::single("steel_bar"), "local", Pose::from_parts(Vector::new(0.0, 0.5, 80.0), lay), Vector::ZERO);
        let b = w.insert_body(2, Shape::single("steel_bar"), "local", Pose::from_parts(Vector::new(0.03, 0.5, 80.52), Rotation::from_rotation_y(1.5) * lay), Vector::ZERO);
        assert!(a["id"] == 1 && b["id"] == 2);
        w.next_body = 3;
        let out = w.join("local", Some(1), Vector::new(0.0, 0.5, 80.0), Vector::new(0.0, 1.5, 80.5));
        let add = out.iter().find(|m| m["t"] == "body_add").unwrap_or_else(|| panic!("이어졌다: {out:?}"));
        let shape: Shape = serde_json::from_value(add["body"]["shape"].clone()).unwrap();
        assert_eq!(shape.parts.len(), 2);
        assert_eq!(shape.joints[0].method, "weld");
        assert_eq!(w.bodies.len(), 1, "하나의 강체");
        // T 자: 봉 b 의 끝면이 봉 a 의 옆면(반지름 10 mm)에 딱 붙고, a 의 축 방향으로는 25 mm 격자에 놓인다
        let pb = v3(shape.parts[1].p);
        assert!((pb.z.abs() - 0.51).abs() < 1e-3, "맞닿음 {pb:?}");
        let cell = (pb.y - 0.01 + 0.5) / 0.025;
        assert!((cell - cell.round()).abs() < 1e-3, "격자 {pb:?}");
        // 용접은 풀 수 없다
        let id = *w.bodies.keys().next().unwrap();
        let out = w.split("local", Some(id), Vector::new(0.0, 0.5, 80.0), Vector::new(0.0, 1.5, 80.5));
        assert_eq!(out[0]["t"], "deny");
        // 알루미늄 프로파일을 볼트로 이었다가 푼다
        let pid = w.new_id();
        let pose = w.body_pose(id).unwrap();
        w.insert_body(pid, Shape::single("aluminium_profile"), "local", Pose::from_parts(pose.translation + Vector::new(0.0, 0.0, -0.02), lay), Vector::ZERO);
        let out = w.join("local", Some(pid), pose.translation, pose.translation + Vector::new(0.0, 1.0, 0.0));
        assert!(out.iter().any(|m| m["t"] == "body_add"), "{out:?}");
        let id = *w.bodies.keys().next().unwrap();
        assert_eq!(w.bodies[&id].shape.parts.len(), 3);
        let out = w.split("local", Some(id), pose.translation, pose.translation + Vector::new(0.0, 1.0, 0.0));
        assert_eq!(out.iter().filter(|m| m["t"] == "body_add").count(), 2, "볼트를 풀면 둘로 나뉜다: {out:?}");
        let _ = fs::remove_dir_all(&dir);
    }
}
