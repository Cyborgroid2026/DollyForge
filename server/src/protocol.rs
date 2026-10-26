//! 바이너리 프로토콜. 모든 값은 little-endian.
//! WebSocket 바이너리 메시지 1개 = [u8 opcode] [payload]
//! 문자열: [u16 바이트 길이][UTF-8], 큰 덩어리(기기 JSON): [u32 바이트 길이][UTF-8]
//! C# 쪽 구현: godot/src/Core/Net/Protocol.cs

use std::io;

pub mod op {
    // 클라이언트 → 서버
    pub const REGISTER: u8 = 0x01;
    pub const REQUEST_CODE: u8 = 0x02;
    pub const LOGIN: u8 = 0x03;
    pub const POSE: u8 = 0x10;
    pub const CHAT: u8 = 0x12;
    pub const PURCHASE: u8 = 0x20;
    pub const CONSUME: u8 = 0x21;
    pub const REFUND: u8 = 0x22;
    pub const STOW_DEVICE: u8 = 0x23;
    pub const LIST_DEVICES: u8 = 0x24;
    pub const RETRIEVE_DEVICE: u8 = 0x25;
    pub const SAVE_BODY: u8 = 0x26;

    // 서버 → 클라이언트
    pub const REGISTERED: u8 = 0x81;
    pub const CODE_SENT: u8 = 0x82;
    pub const LOGIN_OK: u8 = 0x83;
    pub const PROFILE: u8 = 0x84;
    pub const MESSAGE: u8 = 0x85;
    pub const ERROR: u8 = 0x8F;
    pub const PLAYER_JOINED: u8 = 0x91;
    pub const PLAYER_MOVED: u8 = 0x92;
    pub const PLAYER_LEFT: u8 = 0x93;
    pub const CHAT_BROADCAST: u8 = 0x96;
    pub const PURCHASE_RESULT: u8 = 0xA0;
    pub const DEVICE_LIST: u8 = 0xA4;
    pub const DEVICE_DATA: u8 = 0xA5;
    pub const BODY_DATA: u8 = 0xA6;
}

/// 클라이언트 메시지 최대 크기 (기기 JSON 보관 포함)
pub const MAX_FRAME: usize = 600 * 1024;

#[derive(Debug, Clone, PartialEq)]
pub struct OrderLine {
    pub sku: String,
    pub qty: u16,
    pub length_mm: u32,
    pub width_mm: u32,
}

#[derive(Debug, Clone)]
pub enum C2S {
    Register { phone: String, gender: u8 },
    RequestCode { token: String },
    Login { token: String, code: String },
    Pose { x: f32, y: f32, z: f32, yaw: f32 },
    Chat { text: String },
    Purchase { lines: Vec<OrderLine> },
    Consume { sku: String, qty: u16 },
    Refund { sku: String, qty: u16 },
    StowDevice { name: String, json: String },
    ListDevices,
    RetrieveDevice { name: String },
    /// 몸 구성: 표준이 아닌 모듈 품번들, 몸에 찬 기기 품번들
    SaveBody { modules: Vec<String>, worn: Vec<String> },
}

pub struct Writer {
    buf: Vec<u8>,
}

impl Writer {
    pub fn new(opcode: u8) -> Self {
        Writer { buf: vec![opcode] }
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
    pub fn i64(mut self, v: i64) -> Self {
        self.buf.extend_from_slice(&v.to_le_bytes());
        self
    }
    pub fn f32(mut self, v: f32) -> Self {
        self.buf.extend_from_slice(&v.to_le_bytes());
        self
    }
    pub fn str(mut self, s: &str) -> Self {
        // u16 길이를 넘지 않도록 UTF-8 문자 경계에서 자른다
        let mut end = s.len().min(u16::MAX as usize);
        while !s.is_char_boundary(end) {
            end -= 1;
        }
        self.buf.extend_from_slice(&(end as u16).to_le_bytes());
        self.buf.extend_from_slice(&s.as_bytes()[..end]);
        self
    }
    pub fn blob(mut self, s: &str) -> Self {
        self.buf.extend_from_slice(&(s.len() as u32).to_le_bytes());
        self.buf.extend_from_slice(s.as_bytes());
        self
    }
    pub fn finish(self) -> Vec<u8> {
        self.buf
    }
}

pub struct Reader<'a> {
    data: &'a [u8],
    pos: usize,
}

fn bad(what: &str) -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, what.to_string())
}

