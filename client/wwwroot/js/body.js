// 돌리(티타늄 사이보그) 신체 생성.
// 캡슐을 이어 붙이지 않고, 해부학적 단면(타원)을 높이별로 정의한 뒤 Catmull-Rom 으로 보간해
// 하나의 매끈한 곡면으로 이어 만든다(loft).
// 디자인 원칙: 특징이 거의 없는 완벽한 사이보그. 얼굴·문양·발광 표식 없이 균일한 새틴 티타늄,
// 관절에는 정밀 가공을 암시하는 아주 가는 분할선만 둔다. 성별은 실루엣과 비례(키, 어깨/골반,
// 허리, 절제된 가슴·골반 곡선)로만 구분된다.
import * as THREE from '../lib/three/three.module.min.js';

// ---------- 로프트 ----------

const DEFAULTS = { cx: 0, cz: 0, sq: 1, bust: 0, glute: 0, pec: 0 };
const KEYS = ['y', 'rx', 'f', 'b', 'cx', 'cz', 'sq', 'bust', 'glute', 'pec'];

function catmull(p0, p1, p2, p3, t) {
    const t2 = t * t, t3 = t2 * t;
    return 0.5 * (2 * p1 + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t2 + (-p0 + 3 * p1 - 3 * p2 + p3) * t3);
}

function sectionAt(S, i, t) {
    const p0 = S[Math.max(i - 1, 0)], p1 = S[i], p2 = S[i + 1], p3 = S[Math.min(i + 2, S.length - 1)];
    const out = {};
    for (const k of KEYS) out[k] = catmull(p0[k], p1[k], p2[k], p3[k], t);
    out.rx = Math.max(out.rx, 0.001);
    out.f = Math.max(out.f, 0.001);
    out.b = Math.max(out.b, 0.001);
    return out;
}

const angDiff = (a, b) => Math.atan2(Math.sin(a - b), Math.cos(a - b));
const bump = (th, center, width) => Math.exp(-((angDiff(th, center) / width) ** 2));
const spow = (v, e) => Math.sign(v) * Math.abs(v) ** (1 / e);

// θ=π/2 가 정면(+z)
function ringPoint(s, th) {
    const c = Math.cos(th), sn = Math.sin(th);
    let x = s.cx + s.rx * spow(c, s.sq);
    let z = s.cz + (sn >= 0 ? s.f : s.b) * spow(sn, s.sq);
    for (const side of [-1, 1]) {
        if (s.bust > 0) {
            const g = bump(th, Math.PI / 2 + side * 0.56, 0.36);
            z += s.bust * g;
            x += s.bust * g * c * 0.3;
        }
        if (s.pec > 0) z += s.pec * bump(th, Math.PI / 2 + side * 0.62, 0.5);
        if (s.glute > 0) {
            const g = bump(th, -Math.PI / 2 + side * 0.52, 0.48);
            z -= s.glute * g;
            x += s.glute * g * c * 0.2;
        }
    }
    return [x, s.y, z];
}

export function loft(sections, radial = 48, perSpan = 6) {
    const S = sections.map(s => ({ ...DEFAULTS, ...s }));
    const rings = [];
    for (let i = 0; i < S.length - 1; i++)
        for (let j = 0; j < perSpan; j++) rings.push(sectionAt(S, i, j / perSpan));
    rings.push(S[S.length - 1]);

    const pos = [];
    for (const r of rings)
        for (let k = 0; k < radial; k++) pos.push(...ringPoint(r, (k / radial) * Math.PI * 2));
    const idx = [];
    for (let r = 0; r < rings.length - 1; r++) {
        for (let k = 0; k < radial; k++) {
            const a = r * radial + k, b = r * radial + ((k + 1) % radial), c = a + radial, d = b + radial;
            idx.push(a, c, b, b, c, d);
        }
    }
    // 양 끝 마개
    const cap = (r, top) => {
        const base = r * radial;
        let x = 0, y = 0, z = 0;
        for (let k = 0; k < radial; k++) { x += pos[(base + k) * 3]; y += pos[(base + k) * 3 + 1]; z += pos[(base + k) * 3 + 2]; }
        const center = pos.length / 3;
        pos.push(x / radial, y / radial, z / radial);
        for (let k = 0; k < radial; k++) {
            const a = base + k, b = base + ((k + 1) % radial);
            if (top) idx.push(center, b, a); else idx.push(center, a, b);
        }
    };
    cap(0, false);
    cap(rings.length - 1, true);

    const geo = new THREE.BufferGeometry();
    geo.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
    geo.setIndex(idx);
    geo.computeVertexNormals();
    return geo;
}

