//! Dolly Forge 세계 서버 (0.12 · M2): 지형 편집 · 존중 규칙 · Rapier 실물리 · 섹션 저장의 권한을 가진다.
//! C# 엔진이 TCP(127.0.0.1:7701)로 한 줄 JSON 을 주고받는다:
//!   엔진 → 서버  hello{player, ground, trees} · dig{p, n} · pour{p} · pickup{p} · drop{p, v}
//!   서버 → 엔진  welcome{edits, bodies, held} · edit{edit} · deny{msg} · body_add · body_del · bodies{list} · held{item}
//! 물리는 60 Hz, 움직이는 물건의 자세는 20 Hz 로 보낸다. (계정·경제는 server/ 의 계정 서버가 맡는다)

mod content;
mod world;
mod zones;

use serde_json::{json, Value};
use std::collections::HashMap;
use std::io::{BufRead, BufReader, Write};
use std::net::{TcpListener, TcpStream};
use std::sync::mpsc;
use std::thread;
use std::time::{Duration, Instant};

enum Event {
    Join(u32, TcpStream),
    Msg(u32, Value),
    Leave(u32),
}

fn main() {
    let addr = std::env::var("DOLLY_WORLD_ADDR").unwrap_or_else(|_| "127.0.0.1:7701".into());
    let data = std::env::var("DOLLY_WORLD_DATA").map(Into::into).unwrap_or_else(|_| std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("data"));
    let pack = content::Pack::load(&content::default_dir()).unwrap_or_else(|e| panic!("콘텐츠 팩을 읽을 수 없습니다: {e}"));
    let mut w = world::World::open(pack, data.clone());
    println!("[world] Dolly Forge 세계 서버 tcp://{addr}  (편집 {}개, 저장 {})", w.edit_count(), data.display());

    let listener = TcpListener::bind(&addr).unwrap_or_else(|e| panic!("{addr} 바인드 실패: {e}"));
    let (tx, rx) = mpsc::channel::<Event>();
    thread::spawn(move || {
        let mut next = 1u32;
        for s in listener.incoming().flatten() {
            let id = next;
            next += 1;
            let _ = s.set_nodelay(true);
            let Ok(reader) = s.try_clone() else { continue };
            let _ = tx.send(Event::Join(id, s));
            let tx = tx.clone();
            thread::spawn(move || {
                for line in BufReader::new(reader).lines() {
                    let Ok(line) = line else { break };
                    match serde_json::from_str::<Value>(&line) {
                        Ok(v) => {
                            if tx.send(Event::Msg(id, v)).is_err() {
                                return;
                            }
                        }
                        Err(e) => eprintln!("[world] 접속 {id}: JSON 오류 {e}"),
                    }
                }
                let _ = tx.send(Event::Leave(id));
            });
        }
    });

    let mut conns: HashMap<u32, (TcpStream, String)> = HashMap::new();
    let send = |conns: &mut HashMap<u32, (TcpStream, String)>, to: Option<u32>, v: &Value| {
        let mut line = v.to_string();
        line.push('\n');
        conns.retain(|id, (s, _)| to.is_some_and(|t| t != *id) || s.write_all(line.as_bytes()).is_ok());
    };

    let dt = Duration::from_secs_f32(1.0 / 60.0);
    let mut next = Instant::now();
    let mut tick = 0u64;
    loop {
        // 요청 처리 (다음 물리 단계까지)
        while let Ok(ev) = rx.recv_timeout(next.saturating_duration_since(Instant::now())) {
            match ev {
                Event::Join(id, s) => {
                    println!("[world] 접속 {id}");
                    conns.insert(id, (s, String::new()));
                }
                Event::Leave(id) => {
                    println!("[world] 접속 {id} 끊김");
                    conns.remove(&id);
                    w.save_bodies();
                }
                Event::Msg(id, m) => {
                    if m["t"] == "hello" {
                        let player = m["player"].as_str().unwrap_or("local").to_string();
                        if let Some(c) = conns.get_mut(&id) {
                            c.1 = player.clone();
                        }
                        w.set_ground(&m["ground"], &m["trees"]);
                        let welcome = w.welcome(&player);
                        send(&mut conns, Some(id), &welcome);
                        continue;
                    }
                    let player = conns.get(&id).map(|c| c.1.clone()).unwrap_or_else(|| "local".into());
                    for out in w.request(&player, &m) {
                        // 거절과 들기 상태는 요청한 이에게, 세계의 변화는 모두에게
                        let only = matches!(out["t"].as_str(), Some("deny" | "held"));
                        send(&mut conns, if only { Some(id) } else { None }, &out);
                    }
                }
            }
        }
        next += dt;
        if next < Instant::now() {
            next = Instant::now() + dt;
        }
        w.step();
        tick += 1;
        if tick % 3 == 0 {
            let list = w.moving();
            if !list.is_empty() {
                send(&mut conns, None, &json!({"t": "bodies", "list": list}));
            }
        }
        if tick % 120 == 0 {
            w.save_bodies();
        }
    }
}
