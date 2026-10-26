//! 월드 서버 상태. 단일 게임 루프 스레드가 소유한다.
//! 서버가 권위를 갖는 것: QR 계정·인증, 예산과 결제(가격을 직접 다시 계산), 소모품 재고,
//! 기기 창고, 다른 플레이어의 위치 중계. 물리는 각 클라이언트(Godot·Jolt)가 계산한다.

use crate::catalog::Catalog;
use crate::data::*;
use crate::json;
use crate::protocol::{op, OrderLine, Writer, C2S};
use crate::store::{Account, Store};
use crate::ws;
use std::collections::HashMap;
use std::fs::File;
use std::io::Read;
use std::net::{Shutdown, TcpStream};
use std::sync::mpsc::Sender;
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

pub enum Event {
    Connected { conn: u32, out: Sender<Vec<u8>>, stream: TcpStream },
    Message { conn: u32, msg: C2S },
    Disconnected { conn: u32 },
}

const MSG_TOAST: u8 = 0;
const CODE_TTL: Duration = Duration::from_secs(300);
const CODE_MAX_ATTEMPTS: u8 = 5;

struct Online {
    acc: Account,
    moved: bool,
    dirty: bool,
}

struct Session {
    out: Sender<Vec<u8>>,
    stream: TcpStream,
    player: Option<Online>,
}

impl Session {
    fn send(&self, payload: &[u8]) {
        let _ = self.out.send(ws::binary(payload));
    }
}

struct PendingCode {
    code: String,
    expires: Instant,
    attempts: u8,
    sent_at: Instant,
}

enum Dest {
    One(u32),
    Others { except: u32, near: Option<(f32, f32)> },
    All,
}

pub struct Game {
    sessions: HashMap<u32, Session>,
    store: Store,
    catalog: Catalog,
    codes: HashMap<String, PendingCode>,
    rng: Rng,
    dev_echo_code: bool,
    outbox: Vec<(Dest, Vec<u8>)>,
}

// ---------- 메시지 ----------

fn msg_text(kind: u8, text: &str) -> Vec<u8> {
    Writer::new(op::MESSAGE).u8(kind).str(text).finish()
}

fn msg_error(text: &str) -> Vec<u8> {
    Writer::new(op::ERROR).str(text).finish()
}

fn msg_profile(a: &Account) -> Vec<u8> {
    let stock: Vec<_> = a.stock.iter().filter(|(_, &n)| n > 0).collect();
    let mut w = Writer::new(op::PROFILE).i64(a.balance_krw).u16(stock.len() as u16);
    for (sku, n) in stock {
        w = w.str(sku).u32(*n as u32);
    }
    w.finish()
}

fn msg_joined(id: u32, a: &Account) -> Vec<u8> {
    let p = a.pose;
    Writer::new(op::PLAYER_JOINED).u32(id).u8(a.gender).f32(p[0]).f32(p[1]).f32(p[2]).f32(p[3]).finish()
}

fn msg_moved(id: u32, a: &Account) -> Vec<u8> {
    let p = a.pose;
    Writer::new(op::PLAYER_MOVED).u32(id).f32(p[0]).f32(p[1]).f32(p[2]).f32(p[3]).finish()
}

/// 원화 표기: 425000 → "₩425,000"
fn won(n: i64) -> String {
    let digits = n.unsigned_abs().to_string();
    let mut out = String::new();
    for (i, c) in digits.chars().enumerate() {
        if i > 0 && (digits.len() - i) % 3 == 0 {
            out.push(',');
        }
        out.push(c);
    }
    format!("{}₩{out}", if n < 0 { "-" } else { "" })
}

fn normalize_phone(raw: &str) -> Option<String> {
    let digits: String = raw.chars().filter(|c| c.is_ascii_digit()).collect();
    let ok = (10..=11).contains(&digits.len()) && digits.starts_with("01") && raw.len() <= 20;
    ok.then_some(digits)
}

