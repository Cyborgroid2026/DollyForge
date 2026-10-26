//! 서버 권위 게임 상태. 단일 게임 루프 스레드가 소유한다.
//! 마을 시설과 자원 노드는 모든 플레이어가 공유하고, 일상·연구·외출 진행과
//! 하람의 나이(개인 시간선)는 플레이어별로 따로 흐른다.

use crate::data::*;
use crate::protocol::{op, Writer, C2S};
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
const MSG_DIALOGUE: u8 = 1;
const MSG_SYSTEM: u8 = 2;

const CODE_TTL: Duration = Duration::from_secs(300);
const CODE_MAX_ATTEMPTS: u8 = 5;
const CRYO_CONFIRM: Duration = Duration::from_secs(5);

struct OnlinePlayer {
    acc: Account,
    x: f32,
    y: f32,
    z: f32,
    yaw: f32,
    last_move: Instant,
    moved: bool,
    dirty: bool,
    cryo_armed: Option<Instant>,
}

struct Session {
    out: Sender<Vec<u8>>,
    stream: TcpStream,
    player: Option<OnlinePlayer>,
}

impl Session {
    fn send(&self, payload: &[u8]) {
        let _ = self.out.send(ws::binary(payload));
    }
}

struct Node {
    kind: u8,
    x: f32,
    z: f32,
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
    nodes: HashMap<u32, Node>,
    next_node: u32,
    node_timer: f32,
    codes: HashMap<String, PendingCode>,
    rng: Rng,
    dev_echo_code: bool,
    outbox: Vec<(Dest, Vec<u8>)>,
}

// ---------- 메시지 생성 ----------

fn msg_text(kind: u8, text: &str) -> Vec<u8> {
    Writer::new(op::MESSAGE).u8(kind).str(text).finish()
}

fn msg_error(text: &str) -> Vec<u8> {
    Writer::new(op::ERROR).str(text).finish()
}

fn msg_state(p: &OnlinePlayer) -> Vec<u8> {
    let a = &p.acc;
    let mut w = Writer::new(op::PLAYER_STATE)
        .u16(a.day)
        .u16(a.years)
        .u8(a.phase as u8)
        .u8(a.tasks)
        .f32(a.battery)
        .f32(a.bio)
        .u16(a.memory);
    for n in a.inv {
        w = w.u16(n);
    }
    w.u8(a.collected).u8(a.research).u8(a.haram_age).u8(a.haram_alive as u8).u8(a.gender).finish()
}

fn msg_teleport(x: f32, y: f32, z: f32, yaw: f32) -> Vec<u8> {
    Writer::new(op::TELEPORT).f32(x).f32(y).f32(z).f32(yaw).finish()
}

fn msg_joined(id: u32, p: &OnlinePlayer) -> Vec<u8> {
    Writer::new(op::PLAYER_JOINED).u32(id).u8(p.acc.gender).f32(p.x).f32(p.y).f32(p.z).f32(p.yaw).finish()
}

fn msg_moved(id: u32, p: &OnlinePlayer) -> Vec<u8> {
    Writer::new(op::PLAYER_MOVED).u32(id).f32(p.x).f32(p.y).f32(p.z).f32(p.yaw).finish()
}

fn msg_node_spawn(id: u32, n: &Node) -> Vec<u8> {
    Writer::new(op::NODE_SPAWN).u32(id).u8(n.kind).f32(n.x).f32(n.z).finish()
}

// ---------- 규칙 도우미 ----------

fn has_task(a: &Account, bit: u8) -> bool {
    a.tasks & (1 << bit) != 0
}

/// 처음 완료했으면 true
fn complete_task(a: &mut Account, bit: u8) -> bool {
    let fresh = !has_task(a, bit);
    a.tasks |= 1 << bit;
    fresh
}

fn gate_open(phase: Phase) -> bool {
    phase == Phase::Outing
}

fn inside_fence(x: f32, z: f32) -> bool {
    x.hypot(z) < FENCE_RADIUS
}

