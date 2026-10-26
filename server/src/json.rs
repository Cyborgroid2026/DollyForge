//! 최소 JSON 파서 (외부 crate 없이). 카탈로그와 보관 기기 문서를 읽는 데 쓴다.

use std::collections::BTreeMap;

#[derive(Debug, Clone, PartialEq)]
pub enum Json {
    Null,
    Bool(bool),
    Num(f64),
    Str(String),
    Arr(Vec<Json>),
    Obj(BTreeMap<String, Json>),
}

impl Json {
    pub fn get(&self, key: &str) -> Option<&Json> {
        match self {
            Json::Obj(m) => m.get(key),
            _ => None,
        }
    }
    pub fn str(&self) -> Option<&str> {
        match self {
            Json::Str(s) => Some(s),
            _ => None,
        }
    }
    pub fn num(&self) -> Option<f64> {
        match self {
            Json::Num(n) => Some(*n),
            _ => None,
        }
    }
    pub fn int(&self) -> Option<i64> {
        self.num().filter(|n| n.fract() == 0.0).map(|n| n as i64)
    }
    pub fn bool(&self) -> Option<bool> {
        match self {
            Json::Bool(b) => Some(*b),
            _ => None,
        }
    }
    pub fn arr(&self) -> Option<&[Json]> {
        match self {
            Json::Arr(a) => Some(a),
            _ => None,
        }
    }
}

pub fn parse(text: &str) -> Result<Json, String> {
    let mut p = Parser { s: text.as_bytes(), i: 0, depth: 0 };
    let v = p.value()?;
    p.ws();
    if p.i != p.s.len() {
        return Err(format!("trailing data at {}", p.i));
    }
    Ok(v)
}

struct Parser<'a> {
    s: &'a [u8],
    i: usize,
    depth: usize,
}