// ---------- 체형 정의 (단위 m) ----------

// 여성: 키 1.66 / 어깨 너비 ≈ 0.37, 허리 ≈ 0.25, 골반 ≈ 0.37 (허리/골반 ≈ 0.68)
// 남성: 키 1.78 / 어깨 너비 ≈ 0.46, 허리 ≈ 0.29, 골반 ≈ 0.33 (허리/골반 ≈ 0.88)
const BODY = {
    0: {
        torso: [
            { y: 0.735, rx: 0.03, f: 0.03, b: 0.04, cz: -0.005 },
            { y: 0.765, rx: 0.1, f: 0.065, b: 0.08 },
            { y: 0.815, rx: 0.165, f: 0.088, b: 0.105, glute: 0.014 },
            { y: 0.87, rx: 0.185, f: 0.095, b: 0.12, glute: 0.02 },
            { y: 0.93, rx: 0.175, f: 0.09, b: 0.105, glute: 0.01 },
            { y: 1.0, rx: 0.14, f: 0.08, b: 0.08 },
            { y: 1.05, rx: 0.125, f: 0.075, b: 0.072 },
            { y: 1.11, rx: 0.132, f: 0.078, b: 0.076 },
            { y: 1.17, rx: 0.142, f: 0.083, b: 0.082 },
            { y: 1.22, rx: 0.15, f: 0.085, b: 0.084, bust: 0.026 },
            { y: 1.265, rx: 0.155, f: 0.083, b: 0.085, bust: 0.024 },
            { y: 1.31, rx: 0.158, f: 0.075, b: 0.08, bust: 0.004 },
            { y: 1.35, rx: 0.155, f: 0.06, b: 0.07 },
            { y: 1.385, rx: 0.1, f: 0.05, b: 0.055 },
            { y: 1.4, rx: 0.05, f: 0.045, b: 0.045 },
        ],
        sq: 1.0,
        neck: { y0: 1.36, y1: 1.5, r: 0.042 },
        head: { base: 1.425, scale: 1.0, jaw: 0.054, chin: 0.022 },
        shoulder: { x: 0.165, y: 1.335, delt: 0.042 },
        arm: { upper: 0.285, fore: 0.24, r: 1.0, bicep: 0 },
        hip: { x: 0.088, y: 0.84 },
        leg: { thigh: 0.4, shin: 0.38, r: 1.04, q: 0.045 },
        foot: 0.225,
    },
    1: {
        torso: [
            { y: 0.785, rx: 0.035, f: 0.035, b: 0.04 },
            { y: 0.815, rx: 0.105, f: 0.07, b: 0.08 },
            { y: 0.87, rx: 0.17, f: 0.095, b: 0.1, glute: 0.008 },
            { y: 0.92, rx: 0.176, f: 0.098, b: 0.108, glute: 0.012 },
            { y: 0.99, rx: 0.156, f: 0.094, b: 0.095 },
            { y: 1.06, rx: 0.138, f: 0.092, b: 0.088 },
            { y: 1.14, rx: 0.15, f: 0.098, b: 0.094 },
            { y: 1.22, rx: 0.172, f: 0.104, b: 0.102 },
            { y: 1.3, rx: 0.192, f: 0.11, b: 0.106, pec: 0.014 },
            { y: 1.37, rx: 0.203, f: 0.108, b: 0.102, pec: 0.01 },
            { y: 1.425, rx: 0.205, f: 0.085, b: 0.09 },
            { y: 1.465, rx: 0.14, f: 0.065, b: 0.07 },
            { y: 1.49, rx: 0.065, f: 0.055, b: 0.055 },
        ],
        sq: 1.18,
        neck: { y0: 1.44, y1: 1.6, r: 0.055 },
        head: { base: 1.525, scale: 1.08, jaw: 0.066, chin: 0.032 },
        shoulder: { x: 0.205, y: 1.415, delt: 0.056 },
        arm: { upper: 0.31, fore: 0.27, r: 1.22, bicep: 0.012 },
        hip: { x: 0.09, y: 0.9 },
        leg: { thigh: 0.44, shin: 0.42, r: 1.07, q: 0.02 },
        foot: 0.26,
    },
};