fn mask_phone(phone: &str) -> String {
    let tail = &phone[phone.len().saturating_sub(4)..];
    format!("{}-****-{}", &phone[..3.min(phone.len())], tail)
}

/// 주문 금액을 카탈로그로 다시 계산한다. (단가 목록, 상품 합계) 또는 거절 이유.
pub fn price_order(catalog: &Catalog, lines: &[OrderLine]) -> Result<(Vec<i64>, i64), String> {
    if lines.is_empty() {
        return Err("주문할 품목이 없습니다.".into());
    }
    if lines.len() > MAX_ORDER_LINES {
        return Err("주문서가 너무 깁니다.".into());
    }
    let mut units = Vec::with_capacity(lines.len());
    let mut subtotal = 0i64;
    for l in lines {
        let item = catalog.items.get(&l.sku).ok_or_else(|| format!("없는 품번입니다: {}", l.sku))?;
        if l.qty == 0 || l.qty as i64 > MAX_ORDER_QTY {
            return Err(format!("{} 수량은 1–{MAX_ORDER_QTY} 입니다.", item.name_ko));
        }
        let unit = item.unit_price(l.length_mm as i64, l.width_mm as i64)?;
        units.push(unit);
        subtotal += unit * l.qty as i64;
    }
    Ok((units, subtotal))
}

impl Game {
    pub fn new(store: Store, catalog: Catalog, dev_echo_code: bool) -> Self {
        Game {
            sessions: HashMap::new(),
            store,
            catalog,
            codes: HashMap::new(),
            rng: Rng::seeded(),
            dev_echo_code,
            outbox: Vec::new(),
        }
    }

    pub fn handle(&mut self, ev: Event) {
        match ev {
            Event::Connected { conn, out, stream } => {
                self.sessions.insert(conn, Session { out, stream, player: None });
            }
            Event::Disconnected { conn } => self.disconnect(conn),
            Event::Message { conn, msg } => self.on_message(conn, msg),
        }
        self.flush();
    }

    /// 위치 중계 (TICK_HZ)
    pub fn tick(&mut self) {
        for (&id, s) in self.sessions.iter_mut() {
            let Some(p) = s.player.as_mut() else { continue };
            if p.moved {
                p.moved = false;
                let near = Some((p.acc.pose[0], p.acc.pose[2]));
                self.outbox.push((Dest::Others { except: id, near }, msg_moved(id, &p.acc)));
            }
        }
        let now = Instant::now();
        self.codes.retain(|_, c| c.expires > now);
        self.flush();
    }

    fn flush(&mut self) {
        for (dest, payload) in self.outbox.drain(..) {
            match dest {
                Dest::One(id) => {
                    if let Some(s) = self.sessions.get(&id) {
                        s.send(&payload);
                    }
                }
                Dest::Others { except, near } => {
                    let frame = ws::binary(&payload);
                    for (&id, s) in &self.sessions {
                        let Some(p) = &s.player else { continue };
                        if id == except {
                            continue;
                        }
                        if let Some((x, z)) = near {
                            if (p.acc.pose[0] - x).hypot(p.acc.pose[2] - z) > AOI_RADIUS {
                                continue;
                            }
                        }
                        let _ = s.out.send(frame.clone());
                    }
                }
                Dest::All => {
                    let frame = ws::binary(&payload);
                    for s in self.sessions.values().filter(|s| s.player.is_some()) {
                        let _ = s.out.send(frame.clone());
                    }
                }
            }
        }
    }

    fn reply(&mut self, conn: u32, payload: Vec<u8>) {
        self.outbox.push((Dest::One(conn), payload));
    }

    fn error(&mut self, conn: u32, text: &str) {
        self.reply(conn, msg_error(text));
    }

