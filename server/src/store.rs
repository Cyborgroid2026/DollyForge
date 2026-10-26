//! 계정·창고 저장소 (파일). 운영 시 DB 로 교체한다.
//!   data/accounts/<qr_token>.txt        key=value: 전화번호, 성별, 잔액, 소모품 재고, 설치된 체결 부품, 마지막 위치,
//!                                        몸 구성(표준이 아닌 모듈 품번), 몸에 찬 기기 품번
//!   data/devices/<qr_token>/<이름>.json 창고에 보관한 기기 (C# 코어의 기기 JSON)

use std::collections::{BTreeMap, HashMap};
use std::fs;
use std::io;
use std::path::PathBuf;

#[derive(Clone, Debug, PartialEq)]
pub struct Account {
    pub token: String,
    pub phone: String,
    pub gender: u8, // 0 여성, 1 남성
    pub balance_krw: i64,
    /// 사 두고 아직 쓰지 않은 소모품 (품번 → 개수)
    pub stock: BTreeMap<String, i64>,
    /// 기기에 설치된 체결 부품 (해체하면 이만큼만 되돌릴 수 있다)
    pub installed: BTreeMap<String, i64>,
    pub pose: [f32; 4], // x, y, z, yaw
    /// 표준이 아닌 몸 모듈 품번 (예: CY-F-SHINL-HD)
    pub body: Vec<String>,
    /// 몸에 찬 Android Tool 기기 품번
    pub worn: Vec<String>,
}

impl Account {
    pub fn new(token: String, phone: String, gender: u8, balance_krw: i64, spawn: (f32, f32, f32)) -> Self {
        Account {
            token,
            phone,
            gender,
            balance_krw,
            stock: BTreeMap::new(),
            installed: BTreeMap::new(),
            pose: [spawn.0, spawn.1, spawn.2, 0.0],
            body: Vec::new(),
            worn: Vec::new(),
        }
    }

    fn map_to_text(m: &BTreeMap<String, i64>) -> String {
        m.iter().filter(|(_, &n)| n > 0).map(|(k, n)| format!("{k}:{n}")).collect::<Vec<_>>().join(";")
    }

    fn text_to_map(t: &str) -> BTreeMap<String, i64> {
        t.split(';')
            .filter_map(|p| p.split_once(':'))
            .filter_map(|(k, v)| v.parse().ok().map(|n| (k.to_string(), n)))
            .collect()
    }

    fn text_to_list(t: &str) -> Vec<String> {
        t.split(',').filter(|s| !s.is_empty()).map(str::to_string).collect()
    }

    fn serialize(&self) -> String {
        let p = self.pose;
        format!(
            "phone={}\ngender={}\nbalance={}\nstock={}\ninstalled={}\npose={},{},{},{}\nbody={}\nworn={}\n",
            self.phone,
            self.gender,
            self.balance_krw,
            Self::map_to_text(&self.stock),
            Self::map_to_text(&self.installed),
            p[0],
            p[1],
            p[2],
            p[3],
            self.body.join(","),
            self.worn.join(",")
        )
    }

    fn deserialize(token: &str, text: &str) -> Option<Self> {
        let kv: HashMap<&str, &str> = text.lines().filter_map(|l| l.split_once('=')).collect();
        let mut pose = [0.0f32; 4];
        if let Some(v) = kv.get("pose") {
            for (i, x) in v.split(',').take(4).enumerate() {
                pose[i] = x.parse().unwrap_or(0.0);
            }
        }
        Some(Account {
            token: token.to_string(),
            phone: kv.get("phone")?.to_string(),
            gender: kv.get("gender")?.parse().ok()?,
            balance_krw: kv.get("balance")?.parse().ok()?,
            stock: Self::text_to_map(kv.get("stock").copied().unwrap_or("")),
            installed: Self::text_to_map(kv.get("installed").copied().unwrap_or("")),
            pose,
            body: Self::text_to_list(kv.get("body").copied().unwrap_or("")),
            worn: Self::text_to_list(kv.get("worn").copied().unwrap_or("")),
        })
    }
}

pub struct Store {
    dir: PathBuf,
    by_phone: HashMap<String, String>, // phone -> token
}

fn valid_token(token: &str) -> bool {
    token.len() == 32 && token.bytes().all(|b| b.is_ascii_hexdigit())
}

/// 기기 이름 → 파일 이름. 경로 구분자·제어 문자를 막고 길이를 제한한다 (한글 허용).
pub fn device_file_name(name: &str) -> Option<String> {
    let clean: String = name
        .trim()
        .chars()
        .filter(|c| !c.is_control() && !matches!(c, '/' | '\\' | ':' | '*' | '?' | '"' | '<' | '>' | '|'))
        .take(40)
        .collect();
    let clean = clean.trim_matches('.').trim().to_string();
    (!clean.is_empty()).then(|| format!("{clean}.json"))
}