// ---------- 재질 ----------

export function shellMaterials() {
    return {
        // 새틴 마감 티타늄: 거울 반사가 아닌, 정밀 가공된 금속의 부드러운 광택
        shell: new THREE.MeshPhysicalMaterial({
            color: 0xc3c7cc, metalness: 1, roughness: 0.36, clearcoat: 0.15, clearcoatRoughness: 0.4,
        }),
        seam: new THREE.MeshStandardMaterial({ color: 0x3a3f45, metalness: 0.8, roughness: 0.5 }),
    };
}

// ---------- 반사 환경 ----------

// 하늘·지평선·땅과 태양, 부드러운 반사판이 있는 실외 반사 환경. 금속 표면이 주변을 자연스럽게 반사하게 한다.
export function outdoorEnvironment(renderer) {
    const env = new THREE.Scene();
    const sky = new THREE.SphereGeometry(50, 48, 24);
    const colors = [];
    const top = new THREE.Color(0x7fa9d6), horizon = new THREE.Color(0xf2efe8), ground = new THREE.Color(0x8c8270);
    const pos = sky.attributes.position;
    for (let i = 0; i < pos.count; i++) {
        const t = pos.getY(i) / 50;
        const c = t >= 0 ? horizon.clone().lerp(top, Math.pow(t, 0.6)) : horizon.clone().lerp(ground, Math.min(1, -t * 4));
        colors.push(c.r, c.g, c.b);
    }
    sky.setAttribute('color', new THREE.Float32BufferAttribute(colors, 3));
    env.add(new THREE.Mesh(sky, new THREE.MeshBasicMaterial({ vertexColors: true, side: THREE.BackSide })));
    const light = (w, h, intensity, x, y, z) => {
        const m = new THREE.Mesh(new THREE.PlaneGeometry(w, h), new THREE.MeshBasicMaterial({ side: THREE.DoubleSide }));
        m.material.color.setScalar(intensity);
        m.position.set(x, y, z);
        m.lookAt(0, 0, 0);
        env.add(m);
    };
    light(6, 6, 18, 20, 30, 15);    // 태양
    light(30, 6, 2.5, 0, 12, -40);  // 지평선 반사판
    light(30, 6, 2.0, -40, 10, 10);
    const pmrem = new THREE.PMREMGenerator(renderer);
    const tex = pmrem.fromScene(env, 0.02).texture;
    pmrem.dispose();
    return tex;
}

// ---------- 부위 ----------

function mesh(geo, mat, parent, x = 0, y = 0, z = 0) {
    const m = new THREE.Mesh(geo, mat);
    m.position.set(x, y, z);
    m.castShadow = true;
    parent.add(m);
    return m;
}

