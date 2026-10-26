//! Dolly Forge 월드 서버 진입점.
//! 접속마다 읽기/쓰기 스레드를 두고, 게임 상태는 단일 게임 루프 스레드가 소유한다.

mod data;
mod game;
mod protocol;
mod store;
mod ws;

use game::{Event, Game};
use std::env;
use std::io::BufReader;
use std::net::{TcpListener, TcpStream};
use std::sync::mpsc::{self, RecvTimeoutError, Sender};
use std::thread;
use std::time::{Duration, Instant};
use ws::WsMessage;

const MAX_MSGS_PER_SEC: u32 = 120;

fn main() {
    let addr = env::var("DOLLY_ADDR").unwrap_or_else(|_| "127.0.0.1:7700".into());
    let data_dir = env::var("DOLLY_DATA").unwrap_or_else(|_| "data/accounts".into());
    let dev_echo = env::var("DOLLY_DEV_ECHO_CODE").map_or(false, |v| v == "1");

    let store = store::Store::open(&data_dir).expect("계정 저장소를 열 수 없습니다");
    let listener = TcpListener::bind(&addr).unwrap_or_else(|e| panic!("{addr} 바인드 실패: {e}"));
    let (tx, rx) = mpsc::channel::<Event>();

    let mut game = Game::new(store, dev_echo);
    println!(
        "[server] Dolly Forge 월드 서버 ws://{addr}  (계정 {}개, 데이터 {data_dir}{})",
        game.account_count(),
        if dev_echo { ", 개발 모드: 인증코드 클라이언트에 표시" } else { "" }
    );

    thread::spawn(move || {
        let mut next_id = 1u32;
        for stream in listener.incoming().flatten() {
            let conn = next_id;
            next_id += 1;
            let tx = tx.clone();
            thread::spawn(move || handle_connection(conn, stream, tx));
        }
    });

    let tick = Duration::from_secs_f32(1.0 / data::TICK_HZ as f32);
    let mut next_tick = Instant::now() + tick;
    let mut last_report = Instant::now();
    loop {
        let now = Instant::now();
        if now >= next_tick {
            game.tick(tick.as_secs_f32());
            next_tick += tick;
            if next_tick < now {
                next_tick = now + tick;
            }
            if last_report.elapsed() > Duration::from_secs(60) {
                last_report = Instant::now();
                game.save_all();
                println!("[server] 접속 {}명", game.online_count());
            }
            continue;
        }
        match rx.recv_timeout(next_tick - now) {
            Ok(ev) => game.handle(ev),
            Err(RecvTimeoutError::Timeout) => {}
            Err(RecvTimeoutError::Disconnected) => break,
        }
    }
}

fn handle_connection(conn: u32, mut stream: TcpStream, tx: Sender<Event>) {
    let _ = stream.set_nodelay(true);
    let Ok(read_half) = stream.try_clone() else { return };
    let mut reader = BufReader::new(read_half);
    if ws::handshake(&mut reader, &mut stream).is_err() {
        return;
    }
    let (out_tx, out_rx) = mpsc::channel::<Vec<u8>>();
    let Ok(mut writer) = stream.try_clone() else { return };
    thread::spawn(move || {
        for frame in out_rx {
            if ws::write_all(&mut writer, &frame).is_err() {
                break;
            }
        }
    });
    let Ok(game_handle) = stream.try_clone() else { return };
    if tx.send(Event::Connected { conn, out: out_tx.clone(), stream: game_handle }).is_err() {
        return;
    }

    let mut window_start = Instant::now();
    let mut count = 0u32;
    loop {
        let msg = match ws::read_message(&mut reader) {
            Ok(m) => m,
            Err(_) => break,
        };
        if window_start.elapsed() > Duration::from_secs(1) {
            window_start = Instant::now();
            count = 0;
        }
        count += 1;
        if count > MAX_MSGS_PER_SEC {
            eprintln!("[net] #{conn} 메시지 과다 — 연결 종료");
            break;
        }
        match msg {
            WsMessage::Binary(payload) => match protocol::decode(&payload) {
                Ok(msg) => {
                    if tx.send(Event::Message { conn, msg }).is_err() {
                        break;
                    }
                }
                Err(e) => {
                    eprintln!("[net] #{conn} 잘못된 메시지: {e}");
                    break;
                }
            },
            WsMessage::Ping(p) => {
                let _ = out_tx.send(ws::frame(0xA, &p));
            }
            WsMessage::Text => {}
            WsMessage::Close => {
                let _ = out_tx.send(ws::frame(0x8, &[]));
                break;
            }
        }
    }
    let _ = tx.send(Event::Disconnected { conn });
}