impl Store {
    pub fn open(dir: impl Into<PathBuf>) -> io::Result<Self> {
        let dir = dir.into();
        fs::create_dir_all(dir.join("accounts"))?;
        fs::create_dir_all(dir.join("devices"))?;
        let mut by_phone = HashMap::new();
        for entry in fs::read_dir(dir.join("accounts"))? {
            let path = entry?.path();
            let Some(token) = path.file_stem().and_then(|s| s.to_str()) else { continue };
            if !valid_token(token) {
                continue;
            }
            if let Some(acc) = fs::read_to_string(&path).ok().and_then(|t| Account::deserialize(token, &t)) {
                by_phone.insert(acc.phone.clone(), acc.token);
            }
        }
        Ok(Store { dir, by_phone })
    }

    pub fn account_count(&self) -> usize {
        self.by_phone.len()
    }

    pub fn token_for_phone(&self, phone: &str) -> Option<&String> {
        self.by_phone.get(phone)
    }

    pub fn load(&self, token: &str) -> Option<Account> {
        if !valid_token(token) {
            return None;
        }
        let text = fs::read_to_string(self.dir.join("accounts").join(format!("{token}.txt"))).ok()?;
        Account::deserialize(token, &text)
    }

    pub fn save(&mut self, acc: &Account) {
        let path = self.dir.join("accounts").join(format!("{}.txt", acc.token));
        let tmp = path.with_extension("tmp");
        match fs::write(&tmp, acc.serialize()).and_then(|_| fs::rename(&tmp, &path)) {
            Ok(()) => {
                self.by_phone.insert(acc.phone.clone(), acc.token.clone());
            }
            Err(e) => eprintln!("[store] 저장 실패 {}: {e}", acc.token),
        }
    }

    fn device_dir(&self, token: &str) -> PathBuf {
        self.dir.join("devices").join(token)
    }

    /// (이름, 바이트 수) 목록, 이름순
    pub fn list_devices(&self, token: &str) -> Vec<(String, String)> {
        let mut out = Vec::new();
        if let Ok(rd) = fs::read_dir(self.device_dir(token)) {
            for e in rd.flatten() {
                let p = e.path();
                if p.extension().and_then(|x| x.to_str()) != Some("json") {
                    continue;
                }
                if let (Some(stem), Ok(text)) = (p.file_stem().and_then(|s| s.to_str()), fs::read_to_string(&p)) {
                    out.push((stem.to_string(), text));
                }
            }
        }
        out.sort_by(|a, b| a.0.cmp(&b.0));
        out
    }

    pub fn put_device(&self, token: &str, name: &str, json: &str) -> io::Result<String> {
        let file = device_file_name(name).ok_or_else(|| io::Error::new(io::ErrorKind::InvalidInput, "이름이 비어 있습니다"))?;
        let dir = self.device_dir(token);
        fs::create_dir_all(&dir)?;
        let path = dir.join(&file);
        if path.exists() {
            return Err(io::Error::new(io::ErrorKind::AlreadyExists, "같은 이름의 기기가 이미 창고에 있습니다"));
        }
        fs::write(&path, json)?;
        Ok(file.trim_end_matches(".json").to_string())
    }

    /// 창고에서 꺼낸다 (파일은 지워진다 — 같은 기기를 두 번 꺼낼 수 없다)
    pub fn take_device(&self, token: &str, name: &str) -> io::Result<String> {
        let file = device_file_name(name).ok_or_else(|| io::Error::new(io::ErrorKind::InvalidInput, "이름이 비어 있습니다"))?;
        let path = self.device_dir(token).join(file);
        let text = fs::read_to_string(&path)?;
        fs::remove_file(&path)?;
        Ok(text)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn temp_dir(tag: &str) -> PathBuf {
        let d = std::env::temp_dir().join(format!("dolly-store-{tag}-{}", std::process::id()));
        let _ = fs::remove_dir_all(&d);
        d
    }

    #[test]
    fn account_roundtrip() {
        let mut a = Account::new("ab".repeat(16), "01012345678".into(), 1, 300_000, (0.0, 0.2, -2.5));
        a.stock.insert("FS-BOLT-M8".into(), 8);
        a.installed.insert("FS-BOLT-M8".into(), 2);
        a.pose = [1.5, 0.2, -3.0, 0.7];
        a.body = vec!["CY-M-SHINL-HD".into(), "CY-M-SHINR-HD".into()];
        a.worn = vec!["ATE-HS-G".into()];
        let b = Account::deserialize(&a.token, &a.serialize()).unwrap();
        assert_eq!(a, b);
    }

    #[test]
    fn devices_cannot_be_duplicated() {
        let dir = temp_dir("dev");
        let s = Store::open(&dir).unwrap();
        let token = "cd".repeat(16);
        assert_eq!(s.put_device(&token, "2륜 로봇", "{}").unwrap(), "2륜 로봇");
        assert!(s.put_device(&token, "2륜 로봇", "{}").is_err());
        assert_eq!(s.list_devices(&token).len(), 1);
        assert_eq!(s.take_device(&token, "2륜 로봇").unwrap(), "{}");
        assert!(s.take_device(&token, "2륜 로봇").is_err(), "두 번 꺼낼 수 없다");
        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn device_names_cannot_escape_directory() {
        assert_eq!(device_file_name("../../etc/passwd").as_deref(), Some("etcpasswd.json"));
        assert_eq!(device_file_name("  ").as_deref(), None);
        assert_eq!(device_file_name("로봇 v2").as_deref(), Some("로봇 v2.json"));
    }
}
