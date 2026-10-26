// Dolly Forge 3D 렌더러 (Three.js r160 + WebXR).
// 게임 규칙은 C#(GameSession) 과 Rust 서버가 가진다. 이 모듈은 월드 그리기, 입력,
// 로컬 충돌, 원격 플레이어 보간만 맡고 결과를 C# 으로 콜백한다.
import * as THREE from '../lib/three/three.module.min.js';
import { VRButton } from '../lib/three/addons/webxr/VRButton.js';

const FENCE_R = 18, GATE_W = 1.6, WORLD_R = 60, REACH = 3.5, BODY_R = 0.3;
const WALK = 3.5, RUN = 6.0, SEND_HZ = 10;
const SPAWN = { x: -6.5, z: 0, yaw: Math.PI / 2 };
const HARAM_POS = { x: -22, z: -34 };
const HOUSE_POS = { x: -27, z: -37 };
const RES_COLORS = [0xb8c4cc, 0xdff2ff, 0xc87533, 0x4caf50];

let dotnet, renderer, scene, camera, rig, hemi, sun;
let mode = 'preview';
let yaw = SPAWN.yaw, pitch = 0;
let phase = 0, research = 0, years = 0, gateOpen = false;
let preview = null, previewAngle = 0, previewGender = 0;
let hovered = 0, lastSend = 0, lastSent = { x: 0, z: 0, yaw: 0 };
let towerRings = [], gateBars, haram, grave, pillars = [];
let vrHud, vrCtx, vrTex, snapReady = true;
const keys = {};
const walls = [], circles = [], trees = [];
const targets = [];               // 상호작용 가능한 루트 오브젝트 (userData.tid)
const remotes = new Map();        // id -> { obj, to }
const nodes = new Map();          // id -> obj
const controllers = [];
const raycaster = new THREE.Raycaster();
const tmpMat = new THREE.Matrix4();
const clock = new THREE.Clock();

// ---------- 공개 API (C# Engine.cs 에서 호출) ----------

export function init(hostId, dotnetRef, stations, gender) {
    dotnet = dotnetRef;
    const host = document.getElementById(hostId);

    renderer = new THREE.WebGLRenderer({ antialias: true });
    renderer.setPixelRatio(Math.min(devicePixelRatio, 2));
    renderer.setSize(host.clientWidth, host.clientHeight);
    renderer.xr.enabled = true;
    host.appendChild(renderer.domElement);

    scene = new THREE.Scene();
    camera = new THREE.PerspectiveCamera(70, host.clientWidth / host.clientHeight, 0.05, 300);
    rig = new THREE.Group();
    rig.add(camera);
    scene.add(rig);
    camera.position.y = 1.6;

    hemi = new THREE.HemisphereLight(0xffffff, 0x445533, 0.9);
    sun = new THREE.DirectionalLight(0xffffff, 1.6);
    scene.add(hemi, sun);
    scene.fog = new THREE.Fog(0xcfe8ff, 35, 120);

    buildWorld(stations);
    setupInput();
    setupXr();
    applyLook();
    showPreview(gender);

    addEventListener('resize', () => {
        camera.aspect = host.clientWidth / host.clientHeight;
        camera.updateProjectionMatrix();
        renderer.setSize(host.clientWidth, host.clientHeight);
    });
    renderer.setAnimationLoop(frame);
}

export function showPreview(gender) {
    previewGender = gender;
    if (preview) scene.remove(preview);
    preview = makeCyborg(gender);
    preview.position.set(0, 0, 0);
    scene.add(preview);
}

export function enterWorld() {
    mode = 'world';
    if (preview) { scene.remove(preview); preview = null; }
    camera.position.set(0, 1.6, 0);
    camera.rotation.set(0, 0, 0);
    teleport(SPAWN.x, 0, SPAWN.z, SPAWN.yaw);
    const btn = VRButton.createButton(renderer);
    btn.id = 'vr-button';
    document.body.appendChild(btn);
}

export function setWorld(p, r, y, haramAlive, haramAge) {
    const lookChanged = p !== phase;
    phase = p; research = r; years = y;
    gateOpen = phase === 2;
    gateBars.position.y = gateOpen ? 3.2 : 0;
    pillars.forEach(o => (o.visible = phase >= 3));
    for (const t of trees) t.obj.scale.setScalar(t.base * (1 + years * 0.03));
    haram.visible = haramAlive;
    grave.visible = !haramAlive;
    if (haramAlive) {
        const age = haramAge - 72;
        haram.scale.set(1, 1 - age * 0.004, 1);
        haram.userData.hair.material.color.setHSL(0.08, 0.15, Math.min(0.85, 0.45 + age * 0.02));
    }
    for (const n of nodes.values()) n.userData.beacon.visible = research >= 1;
    if (lookChanged) applyLook();
}

