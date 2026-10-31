//! 콘텐츠 팩 (content/dolly) 에서 세계 서버가 쓰는 것만 읽는다: 재료(밀도·마찰·반발·계열), 아이템 치수·질량, 구역 규칙, 집 크기·가구,
//! 세계 배치, 결합 규칙. C# 엔진이 같은 파일을 읽어 지형·집·부품을 그리기 때문에, 서버의 판정과 화면이 어긋나지 않는다.

use serde_json::Value;
use std::collections::HashMap;
use std::fs;
use std::path::{Path, PathBuf};

pub struct Material {
    pub kind: String,
    pub density: f32,
    pub mu_s: f32,
    pub restitution: f32,
}

pub struct Item {
    pub name: String,
    pub cat: String,
    pub shape: String,
    pub mm: Vec<f32>,
    pub edge_mm: f32,
    pub mat: String,
    pub mass: f32,
    pub uses: Vec<String>,
}

pub struct Zone {
    pub name: String,
    pub rules: HashMap<String, String>,
}

/// 집 안의 옮길 수 있는 가구 하나 (집 기준 자리)
pub struct Movable {
    pub item: String,
    /// 집 기준 [x, z] 가운데
    pub at: (f32, f32),
    pub yaw_deg: f32,
}

pub struct Starter {
    pub item: String,
    pub at: (f32, f32),
    pub yaw_deg: f32,
}

pub struct Layout {
    pub house_x: Vec<f32>,
    pub own_house: usize,
    pub origin_dx: f32,
    pub footprint: (f32, f32),
    pub wall_height: f32,
    pub lot_x_half: f32,
    pub lot_z: (f32, f32),
    pub road_z: f32,
    pub road_half: f32,
    pub field_x_half: f32,
    pub field_z: (f32, f32),
    pub settled_center: (f32, f32),
    pub settled_radius: (f32, f32),
    pub settled_edge: (f32, f32),
    pub edit_reach: f32,
    pub dig_volume: f32,
    pub dig_yields: String,
    pub movable: Vec<Movable>,
    pub starter: Vec<Starter>,
}

pub struct JoinMethod {
    pub id: String,
    pub name: String,
    pub when: String,
    pub never_uses: Vec<String>,
    pub undo: bool,
    pub consumes: Vec<(String, u32)>,
}

pub struct Joining {
    pub grid: f32,
    pub fine: f32,
    pub contact: f32,
    pub rigid_kinds: Vec<String>,
    pub methods: Vec<JoinMethod>,
}

pub struct Pack {
    pub materials: HashMap<String, Material>,
    pub items: HashMap<String, Item>,
    pub zones: HashMap<String, Zone>,
    pub layout: Layout,
    pub joining: Joining,
    pub carry_limit_kg: f32,
}

/// 집 바닥 윗면 (엔진 HouseModel.FloorY 와 같다)
pub const FLOOR_Y: f32 = 0.02;

fn read(dir: &Path, rel: &str) -> Result<Value, String> {
    let p = dir.join(rel);
    let text = fs::read_to_string(&p).map_err(|e| format!("{}: {e}", p.display()))?;
    serde_json::from_str(&text).map_err(|e| format!("{}: {e}", p.display()))
}

fn f(v: &Value) -> f32 {
    v.as_f64().unwrap_or(0.0) as f32
}

fn pair(v: &Value) -> (f32, f32) {
    (f(&v[0]), f(&v[1]))
}

fn strs(v: &Value) -> Vec<String> {
    v.as_array().into_iter().flatten().filter_map(|x| x.as_str().map(String::from)).collect()
}

