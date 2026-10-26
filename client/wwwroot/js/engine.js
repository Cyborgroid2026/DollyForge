// Dolly Forge 3D 렌더러 (Three.js r160 + WebXR).
// 게임 규칙은 C#(GameSession) 과 Rust 서버가 가진다. 이 모듈은 월드 그리기, 입력,
// 로컬 충돌, 원격 플레이어 보간만 맡고 결과를 C# 으로 콜백한다.
import * as THREE from '../lib/three/three.module.min.js';
import { VRButton } from '../lib/three/addons/webxr/VRButton.js';
import { Reflector } from '../lib/three/addons/objects/Reflector.js';
import { EffectComposer } from '../lib/three/addons/postprocessing/EffectComposer.js';
import { RenderPass } from '../lib/three/addons/postprocessing/RenderPass.js';
import { GTAOPass } from '../lib/three/addons/postprocessing/GTAOPass.js';
import { UnrealBloomPass } from '../lib/three/addons/postprocessing/UnrealBloomPass.js';
import { SMAAPass } from '../lib/three/addons/postprocessing/SMAAPass.js';
import { OutputPass } from '../lib/three/addons/postprocessing/OutputPass.js';
import { makeCyborg, outdoorEnvironment } from './body.js';
import {
    terrainMaterial, trailMaterial, plankMaterial, plasterMaterial, barkMaterial, rockMaterial,
    woodMaterial, metalMaterial, foliageMaterial, worldUV, boxUV, loadSky,
} from './materials.js';

// 이동·충돌·중력은 서버 물리(Rust/Rapier)가 계산한다. 여기서는 입력을 보내고 결과를 그린다.
const FENCE_R = 18, GATE_W = 1.6, REACH = 3.5;
const INPUT_HZ = 20;
const EYE = [1.55, 1.66]; // 성별별 눈높이 (m)
const SPAWN = { x: -6.5, z: 0, yaw: Math.PI / 2 };
const MIRROR = { x: -3.2, z: 3.4 };
const SELF_LAYER = 1; // 내 몸은 1인칭 카메라에는 안 보이고 거울에만 비친다
const HARAM_POS = { x: -22, z: -34 };
const HOUSE_POS = { x: -27, z: -37 };
const RES_COLORS = [0xb8c4cc, 0xdff2ff, 0xc87533, 0x4caf50];