fn crosses_fence(ox: f32, oz: f32, x: f32, z: f32, open: bool) -> bool {
    if inside_fence(ox, oz) == inside_fence(x, z) {
        return false;
    }
    !(open && x.abs() < GATE_HALF_WIDTH && z < 0.0)
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

fn ending_text(a: &Account, eternal: bool) -> (String, String) {
    let memory_line = format!("\n\n기억 조각 {}개 · 경과한 세월 {}년 · {}일째의 선택.", a.memory, a.years, a.day);
    if eternal {
        let haram = if a.haram_alive {
            "하람은 몇 해 뒤 세상을 떠났다. 당신은 그 장례식에서도, 그 후 백 년이 지나서도 같은 모습이었다."
        } else {
            "하람의 편지는 당신의 기억 회로에 영원히 새겨졌다. 지워지지도, 바래지도 않는다."
        };
        (
            "영원한 티타늄".into(),
            format!("마지막 유기 조직이 티타늄으로 대체되었다. 더 이상 동면은 필요 없다.\n{haram}{memory_line}"),
        )
    } else {
        let haram = if a.haram_alive {
            "그날 저녁, 하람과 함께 노을을 보았다. 끝이 있기에 오늘이 선명했다."
        } else {
            "하람의 묘 옆에, 언젠가 당신의 자리도 생길 것이다. 그 사실이 이상하게 따뜻했다."
        };
        (
            "유한한 아침".into(),
            format!("당신은 영생 프로토콜을 봉인했다. 생체 조직은 언젠가 멈출 것이다.\n{haram}{memory_line}"),
        )
    }
}

impl Game {
    pub fn new(store: Store, dev_echo_code: bool) -> Self {
        Game {
            sessions: HashMap::new(),
            store,
            nodes: HashMap::new(),
            next_node: NODE_ID_BASE,
            node_timer: 0.0,
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

    pub fn tick(&mut self, dt: f32) {
        // 자원 노드 리스폰
        self.node_timer -= dt;
        if self.nodes.len() < NODE_TARGET && self.node_timer <= 0.0 {
            self.node_timer = NODE_RESPAWN_SECS;
            self.spawn_node();
        }
        // 이동 브로드캐스트 + 변경된 상태 전송
        for (&id, s) in self.sessions.iter_mut() {
            let Some(p) = s.player.as_mut() else { continue };
            if p.moved {
                p.moved = false;
                self.outbox.push((Dest::Others { except: id, near: Some((p.x, p.z)) }, msg_moved(id, p)));
            }
            if p.dirty {
                p.dirty = false;
                self.outbox.push((Dest::One(id), msg_state(p)));
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
                            if (p.x - x).hypot(p.z - z) > AOI_RADIUS {
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

    fn toast(&mut self, conn: u32, text: &str) {
        self.reply(conn, msg_text(MSG_TOAST, text));
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
            _ if !logged_in => self.reply(conn, msg_error("로그인이 필요합니다.")),
            C2S::Move { x, y, z, yaw } => self.on_move(conn, x, y, z, yaw),
            C2S::Interact { target } => self.on_interact(conn, target),
            C2S::Chat { text } => {
                let text: String = text.trim().chars().take(120).collect();
                if !text.is_empty() {
                    self.outbox.push((Dest::All, Writer::new(op::CHAT_BROADCAST).u32(conn).str(&text).finish()));
                }
            }
            C2S::Restart => self.on_restart(conn),
        }
    }

    // ---------- QR 계정 / 인증 ----------

    fn on_register(&mut self, conn: u32, phone: &str, gender: u8) {
        let Some(phone) = normalize_phone(phone) else {
            return self.reply(conn, msg_error("전화번호 형식이 올바르지 않습니다. (예: 010-1234-5678)"));
        };
        let token = match self.store.token_for_phone(&phone) {
            Some(t) => t.clone(),
            None => {
                let token = random_token(&mut self.rng);
                self.store.save(&Account::new(token.clone(), phone.clone(), gender.min(1)));
                println!("[auth] 새 QR 계정 {}", mask_phone(&phone));
                token
            }
        };
        self.reply(conn, Writer::new(op::REGISTERED).str(&token).finish());
    }

    fn on_request_code(&mut self, conn: u32, token: &str) {
        let Some(acc) = self.store.load(token) else {
            return self.reply(conn, msg_error("등록되지 않은 QR 계정입니다."));
        };
        let cooldown = if self.dev_echo_code { Duration::from_secs(1) } else { Duration::from_secs(30) };
        if let Some(prev) = self.codes.get(token) {
            if prev.sent_at.elapsed() < cooldown {
                return self.reply(conn, msg_error("잠시 후 다시 요청해 주세요."));
            }
        }
        let code = format!("{:06}", self.rng.next() % 1_000_000);
        // 첫 버전: SMS 게이트웨이 대신 서버 콘솔에 출력한다.
        println!("[SMS → {}] [Dolly Forge] 인증코드 {code}", mask_phone(&acc.phone));
        let now = Instant::now();
        self.codes.insert(
            token.to_string(),
            PendingCode { code: code.clone(), expires: now + CODE_TTL, attempts: 0, sent_at: now },
        );
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
            return self.reply(conn, msg_error("인증 코드가 틀렸거나 만료되었습니다."));
        }
        self.codes.remove(token);

        // 같은 계정의 기존 접속은 끊는다.
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
            return self.reply(conn, msg_error("계정을 불러오지 못했습니다."));
        };
        let (x, y, z) = SPAWN;
        let player = OnlinePlayer {
            acc,
            x,
            y,
            z,
            yaw: SPAWN_YAW,
            last_move: Instant::now(),
            moved: false,
            dirty: false,
            cryo_armed: None,
        };
        println!("[game] 입장 #{conn} ({})", mask_phone(&player.acc.phone));

        self.reply(conn, Writer::new(op::LOGIN_OK).u32(conn).u8(player.acc.gender).finish());
        self.reply(conn, msg_state(&player));
        self.reply(conn, msg_teleport(x, y, z, SPAWN_YAW));
        let others: Vec<Vec<u8>> = self
            .sessions
            .iter()
            .filter_map(|(&id, s)| s.player.as_ref().map(|p| msg_joined(id, p)))
            .collect();
        for m in others {
            self.reply(conn, m);
        }
        let nodes: Vec<Vec<u8>> = self.nodes.iter().map(|(&id, n)| msg_node_spawn(id, n)).collect();
        for m in nodes {
            self.reply(conn, m);
        }
        self.outbox.push((Dest::Others { except: conn, near: None }, msg_joined(conn, &player)));
        let greeting = format!("Day {} — {}", player.acc.day, phase_hint(player.acc.phase));
        self.reply(conn, msg_text(MSG_SYSTEM, &greeting));
        if let Some(s) = self.sessions.get_mut(&conn) {
            s.player = Some(player);
        }
    }

    // ---------- 이동 ----------

    fn on_move(&mut self, conn: u32, x: f32, y: f32, z: f32, yaw: f32) {
        let Some(p) = self.sessions.get_mut(&conn).and_then(|s| s.player.as_mut()) else { return };
        if matches!(p.acc.phase, Phase::Finished) {
            return;
        }
        let now = Instant::now();
        let dt = now.duration_since(p.last_move).as_secs_f32().min(1.0);
        p.last_move = now;
        let dist = (x - p.x).hypot(z - p.z);
        let invalid = dist > MAX_SPEED * dt.max(0.05) + 0.5
            || x.hypot(z) > WORLD_RADIUS + 0.5
            || y.abs() > 5.0
            || crosses_fence(p.x, p.z, x, z, gate_open(p.acc.phase));
        if invalid {
            let tp = msg_teleport(p.x, p.y, p.z, p.yaw);
            return self.reply(conn, tp);
        }
        let rate = if inside_fence(x, z) { BATTERY_PER_M_INSIDE } else { BATTERY_PER_M_OUTSIDE };
        let rate = if p.acc.research >= P_SPRING { rate * 0.5 } else { rate };
        p.x = x;
        p.y = y;
        p.z = z;
        p.yaw = yaw;
        p.moved = true;
        if dist > 0.001 {
            p.acc.battery = (p.acc.battery - dist * rate).max(0.0);
            p.dirty = true;
        }
        if p.acc.battery <= 0.0 {
            for i in 0..4 {
                p.acc.inv[i] = p.acc.inv[i].saturating_sub(p.acc.today[i]);
            }
            p.acc.battery = 100.0;
            self.outbox.push((
                Dest::One(conn),
                msg_text(MSG_SYSTEM, "배터리 방전! 비상 귀환 프로토콜 가동 — 오늘 수집한 자원을 잃었습니다."),
            ));
            self.end_day(conn);
        }
    }

    // ---------- 하루 진행 ----------

    fn end_day(&mut self, conn: u32) {
        let Some(p) = self.sessions.get_mut(&conn).and_then(|s| s.player.as_mut()) else { return };
        let decay = if p.acc.research >= P_CULTURE { BIO_DECAY_CULTURE } else { BIO_DECAY };
        p.acc.bio -= decay;
        if p.acc.bio <= 0.0 {
            self.outbox.push((
                Dest::One(conn),
                msg_text(MSG_SYSTEM, "생체 안정도 0% — 강제 인공 동면이 시작됩니다."),
            ));
            return self.cryo(conn);
        }
        let a = &mut p.acc;
        a.day += 1;
        let mut text = format!("Day {} 아침. 생체 안정도 {:.0}%.", a.day, a.bio);
        if a.bio < 30.0 {
            text.push_str(" 위험 — 집의 인공 동면 포드 사용을 고려하세요.");
        }
        self.start_morning(conn, &text);
    }

    fn cryo(&mut self, conn: u32) {
        let Some(p) = self.sessions.get_mut(&conn).and_then(|s| s.player.as_mut()) else { return };
        let a = &mut p.acc;
        a.years += CRYO_YEARS;
        a.cryo_count += 1;
        a.haram_age = a.haram_age.saturating_add(CRYO_YEARS as u8);
        if a.haram_alive && a.haram_age >= HARAM_DEATH_AGE {
            a.haram_alive = false;
        }
        a.bio = 100.0;
        a.battery = 100.0;
        a.day += 1;
        let text = format!(
            "인공 동면 종료. {}년이 흘렀습니다 (누적 {}년). 바깥의 나무가 훌쩍 자랐습니다.",
            CRYO_YEARS, a.years
        );
        self.start_morning(conn, &text);
    }

    fn start_morning(&mut self, conn: u32, text: &str) {
        let Some(p) = self.sessions.get_mut(&conn).and_then(|s| s.player.as_mut()) else { return };
        let a = &mut p.acc;
        a.phase = Phase::Daily;
        a.tasks = 0;
        a.collected = 0;
        a.today = [0; 4];
        let (x, y, z) = SPAWN;
        p.x = x;
        p.y = y;
        p.z = z;
        p.yaw = SPAWN_YAW;
        p.moved = true;
        p.dirty = false;
        p.cryo_armed = None;
        let state = msg_state(p);
        self.store.save(&p.acc);
        self.reply(conn, msg_teleport(x, y, z, SPAWN_YAW));
        self.reply(conn, state);
        self.reply(conn, msg_text(MSG_SYSTEM, text));
    }

    fn check_phase_done(&mut self, conn: u32) {
        let Some(p) = self.sessions.get_mut(&conn).and_then(|s| s.player.as_mut()) else { return };
        let a = &mut p.acc;
        let full = (1u8 << a.phase.task_count()) - 1;
        if a.tasks != full {
            return;
        }
        let text = match a.phase {
            Phase::Daily => {
                a.phase = Phase::Research;
                "일상 과제를 모두 마쳤습니다. 연구소로 이동하세요."
            }
            Phase::Research => {
                a.phase = Phase::Outing;
                "연구 시간이 끝났습니다. 울타리 남쪽 문이 열렸습니다 — 외출하세요."
            }
            _ => return,
        };
        a.tasks = 0;
        self.store.save(&p.acc);
        self.reply(conn, msg_text(MSG_SYSTEM, text));
    }

    // ---------- 상호작용 ----------

    fn on_interact(&mut self, conn: u32, target: u32) {
        if target >= NODE_ID_BASE {
            self.collect(conn, target);
        } else {
            self.use_station(conn, target);
        }
        if let Some(p) = self.sessions.get_mut(&conn).and_then(|s| s.player.as_mut()) {
            p.dirty = true;
        }
        self.check_phase_done(conn);
    }

    fn collect(&mut self, conn: u32, id: u32) {
        let Some(p) = self.sessions.get_mut(&conn).and_then(|s| s.player.as_mut()) else { return };
        let Some(node) = self.nodes.get(&id) else { return };
        if (node.x - p.x).hypot(node.z - p.z) > INTERACT_RANGE {
            return self.toast(conn, "너무 멉니다.");
        }
        if p.acc.phase != Phase::Outing {
            return self.toast(conn, "지금은 외출 시간이 아닙니다.");
        }
        let kind = node.kind as usize;
        self.nodes.remove(&id);
        let a = &mut p.acc;
        a.inv[kind] += 1;
        a.today[kind] += 1;
        a.collected = a.collected.saturating_add(1);
        if a.collected >= COLLECT_GOAL {
            complete_task(a, task::COLLECT);
        }
        let text = format!("+1 {} ({}/{})", RESOURCE_NAMES[kind], a.collected.min(COLLECT_GOAL), COLLECT_GOAL);
        self.outbox.push((Dest::All, Writer::new(op::NODE_REMOVE).u32(id).finish()));
        self.toast(conn, &text);
    }

    fn use_station(&mut self, conn: u32, target: u32) {
        let Some(s) = self.sessions.get_mut(&conn) else { return };
        let Some(p) = s.player.as_mut() else { return };
        let Some((sx, sz)) = station_pos(target) else { return };
        if (sx - p.x).hypot(sz - p.z) > INTERACT_RANGE {
            return self.toast(conn, "너무 멉니다.");
        }
        use target::*;
        let phase = p.acc.phase;
        if target != CRYO_POD {
            p.cryo_armed = None;
        }
        let a = &mut p.acc;
        let text: String = match target {
            CHARGE_DOCK => {
                a.battery = 100.0;
                if phase == Phase::Daily {
                    complete_task(a, task::CHARGE);
                }
                "충전 완료. 배터리 100%".into()
            }
            FOOD_TABLE | OIL_CAN | JOURNAL if phase != Phase::Daily => "지금은 일상 시간이 아닙니다.".into(),
            FOOD_TABLE => {
                if complete_task(a, task::EAT) {
                    a.bio = (a.bio + BIO_MEAL).min(100.0);
                    format!("생체 조직에 영양을 공급했습니다. 생체 안정도 {:.0}%", a.bio)
                } else {
                    "이미 식사를 했습니다.".into()
                }
            }
            OIL_CAN => {
                if complete_task(a, task::OIL) {
                    "관절에 윤활유를 보충했습니다. 움직임이 부드럽습니다.".into()
                } else {
                    "이미 윤활유를 보충했습니다.".into()
                }
            }
            JOURNAL => {
                if complete_task(a, task::JOURNAL) {
                    a.memory += 1;
                    format!("기억 일지를 기록했습니다. 기억 조각 {}개", a.memory)
                } else {
                    "오늘의 일지는 이미 썼습니다.".into()
                }
            }
            BED => match phase {
                Phase::Outing if has_task(a, task::COLLECT) && has_task(a, task::VISIT) => {
                    complete_task(a, task::SLEEP);
                    return self.end_day(conn);
                }
                Phase::Outing => "자원 3개 수집과 하람 방문을 먼저 마치세요.".into(),
                _ => "아직 잘 시간이 아닙니다.".into(),
            },
            CRYO_POD => {
                if phase != Phase::Daily {
                    "동면 포드는 아침(일상 시간)에만 사용할 수 있습니다.".into()
                } else if p.cryo_armed.map_or(false, |t| t.elapsed() < CRYO_CONFIRM) {
                    return self.cryo(conn);
                } else {
                    p.cryo_armed = Some(Instant::now());
                    format!(
                        "인공 동면 포드: 생체 안정도가 100%로 회복되지만 {}년이 흐릅니다. 5초 안에 다시 사용하면 동면에 들어갑니다.",
                        CRYO_YEARS
                    )
                }
            }
            GOODS_SHELF => GOODS_TEXT.into(),
            BLACKBOARD | ANALYZER | WORKBENCH if phase != Phase::Research => "지금은 연구 시간이 아닙니다.".into(),
            BLACKBOARD => {
                complete_task(a, task::BOARD);
                match PROJECTS.get(a.research as usize) {
                    Some(proj) => format!(
                        "다음 연구: {} — 필요 재료: {} (보유: {})",
                        proj.name,
                        cost_text(&proj.cost),
                        cost_text(&a.inv)
                    ),
                    None => "모든 연구를 마쳤습니다.".into(),
                }
            }
            ANALYZER => {
                if !has_task(a, task::BOARD) {
                    "연구 칠판을 먼저 확인하세요.".into()
                } else {
                    complete_task(a, task::ANALYZE);
                    "시료 분석기가 웅웅거리며 돌아갑니다. 진공관이 주황빛으로 달아오릅니다.".into()
                }
            }
            WORKBENCH => {
                if !(has_task(a, task::BOARD) && has_task(a, task::ANALYZE)) {
                    "칠판 확인과 시료 분석을 먼저 하세요.".into()
                } else if has_task(a, task::BENCH) {
                    "오늘 연구는 이미 끝났습니다.".into()
                } else {
                    let idx = a.research as usize;
                    let proj = &PROJECTS[idx.min(PROJECTS.len() - 1)];
                    if a.inv.iter().zip(proj.cost).all(|(&have, need)| have >= need) {
                        for i in 0..4 {
                            a.inv[i] -= proj.cost[i];
                        }
                        a.research += 1;
                        if a.research as usize == PROJECTS.len() {
                            a.phase = Phase::Ending;
                            a.tasks = 0;
                            self.store.save(&p.acc);
                            return self.reply(
                                conn,
                                msg_text(
                                    MSG_SYSTEM,
                                    "영생 프로토콜 완성. 연구소 입구의 두 기둥 중 하나를 선택하세요 — 왼쪽: 영원, 오른쪽: 유한.",
                                ),
                            );
                        }
                        complete_task(a, task::BENCH);
                        format!("연구 완료: {} — {}", proj.name, proj.effect)
                    } else {
                        complete_task(a, task::BENCH);
                        format!(
                            "재료 부족 (필요: {}). 오늘은 자료 정리로 마무리합니다. 외출에서 재료를 모으세요.",
                            cost_text(&proj.cost)
                        )
                    }
                }
            }
            PILLAR_ETERNAL | PILLAR_MORTAL => {
                if phase != Phase::Ending {
                    "차가운 금속 기둥이다. 아직 아무 반응이 없다.".into()
                } else {
                    let eternal = target == PILLAR_ETERNAL;
                    let (title, body) = ending_text(a, eternal);
                    a.phase = Phase::Finished;
                    a.tasks = 1;
                    self.store.save(&p.acc);
                    return self.reply(
                        conn,
                        Writer::new(op::ENDING).u8(if eternal { 0 } else { 1 }).str(&title).str(&body).finish(),
                    );
                }
            }
            HARAM => {
                let (line, kind) = haram_talk(a);
                if phase == Phase::Outing && complete_task(a, task::VISIT) {
                    a.memory += if a.research >= P_RADIO { 2 } else { 1 };
                }
                return self.reply(conn, msg_text(kind, &line));
            }
            _ => return,
        };
        self.toast(conn, &text);
    }

    fn on_restart(&mut self, conn: u32) {
        let Some(p) = self.sessions.get_mut(&conn).and_then(|s| s.player.as_mut()) else { return };
        if p.acc.phase != Phase::Finished {
            return;
        }
        p.acc.reset_progress();
        self.start_morning(conn, "새로운 여정이 시작됩니다. Day 1 아침.");
    }

    fn spawn_node(&mut self) {
        for _ in 0..20 {
            let r = 22.0 + self.rng.unit() * 34.0;
            let t = self.rng.unit() * std::f32::consts::TAU;
            let (x, z) = (r * t.cos(), r * t.sin());
            let (hx, hz) = station_pos(target::HARAM).unwrap();
            if (x - hx).hypot(z - hz) < 8.0 || self.nodes.values().any(|n| (n.x - x).hypot(n.z - z) < 3.0) {
                continue;
            }
            let total: u32 = RESOURCE_WEIGHTS.iter().sum();
            let mut roll = (self.rng.next() % total as u64) as u32;
            let mut kind = 0u8;
            for (i, w) in RESOURCE_WEIGHTS.iter().enumerate() {
                if roll < *w {
                    kind = i as u8;
                    break;
                }
                roll -= w;
            }
            let id = self.next_node;
            self.next_node += 1;
            let node = Node { kind, x, z };
            self.outbox.push((Dest::All, msg_node_spawn(id, &node)));
            self.nodes.insert(id, node);
            return;
        }
    }

    pub fn online_count(&self) -> usize {
        self.sessions.values().filter(|s| s.player.is_some()).count()
    }

    pub fn account_count(&self) -> usize {
        self.store.account_count()
    }

    pub fn save_all(&mut self) {
        for s in self.sessions.values() {
            if let Some(p) = &s.player {
                self.store.save(&p.acc);
            }
        }
    }
}

fn haram_talk(a: &mut Account) -> (String, u8) {
    if !a.haram_alive {
        if !a.letter_read {
            a.letter_read = true;
            a.memory += 3;
            return (format!("하람의 묘비 아래에서 편지를 발견했다.\n{HARAM_LETTER}"), MSG_DIALOGUE);
        }
        return ("하람의 묘비 앞에 들꽃이 피어 있다.".into(), MSG_DIALOGUE);
    }
    let prefix = format!("하람 ({}세): ", a.haram_age);
    let line = if a.cryo_count > a.haram_seen_cryo {
        let slept = (a.cryo_count - a.haram_seen_cryo) * CRYO_YEARS;
        a.haram_seen_cryo = a.cryo_count;
        format!("{slept}년이나 잤구나… 너는 그대로인데, 나만 이렇게 늙었네.")
    } else if a.research >= P_RADIO && a.visits % 2 == 1 {
        HARAM_DEEP_LINES[(a.visits as usize / 2) % HARAM_DEEP_LINES.len()].to_string()
    } else {
        HARAM_LINES[a.visits as usize % HARAM_LINES.len()].to_string()
    };
    a.visits += 1;
    (prefix + &line, MSG_DIALOGUE)
}

fn phase_hint(phase: Phase) -> &'static str {
    match phase {
        Phase::Daily => "일상 시간입니다. 집에서 일상 과제를 하세요.",
        Phase::Research => "연구 시간입니다. 연구소로 이동하세요.",
        Phase::Outing => "외출 시간입니다. 남쪽 문으로 나가세요.",
        Phase::Ending => "연구소의 두 기둥 중 하나를 선택하세요.",
        Phase::Finished => "여정을 마쳤습니다.",
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

/// xorshift64* — 게임플레이용 난수 (보안 용도 아님)
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
    pub fn unit(&mut self) -> f32 {
        (self.next() >> 40) as f32 / (1u64 << 24) as f32
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn fence_blocks_unless_gate_open() {
        assert!(crosses_fence(0.0, -17.0, 0.0, -19.0, false));
        assert!(!crosses_fence(0.0, -17.0, 0.0, -19.0, true));
        assert!(crosses_fence(10.0, -13.0, 10.0, -16.0, true)); // 문이 아닌 곳
        assert!(!crosses_fence(0.0, 0.0, 1.0, 1.0, false));
    }

    #[test]
    fn phone_normalization() {
        assert_eq!(normalize_phone("010-1234-5678").as_deref(), Some("01012345678"));
        assert!(normalize_phone("12345").is_none());
        assert_eq!(mask_phone("01012345678"), "010-****-5678");
    }

    #[test]
    fn haram_reacts_to_cryo() {
        let mut a = Account::new("00".repeat(16), "01000000000".into(), 0);
        a.cryo_count = 1;
        a.haram_age = 82;
        let (line, _) = haram_talk(&mut a);
        assert!(line.contains("10년이나 잤구나"));
        a.haram_alive = false;
        let (line, _) = haram_talk(&mut a);
        assert!(line.contains("편지"));
        assert_eq!(a.memory, 3);
    }
}