    fn disconnect(&mut self, conn: u32) {
        if let Some(s) = self.sessions.remove(&conn) {
            let _ = s.stream.shutdown(Shutdown::Both);
            if let Some(p) = s.player {
                self.store.save(&p.acc);
                self.outbox.push((Dest::All, Writer::new(op::PLAYER_LEFT).u32(conn).finish()));
                println!("[game] 퇴장 #{conn} ({})", mask_phone(&p.acc.phone));
            }
        }
    }

    fn on_message(&mut self, conn: u32, msg: C2S) {
        let logged_in = self.sessions.get(&conn).map_or(false, |s| s.player.is_some());
        match msg {
            C2S::Register { phone, gender } => self.on_register(conn, &phone, gender),
            C2S::RequestCode { token } => self.on_request_code(conn, &token),
            C2S::Login { token, code } => self.on_login(conn, &token, &code),
            _ if !logged_in => self.error(conn, "로그인이 필요합니다."),
            C2S::Pose { x, y, z, yaw } => self.on_pose(conn, x, y, z, yaw),
            C2S::Chat { text } => {
                let text: String = text.trim().chars().take(120).collect();
                if !text.is_empty() {
                    self.outbox.push((Dest::All, Writer::new(op::CHAT_BROADCAST).u32(conn).str(&text).finish()));
                }
            }
            C2S::Purchase { lines } => self.on_purchase(conn, &lines),
            C2S::Consume { sku, qty } => self.on_stock_move(conn, &sku, qty as i64, true),
            C2S::Refund { sku, qty } => self.on_stock_move(conn, &sku, qty as i64, false),
            C2S::StowDevice { name, json } => self.on_stow(conn, &name, &json),
            C2S::ListDevices => self.send_device_list(conn),
            C2S::RetrieveDevice { name } => self.on_retrieve(conn, &name),
        }
    }

    fn player(&mut self, conn: u32) -> Option<&mut Online> {
        self.sessions.get_mut(&conn).and_then(|s| s.player.as_mut())
    }

    // ---------- QR 계정 / 인증 ----------

    fn on_register(&mut self, conn: u32, phone: &str, gender: u8) {
        let Some(phone) = normalize_phone(phone) else {
            return self.error(conn, "전화번호 형식이 올바르지 않습니다. (예: 010-1234-5678)");
        };
        let token = match self.store.token_for_phone(&phone) {
            Some(t) => t.clone(),
            None => {
                let token = random_token(&mut self.rng);
                let acc = Account::new(token.clone(), phone.clone(), gender.min(1), self.catalog.start_budget_krw, SPAWN);
                self.store.save(&acc);
                println!("[auth] 새 QR 계정 {}", mask_phone(&phone));
                token
            }
        };
        self.reply(conn, Writer::new(op::REGISTERED).str(&token).finish());
    }

    fn on_request_code(&mut self, conn: u32, token: &str) {
        let Some(acc) = self.store.load(token) else {
            return self.error(conn, "등록되지 않은 QR 계정입니다.");
        };
        let cooldown = if self.dev_echo_code { Duration::from_secs(1) } else { Duration::from_secs(30) };
        if let Some(prev) = self.codes.get(token) {
            if prev.sent_at.elapsed() < cooldown {
                return self.error(conn, "잠시 후 다시 요청해 주세요.");
            }
        }
        let code = format!("{:06}", self.rng.next() % 1_000_000);
        // 첫 버전: SMS 게이트웨이 대신 서버 콘솔에 출력한다.
        println!("[SMS → {}] [Dolly Forge] 인증코드 {code}", mask_phone(&acc.phone));
        let now = Instant::now();
        self.codes.insert(token.to_string(), PendingCode { code: code.clone(), expires: now + CODE_TTL, attempts: 0, sent_at: now });
        let echo = if self.dev_echo_code { code.as_str() } else { "" };
        self.reply(conn, Writer::new(op::CODE_SENT).str(&mask_phone(&acc.phone)).str(echo).finish());
    }

