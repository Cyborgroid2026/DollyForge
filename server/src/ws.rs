//! 최소 WebSocket(RFC 6455) 서버 구현. 외부 crate 없이 std 만 사용한다.
//! 조각난(fragmented) 메시지는 지원하지 않는다 — 브라우저는 작은 메시지를 나누지 않는다.

use std::io::{self, BufRead, BufReader, Read, Write};
use std::net::TcpStream;

const GUID: &str = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
const MAX_PAYLOAD: u64 = 1024 * 1024;

pub enum WsMessage {
    Binary(Vec<u8>),
    Text,
    Ping(Vec<u8>),
    Close,
}

/// HTTP Upgrade 요청을 읽고 101 응답을 보낸다. WebSocket 요청이 아니면 간단한 상태 페이지를 응답하고 Err.
pub fn handshake(reader: &mut BufReader<TcpStream>, stream: &mut TcpStream) -> io::Result<()> {
    let mut key = None;
    let mut first = true;
    loop {
        let mut line = String::new();
        if reader.read_line(&mut line)? == 0 {
            return Err(io::Error::new(io::ErrorKind::UnexpectedEof, "closed during handshake"));
        }
        if line.len() > 8192 {
            return Err(io::Error::new(io::ErrorKind::InvalidData, "header too long"));
        }
        let line = line.trim_end();
        if first {
            first = false;
            continue;
        }
        if line.is_empty() {
            break;
        }
        if let Some((name, value)) = line.split_once(':') {
            if name.trim().eq_ignore_ascii_case("sec-websocket-key") {
                key = Some(value.trim().to_string());
            }
        }
    }
    let Some(key) = key else {
        let body = "Dolly Forge world server is running. Connect with WebSocket.\n";
        write!(
            stream,
            "HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{body}",
            body.len()
        )?;
        return Err(io::Error::new(io::ErrorKind::InvalidInput, "not a websocket request"));
    };
    let accept = base64(&sha1(format!("{key}{GUID}").as_bytes()));
    write!(
        stream,
        "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"
    )?;
    stream.flush()
}

pub fn read_message(r: &mut impl Read) -> io::Result<WsMessage> {
    let mut head = [0u8; 2];
    r.read_exact(&mut head)?;
    let opcode = head[0] & 0x0F;
    let fin = head[0] & 0x80 != 0;
    let masked = head[1] & 0x80 != 0;
    let mut len = (head[1] & 0x7F) as u64;
    if len == 126 {
        let mut b = [0u8; 2];
        r.read_exact(&mut b)?;
        len = u16::from_be_bytes(b) as u64;
    } else if len == 127 {
        let mut b = [0u8; 8];
        r.read_exact(&mut b)?;
        len = u64::from_be_bytes(b);
    }
    if !fin || opcode == 0 {
        return Err(io::Error::new(io::ErrorKind::InvalidData, "fragmented frames unsupported"));
    }
    if !masked || len > MAX_PAYLOAD {
        return Err(io::Error::new(io::ErrorKind::InvalidData, "bad client frame"));
    }
    let mut mask = [0u8; 4];
    r.read_exact(&mut mask)?;
    let mut payload = vec![0u8; len as usize];
    r.read_exact(&mut payload)?;
    for (i, b) in payload.iter_mut().enumerate() {
        *b ^= mask[i % 4];
    }
    Ok(match opcode {
        0x1 => WsMessage::Text,
        0x2 => WsMessage::Binary(payload),
        0x8 => WsMessage::Close,
        0x9 => WsMessage::Ping(payload),
        0xA => WsMessage::Text, // pong: 무시
        _ => return Err(io::Error::new(io::ErrorKind::InvalidData, "unknown ws opcode")),
    })
}

/// 서버 → 클라이언트 프레임 (마스킹 없음)
pub fn frame(opcode: u8, payload: &[u8]) -> Vec<u8> {
    let mut out = Vec::with_capacity(payload.len() + 10);
    out.push(0x80 | opcode);
    let len = payload.len();
    if len < 126 {
        out.push(len as u8);
    } else if len <= u16::MAX as usize {
        out.push(126);
        out.extend_from_slice(&(len as u16).to_be_bytes());
    } else {
        out.push(127);
        out.extend_from_slice(&(len as u64).to_be_bytes());
    }
    out.extend_from_slice(payload);
    out
}

