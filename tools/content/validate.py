#!/usr/bin/env python3
"""콘텐츠 팩 검증기 (표준 라이브러리만 사용).

content/dolly/ 의 재료·아이템·제외 목록·생물군계·구역·집 설계도를 함께 검사한다.
  1. 아이템: id 중복, 재료·형상·치수, 연결부 종류, 모서리 반지름 규격(0.6), 질량 = 밀도 × 부피 × 채움 (parts 는 합)
  2. 얻는 방법: 공정(process:단계+단계:입력)의 단계가 설비·손 공정에 있는지, 입력이 아이템·재료인지, 동물 산물의 동물이 있는지
  3. 현실 필터: 아이템의 ref 가 excluded.json 에 적힌 것과 겹치지 않는지 (그 항목의 replace 로 지정된 대응물은 허용)
  4. 생물군계·구역·집: 가리키는 id 가 있는지, 시작 지점이 하나인지, 가구가 방 안에 있는지
사용: python3 tools/content/validate.py [팩 폴더, 기본 content/dolly]  → 문제가 있으면 종료 코드 1
"""
import json, math, os, re, sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
PACK = sys.argv[1] if len(sys.argv) > 1 else os.path.join(ROOT, "content", "dolly")
errors, warnings = [], []


def load(rel):
    with open(os.path.join(PACK, rel), encoding="utf-8") as f:
        return json.load(f)


def err(msg): errors.append(msg)
def warn(msg): warnings.append(msg)


# ── 부피 (m³) ─────────────────────────────────────────────────────────────
def volume(shape, mm, edge=0.0):
    m = [v / 1000 for v in mm]
    if shape == "box":
        w, d, h = m
        r = min(edge / 1000, w / 2, d / 2, h / 2)
        a, b, c = w - 2 * r, d - 2 * r, h - 2 * r
        # 둥근 상자 = 안쪽 상자 ⊕ 반지름 r 구 (민코프스키 합)
        return a * b * c + 2 * r * (a * b + b * c + c * a) + math.pi * r * r * (a + b + c) + 4 / 3 * math.pi * r ** 3
    if shape == "cyl":
        d, h = m
        return math.pi * (d / 2) ** 2 * h
    if shape == "tube":
        od, idm, h = m
        return math.pi * ((od / 2) ** 2 - (idm / 2) ** 2) * h
    if shape == "sphere":
        return 4 / 3 * math.pi * (m[0] / 2) ** 3
    raise ValueError(shape)


DIMS = {"box": 3, "cyl": 2, "tube": 3, "sphere": 1}


def norm_ref(s):
    s = re.sub(r"\(.*?\)", "", s.lower())
    return [t.strip() for t in s.split("/") if t.strip()]


