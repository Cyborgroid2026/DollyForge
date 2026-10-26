//! 계정 저장소. data/accounts/<qr_token>.txt 에 key=value 형식으로 저장한다.
//! 첫 버전용 파일 저장소이며, 운영 시 DB 로 교체한다.

use crate::data::*;
use std::collections::HashMap;
use std::fs;
use std::io;
use std::path::PathBuf;

#[derive(Clone, Debug)]
pub struct Account {
    pub token: String,
    pub phone: String,
    pub gender: u8, // 0 여성, 1 남성
    pub day: u16,
    pub years: u16,
    pub phase: Phase,
    pub tasks: u8,
    pub battery: f32,
    pub bio: f32,
    pub memory: u16,
    pub inv: [u16; 4],
    pub collected: u8,
    pub today: [u16; 4], // 오늘 외출에서 얻은 자원 (셧다운 시 분실)
    pub research: u8,    // 완료한 연구 수
    pub haram_age: u8,
    pub haram_alive: bool,
    pub cryo_count: u16,
    pub haram_seen_cryo: u16,
    pub visits: u16,
    pub letter_read: bool,
}

impl Account {
    pub fn new(token: String, phone: String, gender: u8) -> Self {
        let mut a = Account {
            token,
            phone,
            gender,
            day: 1,
            years: 0,
            phase: Phase::Daily,
            tasks: 0,
            battery: 100.0,
            bio: BIO_START,
            memory: 0,
            inv: START_INVENTORY,
            collected: 0,
            today: [0; 4],
            research: 0,
            haram_age: HARAM_START_AGE,
            haram_alive: true,
            cryo_count: 0,
            haram_seen_cryo: 0,
            visits: 0,
            letter_read: false,
        };
        a.reset_progress();
        a
    }

    /// 계정 정보는 유지하고 진행 상황만 처음으로 되돌린다.
    pub fn reset_progress(&mut self) {
        let keep = (self.token.clone(), self.phone.clone(), self.gender);
        *self = Account {
            token: keep.0,
            phone: keep.1,
            gender: keep.2,
            day: 1,
            years: 0,
            phase: Phase::Daily,
            tasks: 0,
            battery: 100.0,
            bio: BIO_START,
            memory: 0,
            inv: START_INVENTORY,
            collected: 0,
            today: [0; 4],
            research: 0,
            haram_age: HARAM_START_AGE,
            haram_alive: true,
            cryo_count: 0,
            haram_seen_cryo: 0,
            visits: 0,
            letter_read: false,
        };
    }

    fn serialize(&self) -> String {
        let inv = |a: &[u16; 4]| a.iter().map(|n| n.to_string()).collect::<Vec<_>>().join(",");
        format!(
            "phone={}\ngender={}\nday={}\nyears={}\nphase={}\ntasks={}\nbattery={}\nbio={}\nmemory={}\n\
             inv={}\ncollected={}\ntoday={}\nresearch={}\nharam_age={}\nharam_alive={}\ncryo_count={}\n\
             haram_seen_cryo={}\nvisits={}\nletter_read={}\n",
            self.phone,
            self.gender,
            self.day,
            self.years,
            self.phase as u8,
            self.tasks,
            self.battery,
            self.bio,
            self.memory,
            inv(&self.inv),
            self.collected,
            inv(&self.today),
            self.research,
            self.haram_age,
            self.haram_alive as u8,
            self.cryo_count,
            self.haram_seen_cryo,
            self.visits,
            self.letter_read as u8,
        )
    }

    fn deserialize(token: &str, text: &str) -> Option<Self> {
        let kv: HashMap<&str, &str> = text.lines().filter_map(|l| l.split_once('=')).collect();
        let get = |k: &str| kv.get(k).copied();
        let num = |k: &str| get(k).and_then(|v| v.parse::<f64>().ok());
        let arr = |k: &str| -> [u16; 4] {
            let mut out = [0u16; 4];
            if let Some(v) = get(k) {
                for (i, p) in v.split(',').take(4).enumerate() {
                    out[i] = p.parse().unwrap_or(0);
                }
            }
            out
        };
        let mut a = Account::new(token.to_string(), get("phone")?.to_string(), num("gender")? as u8);
        a.day = num("day").unwrap_or(1.0) as u16;
        a.years = num("years").unwrap_or(0.0) as u16;
        a.phase = Phase::from_u8(num("phase").unwrap_or(0.0) as u8);
        a.tasks = num("tasks").unwrap_or(0.0) as u8;
        a.battery = num("battery").unwrap_or(100.0) as f32;
        a.bio = num("bio").unwrap_or(BIO_START as f64) as f32;
        a.memory = num("memory").unwrap_or(0.0) as u16;
        a.inv = arr("inv");
        a.collected = num("collected").unwrap_or(0.0) as u8;
        a.today = arr("today");
        a.research = num("research").unwrap_or(0.0) as u8;
        a.haram_age = num("haram_age").unwrap_or(HARAM_START_AGE as f64) as u8;
        a.haram_alive = num("haram_alive").unwrap_or(1.0) != 0.0;
        a.cryo_count = num("cryo_count").unwrap_or(0.0) as u16;
        a.haram_seen_cryo = num("haram_seen_cryo").unwrap_or(0.0) as u16;
        a.visits = num("visits").unwrap_or(0.0) as u16;
        a.letter_read = num("letter_read").unwrap_or(0.0) != 0.0;
        Some(a)
    }
}

pub struct Store {
    dir: PathBuf,
    by_phone: HashMap<String, String>, // phone -> token
}

fn valid_token(token: &str) -> bool {
    token.len() == 32 && token.bytes().all(|b| b.is_ascii_hexdigit())
}

impl Store {
    pub fn open(dir: impl Into<PathBuf>) -> io::Result<Self> {
        let dir = dir.into();
        fs::create_dir_all(&dir)?;
        let mut by_phone = HashMap::new();
        for entry in fs::read_dir(&dir)? {
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
        let text = fs::read_to_string(self.dir.join(format!("{token}.txt"))).ok()?;
        Account::deserialize(token, &text)
    }

    pub fn save(&mut self, acc: &Account) {
        let path = self.dir.join(format!("{}.txt", acc.token));
        let tmp = path.with_extension("tmp");
        let result = fs::write(&tmp, acc.serialize()).and_then(|_| fs::rename(&tmp, &path));
        match result {
            Ok(()) => {
                self.by_phone.insert(acc.phone.clone(), acc.token.clone());
            }
            Err(e) => eprintln!("[store] 저장 실패 {}: {e}", acc.token),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn serialize_roundtrip() {
        let mut a = Account::new("ab".repeat(16), "01012345678".into(), 1);
        a.inv = [3, 1, 4, 1];
        a.phase = Phase::Outing;
        a.haram_alive = false;
        let b = Account::deserialize(&a.token, &a.serialize()).unwrap();
        assert_eq!(b.inv, [3, 1, 4, 1]);
        assert_eq!(b.phase, Phase::Outing);
        assert!(!b.haram_alive);
        assert_eq!(b.gender, 1);
    }
}