    fn on_login(&mut self, conn: u32, token: &str, code: &str) {
        let verified = match self.codes.get_mut(token) {
            Some(p) if p.expires > Instant::now() && p.attempts < CODE_MAX_ATTEMPTS => {
                p.attempts += 1;
                p.code == code
            }
            _ => false,
        };
        if !verified {
            return self.error(conn, "인증 코드가 틀렸거나 만료되었습니다.");
        }
        self.codes.remove(token);

        // 같은 계정의 기존 접속은 끊는다 (그 접속의 상태를 먼저 저장)
        let dup: Vec<u32> = self
            .sessions
            .iter()
            .filter(|(&id, s)| id != conn && s.player.as_ref().map_or(false, |p| p.acc.token == token))
            .map(|(&id, _)| id)
            .collect();
        for id in dup {
            if let Some(s) = self.sessions.get(&id) {
                s.send(&msg_error("다른 기기에서 접속하여 연결이 종료되었습니다."));
            }
            self.disconnect(id);
        }

        let Some(acc) = self.store.load(token) else {
            return self.error(conn, "계정을 불러오지 못했습니다.");
        };
        println!("[game] 입장 #{conn} ({})", mask_phone(&acc.phone));
        self.reply(conn, Writer::new(op::LOGIN_OK).u32(conn).u8(acc.gender).finish());
        self.reply(conn, msg_profile(&acc));
        let others: Vec<Vec<u8>> = self
            .sessions
            .iter()
            .filter_map(|(&id, s)| s.player.as_ref().map(|p| msg_joined(id, &p.acc)))
            .collect();
        for m in others {
            self.reply(conn, m);
        }
        self.outbox.push((Dest::Others { except: conn, near: None }, msg_joined(conn, &acc)));
        if let Some(s) = self.sessions.get_mut(&conn) {
            s.player = Some(Online { acc, moved: false, dirty: false });
        }
    }

    // ---------- 위치 ----------

    fn on_pose(&mut self, conn: u32, x: f32, y: f32, z: f32, yaw: f32) {
        if x.abs() > WORLD_LIMIT || z.abs() > WORLD_LIMIT || !(-100.0..=500.0).contains(&y) {
            return;
        }
        if let Some(p) = self.player(conn) {
            p.acc.pose = [x, y, z, yaw];
            p.moved = true;
            p.dirty = true;
        }
    }

    // ---------- 경제 ----------

    fn on_purchase(&mut self, conn: u32, lines: &[OrderLine]) {
        let priced = price_order(&self.catalog, lines);
        let shipping = priced.as_ref().map(|(_, s)| self.catalog.shipping_for(*s)).unwrap_or(0);
        let pack: HashMap<String, (bool, i64)> =
            lines.iter().filter_map(|l| self.catalog.items.get(&l.sku).map(|i| (l.sku.clone(), (i.physical, i.pack_qty)))).collect();
        let Some(p) = self.player(conn) else { return };
        let fail = |msg: String, balance: i64| {
            Writer::new(op::PURCHASE_RESULT).u8(0).str(&msg).u32(0).i64(balance).u16(0).finish()
        };
        let (units, subtotal) = match priced {
            Ok(v) => v,
            Err(e) => {
                let m = fail(e, p.acc.balance_krw);
                return self.reply(conn, m);
            }
        };
        let total = subtotal + shipping;
        if total > p.acc.balance_krw {
            let m = fail(format!("잔액이 부족합니다. (필요 {}, 잔액 {})", won(total), won(p.acc.balance_krw)), p.acc.balance_krw);
            return self.reply(conn, m);
        }
        p.acc.balance_krw -= total;
        for l in lines {
            let (physical, pack_qty) = pack[&l.sku];
            if !physical {
                *p.acc.stock.entry(l.sku.clone()).or_insert(0) += pack_qty * l.qty as i64;
            }
        }
        let mut w = Writer::new(op::PURCHASE_RESULT)
            .u8(1)
            .str(&format!("결제 완료 {}. 잔액 {}", won(total), won(p.acc.balance_krw)))
            .u32(total as u32)
            .i64(p.acc.balance_krw)
            .u16(lines.len() as u16);
        for (l, unit) in lines.iter().zip(units) {
            w = w.str(&l.sku).u16(l.qty).u32(l.length_mm).u32(l.width_mm).u32(unit as u32);
        }
        let receipt = w.finish();
        let profile = msg_profile(&p.acc);
        let acc = p.acc.clone();
        self.store.save(&acc);
        println!("[shop] {} 주문 {} (잔액 {})", mask_phone(&acc.phone), won(total), won(acc.balance_krw));
        self.reply(conn, receipt);
        self.reply(conn, profile);
    }