/// 형상 부피 m³ (tools/content/validate.py 의 volume 과 같은 식: 둥근 상자 = 안쪽 상자 ⊕ 구)
pub fn volume(shape: &str, mm: &[f32], edge_mm: f32) -> f32 {
    let m: Vec<f32> = mm.iter().map(|v| v / 1000.0).collect();
    let pi = std::f32::consts::PI;
    match (shape, m.as_slice()) {
        ("box", [w, d, h]) => {
            let r = (edge_mm / 1000.0).min(w / 2.0).min(d / 2.0).min(h / 2.0);
            let (a, b, c) = (w - 2.0 * r, d - 2.0 * r, h - 2.0 * r);
            a * b * c + 2.0 * r * (a * b + b * c + c * a) + pi * r * r * (a + b + c) + 4.0 / 3.0 * pi * r.powi(3)
        }
        ("cyl", [d, h]) => pi * (d / 2.0).powi(2) * h,
        ("tube", [od, id, h]) => pi * ((od / 2.0).powi(2) - (id / 2.0).powi(2)) * h,
        ("sphere", [d, ..]) => 4.0 / 3.0 * pi * (d / 2.0).powi(3),
        _ => 0.0,
    }
}

/// 저장소 안의 content/dolly 를 찾는다 (DOLLY_CONTENT 로 바꿀 수 있다)
pub fn default_dir() -> PathBuf {
    std::env::var("DOLLY_CONTENT")
        .map(PathBuf::from)
        .unwrap_or_else(|_| Path::new(env!("CARGO_MANIFEST_DIR")).join("../content/dolly"))
}