def main():
    pack = load("pack.json")
    std = pack["standard"]
    joints_ok = set(pack["joints"])

    # ── 재료 ──────────────────────────────────────────────────────────────
    mats = {}
    for m in load(pack["files"]["materials"])["materials"]:
        if m["id"] in mats: err(f"재료 id 중복: {m['id']}")
        for k in ("name", "density", "mu_s", "mu_k", "restitution"):
            if k not in m: err(f"재료 {m['id']}: {k} 없음")
        if m.get("density", 0) <= 0: err(f"재료 {m['id']}: 밀도가 0 이하")
        mats[m["id"]] = m

    # ── 아이템 ────────────────────────────────────────────────────────────
    items, by_cat = {}, {}
    for rel in pack["files"]["items"]:
        doc = load(rel)
        cat = doc["category"]
        for it in doc["items"]:
            iid = it["id"]
            if iid in items: err(f"아이템 id 중복: {iid} ({rel})")
            it["_cat"] = cat
            items[iid] = it
            by_cat.setdefault(cat, []).append(iid)

    processes = set(pack["hand_processes"]) | set(pack["locked_processes"])
    for it in items.values():
        processes |= set(it.get("process", []))

    heavy, masses = [], {}
    for iid, it in items.items():
        shape = it.get("shape")
        where = f"{it['_cat']}/{iid}"
        if "name" not in it: err(f"{where}: 이름 없음")
        if shape == "field":
            if it.get("mat") not in mats: err(f"{where}: 재료 {it.get('mat')} 없음")
            if it.get("dig_to") not in items: err(f"{where}: 퍼낸 결과 {it.get('dig_to')} 없음")
            continue
        if shape == "animal":
            if it.get("mass_kg", 0) <= 0: err(f"{where}: 동물 질량 없음")
            for p in it.get("products", []):
                if p not in items: err(f"{where}: 산물 {p} 없음")
            continue
        if shape not in DIMS: err(f"{where}: 알 수 없는 형상 {shape}"); continue
        mm = it.get("mm", [])
        if len(mm) != DIMS[shape] or any(v <= 0 for v in mm): err(f"{where}: 치수 {mm} 가 {shape} 형상에 맞지 않음"); continue
        if it.get("mat") not in mats: err(f"{where}: 재료 {it.get('mat')} 없음"); continue
        for j in it.get("joints", []):
            if j not in joints_ok: err(f"{where}: 연결부 {j} 는 규격에 없음")
        # 모서리 반지름 규격 (상자만: 원기둥·구는 형상 자체가 둥글다).
        # 크기 등급은 가장 긴 치수로, 단 반지름은 두께를 넘을 수 없어 가장 얇은 치수의 1/4 이 상한
        if shape == "box":
            big = max(mm)
            need = std["edge_min_mm"]["small"] if big < std["size_class_mm"]["small_below"] else \
                   std["edge_min_mm"]["large"] if big > std["size_class_mm"]["large_above"] else std["edge_min_mm"]["medium"]
            need = min(need, 0.25 * min(mm))
            if it.get("edge", 0) < need: err(f"{where}: 모서리 반지름 {it.get('edge', 0)} mm < 규격 {need} mm")
        # 질량
        if "parts" in it:
            mass = 0.0
            for p in it["parts"]:
                if p["mat"] not in mats: err(f"{where}: 부분 재료 {p['mat']} 없음"); continue
                mass += mats[p["mat"]]["density"] * volume(p["shape"], p["mm"], p.get("edge", 0)) * p.get("fill", 1.0)
        else:
            mass = mats[it["mat"]]["density"] * volume(shape, mm, it.get("edge", 0)) * it.get("fill", 1.0)
        if "mass_kg" in it:
            if abs(mass - it["mass_kg"]) / it["mass_kg"] > 0.5:
                warn(f"{where}: 적어 둔 질량 {it['mass_kg']} kg 이 형상 계산 {mass:.2f} kg 와 많이 다름 (적은 값을 쓴다)")
            mass = it["mass_kg"]
        masses[iid] = mass
        if mass > std["carry_limit_kg"]: heavy.append((iid, mass))
        # 얻는 방법
        for src in [s.strip() for s in it.get("from", "").split(" / ") if s.strip()]:   # " / " = 여러 방법 중 하나
            kind, _, rest = src.partition(":")
            if kind == "process":
                steps, _, inputs = rest.partition(":")
                for st in steps.split("+"):
                    if st not in processes: err(f"{where}: 공정 '{st}' 를 하는 설비·손 공정이 없음")
                for inp in [x for x in inputs.split("+") if x]:
                    if inp not in items and inp not in mats: err(f"{where}: 공정 입력 '{inp}' 가 아이템·재료가 아님")
            elif kind == "animal":
                if rest not in items or items[rest].get("shape") != "animal": err(f"{where}: 동물 '{rest}' 없음")
            elif kind not in ("dig", "gather", "harvest", "buy", "sieve"):
                err(f"{where}: 알 수 없는 얻는 방법 '{src}'")

    # ── 현실 필터 ─────────────────────────────────────────────────────────
    exc = load(pack["files"]["excluded"])
    reasons = exc["reasons"]
    banned = {}
    for e in exc["excluded"]:
        if e["why"] not in reasons: err(f"제외 목록: 이유 '{e['why']}' 정의 없음")
        rep = e.get("replace", [])
        rep = [rep] if isinstance(rep, str) else rep
        for r in rep:
            if r not in items and r not in mats: err(f"제외 목록: 대응물 '{r}' 가 아이템·재료에 없음 ({e['ref']})")
        for tok in norm_ref(e["ref"]):
            banned[tok] = (e["why"], set(rep))
    for iid, it in items.items():
        for tok in norm_ref(it.get("ref") or ""):
            for b, (why, rep) in banned.items():
                # 단어 단위 포함: 'netherite' 는 'netherite ingot' 도 막고, 'redstone dust' 는 'redstone torch' 를 막지 않는다
                if re.search(r"(^|\s)" + re.escape(b) + r"($|\s)", tok) and iid not in rep:
                    err(f"{it['_cat']}/{iid}: ref '{tok}' 는 제외 대상 '{b}' ({reasons[why]})")

    # ── 구역 ──────────────────────────────────────────────────────────────
    zdoc = load(pack["files"]["zones"])
    values = {"allow", "owner", "invited", "own_placed", "anchors", "public", "deny"}
    zones = {}
    for z in zdoc["zones"]:
        zones[z["id"]] = z
        for a in zdoc["actions"]:
            if z.get(a) not in values: err(f"구역 {z['id']}: {a} = {z.get(a)} 는 정의되지 않은 권한")
    if zones.get("wild", {}).get("dig") != "allow": err("구역: 야생은 파기가 자유여야 한다 (밖은 예외)")
    if zones.get("home_shell", {}).get("dig") != "deny": err("구역: 집 골격은 부술 수 없어야 한다")

    # ── 집 ────────────────────────────────────────────────────────────────
    houses = {h["id"]: h for h in load(pack["files"]["houses"])["houses"]}
    for hid, h in houses.items():
        spawns = 0
        fw, fd = h["footprint_m"]
        for room in h["rooms"]:
            rx, rz = room["pos_m"]; rw, rd = room["size_m"]
            if rx + rw > fw + 1e-6 or rz + rd > fd + 1e-6: err(f"집 {hid}/{room['id']}: 방이 집 밖으로 나감")
            for f in room.get("furniture", []):
                if f["item"] not in items: err(f"집 {hid}/{room['id']}: 가구 {f['item']} 없음"); continue
                px, pz = f["pos_m"]
                if not (-1e-6 <= px <= rw + 1e-6 and -1e-6 <= pz <= rd + 1e-6): err(f"집 {hid}/{room['id']}: {f['item']} 자리가 방 밖")
                spawns += 1 if f.get("spawn_point") else 0
        for s in h.get("yard", {}).get("start_items", []):
            if s not in items: err(f"집 {hid}: 마당 시작 물건 {s} 없음")
        if spawns != 1: err(f"집 {hid}: 시작 지점이 {spawns}곳 (1곳이어야 함)")

    # ── 생물군계 ─────────────────────────────────────────────────────────
    bio = load(pack["files"]["biomes"])
    for r in bio["regions"]:
        if r["zone"] not in zones: err(f"지역 {r['id']}: 구역 {r['zone']} 없음")
        for key in ("ground", "trees", "plants", "animals", "ores"):
            for x in r.get(key, []):
                if x not in items and x not in mats: err(f"지역 {r['id']}: {key} '{x}' 없음")
        if "spawn" in r and r["spawn"] not in houses: err(f"지역 {r['id']}: 시작 집 {r['spawn']} 없음")
    if sum(1 for r in bio["regions"] if "spawn" in r) != 1: err("생물군계: 시작 지역이 하나여야 한다")

    # ── 요약 ──────────────────────────────────────────────────────────────
    refs = sum(1 for it in items.values() if it.get("ref"))
    print(f"[content] 팩 {pack['namespace']} {pack['version']}: 재료 {len(mats)}종, 아이템 {len(items)}종 (마인크래프트 대응 {refs} · 새로 더함 {len(items) - refs})")
    for cat, ids in by_cat.items():
        ms = [masses[i] for i in ids if i in masses]
        span = f", 질량 {min(ms):.3g}–{max(ms):.3g} kg" if ms else ""
        print(f"  {cat:<11} {len(ids):3d}종{span}")
    print(f"  제외 {len(exc['excluded'])}묶음 (이유 {len(reasons)}가지), 지역 {len(bio['regions'])}곳, 구역 {len(zones)}종, 집 {len(houses)}채")
    if heavy:
        print(f"  {std['carry_limit_kg']} kg 넘는 것 {len(heavy)}종 (수레·레일·여럿이 옮김): " +
              ", ".join(f"{i} {m:.0f} kg" for i, m in sorted(heavy, key=lambda x: -x[1])[:8]) + (" …" if len(heavy) > 8 else ""))
    for w in warnings: print(f"  주의: {w}")
    for e in errors: print(f"  오류: {e}")
    print(f"[content] {'통과' if not errors else f'오류 {len(errors)}건'}")
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
