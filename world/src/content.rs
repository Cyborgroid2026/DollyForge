//! 콘텐츠 팩 (content/dolly) 에서 세계 서버가 쓰는 것만 읽는다: 재료(밀도·마찰·반발), 아이템 치수, 구역 규칙, 집 크기, 세계 배치.
//! C# 엔진이 같은 파일을 읽어 지형·집을 짓기 때문에, 서버의 판정과 화면이 어긋나지 않는다.

use serde_json::Value;
use std::collections::HashMap;
use std::fs;
use std::path::{Path, PathBuf};

pub struct Material {
    pub density: f32,
    pub mu_s: f32,
    pub restitution: f32,
}

pub struct Item {
    pub name: String,
    pub mm: Vec<f32>,
    pub edge_mm: f32,
    pub mat: String,
}

pub struct Zone {
    pub name: String,
    pub rules: HashMap<String, String>,
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
}

pub struct Pack {
    pub materials: HashMap<String, Material>,
    pub items: HashMap<String, Item>,
    pub zones: HashMap<String, Zone>,
    pub layout: Layout,
    pub carry_limit_kg: f32,
}

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
                Material { density: f(&m["density"]), mu_s: f(&m["mu_s"]), restitution: f(&m["restitution"]) },
            );
        }

        let mut items = HashMap::new();
        for rel in files["items"].as_array().into_iter().flatten() {
            let doc = read(dir, rel.as_str().unwrap_or_default())?;
            for it in doc["items"].as_array().into_iter().flatten() {
                items.insert(
                    it["id"].as_str().unwrap_or_default().to_string(),
                    Item {
                        name: it["name"].as_str().unwrap_or_default().to_string(),
                        mm: it["mm"].as_array().map(|a| a.iter().map(f).collect()).unwrap_or_default(),
                        edge_mm: f(&it["edge"]),
                        mat: it["mat"].as_str().unwrap_or_default().to_string(),
                    },
                );
            }
        }

        let zdoc = read(dir, &s("zones"))?;
        let actions: Vec<String> = zdoc["actions"].as_array().into_iter().flatten().filter_map(|a| a.as_str().map(String::from)).collect();
        let mut zones = HashMap::new();
        for z in zdoc["zones"].as_array().into_iter().flatten() {
            let rules = actions.iter().map(|a| (a.clone(), z[a.as_str()].as_str().unwrap_or("deny").to_string())).collect();
            zones.insert(z["id"].as_str().unwrap_or_default().to_string(), Zone { name: z["name"].as_str().unwrap_or_default().to_string(), rules });
        }

        let w = read(dir, &s("world"))?;
        let house_id = w["house"].as_str().unwrap_or_default();
        let houses = read(dir, &s("houses"))?;
        let house = houses["houses"].as_array().into_iter().flatten().find(|h| h["id"] == house_id).ok_or(format!("집 설계도 {house_id} 없음"))?;
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
        };
        let carry_limit_kg = f(&pack["standard"]["carry_limit_kg"]);
        Ok(Pack { materials, items, zones, layout, carry_limit_kg })
    }

    /// 아이템의 실제 질량 (상자 부피 × 재료 밀도)
    pub fn item_mass(&self, id: &str) -> f32 {
        let Some(it) = self.items.get(id) else { return 1.0 };
        let vol: f32 = it.mm.iter().map(|d| d / 1000.0).product();
        vol * self.materials.get(&it.mat).map_or(1000.0, |m| m.density)
    }
}