export function teleport(x, y, z, yawRad) {
    rig.position.set(x, 0, z);
    yaw = yawRad;
    pitch = 0;
    lastSent = { x, z, yaw };
}

export function upsertPlayer(id, gender, x, y, z, yawRad) {
    let r = remotes.get(id);
    if (!r) {
        const obj = makeCyborg(gender);
        const tag = makeLabel(`돌리 #${id}`, 0.9);
        tag.position.y = 2.1;
        obj.add(tag);
        scene.add(obj);
        r = { obj, to: { x, z, yaw: yawRad } };
        remotes.set(id, r);
        obj.position.set(x, 0, z);
    }
    r.to = { x, z, yaw: yawRad };
}

export function movePlayer(id, x, y, z, yawRad) {
    const r = remotes.get(id);
    if (r) r.to = { x, z, yaw: yawRad };
}

export function removePlayer(id) {
    const r = remotes.get(id);
    if (r) { scene.remove(r.obj); remotes.delete(id); }
}

export function addNode(id, kind, x, z) {
    if (nodes.has(id)) return;
    const obj = makeResource(kind);
    obj.position.set(x, 0, z);
    obj.userData.tid = id;
    obj.userData.beacon.visible = research >= 1;
    scene.add(obj);
    targets.push(obj);
    nodes.set(id, obj);
}

export function removeNode(id) {
    const obj = nodes.get(id);
    if (!obj) return;
    scene.remove(obj);
    nodes.delete(id);
    const i = targets.indexOf(obj);
    if (i >= 0) targets.splice(i, 1);
}

export function clearWorld() {
    for (const id of [...remotes.keys()]) removePlayer(id);
    for (const id of [...nodes.keys()]) removeNode(id);
}

export function setVrHud(lines) {
    if (!vrCtx) return;
    vrCtx.fillStyle = 'rgba(10,14,20,0.88)';
    vrCtx.fillRect(0, 0, 512, 320);
    vrCtx.strokeStyle = '#66d9ff';
    vrCtx.lineWidth = 3;
    vrCtx.strokeRect(2, 2, 508, 316);
    vrCtx.fillStyle = '#e6eef5';
    vrCtx.font = '22px sans-serif';
    lines.slice(0, 11).forEach((l, i) => vrCtx.fillText(l, 14, 34 + i * 27, 484));
    vrTex.needsUpdate = true;
}

export function exitVr() {
    renderer.xr.getSession()?.end();
}

// ---------- 월드 구성 ----------

function mat(color, opts = {}) {
    return new THREE.MeshStandardMaterial({ color, roughness: 0.8, ...opts });
}

function box(w, h, d, color, x, y, z, parent = scene, opts) {
    const m = new THREE.Mesh(new THREE.BoxGeometry(w, h, d), mat(color, opts));
    m.position.set(x, y, z);
    parent.add(m);
    return m;
}

function cyl(rt, rb, h, color, x, y, z, parent = scene, opts) {
    const m = new THREE.Mesh(new THREE.CylinderGeometry(rt, rb, h, 16), mat(color, opts));
    m.position.set(x, y, z);
    parent.add(m);
    return m;
}

function wall(w, h, d, color, x, z) {
    box(w, h, d, color, x, h / 2, z);
    walls.push({ x1: x - w / 2, x2: x + w / 2, z1: z - d / 2, z2: z + d / 2 });
}

function makeLabel(text, scale = 1) {
    const c = document.createElement('canvas');
    c.width = 512; c.height = 96;
    const g = c.getContext('2d');
    g.fillStyle = 'rgba(10,14,20,0.7)';
    g.beginPath(); g.roundRect(4, 8, 504, 80, 24); g.fill();
    g.fillStyle = '#e6eef5';
    g.font = 'bold 44px sans-serif';
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    g.fillText(text, 256, 50);
    const tex = new THREE.CanvasTexture(c);
    tex.colorSpace = THREE.SRGBColorSpace;
    const s = new THREE.Sprite(new THREE.SpriteMaterial({ map: tex, depthWrite: false }));
    s.scale.set(1.6 * scale, 0.3 * scale, 1);
    return s;
}

// 결정적 난수 — 모든 클라이언트에서 같은 숲이 생성된다.
function rng(seed) {
    return () => {
        seed |= 0; seed = (seed + 0x6D2B79F5) | 0;
        let t = Math.imul(seed ^ (seed >>> 15), 1 | seed);
        t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
        return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
    };
}

