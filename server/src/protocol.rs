//! 바이너리 프로토콜. 모든 값은 little-endian.
//! 프레임: [u16 길이(opcode+payload)] [u8 opcode] [payload]
//! 문자열: [u16 바이트 길이] [UTF-8 바이트]
//! C# 쪽 구현: client/Unity/Assets/DollyForge/Core/Protocol.cs

use std::io::{self, Read};

pub mod op {
    // Client -> Server
    pub const REGISTER: u8 = 0x01;
    pub const REQUEST_CODE: u8 = 0x02;
    pub const LOGIN: u8 = 0x03;
    pub const MOVE: u8 = 0x10;
    pub const INTERACT: u8 = 0x11;
    pub const CHAT: u8 = 0x12;
    pub const CHOOSE: u8 = 0x13;
    pub const RESTART: u8 = 0x14;

    // Server -> Client
    pub const REGISTERED: u8 = 0x81;
    pub const CODE_SENT: u8 = 0x82;
    pub const LOGIN_OK: u8 = 0x83;
    pub const PLAYER_STATE: u8 = 0x84;
    pub const MESSAGE: u8 = 0x85;
    pub const ENDING: u8 = 0x86;
    pub const TELEPORT: u8 = 0x87;
    pub const ERROR: u8 = 0x8F;
    pub const PLAYER_JOINED: u8 = 0x91;
    pub const PLAYER_MOVED: u8 = 0x92;
    pub const PLAYER_LEFT: u8 = 0x93;
    pub const NODE_SPAWN: u8 = 0x94;
    pub const NODE_REMOVE: u8 = 0x95;
    pub const CHAT_BROADCAST: u8 = 0x96;
}

pub const MAX_FRAME: usize = 4096;

#[derive(Debug, Clone)]
pub enum C2S {
    Register { phone: String, gender: u8 },
    RequestCode { token: String },
    Login { token: String, code: String },
    Move { x: f32, y: f32, z: f32, yaw: f32 },
    Interact { target: u32 },
    Chat { text: String },
    Choose { choice: u8 },
    Restart,
}

pub struct Writer {
    buf: Vec<u8>,
}

impl Writer {
    pub fn new(opcode: u8) -> Self {
        Writer { buf: vec![0, 0, opcode] }
    }
    pub fn u8(mut self, v: u8) -> Self {
        self.buf.push(v);
        self
    }
    pub fn u16(mut self, v: u16) -> Self {
        self.buf.extend_from_slice(&v.to_le_bytes());
        self
    }
    pub fn u32(mut self, v: u32) -> Self {
        self.buf.extend_from_slice(&v.to_le_bytes());
        self
    }
    pub fn f32(mut self, v: f32) -> Self {
        self.buf.extend_from_slice(&v.to_le_bytes());
        self
    }
    pub fn str(mut self, s: &str) -> Self {
        let bytes = s.as_bytes();
        let len = bytes.len().min(MAX_FRAME / 2);
        self.buf.extend_from_slice(&(len as u16).to_le_bytes());
        self.buf.extend_from_slice(&bytes[..len]);
        self
    }
    pub fn finish(mut self) -> Vec<u8> {
        let len = (self.buf.len() - 2) as u16;
        self.buf[0..2].copy_from_slice(&len.to_le_bytes());
        self.buf
    }
}

pub struct Reader<'a> {
    data: &'a [u8],
    pos: usize,
}

fn eof() -> io::Error {
    io::Error::new(io::ErrorKind::UnexpectedEof, "payload too short")
}

impl<'a> Reader<'a> {
    pub fn new(data: &'a [u8]) -> Self {
        Reader { data, pos: 0 }
    }
    fn take(&mut self, n: usize) -> io::Result<&'a [u8]> {
        if self.pos + n > self.data.len() {
            return Err(eof());
        }
        let s = &self.data[self.pos..self.pos + n];
        self.pos += n;
        Ok(s)
    }
    pub fn u8(&mut self) -> io::Result<u8> {
        Ok(self.take(1)?[0])
    }
    pub fn u16(&mut self) -> io::Result<u16> {
        Ok(u16::from_le_bytes(self.take(2)?.try_into().unwrap()))
    }
    pub fn u32(&mut self) -> io::Result<u32> {
        Ok(u32::from_le_bytes(self.take(4)?.try_into().unwrap()))
    }
    pub fn f32(&mut self) -> io::Result<f32> {
        let v = f32::from_le_bytes(self.take(4)?.try_into().unwrap());
        if v.is_finite() {
            Ok(v)
        } else {
            Err(io::Error::new(io::ErrorKind::InvalidData, "non-finite float"))
        }
    }
    pub fn str(&mut self) -> io::Result<String> {
        let len = self.u16()? as usize;
        let bytes = self.take(len)?;
        String::from_utf8(bytes.to_vec())
            .map_err(|_| io::Error::new(io::ErrorKind::InvalidData, "invalid utf-8"))
    }
}

/// 프레임 하나를 읽는다. 반환값의 첫 바이트가 opcode.
pub fn read_frame(r: &mut impl Read) -> io::Result<Vec<u8>> {
    let mut len = [0u8; 2];
    r.read_exact(&mut len)?;
    let len = u16::from_le_bytes(len) as usize;
    if len == 0 || len > MAX_FRAME {
        return Err(io::Error::new(io::ErrorKind::InvalidData, "bad frame length"));
    }
    let mut buf = vec![0u8; len];
    r.read_exact(&mut buf)?;
    Ok(buf)
}

pub fn decode(frame: &[u8]) -> io::Result<C2S> {
    let mut r = Reader::new(&frame[1..]);
    let msg = match frame[0] {
        op::REGISTER => C2S::Register { phone: r.str()?, gender: r.u8()? },
        op::REQUEST_CODE => C2S::RequestCode { token: r.str()? },
        op::LOGIN => C2S::Login { token: r.str()?, code: r.str()? },
        op::MOVE => C2S::Move { x: r.f32()?, y: r.f32()?, z: r.f32()?, yaw: r.f32()? },
        op::INTERACT => C2S::Interact { target: r.u32()? },
        op::CHAT => C2S::Chat { text: r.str()? },
        op::CHOOSE => C2S::Choose { choice: r.u8()? },
        op::RESTART => C2S::Restart,
        other => {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                format!("unknown opcode 0x{other:02x}"),
            ))
        }
    };
    Ok(msg)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn roundtrip_move() {
        let frame = Writer::new(op::MOVE).f32(1.5).f32(0.0).f32(-3.0).f32(0.25).finish();
        let body = read_frame(&mut &frame[..]).unwrap();
        match decode(&body).unwrap() {
            C2S::Move { x, z, yaw, .. } => assert_eq!((x, z, yaw), (1.5, -3.0, 0.25)),
            m => panic!("unexpected {m:?}"),
        }
    }

    #[test]
    fn roundtrip_korean_string() {
        let frame = Writer::new(op::CHAT).str("안녕, 돌리").finish();
        let body = read_frame(&mut &frame[..]).unwrap();
        match decode(&body).unwrap() {
            C2S::Chat { text } => assert_eq!(text, "안녕, 돌리"),
            m => panic!("unexpected {m:?}"),
        }
    }

    #[test]
    fn rejects_truncated_payload() {
        let frame = Writer::new(op::INTERACT).u8(1).finish();
        let body = read_frame(&mut &frame[..]).unwrap();
        assert!(decode(&body).is_err());
    }
}