// 관절 분할선: 단면 타원을 따라 감는 얇은 고리
function seamRing(parent, mat, y, rx, rz, cz = 0) {
    const ring = mesh(new THREE.TorusGeometry(1, 0.025, 6, 48), mat, parent, 0, y, cz);
    ring.rotation.x = Math.PI / 2;
    ring.scale.set(rx * 1.004, rz * 1.004, 0.05);
    return ring;
}

function scaleSections(sections, k) {
    return sections.map(s => ({ ...s, rx: s.rx * k, f: s.f * k, b: s.b * k }));
}

function buildHead(P, M, g) {
    const h = P.head, k = h.scale;
    const head = new THREE.Group();
    head.position.y = h.base;
    // 아래(턱)에서 정수리까지. 얼굴은 없다.
    const sections = [
        { y: 0, rx: h.chin * 0.6, f: 0.014, b: 0.012, cz: 0.036 },
        { y: 0.016, rx: h.chin, f: 0.026, b: 0.024, cz: 0.034, sq: P.sq },
        { y: 0.048, rx: h.jaw, f: 0.056, b: 0.058, cz: 0.016, sq: P.sq },
        { y: 0.085, rx: h.jaw * 1.12, f: 0.085, b: 0.085, cz: 0.006 },
        { y: 0.125, rx: 0.073, f: 0.092, b: 0.1, cz: 0 },
        { y: 0.165, rx: 0.074, f: 0.088, b: 0.104, cz: -0.004 },
        { y: 0.2, rx: 0.066, f: 0.072, b: 0.094, cz: -0.008 },
        { y: 0.226, rx: 0.042, f: 0.044, b: 0.062, cz: -0.012 },
        { y: 0.238, rx: 0.006, f: 0.006, b: 0.008, cz: -0.014 },
    ].map(s => ({ ...s, y: s.y * k, rx: s.rx * k, f: s.f * k, b: s.b * k, cz: (s.cz ?? 0) * k }));
    mesh(loft(sections, 56, 7), M.shell, head);
    g.add(head);
    return head;
}

function buildArm(P, M, side) {
    const A = P.arm, r = A.r;
    const arm = new THREE.Group();
    arm.position.set(side * P.shoulder.x, P.shoulder.y, 0);
    arm.rotation.z = side * 0.09;
    mesh(new THREE.SphereGeometry(P.shoulder.delt, 24, 16), M.shell, arm).scale.set(1, 1.1, 1.05);

    const U = A.upper;
    const upper = scaleSections([
        { y: 0, rx: 0.031, f: 0.032, b: 0.033 },
        { y: U * 0.25, rx: 0.035, f: 0.037 + A.bicep * 0.5, b: 0.037 },
        { y: U * 0.55, rx: 0.038, f: 0.041 + A.bicep, b: 0.04 },
        { y: U * 0.85, rx: 0.042, f: 0.04, b: 0.042 },
        { y: U, rx: 0.043, f: 0.04, b: 0.042 },
    ], r);
    mesh(loft(upper, 32, 5), M.shell, arm, 0, -U, 0);

    const elbow = new THREE.Group();
    elbow.position.y = -U;
    elbow.rotation.x = -0.14;
    arm.add(elbow);
    seamRing(elbow, M.seam, 0, 0.033 * r, 0.034 * r);

    const F = A.fore;
    const fore = scaleSections([
        { y: 0, rx: 0.025, f: 0.018, b: 0.019 },
        { y: F * 0.3, rx: 0.028, f: 0.022, b: 0.024 },
        { y: F * 0.7, rx: 0.036, f: 0.031, b: 0.033 },
        { y: F * 0.92, rx: 0.035, f: 0.032, b: 0.033 },
        { y: F, rx: 0.031, f: 0.031, b: 0.031 },
    ], r);
    mesh(loft(fore, 32, 5), M.shell, elbow, 0, -F, 0);
    seamRing(elbow, M.seam, -F + 0.004, 0.026 * r, 0.02 * r);

    const hand = buildHand(M, r);
    hand.position.y = -F;
    elbow.add(hand);
    if (side < 0) arm.scale.x = -1; // 왼팔은 거울 대칭 (엄지 방향 유지)
    arm.userData.elbow = elbow;
    return arm;
}