    /// consume: 재고 → 설치, refund: 설치 → 재고 (설치된 만큼만)
    fn on_stock_move(&mut self, conn: u32, sku: &str, qty: i64, consume: bool) {
        if qty <= 0 || !self.catalog.items.get(sku).map_or(false, |i| !i.physical) {
            return self.error(conn, "체결 소모품이 아닙니다.");
        }
        let Some(p) = self.player(conn) else { return };
        let (from, to) = if consume { (&mut p.acc.stock, &mut p.acc.installed) } else { (&mut p.acc.installed, &mut p.acc.stock) };
        let have = *from.get(sku).unwrap_or(&0);
        if have < qty {
            let why = if consume { "재고가 부족합니다." } else { "설치된 수량보다 많이 회수할 수 없습니다." };
            return self.error(conn, why);
        }
        from.insert(sku.to_string(), have - qty);
        *to.entry(sku.to_string()).or_insert(0) += qty;
        let profile = msg_profile(&p.acc);
        let acc = p.acc.clone();
        self.store.save(&acc);
        self.reply(conn, profile);
    }

    // ---------- 창고 ----------

    fn on_stow(&mut self, conn: u32, name: &str, json_text: &str) {
        let Some(token) = self.player(conn).map(|p| p.acc.token.clone()) else { return };
        if json_text.len() > MAX_DEVICE_BYTES {
            return self.error(conn, "기기 문서가 너무 큽니다.");
        }
        if self.store.list_devices(&token).len() >= MAX_DEVICES {
            return self.error(conn, "창고가 가득 찼습니다.");
        }
        let parts = match json::parse(json_text) {
            Ok(doc) => doc.get("parts").and_then(|p| p.arr()).map(|a| a.len()),
            Err(_) => None,
        };
        if parts.unwrap_or(0) == 0 {
            return self.error(conn, "올바른 기기 문서가 아닙니다.");
        }
        match self.store.put_device(&token, name, json_text) {
            Ok(saved) => {
                self.reply(conn, msg_text(MSG_TOAST, &format!("창고에 넣었습니다: {saved} (부품 {}개)", parts.unwrap())));
                self.send_device_list(conn);
            }
            Err(e) => self.error(conn, &e.to_string()),
        }
    }

    fn on_retrieve(&mut self, conn: u32, name: &str) {
        let Some(token) = self.player(conn).map(|p| p.acc.token.clone()) else { return };
        match self.store.take_device(&token, name) {
            Ok(json_text) => {
                self.reply(conn, Writer::new(op::DEVICE_DATA).str(name).blob(&json_text).finish());
                self.send_device_list(conn);
            }
            Err(_) => self.error(conn, "창고에 그런 기기가 없습니다."),
        }
    }

    fn send_device_list(&mut self, conn: u32) {
        let Some(token) = self.player(conn).map(|p| p.acc.token.clone()) else { return };
        let list = self.store.list_devices(&token);
        let mut w = Writer::new(op::DEVICE_LIST).u16(list.len() as u16);
        for (name, text) in &list {
            let parts = json::parse(text).ok().and_then(|d| d.get("parts").and_then(|p| p.arr()).map(|a| a.len())).unwrap_or(0);
            w = w.str(name).u16(parts as u16).u32(text.len() as u32);
        }
        self.reply(conn, w.finish());
    }