function buildWorld(stations) {
    const ground = new THREE.Mesh(new THREE.CircleGeometry(WORLD_R + 60, 64), mat(0x6f8f4e));
    ground.rotation.x = -Math.PI / 2;
    scene.add(ground);
    const plaza = new THREE.Mesh(new THREE.CircleGeometry(FENCE_R, 64), mat(0x9c8f78));
    plaza.rotation.x = -Math.PI / 2;
    plaza.position.y = 0.01;
    scene.add(plaza);
    const path = new THREE.Mesh(new THREE.PlaneGeometry(3, 30), mat(0x8a7a5e));
    path.rotation.x = -Math.PI / 2;
    path.position.set(-6, 0.015, -31);
    path.rotation.z = -0.45;
    scene.add(path);

    buildPavilion(-9, -1, 0xd8c8a8);   // 집 (뒷벽이 -x)
    buildPavilion(9, 1, 0xb8c8d0);     // 연구소 (뒷벽이 +x)
    buildTower();
    buildFence();
    buildHaramHouse();
    buildForest();

    for (const s of stations) {
        const obj = buildStation(s);
        if (!obj) continue;
        obj.userData.tid = s.id;
        targets.push(obj);
        if (s.id !== 50 && s.id !== 14 && s.id !== 15) {
            const label = makeLabel(s.label, 0.8);
            label.position.set(s.x, 2.3, s.z);
            scene.add(label);
        }
    }

    const vr = document.createElement('canvas');
    vr.width = 512; vr.height = 320;
    vrCtx = vr.getContext('2d');
    vrTex = new THREE.CanvasTexture(vr);
    vrTex.colorSpace = THREE.SRGBColorSpace;
    vrHud = new THREE.Mesh(new THREE.PlaneGeometry(0.24, 0.15), new THREE.MeshBasicMaterial({ map: vrTex, transparent: true }));
    setVrHud(['Dolly Forge']);
}

function buildPavilion(cx, back, color) {
    const bx = cx + back * 4;
    box(8, 0.2, 7, 0x6b5a48, cx, 0.1, 0);
    wall(0.25, 3, 7, color, bx, 0);
    wall(8, 3, 0.25, color, cx, -3.5);
    wall(8, 3, 0.25, color, cx, 3.5);
    box(8.8, 0.25, 7.8, 0x5a3e2b, cx, 3.1, 0);
    for (const z of [-3.4, 3.4]) cyl(0.12, 0.12, 3, 0x5a3e2b, cx - back * 4, 1.5, z);
}

function buildTower() {
    const g = new THREE.Group();
    g.position.set(0, 0, 9);
    cyl(0.6, 1.2, 1, 0x555a60, 0, 0.5, 0, g, { metalness: 0.6 });
    cyl(0.25, 0.4, 7, 0x8a6a40, 0, 4, 0, g, { metalness: 0.7, roughness: 0.4 });
    const orb = new THREE.Mesh(new THREE.SphereGeometry(0.7, 24, 16),
        mat(0x99e6ff, { emissive: 0x33bbff, emissiveIntensity: 1.2 }));
    orb.position.y = 8;
    g.add(orb);
    for (let i = 0; i < 3; i++) {
        const ring = new THREE.Mesh(new THREE.TorusGeometry(1.2 + i * 0.4, 0.05, 8, 48),
            mat(0xc89b50, { metalness: 0.9, roughness: 0.3 }));
        ring.position.y = 5 + i * 1.1;
        ring.rotation.x = Math.PI / 2;
        g.add(ring);
        towerRings.push(ring);
    }
    scene.add(g);
    circles.push({ x: 0, z: 9, r: 1.2 });
    const label = makeLabel('양자 공명탑', 1);
    label.position.set(0, 2.2, 9);
    scene.add(label);
}

function buildFence() {
    const a = GATE_W / FENCE_R;
    for (const h of [0.5, 1.0]) {
        const rail = new THREE.Mesh(new THREE.TorusGeometry(FENCE_R, 0.04, 6, 160, Math.PI * 2 - 2 * a),
            mat(0x6b4a2b));
        rail.rotation.set(-Math.PI / 2, 0, Math.PI / 2 + a);
        rail.position.y = h;
        scene.add(rail);
    }
    for (let i = 0; i < 64; i++) {
        const t = (i / 64) * Math.PI * 2;
        const x = Math.cos(t) * FENCE_R, z = Math.sin(t) * FENCE_R;
        if (Math.abs(x) < GATE_W + 0.2 && z < 0) continue;
        cyl(0.07, 0.07, 1.3, 0x5a3e2b, x, 0.65, z);
    }
    gateBars = new THREE.Group();
    gateBars.position.set(0, 0, -FENCE_R);
    for (let x = -GATE_W + 0.2; x < GATE_W; x += 0.4) cyl(0.04, 0.04, 1.4, 0x8a8f96, x, 0.7, 0, gateBars, { metalness: 0.8 });
    box(GATE_W * 2, 0.08, 0.08, 0x8a8f96, 0, 1.35, 0, gateBars, { metalness: 0.8 });
    scene.add(gateBars);
    for (const s of [-1, 1]) cyl(0.12, 0.12, 2.2, 0x5a3e2b, s * (GATE_W + 0.1), 1.1, -FENCE_R);
    const sign = makeLabel('남쪽 문 — 아날로그 월드', 1);
    sign.position.set(0, 2.5, -FENCE_R);
    scene.add(sign);
}