pub fn binary(payload: &[u8]) -> Vec<u8> {
    frame(0x2, payload)
}

pub fn write_all(stream: &mut TcpStream, data: &[u8]) -> io::Result<()> {
    stream.write_all(data)
}

pub fn sha1(data: &[u8]) -> [u8; 20] {
    let mut h: [u32; 5] = [0x67452301, 0xEFCDAB89, 0x98BADCFE, 0x10325476, 0xC3D2E1F0];
    let mut msg = data.to_vec();
    let bit_len = (data.len() as u64).wrapping_mul(8);
    msg.push(0x80);
    while msg.len() % 64 != 56 {
        msg.push(0);
    }
    msg.extend_from_slice(&bit_len.to_be_bytes());
    for chunk in msg.chunks(64) {
        let mut w = [0u32; 80];
        for i in 0..16 {
            w[i] = u32::from_be_bytes(chunk[i * 4..i * 4 + 4].try_into().unwrap());
        }
        for i in 16..80 {
            w[i] = (w[i - 3] ^ w[i - 8] ^ w[i - 14] ^ w[i - 16]).rotate_left(1);
        }
        let (mut a, mut b, mut c, mut d, mut e) = (h[0], h[1], h[2], h[3], h[4]);
        for (i, wi) in w.iter().enumerate() {
            let (f, k) = match i {
                0..=19 => ((b & c) | (!b & d), 0x5A827999),
                20..=39 => (b ^ c ^ d, 0x6ED9EBA1),
                40..=59 => ((b & c) | (b & d) | (c & d), 0x8F1BBCDC),
                _ => (b ^ c ^ d, 0xCA62C1D6),
            };
            let t = a.rotate_left(5).wrapping_add(f).wrapping_add(e).wrapping_add(k).wrapping_add(*wi);
            e = d;
            d = c;
            c = b.rotate_left(30);
            b = a;
            a = t;
        }
        for (hv, v) in h.iter_mut().zip([a, b, c, d, e]) {
            *hv = hv.wrapping_add(v);
        }
    }
    let mut out = [0u8; 20];
    for (i, v) in h.iter().enumerate() {
        out[i * 4..i * 4 + 4].copy_from_slice(&v.to_be_bytes());
    }
    out
}

pub fn base64(data: &[u8]) -> String {
    const T: &[u8; 64] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    let mut out = String::new();
    for chunk in data.chunks(3) {
        let b = [chunk[0], *chunk.get(1).unwrap_or(&0), *chunk.get(2).unwrap_or(&0)];
        let n = (b[0] as u32) << 16 | (b[1] as u32) << 8 | b[2] as u32;
        out.push(T[(n >> 18) as usize & 63] as char);
        out.push(T[(n >> 12) as usize & 63] as char);
        out.push(if chunk.len() > 1 { T[(n >> 6) as usize & 63] as char } else { '=' });
        out.push(if chunk.len() > 2 { T[n as usize & 63] as char } else { '=' });
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rfc6455_accept_key() {
        let accept = base64(&sha1(format!("dGhlIHNhbXBsZSBub25jZQ=={GUID}").as_bytes()));
        assert_eq!(accept, "s3pPLMBiTxaQ9kYGzzhZRbK+xOo=");
    }

    #[test]
    fn reads_masked_frame() {
        let payload = b"\x11\x01\x00\x00\x00";
        let mask = [1u8, 2, 3, 4];
        let mut raw = vec![0x82, 0x80 | payload.len() as u8];
        raw.extend_from_slice(&mask);
        raw.extend(payload.iter().enumerate().map(|(i, b)| b ^ mask[i % 4]));
        match read_message(&mut &raw[..]).unwrap() {
            WsMessage::Binary(p) => assert_eq!(p, payload),
            _ => panic!("expected binary"),
        }
    }
}