impl<'a> Reader<'a> {
    pub fn new(data: &'a [u8]) -> Self {
        Reader { data, pos: 0 }
    }
    fn take(&mut self, n: usize) -> io::Result<&'a [u8]> {
        if self.pos + n > self.data.len() {
            return Err(io::Error::new(io::ErrorKind::UnexpectedEof, "payload too short"));
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
            Err(bad("non-finite float"))
        }
    }
    pub fn str(&mut self) -> io::Result<String> {
        let len = self.u16()? as usize;
        String::from_utf8(self.take(len)?.to_vec()).map_err(|_| bad("invalid utf-8"))
    }
    /// [u16 개수][문자열…] (최대 32개)
    pub fn str_list(&mut self) -> io::Result<Vec<String>> {
        let n = self.u16()? as usize;
        if n > 32 {
            return Err(bad("list too long"));
        }
        (0..n).map(|_| self.str()).collect()
    }
    pub fn blob(&mut self) -> io::Result<String> {
        let len = self.u32()? as usize;
        if len > MAX_FRAME {
            return Err(bad("blob too large"));
        }
        String::from_utf8(self.take(len)?.to_vec()).map_err(|_| bad("invalid utf-8"))
    }
}

pub fn decode(frame: &[u8]) -> io::Result<C2S> {
    if frame.is_empty() || frame.len() > MAX_FRAME {
        return Err(bad("bad message length"));
    }
    let mut r = Reader::new(&frame[1..]);
    let msg = match frame[0] {
        op::REGISTER => C2S::Register { phone: r.str()?, gender: r.u8()? },
        op::REQUEST_CODE => C2S::RequestCode { token: r.str()? },
        op::LOGIN => C2S::Login { token: r.str()?, code: r.str()? },
        op::POSE => C2S::Pose { x: r.f32()?, y: r.f32()?, z: r.f32()?, yaw: r.f32()? },
        op::CHAT => C2S::Chat { text: r.str()? },
        op::PURCHASE => {
            let n = r.u16()? as usize;
            let mut lines = Vec::with_capacity(n.min(64));
            for _ in 0..n {
                lines.push(OrderLine { sku: r.str()?, qty: r.u16()?, length_mm: r.u32()?, width_mm: r.u32()? });
            }
            C2S::Purchase { lines }
        }
        op::CONSUME => C2S::Consume { sku: r.str()?, qty: r.u16()? },
        op::REFUND => C2S::Refund { sku: r.str()?, qty: r.u16()? },
        op::STOW_DEVICE => C2S::StowDevice { name: r.str()?, json: r.blob()? },
        op::LIST_DEVICES => C2S::ListDevices,
        op::RETRIEVE_DEVICE => C2S::RetrieveDevice { name: r.str()? },
        op::SAVE_BODY => C2S::SaveBody { modules: r.str_list()?, worn: r.str_list()? },
        other => return Err(bad(&format!("unknown opcode 0x{other:02x}"))),
    };
    Ok(msg)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn roundtrip_purchase() {
        let frame = Writer::new(op::PURCHASE).u16(2)
            .str("AL-2020").u16(3).u32(750).u32(0)
            .str("PL-SS400-3").u16(1).u32(220).u32(200)
            .finish();
        match decode(&frame).unwrap() {
            C2S::Purchase { lines } => {
                assert_eq!(lines.len(), 2);
                assert_eq!(lines[1], OrderLine { sku: "PL-SS400-3".into(), qty: 1, length_mm: 220, width_mm: 200 });
            }
            m => panic!("unexpected {m:?}"),
        }
    }

    #[test]
    fn roundtrip_device_blob() {
        let json = "{\"name\":\"로봇\",\"parts\":[]}".repeat(3000);
        let frame = Writer::new(op::STOW_DEVICE).str("로봇").blob(&json).finish();
        match decode(&frame).unwrap() {
            C2S::StowDevice { name, json: j } => assert_eq!((name.as_str(), j.len()), ("로봇", json.len())),
            m => panic!("unexpected {m:?}"),
        }
    }

    #[test]
    fn rejects_truncated_payload() {
        let frame = Writer::new(op::CONSUME).str("FS-BOLT-M8").finish();
        assert!(decode(&frame).is_err());
    }

    #[test]
    fn str_truncates_on_char_boundary() {
        let long = "가".repeat(30000); // 90 000 바이트
        let frame = Writer::new(op::CHAT).str(&long).finish();
        match decode(&frame).unwrap() {
            C2S::Chat { text } => assert!(text.len() <= u16::MAX as usize && text.chars().all(|c| c == '가')),
            m => panic!("unexpected {m:?}"),
        }
    }
}