function buildHaramHouse() {
    const { x, z } = HOUSE_POS;
    wall(5, 2.6, 0.25, 0xa08060, x, z - 2.5);
    wall(0.25, 2.6, 5, 0xa08060, x - 2.5, z);
    wall(5, 2.6, 0.25, 0xa08060, x, z + 2.5);
    const roof = new THREE.Mesh(new THREE.ConeGeometry(4.2, 2, 4), mat(0x7a3b2e));
    roof.position.set(x, 3.6, z);
    roof.rotation.y = Math.PI / 4;
    scene.add(roof);
    // 전신주 (마을 → 오두막)
    for (let i = 0; i < 5; i++) {
        const px = -2 - i * 4.6, pz = -20 - i * 3.4;
        cyl(0.1, 0.12, 5, 0x5a3e2b, px, 2.5, pz);
        box(1.2, 0.08, 0.08, 0x5a3e2b, px, 4.7, pz);
        circles.push({ x: px, z: pz, r: 0.2 });
    }
    const label = makeLabel('하람의 오두막', 1);
    label.position.set(x, 3.2, z + 2.8);
    scene.add(label);
}

function buildForest() {
    const rand = rng(42);
    let placed = 0;
    while (placed < 150) {
        const r = 21 + rand() * 38, t = rand() * Math.PI * 2;
        const x = Math.cos(t) * r, z = Math.sin(t) * r;
        if (Math.hypot(x - HOUSE_POS.x, z - HOUSE_POS.z) < 7) continue;
        if (Math.abs(x) < 3 && z < -15) continue;
        if (z < -15 && z > -40 && Math.abs((x + 6) - (z + 31) * 0.48) < 3) continue; // 오솔길
        const g = new THREE.Group();
        cyl(0.15, 0.22, 1.6, 0x5b3a22, 0, 0.8, 0, g);
        const leaves = new THREE.Mesh(new THREE.ConeGeometry(1.1, 2.6, 8), mat(0x2f6b3a + Math.floor(rand() * 0x101010)));
        leaves.position.y = 2.8;
        g.add(leaves);
        const base = 0.8 + rand() * 0.6;
        g.position.set(x, 0, z);
        g.scale.setScalar(base);
        scene.add(g);
        trees.push({ obj: g, base });
        circles.push({ x, z, r: 0.35 * base });
        placed++;
    }
    for (let i = 0; i < 40; i++) {
        const r = 20 + rand() * 40, t = rand() * Math.PI * 2;
        const rock = new THREE.Mesh(new THREE.DodecahedronGeometry(0.3 + rand() * 0.6), mat(0x77797c));
        rock.position.set(Math.cos(t) * r, 0.2, Math.sin(t) * r);
        scene.add(rock);
    }
}