// 오른손 기준. 손바닥은 몸 안쪽(-x), 엄지는 정면(+z).
function buildHand(M, r) {
    const hand = new THREE.Group();
    const k = r ** 0.5;
    const palm = scaleSections([
        { y: -0.095, rx: 0.011, f: 0.034, b: 0.036 },
        { y: -0.07, rx: 0.014, f: 0.039, b: 0.04 },
        { y: -0.03, rx: 0.015, f: 0.038, b: 0.037 },
        { y: 0, rx: 0.013, f: 0.026, b: 0.026 },
    ], k);
    mesh(loft(palm, 28, 4), M.shell, hand);
    const fingers = [[0.026, 0.072], [0.009, 0.082], [-0.009, 0.077], [-0.026, 0.062]];
    for (const [z, len] of fingers) {
        const L = len * k;
        const f = new THREE.Group();
        f.position.set(0, -0.093 * k, z * k);
        f.rotation.z = -0.22;
        mesh(loft([
            { y: 0, rx: 0.0055, f: 0.006, b: 0.006 },
            { y: L * 0.5, rx: 0.0075, f: 0.0078, b: 0.0078 },
            { y: L, rx: 0.0085, f: 0.009, b: 0.009 },
        ], 12, 3), M.shell, f, 0, -L, 0);
        hand.add(f);
    }
    const thumb = new THREE.Group();
    thumb.position.set(-0.004, -0.028 * k, 0.034 * k);
    thumb.rotation.set(-0.7, 0, -0.35);
    mesh(loft([
        { y: 0, rx: 0.0065, f: 0.0068, b: 0.0068 },
        { y: 0.03 * k, rx: 0.009, f: 0.0095, b: 0.0095 },
        { y: 0.058 * k, rx: 0.011, f: 0.012, b: 0.012 },
    ], 12, 3), M.shell, thumb, 0, -0.058 * k, 0);
    hand.add(thumb);
    return hand;
}

function buildLeg(P, M, side) {
    const Lg = P.leg, r = Lg.r;
    const leg = new THREE.Group();
    leg.position.set(side * P.hip.x, P.hip.y, 0);
    leg.rotation.z = -side * Lg.q;

    const T = Lg.thigh;
    const thigh = scaleSections([
        { y: 0, rx: 0.044, f: 0.046, b: 0.043 },
        { y: T * 0.12, rx: 0.048, f: 0.052, b: 0.046 },
        { y: T * 0.45, rx: 0.066, f: 0.068, b: 0.064 },
        { y: T * 0.8, rx: 0.08, f: 0.076, b: 0.08 },
        { y: T, rx: 0.08, f: 0.076, b: 0.082 },
        { y: T + 0.05, rx: 0.06, f: 0.058, b: 0.064, cx: -0.012 },
        { y: T + 0.09, rx: 0.025, f: 0.025, b: 0.025, cx: -0.02 },
    ], r);
    mesh(loft(thigh, 40, 5), M.shell, leg, 0, -T, 0);

    const knee = new THREE.Group();
    knee.position.y = -T;
    leg.add(knee);
    seamRing(knee, M.seam, -0.004, 0.044 * r, 0.045 * r);

    const N = Lg.shin;
    const shin = scaleSections([
        { y: 0, rx: 0.026, f: 0.027, b: 0.03 },
        { y: N * 0.15, rx: 0.029, f: 0.029, b: 0.034 },
        { y: N * 0.55, rx: 0.044, f: 0.04, b: 0.058 },
        { y: N * 0.75, rx: 0.046, f: 0.041, b: 0.056 },
        { y: N * 0.92, rx: 0.043, f: 0.045, b: 0.043 },
        { y: N, rx: 0.044, f: 0.046, b: 0.043 },
    ], r);
    mesh(loft(shin, 40, 5), M.shell, knee, 0, -N, 0);
    seamRing(knee, M.seam, -N + 0.004, 0.027 * r, 0.029 * r);

    // 발: 로컬 y(발끝 방향) → 월드 +z 로 회전, f(앞)=발바닥
    const FL = P.foot;
    const foot = mesh(loft(scaleSections([
        { y: 0, rx: 0.022, f: 0.028, b: 0.022 },
        { y: FL * 0.12, rx: 0.03, f: 0.033, b: 0.04 },
        { y: FL * 0.4, rx: 0.036, f: 0.03, b: 0.03 },
        { y: FL * 0.72, rx: 0.042, f: 0.024, b: 0.02 },
        { y: FL * 0.92, rx: 0.036, f: 0.016, b: 0.014 },
        { y: FL, rx: 0.012, f: 0.006, b: 0.005 },
    ], r ** 0.5), 28, 4), M.shell, knee, 0, -N - 0.032, -0.05);
    foot.rotation.x = Math.PI / 2;
    if (side < 0) leg.scale.x = -1; // 왼다리는 거울 대칭 (허벅지 안쪽 방향 유지)
    leg.userData.knee = knee;
    return leg;
}

