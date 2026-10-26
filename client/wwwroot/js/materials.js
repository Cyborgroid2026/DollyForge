// 실사 재질: Poly Haven CC0 스캔 텍스처(색·노멀·거칠기)와 HDRI 하늘.
// 에셋은 tools/fetch-assets.sh 가 wwwroot/assets 에 내려받는다. 없으면 단색으로 대체된다.
import * as THREE from '../lib/three/three.module.min.js';
import { RGBELoader } from '../lib/three/addons/loaders/RGBELoader.js';

const loader = new THREE.TextureLoader();
const cache = new Map();

function tex(path, srgb) {
    const key = path + srgb;
    if (!cache.has(key)) {
        const t = loader.load(path, undefined, undefined, () => {});
        t.wrapS = t.wrapT = THREE.RepeatWrapping;
        t.anisotropy = 8;
        if (srgb) t.colorSpace = THREE.SRGBColorSpace;
        cache.set(key, t);
    }
    return cache.get(key);
}

/// 스캔 재질. repeat 는 텍스처 한 장이 덮는 실제 크기(m)를 기준으로 UV 에서 조절한다.
export function pbr(name, opts = {}) {
    const dir = `assets/tex/${name}`;
    return new THREE.MeshStandardMaterial({
        map: tex(`${dir}/diff.jpg`, true),
        normalMap: tex(`${dir}/nor_gl.jpg`, false),
        roughnessMap: tex(`${dir}/rough.jpg`, false),
        ...opts,
    });
}

const shared = {};
export const terrainMaterial = () => (shared.terrain ??= pbr('aerial_grass_rock'));
export const trailMaterial = () => (shared.trail ??= pbr('rocky_trail'));
export const plankMaterial = () => (shared.plank ??= pbr('weathered_planks'));
export const plasterMaterial = () => (shared.plaster ??= pbr('white_plaster_02'));
export const barkMaterial = () => (shared.bark ??= pbr('bark_brown_02'));
export const rockMaterial = () => (shared.rock ??= pbr('rock_boulder_dry'));
export const woodMaterial = () => (shared.wood ??= pbr('plywood'));
export const metalMaterial = () => (shared.metal ??= pbr('metal_plate', { metalness: 0.85 }));

export function foliageMaterial() {
    return (shared.foliage ??= new THREE.MeshStandardMaterial({
        map: tex('assets/tex/pine/twig_diff.jpg', true),
        alphaMap: tex('assets/tex/pine/twig_alpha.jpg', false),
        normalMap: tex('assets/tex/pine/twig_nor_gl.jpg', false),
        alphaTest: 0.45,
        side: THREE.DoubleSide,
        roughness: 0.85,
    }));
}

/// UV 를 실제 치수(m) / tile 로 늘려 텍스처가 늘어나지 않게 한다. (평면·상자 지오메트리용)
export function worldUV(geo, sx, sy, tile) {
    const uv = geo.attributes.uv;
    for (let i = 0; i < uv.count; i++) uv.setXY(i, uv.getX(i) * sx / tile, uv.getY(i) * sy / tile);
    uv.needsUpdate = true;
    return geo;
}

/// 상자: 면마다 실제 가로·세로 길이에 맞춰 UV 를 조정
export function boxUV(geo, w, h, d, tile) {
    const uv = geo.attributes.uv;
    const dims = [[d, h], [d, h], [w, d], [w, d], [w, h], [w, h]]; // +x -x +y -y +z -z
    for (let f = 0; f < 6; f++) {
        for (let v = 0; v < 4; v++) {
            const i = f * 4 + v;
            uv.setXY(i, uv.getX(i) * dims[f][0] / tile, uv.getY(i) * dims[f][1] / tile);
        }
    }
    uv.needsUpdate = true;
    return geo;
}

/// HDRI 하늘: 배경과 반사·조명 환경을 함께 설정한다.
const SKIES = { day: 'kloofendal_48d_partly_cloudy_puresky', dusk: 'kloppenheim_06_puresky' };
const skyCache = new Map();
export function loadSky(renderer, which) {
    if (!skyCache.has(which)) {
        skyCache.set(which, new Promise((resolve, reject) => {
            new RGBELoader().load(`assets/hdri/${SKIES[which]}.hdr`, (hdr) => {
                hdr.mapping = THREE.EquirectangularReflectionMapping;
                const pmrem = new THREE.PMREMGenerator(renderer);
                const env = pmrem.fromEquirectangular(hdr).texture;
                pmrem.dispose();
                resolve({ background: hdr, environment: env });
            }, undefined, reject);
        }));
    }
    return skyCache.get(which);
}