function buildStation(s) {
    const g = new THREE.Group();
    g.position.set(s.x, 0, s.z);
    const glow = (c) => mat(c, { emissive: c, emissiveIntensity: 1.2 });
    switch (s.id) {
        case 1: // 충전 독
            box(0.9, 2.1, 0.5, 0x2a2f35, 0, 1.05, 0, g, { metalness: 0.6 });
            box(0.5, 0.08, 0.52, 0, 0, 1.6, 0, g, glow(0x66e0ff));
            box(0.5, 0.08, 0.52, 0, 0, 1.3, 0, g, glow(0x66e0ff));
            break;
        case 2: // 영양 식탁
            box(1.4, 0.08, 0.8, 0x8a6a48, 0, 0.8, 0, g);
            for (const [x, z] of [[-0.6, -0.3], [0.6, -0.3], [-0.6, 0.3], [0.6, 0.3]]) box(0.06, 0.8, 0.06, 0x6b4a2b, x, 0.4, z, g);
            cyl(0.18, 0.12, 0.1, 0xdddddd, 0, 0.89, 0, g);
            cyl(0.15, 0.15, 0.05, 0, 0, 0.94, 0, g, glow(0x7be0a0));
            break;
        case 3: // 윤활유 캔
            box(1.2, 0.06, 0.4, 0x6b4a2b, 0, 1.1, 0, g);
            cyl(0.1, 0.12, 0.3, 0xb04030, 0, 1.28, 0, g, { metalness: 0.7, roughness: 0.4 });
            cyl(0.015, 0.015, 0.25, 0x999999, 0.08, 1.45, 0, g).rotation.z = -0.8;
            break;
        case 4: // 기억 일지
            box(1.2, 0.06, 0.7, 0x7a5a3a, 0, 0.78, 0, g);
            box(1.1, 0.75, 0.6, 0x5b3a22, 0, 0.375, 0, g);
            box(0.4, 0.05, 0.3, 0x8b2a2a, 0, 0.84, 0, g);
            box(0.03, 0.03, 0.25, 0xc89b50, 0.25, 0.83, 0, g, { metalness: 0.9 });
            break;
        case 5: // 정비 침대
            box(1.2, 0.45, 2.2, 0x50565c, 0, 0.225, 0, g, { metalness: 0.5 });
            box(1.1, 0.12, 2.0, 0xc9d1d6, 0, 0.5, 0, g);
            break;
        case 6: { // 인공 동면 포드
            cyl(0.6, 0.65, 0.25, 0x2a2f35, 0, 0.125, 0, g, { metalness: 0.7 });
            const glass = new THREE.Mesh(new THREE.CapsuleGeometry(0.45, 1.3, 8, 16),
                new THREE.MeshPhysicalMaterial({ color: 0xbfefff, transparent: true, opacity: 0.35, roughness: 0.05 }));
            glass.position.y = 1.2;
            g.add(glass);
            cyl(0.3, 0.3, 0.05, 0, 0, 2.15, 0, g, glow(0x9fe8ff));
            break;
        }
        case 7: // 굿즈 진열장
            box(1.6, 1.6, 0.35, 0x6b4a2b, 0, 0.8, 0, g);
            cyl(0.07, 0.05, 0.25, 0, -0.5, 1.25, 0.05, g, glow(0xffa040));
            const watch = new THREE.Mesh(new THREE.TorusGeometry(0.1, 0.025, 8, 24), mat(0xc89b50, { metalness: 0.9 }));
            watch.position.set(0, 1.25, 0.05);
            g.add(watch);
            box(0.2, 0.13, 0.03, 0xc9d1d6, 0.5, 0.75, 0.05, g, { metalness: 0.9, roughness: 0.3 });
            break;
        case 11: // 연구 칠판
            box(0.1, 1.4, 2.6, 0x23392c, -0.1, 1.6, 0, g);
            box(0.12, 1.5, 2.7, 0x6b4a2b, -0.05, 1.6, 0, g);
            break;
        case 12: // 시료 분석기
            box(1.0, 1.1, 0.7, 0x3a4048, 0, 0.55, 0, g, { metalness: 0.6 });
            for (let i = -1; i <= 1; i++) cyl(0.07, 0.07, 0.35, 0, i * 0.25, 1.28, 0, g, glow(0xff9a40));
            break;
        case 13: // 작업대
            box(1.6, 0.08, 0.9, 0x7a5a3a, 0, 0.9, 0, g);
            for (const [x, z] of [[-0.7, -0.35], [0.7, -0.35], [-0.7, 0.35], [0.7, 0.35]]) box(0.08, 0.9, 0.08, 0x5b3a22, x, 0.45, z, g);
            for (let i = 0; i < 3; i++) {
                const gear = new THREE.Mesh(new THREE.TorusGeometry(0.12 + i * 0.04, 0.03, 6, 12), mat(0xc89b50, { metalness: 0.9 }));
                gear.rotation.x = Math.PI / 2;
                gear.position.set(-0.4 + i * 0.4, 0.96, 0);
                g.add(gear);
            }
            break;
        case 14: case 15: { // 엔딩 기둥
            const c = s.id === 14 ? 0x66d9ff : 0xffb070;
            cyl(0.35, 0.35, 2.6, 0, 0, 1.3, 0, g, glow(c));
            const label = makeLabel(s.id === 14 ? '영원 — 완전 기계화' : '유한 — 유기체 유지', 0.9);
            label.position.y = 3;
            g.add(label);
            g.visible = false;
            pillars.push(g);
            break;
        }
        case 50: { // 하람
            haram = makeHuman();
            g.add(haram);
            grave = new THREE.Group();
            box(0.6, 0.9, 0.15, 0x8a8f96, 0, 0.45, 0, grave);
            box(1, 0.1, 1.8, 0x5b4a3a, 0, 0.05, 0.9, grave);
            for (let i = 0; i < 5; i++) {
                const f = new THREE.Mesh(new THREE.SphereGeometry(0.05), mat(0xfff0a0, { emissive: 0x806020 }));
                f.position.set(-0.3 + i * 0.15, 0.15, 0.4 + (i % 2) * 0.3);
                grave.add(f);
            }
            grave.visible = false;
            g.add(grave);
            const label = makeLabel(s.label, 0.8);
            label.position.y = 2.1;
            g.add(label);
            circles.push({ x: s.x, z: s.z, r: 0.4 });
            break;
        }
        default:
            return null;
    }
    scene.add(g);
    return g;
}

