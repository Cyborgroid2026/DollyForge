//! 카탈로그와 가격 규칙. server/catalog.json (C# 코어가 생성)을 읽어 결제 금액을 독립적으로 계산한다.
//! 모든 계산은 정수(원, mm)로 한다: C# CatalogItem.UnitPrice 와 같은 식, 0.5 에서 올림.

use crate::json::{self, Json};
use std::collections::HashMap;

#[derive(Debug, Clone)]
pub struct Item {
    pub sku: String,
    pub name_ko: String,
    pub category: String,
    pub price_krw: i64,
    pub pack_qty: i64,
    pub physical: bool,
    pub cut_to_length: Option<(i64, i64, i64)>,          // min, max, cut fee
    pub cut_to_size: Option<(i64, i64, i64, i64, i64)>, // minW, maxW, minD, maxD, cut fee
    pub plate: Option<(i64, i64)>,                      // thickness mm, density kg/m³
}

pub struct Catalog {
    pub start_budget_krw: i64,
    pub shipping_krw: i64,
    pub free_shipping_over_krw: i64,
    pub items: HashMap<String, Item>,
    pub examples: Vec<(String, i64, i64, i64)>,
}

fn round_div(num: i64, den: i64) -> i64 {
    (num + den / 2) / den
}

impl Item {
    /// 주문 단가. 치수가 범위를 벗어나면 Err(이유)
    pub fn unit_price(&self, length_mm: i64, width_mm: i64) -> Result<i64, String> {
        if let Some((min, max, fee)) = self.cut_to_length {
            if length_mm < min || length_mm > max {
                return Err(format!("{} 재단 길이는 {min}–{max} mm 입니다.", self.name_ko));
            }
            return Ok(round_div(self.price_krw * length_mm, 1000) + fee);
        }
        if let (Some((min_w, max_w, min_d, max_d, fee)), Some((t, density))) = (self.cut_to_size, self.plate) {
            if length_mm < min_w || length_mm > max_w || width_mm < min_d || width_mm > max_d {
                return Err(format!("{} 재단 크기는 {min_w}–{max_w} × {min_d}–{max_d} mm 입니다.", self.name_ko));
            }
            let num = self.price_krw as i128 * density as i128 * length_mm as i128 * width_mm as i128 * t as i128;
            return Ok(((num + 500_000_000) / 1_000_000_000) as i64 + fee);
        }
        if length_mm != 0 || width_mm != 0 {
            return Err(format!("{} 은(는) 재단 상품이 아닙니다.", self.name_ko));
        }
        Ok(self.price_krw)
    }
}

impl Catalog {
    pub fn load(path: &str) -> Result<Catalog, String> {
        let text = std::fs::read_to_string(path).map_err(|e| format!("{path}: {e}"))?;
        Catalog::parse(&text)
    }

    pub fn parse(text: &str) -> Result<Catalog, String> {
        let doc = json::parse(text)?;
        let int = |j: &Json, k: &str| j.get(k).and_then(Json::int).ok_or_else(|| format!("missing integer '{k}'"));
        let mut items = HashMap::new();
        for it in doc.get("items").and_then(Json::arr).ok_or("missing items")? {
            let sku = it.get("sku").and_then(Json::str).ok_or("missing sku")?.to_string();
            let range = |key: &str, names: &[&str]| -> Result<Option<Vec<i64>>, String> {
                match it.get(key) {
                    None | Some(Json::Null) => Ok(None),
                    Some(o) => names.iter().map(|n| int(o, n)).collect::<Result<Vec<_>, _>>().map(Some),
                }
            };
            let item = Item {
                name_ko: it.get("nameKo").and_then(Json::str).unwrap_or(&sku).to_string(),
                category: it.get("category").and_then(Json::str).unwrap_or("").to_string(),
                price_krw: int(it, "priceKrw")?,
                pack_qty: int(it, "packQty")?,
                physical: it.get("physical").and_then(Json::bool).unwrap_or(true),
                cut_to_length: range("cutToLength", &["min", "max", "cutFeeKrw"])?.map(|v| (v[0], v[1], v[2])),
                cut_to_size: range("cutToSize", &["minW", "maxW", "minD", "maxD", "cutFeeKrw"])?.map(|v| (v[0], v[1], v[2], v[3], v[4])),
                plate: range("plate", &["thicknessMm", "densityKgM3"])?.map(|v| (v[0], v[1])),
                sku: sku.clone(),
            };
            items.insert(sku, item);
        }
        let mut examples = Vec::new();
        for e in doc.get("examples").and_then(Json::arr).unwrap_or(&[]) {
            examples.push((
                e.get("sku").and_then(Json::str).unwrap_or("").to_string(),
                int(e, "lengthMm")?,
                int(e, "widthMm")?,
                int(e, "unitKrw")?,
            ));
        }
        Ok(Catalog {
            start_budget_krw: int(&doc, "startBudgetKrw")?,
            shipping_krw: int(&doc, "shippingKrw")?,
            free_shipping_over_krw: int(&doc, "freeShippingOverKrw")?,
            items,
            examples,
        })
    }

    pub fn shipping_for(&self, subtotal: i64) -> i64 {
        if subtotal == 0 || subtotal >= self.free_shipping_over_krw {
            0
        } else {
            self.shipping_krw
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn catalog() -> Catalog {
        Catalog::load(concat!(env!("CARGO_MANIFEST_DIR"), "/catalog.json")).expect("catalog.json")
    }

    #[test]
    fn matches_every_csharp_price_example() {
        // C# 코어가 계산한 가격과 원 단위까지 같아야 한다 (경계값·반올림 포함)
        let c = catalog();
        assert!(c.examples.len() > 50);
        for (sku, l, w, expected) in &c.examples {
            let got = c.items[sku].unit_price(*l, *w).unwrap();
            assert_eq!(got, *expected, "{sku} {l}×{w}");
        }
    }

    #[test]
    fn rejects_out_of_range_cuts() {
        let c = catalog();
        assert!(c.items["AL-2020"].unit_price(5000, 0).is_err());
        assert!(c.items["PL-SS400-3"].unit_price(10, 10).is_err());
        assert!(c.items["MT-JGB37-100"].unit_price(100, 0).is_err());
    }
}