impl Pack {
    pub fn load(dir: &Path) -> Result<Pack, String> {
        let pack = read(dir, "pack.json")?;
        let files = &pack["files"];
        let s = |k: &str| files[k].as_str().unwrap_or_default().to_string();

        let mut materials = HashMap::new();
        for m in read(dir, &s("materials"))?["materials"].as_array().into_iter().flatten() {
            materials.insert(
                m["id"].as_str().unwrap_or_default().to_string(),
                Material {
                    kind: m["kind"].as_str().unwrap_or("other").to_string(),
                    density: f(&m["density"]),
                    mu_s: f(&m["mu_s"]),
                    restitution: f(&m["restitution"]),
                },
            );
        }
        let density = |id: &str| materials.get(id).map_or(1000.0, |m: &Material| m.density);

        let mut items = HashMap::new();
        for rel in files["items"].as_array().into_iter().flatten() {
            let doc = read(dir, rel.as_str().unwrap_or_default())?;
            let cat = doc["category"].as_str().unwrap_or_default().to_string();
            for it in doc["items"].as_array().into_iter().flatten() {
                let mm: Vec<f32> = it["mm"].as_array().map(|a| a.iter().map(f).collect()).unwrap_or_default();
                let shape = it["shape"].as_str().unwrap_or_default().to_string();
                let mat = it["mat"].as_str().unwrap_or_default().to_string();
                let fill = it["fill"].as_f64().unwrap_or(1.0) as f32;
                // 질량 = 재료 밀도 × 형상 부피 × 채움 (여러 재료면 부분의 합, 적어 둔 값이 있으면 그것)
                let mass = if let Some(m) = it["mass_kg"].as_f64() {
                    m as f32
                } else if let Some(parts) = it["parts"].as_array() {
                    parts
                        .iter()
                        .map(|p| {
                            let pmm: Vec<f32> = p["mm"].as_array().map(|a| a.iter().map(f).collect()).unwrap_or_default();
                            density(p["mat"].as_str().unwrap_or_default())
                                * volume(p["shape"].as_str().unwrap_or_default(), &pmm, f(&p["edge"]))
                                * p["fill"].as_f64().unwrap_or(1.0) as f32
                        })
                        .sum()
                } else {
                    density(&mat) * volume(&shape, &mm, f(&it["edge"])) * fill
                };
                items.insert(
                    it["id"].as_str().unwrap_or_default().to_string(),
                    Item {
                        name: it["name"].as_str().unwrap_or_default().to_string(),
                        cat: cat.clone(),
                        shape,
                        mm,
                        edge_mm: f(&it["edge"]),
                        mat,
                        mass: mass.max(0.001),
                        uses: strs(&it["uses"]),
                    },
                );
            }
        }

        let zdoc = read(dir, &s("zones"))?;
        let actions = strs(&zdoc["actions"]);
        let mut zones = HashMap::new();
        for z in zdoc["zones"].as_array().into_iter().flatten() {
            let rules = actions.iter().map(|a| (a.clone(), z[a.as_str()].as_str().unwrap_or("deny").to_string())).collect();
            zones.insert(z["id"].as_str().unwrap_or_default().to_string(), Zone { name: z["name"].as_str().unwrap_or_default().to_string(), rules });
        }

        let w = read(dir, &s("world"))?;
        let house_id = w["house"].as_str().unwrap_or_default();
        let houses = read(dir, &s("houses"))?;
        let house = houses["houses"].as_array().into_iter().flatten().find(|h| h["id"] == house_id).ok_or(format!("집 설계도 {house_id} 없음"))?;
        // 옮길 수 있는 가구: 엔진 HouseModel.Furnish 와 같은 자리 (회전한 바닥 모서리가 pos → 가운데)
        let mut movable = vec![];
        for room in house["rooms"].as_array().into_iter().flatten() {
            let (rx, rz) = pair(&room["pos_m"]);
            for fu in room["furniture"].as_array().into_iter().flatten() {
                if fu["movable"] != true {
                    continue;
                }
                let id = fu["item"].as_str().unwrap_or_default().to_string();
                let Some(it) = items.get(&id) else { continue };
                let (w, d) = if it.shape == "cyl" { (it.mm[0] / 1000.0, it.mm[0] / 1000.0) } else { (it.mm[0] / 1000.0, it.mm.get(1).copied().unwrap_or(100.0) / 1000.0) };
                let yaw = f(&fu["yaw_deg"]);
                let k = ((yaw / 90.0).round() as i32).rem_euclid(4);
                let (rw, rd) = if k % 2 == 0 { (w, d) } else { (d, w) };
                let (px, pz) = pair(&fu["pos_m"]);
                movable.push(Movable { item: id, at: (rx + px + rw / 2.0, rz + pz + rd / 2.0), yaw_deg: k as f32 * 90.0 });
            }
        }
        let starter = w["starter"]["items"]
            .as_array()
            .into_iter()
            .flatten()
            .map(|s| Starter { item: s["item"].as_str().unwrap_or_default().to_string(), at: pair(&s["pos"]), yaw_deg: f(&s["yaw_deg"]) })
            .collect();
        let layout = Layout {
            house_x: w["house_x"].as_array().into_iter().flatten().map(f).collect(),
            own_house: w["own_house"].as_u64().unwrap_or(0) as usize,
            origin_dx: f(&w["origin_dx"]),
            footprint: pair(&house["footprint_m"]),
            wall_height: f(&house["wall_height_m"]),
            lot_x_half: f(&w["lot"]["x_half"]),
            lot_z: pair(&w["lot"]["z"]),
            road_z: f(&w["road"]["z"]),
            road_half: f(&w["road"]["half"]),
            field_x_half: f(&w["field"]["x_half"]),
            field_z: pair(&w["field"]["z"]),
            settled_center: pair(&w["settled"]["center"]),
            settled_radius: pair(&w["settled"]["radius"]),
            settled_edge: pair(&w["settled"]["edge"]),
            edit_reach: f(&w["edit_reach_m"]),
            dig_volume: f(&w["dig"]["tool_volume_m3"]),
            dig_yields: w["dig"]["yields"].as_str().unwrap_or_default().to_string(),
            movable,
            starter,
        };

        let j = read(dir, &s("joining"))?;
        let joining = Joining {
            grid: f(&j["snap"]["grid_mm"]) / 1000.0,
            fine: f(&j["snap"]["fine_mm"]) / 1000.0,
            contact: f(&j["snap"]["contact_mm"]) / 1000.0,
            rigid_kinds: strs(&j["rigid_kinds"]),
            methods: j["methods"]
                .as_array()
                .into_iter()
                .flatten()
                .map(|m| JoinMethod {
                    id: m["id"].as_str().unwrap_or_default().to_string(),
                    name: m["name"].as_str().unwrap_or_default().to_string(),
                    when: m["when"].as_str().unwrap_or_default().to_string(),
                    never_uses: strs(&m["never_uses"]),
                    undo: m["undo"].as_bool().unwrap_or(true),
                    consumes: m["consumes"].as_object().into_iter().flatten().map(|(k, v)| (k.clone(), v.as_u64().unwrap_or(0) as u32)).collect(),
                })
                .collect(),
        };
        let carry_limit_kg = f(&pack["standard"]["carry_limit_kg"]);
        Ok(Pack { materials, items, zones, layout, joining, carry_limit_kg })
    }