function makeCyborg(gender) {
    const g = new THREE.Group();
    const ti = mat(0xc9d1d6, { metalness: 0.9, roughness: 0.28 });
    const dark = mat(0x2a2f35, { metalness: 0.6, roughness: 0.5 });
    const glow = mat(0x66e0ff, { emissive: 0x33ccff, emissiveIntensity: 1.6 });
    const female = gender === 0;
    const shoulder = female ? 0.19 : 0.25, hip = female ? 0.17 : 0.13;
    const part = (geo, m, x, y, z, sx = 1, sy = 1, sz = 1) => {
        const mesh = new THREE.Mesh(geo, m);
        mesh.position.set(x, y, z);
        mesh.scale.set(sx, sy, sz);
        g.add(mesh);
        return mesh;
    };
    part(new THREE.SphereGeometry(0.13, 24, 16), ti, 0, 1.68, 0, 1, 1.2, 1.05);   // 얼굴 없는 머리
    part(new THREE.BoxGeometry(0.17, 0.025, 0.02), glow, 0, 1.7, 0.125);          // 바이저
    part(new THREE.CylinderGeometry(0.05, 0.06, 0.12), dark, 0, 1.5, 0);
    part(new THREE.CapsuleGeometry(0.15, 0.32, 6, 12), ti, 0, 1.18, 0, shoulder / 0.15, 1, 0.75);
    part(new THREE.CircleGeometry(0.05, 16), glow, 0, 1.28, 0.115);              // 가슴 코어
    part(new THREE.BoxGeometry(hip * 2.2, 0.16, 0.2), dark, 0, 0.88, 0);
    for (const s of [-1, 1]) {
        part(new THREE.CapsuleGeometry(0.045, 0.5, 4, 8), ti, s * (shoulder + 0.07), 1.12, 0);
        part(new THREE.SphereGeometry(0.05), dark, s * (shoulder + 0.07), 1.38, 0);
        part(new THREE.CapsuleGeometry(0.065, 0.66, 4, 8), ti, s * hip * 0.6, 0.42, 0);
        part(new THREE.SphereGeometry(0.06), dark, s * hip * 0.6, 0.45, 0.03);
    }
    return g;
}

function makeHuman() {
    const g = new THREE.Group();
    const skin = mat(0xe0b896), cloth = mat(0x6a4c7a);
    const head = new THREE.Mesh(new THREE.SphereGeometry(0.12, 16, 12), skin);
    head.position.y = 1.55;
    g.add(head);
    const hair = new THREE.Mesh(new THREE.SphereGeometry(0.125, 16, 12, 0, Math.PI * 2, 0, Math.PI / 2), mat(0x777777));
    hair.position.y = 1.57;
    g.add(hair);
    g.userData.hair = hair;
    for (const s of [-1, 1]) {
        const eye = new THREE.Mesh(new THREE.SphereGeometry(0.015), mat(0x222222));
        eye.position.set(s * 0.04, 1.57, 0.11);
        g.add(eye);
    }
    const body = new THREE.Mesh(new THREE.CylinderGeometry(0.16, 0.28, 1.1, 12), cloth);
    body.position.y = 0.85;
    g.add(body);
    const cane = new THREE.Mesh(new THREE.CylinderGeometry(0.015, 0.015, 1.0), mat(0x5b3a22));
    cane.position.set(0.3, 0.5, 0.1);
    g.add(cane);
    return g;
}

function makeResource(kind) {
    const g = new THREE.Group();
    const c = RES_COLORS[kind];
    if (kind === 0) {
        const m = new THREE.Mesh(new THREE.IcosahedronGeometry(0.4), mat(c, { metalness: 0.9, roughness: 0.35, flatShading: true }));
        m.position.y = 0.3;
        g.add(m);
    } else if (kind === 1) {
        for (let i = 0; i < 4; i++) {
            const m = new THREE.Mesh(new THREE.OctahedronGeometry(0.15 + i * 0.03),
                mat(c, { transparent: true, opacity: 0.8, emissive: 0x335577, roughness: 0.1 }));
            m.position.set(Math.cos(i * 1.6) * 0.15, 0.25 + i * 0.05, Math.sin(i * 1.6) * 0.15);
            m.scale.y = 2;
            g.add(m);
        }
    } else if (kind === 2) {
        const m = new THREE.Mesh(new THREE.DodecahedronGeometry(0.35), mat(c, { metalness: 0.8, roughness: 0.4, flatShading: true }));
        m.position.y = 0.25;
        g.add(m);
    } else {
        for (let i = 0; i < 5; i++) {
            const m = new THREE.Mesh(new THREE.ConeGeometry(0.06, 0.45, 6), mat(c));
            m.position.set(Math.cos(i * 1.3) * 0.12, 0.22, Math.sin(i * 1.3) * 0.12);
            m.rotation.z = Math.cos(i) * 0.3;
            g.add(m);
        }
    }
    const beacon = new THREE.Mesh(new THREE.CylinderGeometry(0.06, 0.06, 12, 8, 1, true),
        new THREE.MeshBasicMaterial({ color: c, transparent: true, opacity: 0.35, depthWrite: false }));
    beacon.position.y = 6;
    g.add(beacon);
    g.userData.beacon = beacon;
    return g;
}