    pub fn online_count(&self) -> usize {
        self.sessions.values().filter(|s| s.player.is_some()).count()
    }

    pub fn account_count(&self) -> usize {
        self.store.account_count()
    }

    /// 위치가 바뀐 계정만 주기적으로 저장
    pub fn save_dirty(&mut self) {
        let mut accs = Vec::new();
        for s in self.sessions.values_mut() {
            if let Some(p) = s.player.as_mut() {
                if p.dirty {
                    p.dirty = false;
                    accs.push(p.acc.clone());
                }
            }
        }
        for a in accs {
            self.store.save(&a);
        }
    }
}

fn random_token(rng: &mut Rng) -> String {
    let mut bytes = [0u8; 16];
    let os = File::open("/dev/urandom").and_then(|mut f| f.read_exact(&mut bytes));
    if os.is_err() {
        for b in bytes.iter_mut() {
            *b = rng.next() as u8;
        }
    }
    bytes.iter().map(|b| format!("{b:02x}")).collect()
}

/// xorshift64* — 인증 코드용 난수 (운영 시 OS 난수로 교체)
pub struct Rng(u64);

impl Rng {
    pub fn seeded() -> Self {
        let t = SystemTime::now().duration_since(UNIX_EPOCH).map(|d| d.as_nanos() as u64).unwrap_or(1);
        Rng(t | 1)
    }
    pub fn next(&mut self) -> u64 {
        self.0 ^= self.0 >> 12;
        self.0 ^= self.0 << 25;
        self.0 ^= self.0 >> 27;
        self.0.wrapping_mul(0x2545F4914F6CDD1D)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn catalog() -> Catalog {
        Catalog::load(concat!(env!("CARGO_MANIFEST_DIR"), "/catalog.json")).unwrap()
    }

    fn line(sku: &str, qty: u16, l: u32, w: u32) -> OrderLine {
        OrderLine { sku: sku.into(), qty, length_mm: l, width_mm: w }
    }

    #[test]
    fn prices_order_like_csharp_quote() {
        // 모터 1 + 65 mm 바퀴 1 + M8 볼트 1묶음 = 16 000 + 3 500 + 3 000 (+ 배송 3 500)
        let c = catalog();
        let (units, subtotal) = price_order(&c, &[line("MT-JGB37-100", 1, 0, 0), line("WH-65-D6", 1, 0, 0), line("FS-BOLT-M8", 1, 0, 0)]).unwrap();
        assert_eq!(units, vec![16000, 3500, 3000]);
        assert_eq!(subtotal + c.shipping_for(subtotal), 26000);
        assert_eq!(c.shipping_for(85000), 0);
    }

    #[test]
    fn rejects_bad_orders() {
        let c = catalog();
        assert!(price_order(&c, &[]).is_err());
        assert!(price_order(&c, &[line("NOPE", 1, 0, 0)]).is_err());
        assert!(price_order(&c, &[line("AL-2020", 1, 10, 0)]).is_err());
        assert!(price_order(&c, &[line("MT-JGB37-100", 0, 0, 0)]).is_err());
        assert!(price_order(&c, &[line("MT-JGB37-100", 1, 300, 0)]).is_err());
    }

    #[test]
    fn formats_won() {
        assert_eq!(won(425000), "₩425,000");
        assert_eq!(won(999), "₩999");
        assert_eq!(won(1_000_000), "₩1,000,000");
    }

    #[test]
    fn phone_normalization() {
        assert_eq!(normalize_phone("010-1234-5678").as_deref(), Some("01012345678"));
        assert!(normalize_phone("12345").is_none());
        assert_eq!(mask_phone("01012345678"), "010-****-5678");
    }
}