    /// 아이템의 실제 질량 kg (재료 밀도 × 형상 부피 × 채움, validate.py 와 같다)
    pub fn item_mass(&self, id: &str) -> f32 {
        self.items.get(id).map_or(1.0, |i| i.mass)
    }

    pub fn kind_of(&self, item: &str) -> &str {
        self.items.get(item).and_then(|i| self.materials.get(&i.mat)).map_or("other", |m| m.kind.as_str())
    }

    /// 맞닿은 두 부품을 잇는 방법 (joining.json 의 앞에 있는 것부터)
    pub fn join_method(&self, a: &str, b: &str) -> Result<&JoinMethod, String> {
        let (ka, kb) = (self.kind_of(a), self.kind_of(b));
        let rigid = |k: &str| self.joining.rigid_kinds.iter().any(|r| r == k);
        let name = |i: &str| self.items.get(i).map_or(i.to_string(), |x| x.name.clone());
        if !rigid(ka) || !rigid(kb) {
            let soft = if rigid(ka) { b } else { a };
            return Err(format!("{}은(는) 단단한 부품이 아니라 잇지 못한다 (담거나 묶는다)", name(soft)));
        }
        let mat = |i: &str| self.items.get(i).map(|x| x.mat.clone()).unwrap_or_default();
        for m in &self.joining.methods {
            let ok = match m.when.as_str() {
                "same_metal" => ka == "metal" && kb == "metal" && mat(a) == mat(b),
                "any_wood" => ka == "wood" || kb == "wood",
                "not_both_wood" => !(ka == "wood" && kb == "wood"),
                _ => false,
            };
            let banned = [a, b].iter().any(|i| self.items.get(*i).is_some_and(|x| x.uses.iter().any(|u| m.never_uses.contains(u))));
            if ok && !banned {
                return Ok(m);
            }
        }
        Err(format!("{}와(과) {}을(를) 잇는 방법이 없다", name(a), name(b)))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn joining_follows_the_real_rules() {
        let p = Pack::load(&default_dir()).expect("콘텐츠 팩");
        assert_eq!(p.join_method("steel_bar", "steel_plate").unwrap().id, "weld", "같은 금속은 용접");
        assert_eq!(p.join_method("steel_bar", "aluminium_profile").unwrap().id, "bolt", "다른 금속은 볼트");
        assert_eq!(p.join_method("plank_oak", "plank_oak").unwrap().id, "screw", "목재끼리는 나사");
        assert_eq!(p.join_method("plank_oak", "steel_plate").unwrap().id, "screw", "한쪽이 목재면 나사");
        assert!(p.join_method("soil_sack", "plank_oak").is_err(), "포대는 잇지 않는다");
        assert!(!p.joining.methods.iter().find(|m| m.id == "weld").unwrap().undo, "용접은 풀 수 없다");
        let desk = p.item_mass("desk");
        assert!(desk > 20.0 && desk < p.carry_limit_kg, "책상은 혼자 들 수 있다 ({desk} kg)");
        assert_eq!(p.layout.movable.iter().filter(|m| m.item == "desk").count(), 1);
    }
}