// ---------- 조명 ----------

const LOOKS = [
    { sky: 0xcfe8ff, sun: [30, 50, 20], si: 1.6, hi: 0.9, sc: 0xffffff },   // 일상: 아침
    { sky: 0xe6f3ff, sun: [0, 60, 5], si: 1.8, hi: 1.0, sc: 0xffffff },     // 연구: 한낮
    { sky: 0xf2a46b, sun: [-40, 14, -30], si: 1.2, hi: 0.6, sc: 0xffb070 }, // 외출: 노을
    { sky: 0x5b4a7a, sun: [-30, 10, 0], si: 0.8, hi: 0.5, sc: 0xc8a0ff },   // 선택
];

function applyLook() {
    const L = LOOKS[Math.min(phase, 3)];
    scene.background = new THREE.Color(L.sky);
    scene.fog.color.set(L.sky);
    sun.position.set(...L.sun);
    sun.intensity = L.si;
    sun.color.set(L.sc);
    hemi.intensity = L.hi;
}

// ---------- 입력 ----------

function inputBlocked() {
    const el = document.activeElement;
    return el && (el.tagName === 'INPUT' || el.tagName === 'TEXTAREA');
}

function setupInput() {
    const canvas = renderer.domElement;
    addEventListener('keydown', (e) => {
        if (inputBlocked()) {
            if (e.code === 'Escape') document.activeElement.blur();
            return;
        }
        keys[e.code] = true;
        if (mode !== 'world') return;
        if (e.code === 'KeyE') interact();
        if (e.code === 'KeyT') {
            e.preventDefault();
            document.exitPointerLock?.();
            dotnet.invokeMethodAsync('OnOpenChat');
        }
    });
    addEventListener('keyup', (e) => { keys[e.code] = false; });
    addEventListener('blur', () => { for (const k in keys) keys[k] = false; });
    canvas.addEventListener('click', () => {
        if (mode !== 'world') return;
        if (document.pointerLockElement !== canvas) canvas.requestPointerLock?.();
        else interact();
    });
    addEventListener('mousemove', (e) => {
        if (document.pointerLockElement !== canvas) return;
        yaw -= e.movementX * 0.0022;
        pitch = Math.max(-1.4, Math.min(1.4, pitch - e.movementY * 0.0022));
    });
}

function setupXr() {
    for (let i = 0; i < 2; i++) {
        const c = renderer.xr.getController(i);
        const line = new THREE.Line(new THREE.BufferGeometry().setFromPoints([new THREE.Vector3(), new THREE.Vector3(0, 0, -REACH)]),
            new THREE.LineBasicMaterial({ color: 0x66d9ff }));
        c.add(line);
        c.addEventListener('selectstart', () => {
            const id = pick(c);
            if (id) dotnet.invokeMethodAsync('OnInteract', id);
        });
        rig.add(c);
        controllers.push(c);
        const grip = renderer.xr.getControllerGrip(i);
        grip.add(new THREE.Mesh(new THREE.BoxGeometry(0.05, 0.05, 0.12), mat(0xc9d1d6, { metalness: 0.8 })));
        if (i === 0) {
            vrHud.position.set(0, 0.06, 0.04);
            vrHud.rotation.x = -Math.PI / 3;
            grip.add(vrHud);
        }
        rig.add(grip);
    }
    renderer.xr.addEventListener('sessionstart', () => { camera.position.set(0, 0, 0); });
    renderer.xr.addEventListener('sessionend', () => { camera.position.set(0, 1.6, 0); camera.rotation.set(0, 0, 0); });
}

function pick(source) {
    if (source === camera) {
        raycaster.setFromCamera({ x: 0, y: 0 }, camera);
    } else {
        tmpMat.identity().extractRotation(source.matrixWorld);
        raycaster.ray.origin.setFromMatrixPosition(source.matrixWorld);
        raycaster.ray.direction.set(0, 0, -1).applyMatrix4(tmpMat);
    }
    raycaster.far = REACH + 1.6;
    const hits = raycaster.intersectObjects(targets.filter(t => t.visible), true);
    for (const h of hits) {
        let o = h.object;
        while (o && !o.userData.tid) o = o.parent;
        if (!o || !o.visible) continue;
        const flat = Math.hypot(o.position.x - rig.position.x, o.position.z - rig.position.z);
        if (flat <= REACH) return o.userData.tid;
    }
    return 0;
}

function interact() {
    if (hovered) dotnet.invokeMethodAsync('OnInteract', hovered);
}

// ---------- 이동 / 충돌 ----------