let dotnet, renderer, scene, camera, rig, hemi, sun;
let mode = 'preview';
let yaw = SPAWN.yaw, pitch = 0;
let phase = 0, research = 0, years = 0, gateOpen = false;
let preview = null, previewAngle = 0, previewGender = 0;
let hovered = 0, lastSend = 0, lastInput = '';
let eye = EYE[0], jumpQueued = false;
const self = { x: 0, y: 0, z: 0, vx: 0, vy: 0, vz: 0, t: 0 };
let towerRings = [], gateBars, haram, grave, pillars = [];
let vrHud, vrCtx, vrTex, snapReady = true;
let selfBody = null, selfSpeed = 0, terrain = null;
let composer = null, treeTrunks = null, treeLeaves = null, treeBase = [];
const keys = {};
const trees = [];
const targets = [];               // 상호작용 가능한 루트 오브젝트 (userData.tid)
const remotes = new Map();        // id -> { obj, to }
const nodes = new Map();          // id -> obj (자원 노드)
const props = new Map();          // id -> obj (물리 소품; 자원 노드도 자세 갱신은 여기로)
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
    renderer.toneMapping = THREE.ACESFilmicToneMapping;
    renderer.toneMappingExposure = 0.95;
    renderer.shadowMap.enabled = true;
    renderer.shadowMap.type = THREE.PCFSoftShadowMap;
    host.appendChild(renderer.domElement);

    scene = new THREE.Scene();
    camera = new THREE.PerspectiveCamera(70, host.clientWidth / host.clientHeight, 0.05, 300);
    rig = new THREE.Group();
    rig.add(camera);
    scene.add(rig);
    camera.position.y = 1.6;

    // 크롬이 실제 금속처럼 보이도록 주변 환경을 반사시킨다.
    scene.environment = outdoorEnvironment(renderer); // HDRI 를 불러오기 전 임시 반사 환경
    hemi = new THREE.HemisphereLight(0xffffff, 0x445533, 0.5);
    sun = new THREE.DirectionalLight(0xffffff, 1.6);
    sun.castShadow = true;
    sun.shadow.mapSize.set(2048, 2048);
    sun.shadow.camera.left = sun.shadow.camera.bottom = -18;
    sun.shadow.camera.right = sun.shadow.camera.top = 18;
    sun.shadow.camera.far = 150;
    sun.shadow.bias = -0.0004;
    sun.shadow.normalBias = 0.02;
    scene.add(hemi, sun, sun.target);
    scene.fog = new THREE.Fog(0xcfe8ff, 70, 240);

    buildWorld(stations);
    setupInput();
    setupXr();
    setupComposer(host.clientWidth, host.clientHeight);
    applyLook();
    showPreview(gender);

    addEventListener('resize', () => {
        camera.aspect = host.clientWidth / host.clientHeight;
        camera.updateProjectionMatrix();
        renderer.setSize(host.clientWidth, host.clientHeight);
        composer.setSize(host.clientWidth, host.clientHeight);
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

export function enterWorld(gender) {
    mode = 'world';
    if (preview) { scene.remove(preview); preview = null; }
    eye = EYE[gender === 1 ? 1 : 0];
    selfBody = makeCyborg(gender);
    selfBody.traverse(o => o.layers.set(SELF_LAYER));
    scene.add(selfBody);
    camera.position.set(0, eye, 0);
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
    updateTreeGrowth();
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
    rig.position.set(x, y, z);
    Object.assign(self, { x, y, z, vx: 0, vy: 0, vz: 0, t: performance.now() });
    yaw = yawRad;
    pitch = 0;
}

/// 서버 물리가 계산한 내 위치·속도 (30Hz)
export function setSelf(x, y, z, vx, vy, vz, grounded) {
    Object.assign(self, { x, y, z, vx, vy, vz, t: performance.now() });
}

/// 서버가 생성한 지형·나무·바위
export function initWorld(n, size, heights, treeData, rockData) {
    const geo = worldUV(new THREE.PlaneGeometry(size, size, n - 1, n - 1), size, size, 4);
    geo.rotateX(-Math.PI / 2);
    const pos = geo.attributes.position;
    for (let i = 0; i < n; i++) for (let j = 0; j < n; j++) pos.setY(i * n + j, heights[i * n + j]);
    geo.computeVertexNormals();
    terrain = new THREE.Mesh(geo, terrainMaterial());
    terrain.receiveShadow = true;
    scene.add(terrain);

    buildTrees(treeData);
    for (let k = 0; k < rockData.length; k += 4) {
        const [x, y, z, size] = rockData.slice(k, k + 4);
        const rock = new THREE.Mesh(new THREE.DodecahedronGeometry(size, 2), rockMaterial());
        rock.position.set(x, y + size * 0.35, z);
        rock.scale.y = 0.7;
        rock.rotation.set(x, z, 0);
        rock.castShadow = rock.receiveShadow = true;
        scene.add(rock);
    }
    updateTreeGrowth();
}

export function addProp(id, kind, size, pose) {
    if (props.has(id)) return;
    const obj = makeProp(kind, size);
    applyPose(obj, pose[0], pose[1], pose[2], pose[3], pose[4], pose[5], pose[6]);
    obj.userData.to = null;
    scene.add(obj);
    props.set(id, obj);
}

/// [id, px, py, pz, qx, qy, qz, qw] × N
export function setPropPoses(a) {
    for (let k = 0; k < a.length; k += 8) {
        const obj = props.get(a[k]);
        if (!obj) continue;
        obj.userData.to = { p: new THREE.Vector3(a[k + 1], a[k + 2], a[k + 3]), q: new THREE.Quaternion(a[k + 4], a[k + 5], a[k + 6], a[k + 7]) };
    }
}

function applyPose(obj, px, py, pz, qx, qy, qz, qw) {
    obj.position.set(px, py, pz);
    obj.quaternion.set(qx, qy, qz, qw);
}

export function upsertPlayer(id, gender, x, y, z, yawRad) {
    let r = remotes.get(id);
    if (!r) {
        const obj = makeCyborg(gender);
        const tag = makeLabel(`돌리 #${id}`, 0.9);
        tag.position.y = 2.1;
        obj.add(tag);
        scene.add(obj);
        r = { obj, to: { x, y, z, yaw: yawRad }, speed: 0 };
        remotes.set(id, r);
        obj.position.set(x, y, z);
    }
    r.to = { x, y, z, yaw: yawRad };
}

export function movePlayer(id, x, y, z, yawRad) {
    const r = remotes.get(id);
    if (r) r.to = { x, y, z, yaw: yawRad };
}

export function removePlayer(id) {
    const r = remotes.get(id);
    if (r) { scene.remove(r.obj); remotes.delete(id); }
}

export function addNode(id, kind, x, y, z) {
    if (nodes.has(id)) return;
    const obj = makeResource(kind);
    obj.position.set(x, y, z);
    obj.userData.tid = id;
    obj.userData.beacon.visible = research >= 1;
    scene.add(obj);
    targets.push(obj);
    nodes.set(id, obj);
    props.set(id, obj);
}

export function removeNode(id) {
    const obj = nodes.get(id);
    if (!obj) return;
    scene.remove(obj);
    nodes.delete(id);
    props.delete(id);
    const i = targets.indexOf(obj);
    if (i >= 0) targets.splice(i, 1);
}

export function clearWorld() {
    for (const id of [...remotes.keys()]) removePlayer(id);
    for (const id of [...nodes.keys()]) removeNode(id);
    for (const obj of props.values()) scene.remove(obj);
    props.clear();
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
    m.castShadow = m.receiveShadow = true;
    parent.add(m);
    return m;
}

function cyl(rt, rb, h, color, x, y, z, parent = scene, opts) {
    const m = new THREE.Mesh(new THREE.CylinderGeometry(rt, rb, h, 16), mat(color, opts));
    m.position.set(x, y, z);
    m.castShadow = m.receiveShadow = true;
    parent.add(m);
    return m;
}

function wall(w, h, d, color, x, z) {
    texBox(w, h, d, plankMaterial(), x, h / 2, z);
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

function buildWorld(stations) {
    // 지면(지형)은 서버의 initWorld 로 생성된다.
    const plaza = new THREE.Mesh(worldUV(new THREE.CircleGeometry(FENCE_R, 64), FENCE_R * 2, FENCE_R * 2, 4), trailMaterial());
    plaza.rotation.x = -Math.PI / 2;
    plaza.position.y = 0.01;
    plaza.receiveShadow = true;
    scene.add(plaza);
    const path = new THREE.Mesh(worldUV(new THREE.PlaneGeometry(3, 30), 3, 30, 3), trailMaterial());
    path.rotation.x = -Math.PI / 2;
    path.position.set(-6, 0.015, -31);
    path.rotation.z = -0.45;
    scene.add(path);

    buildPavilion(-9, -1, 0xd8c8a8);   // 집 (뒷벽이 -x)
    buildPavilion(9, 1, 0xb8c8d0);     // 연구소 (뒷벽이 +x)
    buildTower();
    buildFence();
    buildHaramHouse();
    buildMirror();

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

function buildMirror() {
    const g = new THREE.Group();
    g.position.set(MIRROR.x, 0, MIRROR.z);
    g.rotation.y = Math.atan2(SPAWN.x - MIRROR.x, SPAWN.z - MIRROR.z);
    const mirror = new Reflector(new THREE.PlaneGeometry(1.1, 2.1), {
        textureWidth: 1024, textureHeight: 2048, color: 0xc8d0d8,
    });
    mirror.camera.layers.enableAll(); // 거울에는 내 몸(SELF_LAYER)도 비친다
    mirror.position.set(0, 1.15, 0.03);
    g.add(mirror);
    box(1.24, 2.3, 0.06, 0x5a3e2b, 0, 1.15, -0.01, g);
    box(0.5, 0.06, 0.4, 0x5a3e2b, 0, 0.03, -0.1, g);
    const label = makeLabel('전신 거울', 0.8);
    label.position.y = 2.55;
    g.add(label);
    scene.add(g);
}

function texBox(w, h, d, material, x, y, z, parent = scene, tile = 2) {
    const m = new THREE.Mesh(boxUV(new THREE.BoxGeometry(w, h, d), w, h, d, tile), material);
    m.position.set(x, y, z);
    m.castShadow = m.receiveShadow = true;
    parent.add(m);
    return m;
}

function buildPavilion(cx, back, color) {
    const bx = cx + back * 4;
    const walls = plasterMaterial();
    texBox(8, 0.2, 7, plankMaterial(), cx, 0.1, 0);
    texBox(0.25, 3, 7, walls, bx, 1.5, 0);
    texBox(8, 3, 0.25, walls, cx, 1.5, -3.5);
    texBox(8, 3, 0.25, walls, cx, 1.5, 3.5);
    texBox(8.8, 0.25, 7.8, plankMaterial(), cx, 3.1, 0);
    for (const z of [-3.4, 3.4]) {
        const post = new THREE.Mesh(worldUV(new THREE.CylinderGeometry(0.12, 0.12, 3, 16), 0.75, 3, 1), barkMaterial());
        post.position.set(cx - back * 4, 1.5, z);
        post.castShadow = true;
        scene.add(post);
    }
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
    }
    const label = makeLabel('하람의 오두막', 1);
    label.position.set(x, 3.2, z + 2.8);
    scene.add(label);
}

// 소나무: 실사 껍질 텍스처의 줄기 + 실사 가지 텍스처 카드를 층층이. GPU 인스턴싱으로 한 번에 그린다.
const CARD_LAYERS = 7, CARDS_PER_LAYER = 4;

function buildTrees(data) {
    const count = data.length / 4;
    const trunkGeo = new THREE.CylinderGeometry(0.09, 0.26, 6.5, 12, 1, true);
    trunkGeo.translate(0, 3.1, 0);
    worldUV(trunkGeo, 1.2, 6.5, 1.5);
    treeTrunks = new THREE.InstancedMesh(trunkGeo, barkMaterial(), count);
    const cardGeo = new THREE.PlaneGeometry(1, 1);
    treeLeaves = new THREE.InstancedMesh(cardGeo, foliageMaterial(), count * CARD_LAYERS * CARDS_PER_LAYER);
    treeTrunks.castShadow = treeTrunks.receiveShadow = true;
    treeLeaves.castShadow = treeLeaves.receiveShadow = true;
    treeBase = [];
    for (let k = 0; k < data.length; k += 4) treeBase.push({ x: data[k], y: data[k + 1], z: data[k + 2], s: data[k + 3], seed: k * 7.13 });
    scene.add(treeTrunks, treeLeaves);
}

const _m = new THREE.Matrix4(), _q = new THREE.Quaternion(), _e = new THREE.Euler(), _v = new THREE.Vector3(), _s = new THREE.Vector3();

function updateTreeGrowth() {
    if (!treeTrunks) return;
    const grow = 1 + years * 0.03;
    let c = 0;
    treeBase.forEach((t, i) => {
        const s = t.s * grow;
        _m.compose(_v.set(t.x, t.y - 0.1, t.z), _q.identity(), _s.set(s, s, s));
        treeTrunks.setMatrixAt(i, _m);
        for (let l = 0; l < CARD_LAYERS; l++) {
            const h = (1.6 + l * 0.68) * s;
            const width = (2.6 - l * 0.32) * s;
            for (let j = 0; j < CARDS_PER_LAYER; j++) {
                const a = j * Math.PI / CARDS_PER_LAYER + l * 0.7 + t.seed;
                _e.set(-0.25, a, 0, 'YXZ');
                _q.setFromEuler(_e);
                _m.compose(_v.set(t.x, t.y + h, t.z), _q, _s.set(width, width * 0.7, 1));
                treeLeaves.setMatrixAt(c++, _m);
            }
        }
    });
    treeTrunks.instanceMatrix.needsUpdate = true;
    treeLeaves.instanceMatrix.needsUpdate = true;
}

function makeProp(kind, size) {
    let mesh;
    if (kind === 10) mesh = new THREE.Mesh(new THREE.BoxGeometry(...size), woodMaterial());
    else if (kind === 11) mesh = new THREE.Mesh(new THREE.CylinderGeometry(size[0] / 2, size[0] / 2, size[1], 20), mat(0x6e4a2a, { roughness: 0.7 }));
    else if (kind === 12) mesh = new THREE.Mesh(new THREE.SphereGeometry(size[0] / 2, 24, 16), mat(0xf2f2f2, { roughness: 0.45 }));
    else mesh = new THREE.Mesh(new THREE.SphereGeometry(size[0] / 2, 32, 20), mat(0xb03a2e, { roughness: 0.35 }));
    mesh.castShadow = mesh.receiveShadow = true;
    return mesh;
}

function buildStation(s) {
    const g = new THREE.Group();
    g.position.set(s.x, 0, s.z);
    const glow = (c) => mat(c, { emissive: c, emissiveIntensity: 1.2 });
    switch (s.id) {
        case 1: // 충전 독
            texBox(0.9, 2.1, 0.5, metalMaterial(), 0, 1.05, 0, g, 1);
            box(0.5, 0.08, 0.52, 0, 0, 1.6, 0, g, glow(0x66e0ff));
            box(0.5, 0.08, 0.52, 0, 0, 1.3, 0, g, glow(0x66e0ff));
            break;
        case 2: // 영양 식탁
            texBox(1.4, 0.08, 0.8, woodMaterial(), 0, 0.8, 0, g, 1);
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
            texBox(1.2, 0.06, 0.7, woodMaterial(), 0, 0.78, 0, g, 1);
            texBox(1.1, 0.75, 0.6, woodMaterial(), 0, 0.375, 0, g, 1);
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
            texBox(1.6, 1.6, 0.35, woodMaterial(), 0, 0.8, 0, g, 1);
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
            texBox(1.0, 1.1, 0.7, metalMaterial(), 0, 0.55, 0, g, 1);
            for (let i = -1; i <= 1; i++) cyl(0.07, 0.07, 0.35, 0, i * 0.25, 1.28, 0, g, glow(0xff9a40));
            break;
        case 13: // 작업대
            texBox(1.6, 0.08, 0.9, woodMaterial(), 0, 0.9, 0, g, 1);
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
            break;
        }
        default:
            return null;
    }
    scene.add(g);
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

// 자원 노드: 서버 물리 몸체(바닥이 평평한 덩어리)의 중심이 원점
function makeResource(kind) {
    const g = new THREE.Group();
    const c = RES_COLORS[kind];
    const chunk = (sx, sy, sz, m) => {
        const mesh = new THREE.Mesh(new THREE.DodecahedronGeometry(1, 0), m);
        mesh.scale.set(sx, sy, sz);
        mesh.castShadow = mesh.receiveShadow = true;
        g.add(mesh);
        return mesh;
    };
    if (kind === 0) chunk(0.33, 0.24, 0.29, mat(c, { metalness: 0.85, roughness: 0.38, flatShading: true }));
    else if (kind === 2) chunk(0.29, 0.22, 0.27, mat(c, { metalness: 0.9, roughness: 0.42, flatShading: true }));
    else if (kind === 1) {
        chunk(0.22, 0.12, 0.2, mat(0x8a8a86, { flatShading: true })).position.y = -0.12;
        for (let i = 0; i < 4; i++) {
            const m = new THREE.Mesh(new THREE.OctahedronGeometry(0.09 + i * 0.02),
                new THREE.MeshPhysicalMaterial({ color: c, roughness: 0.05, transmission: 0.6, thickness: 0.2 }));
            m.position.set(Math.cos(i * 1.6) * 0.07, 0.02 + i * 0.03, Math.sin(i * 1.6) * 0.07);
            m.scale.y = 2;
            m.castShadow = true;
            g.add(m);
        }
    } else {
        for (let i = 0; i < 6; i++) {
            const m = new THREE.Mesh(new THREE.ConeGeometry(0.05, 0.32, 6), mat(c, { roughness: 0.8 }));
            m.position.set(Math.cos(i * 1.1) * 0.07, 0.04, Math.sin(i * 1.1) * 0.07);
            m.rotation.set(Math.sin(i) * 0.3, 0, Math.cos(i) * 0.3);
            m.castShadow = true;
            g.add(m);
        }
    }
    // 증폭기 연구 후 보이는 광선 (노드 회전과 무관하게 수직 유지)
    const beacon = new THREE.Mesh(new THREE.CylinderGeometry(0.05, 0.05, 12, 8, 1, true),
        new THREE.MeshBasicMaterial({ color: c, transparent: true, opacity: 0.3, depthWrite: false }));
    beacon.position.y = 6;
    const holder = new THREE.Group();
    holder.add(beacon);
    g.add(holder);
    g.userData.beacon = beacon;
    g.userData.holder = holder;
    return g;
}

// ---------- 조명 ----------

function setupComposer(w, h) {
    composer = new EffectComposer(renderer);
    composer.addPass(new RenderPass(scene, camera));
    const ao = new GTAOPass(scene, camera, w, h);
    ao.updateGtaoMaterial({ radius: 0.6, distanceExponent: 1.5, thickness: 1.2, scale: 1.0 });
    ao.blendIntensity = 0.85;
    composer.addPass(ao);
    composer.addPass(new UnrealBloomPass(new THREE.Vector2(w, h), 0.18, 0.4, 0.92));
    composer.addPass(new SMAAPass(w * renderer.getPixelRatio(), h * renderer.getPixelRatio()));
    composer.addPass(new OutputPass());
}

const LOOKS = [
    { sky: 0xcfe8ff, sun: [30, 50, 20], si: 1.6, hi: 0.9, sc: 0xffffff },   // 일상: 아침
    { sky: 0xe6f3ff, sun: [0, 60, 5], si: 1.8, hi: 1.0, sc: 0xffffff },     // 연구: 한낮
    { sky: 0xf2a46b, sun: [-40, 14, -30], si: 1.2, hi: 0.6, sc: 0xffb070 }, // 외출: 노을
    { sky: 0x5b4a7a, sun: [-30, 10, 0], si: 0.8, hi: 0.5, sc: 0xc8a0ff },   // 선택
];

let skyToken = 0;
function applyLook() {
    const L = LOOKS[Math.min(phase, 3)];
    if (!scene.background) scene.background = new THREE.Color(L.sky);
    scene.fog.color.set(L.sky);
    const token = ++skyToken;
    loadSky(renderer, phase >= 2 ? 'dusk' : 'day').then((sky) => {
        if (token !== skyToken) return;
        scene.background = sky.background;
        scene.environment = sky.environment;
        scene.backgroundIntensity = phase >= 3 ? 0.55 : 1;
    }).catch(() => {});
    sun.position.copy(sun.target.position).add(new THREE.Vector3(...L.sun));
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
        if (e.code === 'Space') { e.preventDefault(); jumpQueued = true; }
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
    renderer.xr.addEventListener('sessionend', () => { camera.position.set(0, eye, 0); camera.rotation.set(0, 0, 0); });
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

// ---------- 입력 → 서버 물리 ----------

const fwd = new THREE.Vector3(), right = new THREE.Vector3();
let runToggle = false, vrJumpHeld = false, vrRunHeld = false;

/// 입력을 월드 좌표 이동 의도로 바꿔 서버로 보낸다. 실제 이동은 서버 물리가 결정한다.
function updateInput(now) {
    const xr = renderer.xr.isPresenting;
    let mx = 0, mz = 0, run = false, jump = jumpQueued;
    if (xr) {
        for (const src of renderer.xr.getSession().inputSources) {
            const gp = src.gamepad;
            if (!gp) continue;
            const ax = gp.axes.length >= 4 ? gp.axes[2] : gp.axes[0] ?? 0;
            const ay = gp.axes.length >= 4 ? gp.axes[3] : gp.axes[1] ?? 0;
            if (src.handedness === 'left') {
                mx = Math.abs(ax) > 0.15 ? ax : 0;
                mz = Math.abs(ay) > 0.15 ? -ay : 0;
                const stick = gp.buttons[3]?.pressed; // 왼쪽 스틱 누르기: 달리기 전환
                if (stick && !vrRunHeld) runToggle = !runToggle;
                vrRunHeld = !!stick;
            }
            if (src.handedness === 'right') {
                if (Math.abs(ax) > 0.7 && snapReady) { yaw -= Math.sign(ax) * Math.PI / 6; snapReady = false; }
                if (Math.abs(ax) < 0.3) snapReady = true;
                const a = gp.buttons[4]?.pressed; // A 버튼: 점프
                if (a && !vrJumpHeld) jump = true;
                vrJumpHeld = !!a;
            }
        }
        run = runToggle;
        camera.getWorldDirection(fwd);
    } else if (!inputBlocked()) {
        mz = (keys.KeyW || keys.ArrowUp ? 1 : 0) - (keys.KeyS || keys.ArrowDown ? 1 : 0);
        mx = (keys.KeyD || keys.ArrowRight ? 1 : 0) - (keys.KeyA || keys.ArrowLeft ? 1 : 0);
        run = !!(keys.ShiftLeft || keys.ShiftRight);
        fwd.set(-Math.sin(yaw), 0, -Math.cos(yaw));
    }
    fwd.y = 0;
    fwd.normalize();
    right.set(-fwd.z, 0, fwd.x);
    const len = Math.max(1, Math.hypot(mx, mz));
    const wx = (fwd.x * mz + right.x * mx) / len, wz = (fwd.z * mz + right.z * mx) / len;
    rig.rotation.y = yaw;
    if (!xr) camera.rotation.set(pitch, 0, 0, 'YXZ');

    const flags = (jump ? 1 : 0) | (run ? 2 : 0);
    const key = `${wx.toFixed(2)},${wz.toFixed(2)},${yaw.toFixed(2)},${flags}`;
    // 바뀌었을 때 즉시, 아니면 0.25초마다 유지 신호
    if ((key !== lastInput && now - lastSend > 1000 / INPUT_HZ) || now - lastSend > 250) {
        lastSend = now;
        lastInput = key;
        jumpQueued = false;
        dotnet.invokeMethodAsync('OnInput', wx, wz, yaw, flags);
    }
}

/// 서버 위치를 향해 부드럽게 따라간다 (속도로 짧게 외삽)
function followServer(dt) {
    const age = Math.min((performance.now() - self.t) / 1000, 0.1);
    const tx = self.x + self.vx * age, ty = self.y + self.vy * age, tz = self.z + self.vz * age;
    const k = 1 - Math.exp(-18 * dt);
    const p = rig.position;
    if (Math.hypot(tx - p.x, tz - p.z) > 3) p.set(tx, ty, tz);
    else p.set(p.x + (tx - p.x) * k, p.y + (ty - p.y) * k, p.z + (tz - p.z) * k);
}

// 그림자 카메라를 플레이어 주변에 붙여 넓은 월드에서도 선명한 그림자를 유지한다.
const sunOffset = new THREE.Vector3();
function followShadow(x, z) {
    sunOffset.copy(sun.position).sub(sun.target.position);
    sun.target.position.set(x, 0, z);
    sun.position.copy(sun.target.position).add(sunOffset);
}

// ---------- 프레임 ----------

function frame(time) {
    const dt = Math.min(clock.getDelta(), 0.1);
    for (const [i, r] of towerRings.entries()) r.rotation.z += dt * (0.4 + i * 0.3) * (i % 2 ? -1 : 1);

    if (mode === 'preview') {
        previewAngle += dt * 0.3;
        rig.position.set(Math.sin(previewAngle) * 2.6, 0, Math.cos(previewAngle) * 2.6);
        rig.rotation.y = 0;
        camera.position.set(0, 1.25, 0);
        camera.lookAt(0, 0.95, 0);
        preview?.userData.animate(dt, 0);
        followShadow(0, 0);
    } else {
        updateInput(time);
        followServer(dt);
        const k = 1 - Math.exp(-10 * dt);
        if (selfBody) {
            const { x, y, z } = rig.position;
            const v = Math.hypot(self.vx, self.vz);
            selfSpeed += (v - selfSpeed) * Math.min(1, dt * 10);
            selfBody.position.set(x, y, z);
            selfBody.rotation.y = yaw + Math.PI;
            selfBody.userData.animate(dt, selfSpeed);
        }
        followShadow(rig.position.x, rig.position.z);
        for (const r of remotes.values()) {
            const px = r.obj.position.x, pz = r.obj.position.z;
            r.obj.position.x += (r.to.x - r.obj.position.x) * k;
            r.obj.position.y += (r.to.y - r.obj.position.y) * k;
            r.obj.position.z += (r.to.z - r.obj.position.z) * k;
            const v = Math.hypot(r.obj.position.x - px, r.obj.position.z - pz) / Math.max(dt, 1e-3);
            r.speed += (v - r.speed) * Math.min(1, dt * 10);
            r.obj.userData.animate(dt, r.speed);
            let d = r.to.yaw + Math.PI - r.obj.rotation.y;
            d = Math.atan2(Math.sin(d), Math.cos(d));
            r.obj.rotation.y += d * k;
        }
        if (haram?.visible) {
            const hx = HARAM_POS.x, hz = HARAM_POS.z;
            if (Math.hypot(rig.position.x - hx, rig.position.z - hz) < 8)
                haram.rotation.y = Math.atan2(rig.position.x - hx, rig.position.z - hz);
        }
        const pk = 1 - Math.exp(-20 * dt);
        for (const obj of props.values()) {
            const to = obj.userData.to;
            if (to) {
                obj.position.lerp(to.p, pk);
                obj.quaternion.slerp(to.q, pk);
            }
            obj.userData.holder?.quaternion.copy(obj.quaternion).invert();
        }

        const source = renderer.xr.isPresenting ? controllers[1] ?? controllers[0] : camera;
        const id = pick(source);
        if (id !== hovered) {
            hovered = id;
            dotnet.invokeMethodAsync('OnHover', id);
        }
    }
    if (renderer.xr.isPresenting) renderer.render(scene, camera);
    else composer.render(dt);
}