// ---------- 공개 API ----------

const SHARED = {};

export function makeCyborg(gender, materials) {
    const P = BODY[gender === 1 ? 1 : 0];
    const M = materials ?? (SHARED.m ??= shellMaterials());
    const g = new THREE.Group();

    const torso = P.torso.map(s => ({ ...s, sq: P.sq }));
    mesh(loft(torso, 64, 7), M.shell, g);

    const waistY = gender === 1 ? 1.06 : 1.05;
    const waist = P.torso.reduce((a, s) => (Math.abs(s.y - waistY) < Math.abs(a.y - waistY) ? s : a));
    seamRing(g, M.seam, waistY, waist.rx * 1.01, (waist.f + waist.b) / 2 * 1.01);

    mesh(new THREE.CylinderGeometry(P.neck.r * 0.92, P.neck.r * 1.15, P.neck.y1 - P.neck.y0, 24), M.shell, g,
        0, (P.neck.y0 + P.neck.y1) / 2, -0.008);
    seamRing(g, M.seam, P.neck.y1 - 0.03, P.neck.r * 0.98, P.neck.r * 0.98, -0.008);

    const head = buildHead(P, M, g);
    const armL = buildArm(P, M, -1), armR = buildArm(P, M, 1);
    const legL = buildLeg(P, M, -1), legR = buildLeg(P, M, 1);
    g.add(armL, armR, legL, legR);

    let phase = 0, amp = 0;
    g.userData.height = gender === 1 ? 1.78 : 1.66;
    // speed: m/s. 걷기 주기와 팔 흔들기를 속도에 맞춘다.
    g.userData.animate = (dt, speed) => {
        const target = Math.min(1, speed / 3);
        amp += (target - amp) * Math.min(1, dt * 8);
        phase += dt * (2.2 + speed * 1.1);
        const s = Math.sin(phase);
        legL.rotation.x = s * 0.5 * amp;
        legR.rotation.x = -s * 0.5 * amp;
        legL.userData.knee.rotation.x = Math.max(0, -Math.cos(phase)) * 0.7 * amp;
        legR.userData.knee.rotation.x = Math.max(0, Math.cos(phase)) * 0.7 * amp;
        armL.rotation.x = -s * 0.35 * amp;
        armR.rotation.x = s * 0.35 * amp;
        const idle = Math.sin(performance.now() / 900) * 0.006;
        head.rotation.x = idle;
        g.children[0].scale.y = 1 + idle * 0.3; // 호흡처럼 미세한 움직임
    };
    return g;
}