function blocked(x, z, ox, oz) {
    for (const b of walls) if (x > b.x1 - BODY_R && x < b.x2 + BODY_R && z > b.z1 - BODY_R && z < b.z2 + BODY_R) return true;
    for (const c of circles) if (Math.hypot(x - c.x, z - c.z) < c.r + BODY_R) return true;
    if (Math.hypot(x, z) > WORLD_R) return true;
    const wasIn = Math.hypot(ox, oz) < FENCE_R, isIn = Math.hypot(x, z) < FENCE_R;
    if (wasIn !== isIn && !(gateOpen && Math.abs(x) < GATE_W - BODY_R && z < 0)) return true;
    return false;
}

function tryMove(dx, dz) {
    const p = rig.position;
    if (!blocked(p.x + dx, p.z, p.x, p.z)) p.x += dx;
    if (!blocked(p.x, p.z + dz, p.x, p.z)) p.z += dz;
}

const fwd = new THREE.Vector3(), right = new THREE.Vector3();

function updateMovement(dt) {
    const xr = renderer.xr.isPresenting;
    let mx = 0, mz = 0, speed = WALK;
    if (xr) {
        for (const src of renderer.xr.getSession().inputSources) {
            const gp = src.gamepad;
            if (!gp) continue;
            const ax = gp.axes.length >= 4 ? gp.axes[2] : gp.axes[0] ?? 0;
            const ay = gp.axes.length >= 4 ? gp.axes[3] : gp.axes[1] ?? 0;
            if (src.handedness === 'left') { mx = Math.abs(ax) > 0.15 ? ax : 0; mz = Math.abs(ay) > 0.15 ? -ay : 0; }
            if (src.handedness === 'right') {
                if (Math.abs(ax) > 0.7 && snapReady) { yaw -= Math.sign(ax) * Math.PI / 6; snapReady = false; }
                if (Math.abs(ax) < 0.3) snapReady = true;
            }
        }
        camera.getWorldDirection(fwd);
    } else if (!inputBlocked()) {
        mz = (keys.KeyW || keys.ArrowUp ? 1 : 0) - (keys.KeyS || keys.ArrowDown ? 1 : 0);
        mx = (keys.KeyD || keys.ArrowRight ? 1 : 0) - (keys.KeyA || keys.ArrowLeft ? 1 : 0);
        if (keys.ShiftLeft || keys.ShiftRight) speed = RUN;
        fwd.set(-Math.sin(yaw), 0, -Math.cos(yaw));
    }
    fwd.y = 0;
    fwd.normalize();
    right.set(-fwd.z, 0, fwd.x);
    const len = Math.hypot(mx, mz);
    if (len > 0) {
        const k = (speed * dt) / Math.max(1, len);
        tryMove((fwd.x * mz + right.x * mx) * k, (fwd.z * mz + right.z * mx) * k);
    }
    rig.rotation.y = yaw;
    if (!xr) camera.rotation.set(pitch, 0, 0, 'YXZ');
}

function sendMove(now) {
    if (now - lastSend < 1000 / SEND_HZ) return;
    const { x, z } = rig.position;
    if (Math.hypot(x - lastSent.x, z - lastSent.z) < 0.02 && Math.abs(yaw - lastSent.yaw) < 0.02) return;
    lastSend = now;
    lastSent = { x, z, yaw };
    dotnet.invokeMethodAsync('OnLocalMove', x, 0, z, yaw);
}

// ---------- 프레임 ----------

function frame(time) {
    const dt = Math.min(clock.getDelta(), 0.1);
    for (const [i, r] of towerRings.entries()) r.rotation.z += dt * (0.4 + i * 0.3) * (i % 2 ? -1 : 1);

    if (mode === 'preview') {
        previewAngle += dt * 0.35;
        rig.position.set(Math.sin(previewAngle) * 3.2, 0, Math.cos(previewAngle) * 3.2);
        rig.rotation.y = 0;
        camera.position.set(0, 1.4, 0);
        camera.lookAt(0, 1.1, 0);
    } else {
        updateMovement(dt);
        sendMove(time);
        const k = 1 - Math.exp(-10 * dt);
        for (const r of remotes.values()) {
            r.obj.position.x += (r.to.x - r.obj.position.x) * k;
            r.obj.position.z += (r.to.z - r.obj.position.z) * k;
            let d = r.to.yaw + Math.PI - r.obj.rotation.y;
            d = Math.atan2(Math.sin(d), Math.cos(d));
            r.obj.rotation.y += d * k;
        }
        if (haram?.visible) {
            const hx = HARAM_POS.x, hz = HARAM_POS.z;
            if (Math.hypot(rig.position.x - hx, rig.position.z - hz) < 8)
                haram.rotation.y = Math.atan2(rig.position.x - hx, rig.position.z - hz);
        }
        for (const n of nodes.values()) n.rotation.y += dt * 0.5;

        const source = renderer.xr.isPresenting ? controllers[1] ?? controllers[0] : camera;
        const id = pick(source);
        if (id !== hovered) {
            hovered = id;
            dotnet.invokeMethodAsync('OnHover', id);
        }
    }
    renderer.render(scene, camera);
}