impl<'a> Parser<'a> {
    fn ws(&mut self) {
        while self.i < self.s.len() && matches!(self.s[self.i], b' ' | b'\n' | b'\r' | b'\t') {
            self.i += 1;
        }
    }
    fn err<T>(&self, what: &str) -> Result<T, String> {
        Err(format!("{what} at byte {}", self.i))
    }
    fn eat(&mut self, lit: &[u8]) -> bool {
        if self.s[self.i..].starts_with(lit) {
            self.i += lit.len();
            true
        } else {
            false
        }
    }
    fn value(&mut self) -> Result<Json, String> {
        self.ws();
        if self.i >= self.s.len() {
            return self.err("unexpected end");
        }
        match self.s[self.i] {
            b'{' => self.object(),
            b'[' => self.array(),
            b'"' => Ok(Json::Str(self.string()?)),
            b't' | b'f' | b'n' => {
                if self.eat(b"true") {
                    Ok(Json::Bool(true))
                } else if self.eat(b"false") {
                    Ok(Json::Bool(false))
                } else if self.eat(b"null") {
                    Ok(Json::Null)
                } else {
                    self.err("bad literal")
                }
            }
            _ => self.number(),
        }
    }
    fn enter(&mut self) -> Result<(), String> {
        self.depth += 1;
        if self.depth > 64 {
            return self.err("nesting too deep");
        }
        Ok(())
    }
    fn object(&mut self) -> Result<Json, String> {
        self.enter()?;
        self.i += 1;
        let mut m = BTreeMap::new();
        self.ws();
        if self.eat(b"}") {
            self.depth -= 1;
            return Ok(Json::Obj(m));
        }
        loop {
            self.ws();
            if self.i >= self.s.len() || self.s[self.i] != b'"' {
                return self.err("expected key");
            }
            let k = self.string()?;
            self.ws();
            if !self.eat(b":") {
                return self.err("expected ':'");
            }
            let v = self.value()?;
            m.insert(k, v);
            self.ws();
            if self.eat(b",") {
                continue;
            }
            if self.eat(b"}") {
                self.depth -= 1;
                return Ok(Json::Obj(m));
            }
            return self.err("expected ',' or '}'");
        }
    }
    fn array(&mut self) -> Result<Json, String> {
        self.enter()?;
        self.i += 1;
        let mut a = Vec::new();
        self.ws();
        if self.eat(b"]") {
            self.depth -= 1;
            return Ok(Json::Arr(a));
        }
        loop {
            a.push(self.value()?);
            self.ws();
            if self.eat(b",") {
                continue;
            }
            if self.eat(b"]") {
                self.depth -= 1;
                return Ok(Json::Arr(a));
            }
            return self.err("expected ',' or ']'");
        }
    }
    fn string(&mut self) -> Result<String, String> {
        self.i += 1; // "
        let mut out = String::new();
        loop {
            let start = self.i;
            while self.i < self.s.len() && self.s[self.i] != b'"' && self.s[self.i] != b'\\' {
                self.i += 1;
            }
            out.push_str(std::str::from_utf8(&self.s[start..self.i]).map_err(|_| "invalid utf-8".to_string())?);
            if self.i >= self.s.len() {
                return self.err("unterminated string");
            }
            if self.s[self.i] == b'"' {
                self.i += 1;
                return Ok(out);
            }
            self.i += 1; // backslash
            let c = *self.s.get(self.i).ok_or("bad escape")?;
            self.i += 1;
            match c {
                b'"' => out.push('"'),
                b'\\' => out.push('\\'),
                b'/' => out.push('/'),
                b'b' => out.push('\u{8}'),
                b'f' => out.push('\u{c}'),
                b'n' => out.push('\n'),
                b'r' => out.push('\r'),
                b't' => out.push('\t'),
                b'u' => {
                    let hex = std::str::from_utf8(self.s.get(self.i..self.i + 4).ok_or("bad \\u")?).map_err(|_| "bad \\u")?;
                    let mut code = u32::from_str_radix(hex, 16).map_err(|_| "bad \\u".to_string())?;
                    self.i += 4;
                    if (0xD800..0xDC00).contains(&code) && self.eat(b"\\u") {
                        let hex2 = std::str::from_utf8(self.s.get(self.i..self.i + 4).ok_or("bad \\u")?).map_err(|_| "bad \\u")?;
                        let low = u32::from_str_radix(hex2, 16).map_err(|_| "bad \\u".to_string())?;
                        self.i += 4;
                        code = 0x10000 + ((code - 0xD800) << 10) + (low - 0xDC00);
                    }
                    out.push(char::from_u32(code).unwrap_or('\u{FFFD}'));
                }
                _ => return self.err("bad escape"),
            }
        }
    }
    fn number(&mut self) -> Result<Json, String> {
        let start = self.i;
        while self.i < self.s.len() && matches!(self.s[self.i], b'-' | b'+' | b'.' | b'e' | b'E' | b'0'..=b'9') {
            self.i += 1;
        }
        let t = std::str::from_utf8(&self.s[start..self.i]).unwrap_or("");
        t.parse::<f64>().map(Json::Num).map_err(|_| format!("bad number '{t}' at byte {start}"))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_nested_documents() {
        let j = parse(r#"{"a": [1, 2.5, -3e2], "b": {"c": "한글 \"q\" é"}, "d": true, "e": null}"#).unwrap();
        assert_eq!(j.get("a").unwrap().arr().unwrap()[2].num(), Some(-300.0));
        assert_eq!(j.get("b").unwrap().get("c").unwrap().str(), Some("한글 \"q\" é"));
        assert_eq!(j.get("d").unwrap().bool(), Some(true));
        assert_eq!(j.get("e"), Some(&Json::Null));
    }

    #[test]
    fn rejects_garbage() {
        assert!(parse("{\"a\": }").is_err());
        assert!(parse("[1, 2").is_err());
        assert!(parse(&"[".repeat(100)).is_err());
    }
}
