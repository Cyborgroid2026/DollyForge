//! 서버 권위 물리 (Rapier 3D). 60Hz 고정 스텝.
//! 실제 물리 값: 중력 9.81 m/s², 실제 재질 밀도(kg/m³), 사람 보행·달리기·점프 속도.
//! 클라이언트는 입력만 보내고, 위치는 모두 여기서 계산된다.

use crate::data::*;
use rapier3d::control::{CharacterAutostep, CharacterCollision, CharacterLength, KinematicCharacterController};
use rapier3d::na::DMatrix;
use rapier3d::prelude::*;
use std::collections::HashMap;

pub const DT: f32 = 1.0 / PHYSICS_HZ as f32;

// 충돌 그룹
const G_WORLD: Group = Group::GROUP_1;
const G_GATE: Group = Group::GROUP_2;
const G_PLAYER: Group = Group::GROUP_3;
const G_PROP: Group = Group::GROUP_4;

#[derive(Clone, Copy, Default)]
pub struct Input {
    pub mx: f32,
    pub mz: f32,
    pub yaw: f32,
    pub jump: bool,
    pub run: bool,
}

pub struct Avatar {
    body: RigidBodyHandle,
    collider: ColliderHandle,
    half: f32,
    radius: f32,
    pub vel: Vector<Real>,
    pub grounded: bool,
    pub input: Input,
    jump_latch: bool,
}

pub struct Prop {
    pub kind: u8,
    pub size: [f32; 3],
    pub body: RigidBodyHandle,
}

/// 지형: n×n 격자, 한 변 길이 size(m), 원점 중심. heights[i*n + j], i 는 z 방향, j 는 x 방향.
pub struct Terrain {
    pub n: usize,
    pub size: f32,
    pub heights: Vec<f32>,
}

impl Terrain {
    fn generate(seed: u32) -> Terrain {
        let n = TERRAIN_N;
        let size = TERRAIN_SIZE;
        let mut heights = vec![0.0; n * n];
        let (hx, hz) = HARAM_HOUSE;
        for i in 0..n {
            for j in 0..n {
                let x = -size / 2.0 + size * j as f32 / (n - 1) as f32;
                let z = -size / 2.0 + size * i as f32 / (n - 1) as f32;
                let r = x.hypot(z);
                // 마을·오두막·오솔길은 평지, 나머지는 완만한 언덕
                let mut mask = smoothstep(FENCE_RADIUS + 2.0, FENCE_RADIUS + 12.0, r);
                mask *= smoothstep(7.0, 14.0, (x - hx).hypot(z - hz));
                mask *= smoothstep(2.5, 7.0, dist_to_segment(x, z, 0.0, -FENCE_RADIUS, hx + 4.0, hz + 2.0));
                let hills = fbm(x * 0.035, z * 0.035, seed) * 4.5 + fbm(x * 0.12, z * 0.12, seed ^ 0x9e37) * 0.6;
                // 월드 가장자리는 높아져 자연스러운 경계가 된다
                let rim = smoothstep(WORLD_RADIUS - 6.0, WORLD_RADIUS + 10.0, r) * 9.0;
                heights[i * n + j] = mask * hills + rim;
            }
        }
        Terrain { n, size, heights }
    }

    /// 격자 삼각형과 같은 방식의 보간 높이
    pub fn height_at(&self, x: f32, z: f32) -> f32 {
        let n = self.n;
        let cell = self.size / (n - 1) as f32;
        let fx = ((x + self.size / 2.0) / cell).clamp(0.0, (n - 1) as f32 - 0.001);
        let fz = ((z + self.size / 2.0) / cell).clamp(0.0, (n - 1) as f32 - 0.001);
        let (j, i) = (fx as usize, fz as usize);
        let (tx, tz) = (fx - j as f32, fz - i as f32);
        let h = |i: usize, j: usize| self.heights[i * n + j];
        let top = h(i, j) * (1.0 - tx) + h(i, j + 1) * tx;
        let bottom = h(i + 1, j) * (1.0 - tx) + h(i + 1, j + 1) * tx;
        top * (1.0 - tz) + bottom * tz
    }
}

fn smoothstep(e0: f32, e1: f32, x: f32) -> f32 {
    let t = ((x - e0) / (e1 - e0)).clamp(0.0, 1.0);
    t * t * (3.0 - 2.0 * t)
}

fn dist_to_segment(px: f32, pz: f32, ax: f32, az: f32, bx: f32, bz: f32) -> f32 {
    let (dx, dz) = (bx - ax, bz - az);
    let t = (((px - ax) * dx + (pz - az) * dz) / (dx * dx + dz * dz)).clamp(0.0, 1.0);
    (px - ax - t * dx).hypot(pz - az - t * dz)
}

fn hash2(x: i32, z: i32, seed: u32) -> f32 {
    let mut h = (x as u32).wrapping_mul(0x8da6b343) ^ (z as u32).wrapping_mul(0xd8163841) ^ seed.wrapping_mul(0xcb1ab31f);
    h ^= h >> 13;
    h = h.wrapping_mul(0x5bd1e995);
    h ^= h >> 15;
    (h & 0xffff) as f32 / 65535.0 * 2.0 - 1.0
}

fn value_noise(x: f32, z: f32, seed: u32) -> f32 {
    let (xi, zi) = (x.floor() as i32, z.floor() as i32);
    let (tx, tz) = (x - xi as f32, z - zi as f32);
    let (sx, sz) = (tx * tx * (3.0 - 2.0 * tx), tz * tz * (3.0 - 2.0 * tz));
    let a = hash2(xi, zi, seed) * (1.0 - sx) + hash2(xi + 1, zi, seed) * sx;
    let b = hash2(xi, zi + 1, seed) * (1.0 - sx) + hash2(xi + 1, zi + 1, seed) * sx;
    a * (1.0 - sz) + b * sz
}

fn fbm(x: f32, z: f32, seed: u32) -> f32 {
    let (mut sum, mut amp, mut freq) = (0.0, 0.5, 1.0);
    for o in 0..4 {
        sum += value_noise(x * freq, z * freq, seed.wrapping_add(o)) * amp;
        amp *= 0.5;
        freq *= 2.0;
    }
    sum
}

pub struct Tree {
    pub x: f32,
    pub y: f32,
    pub z: f32,
    pub scale: f32,
}

pub struct Rock {
    pub x: f32,
    pub y: f32,
    pub z: f32,
    pub size: f32,
}

pub struct Physics {
    pub bodies: RigidBodySet,
    pub colliders: ColliderSet,
    pipeline: PhysicsPipeline,
    params: IntegrationParameters,
    islands: IslandManager,
    broad: BroadPhase,
    narrow: NarrowPhase,
    impulse_joints: ImpulseJointSet,
    multibody_joints: MultibodyJointSet,
    ccd: CCDSolver,
    query: QueryPipeline,
    controller: KinematicCharacterController,
    pub terrain: Terrain,
    pub trees: Vec<Tree>,
    pub rocks: Vec<Rock>,
    pub avatars: HashMap<u32, Avatar>,
    pub props: HashMap<u32, Prop>,
}

/// 결정적 난수 (월드 배치용)
struct Lcg(u64);
impl Lcg {
    fn next(&mut self) -> f32 {
        self.0 = self.0.wrapping_mul(6364136223846793005).wrapping_add(1442695040888963407);
        (self.0 >> 40) as f32 / (1u64 << 24) as f32
    }
}

fn fixed(colliders: &mut ColliderSet, builder: ColliderBuilder) {
    colliders.insert(builder.collision_groups(InteractionGroups::new(G_WORLD, Group::ALL)).friction(0.8).build());
}

fn cuboid(colliders: &mut ColliderSet, w: f32, h: f32, d: f32, x: f32, y: f32, z: f32) {
    fixed(colliders, ColliderBuilder::cuboid(w / 2.0, h / 2.0, d / 2.0).translation(vector![x, y, z]));
}

fn column(colliders: &mut ColliderSet, r: f32, h: f32, x: f32, y0: f32, z: f32) {
    fixed(colliders, ColliderBuilder::cylinder(h / 2.0, r).translation(vector![x, y0 + h / 2.0, z]));
}

impl Physics {
    pub fn new() -> Physics {
        let mut p = Physics {
            bodies: RigidBodySet::new(),
            colliders: ColliderSet::new(),
            pipeline: PhysicsPipeline::new(),
            params: IntegrationParameters { dt: DT, ..Default::default() },
            islands: IslandManager::new(),
            broad: BroadPhase::new(),
            narrow: NarrowPhase::new(),
            impulse_joints: ImpulseJointSet::new(),
            multibody_joints: MultibodyJointSet::new(),
            ccd: CCDSolver::new(),
            query: QueryPipeline::new(),
            controller: KinematicCharacterController {
                offset: CharacterLength::Absolute(0.02),
                autostep: Some(CharacterAutostep {
                    max_height: CharacterLength::Absolute(0.28), // 계단 한 칸 정도
                    min_width: CharacterLength::Absolute(0.15),
                    include_dynamic_bodies: false,
                }),
                snap_to_ground: Some(CharacterLength::Absolute(0.3)),
                max_slope_climb_angle: 38f32.to_radians(),
                min_slope_slide_angle: 45f32.to_radians(),
                ..Default::default()
            },
            terrain: Terrain::generate(TERRAIN_SEED),
            trees: Vec::new(),
            rocks: Vec::new(),
            avatars: HashMap::new(),
            props: HashMap::new(),
        };
        p.build_static_world();
        p.build_props();
        p.query.update(&p.bodies, &p.colliders);
        p
    }

    fn build_static_world(&mut self) {
        let t = &self.terrain;
        let hm = DMatrix::from_fn(t.n, t.n, |i, j| t.heights[i * t.n + j]);
        fixed(&mut self.colliders, ColliderBuilder::heightfield(hm, vector![t.size, 1.0, t.size]));
        let c = &mut self.colliders;

        // 집(뒷벽 -x)과 연구소(뒷벽 +x)
        for (cx, back) in [(-9.0f32, -1.0f32), (9.0, 1.0)] {
            cuboid(c, 8.0, 0.2, 7.0, cx, 0.1, 0.0);
            cuboid(c, 0.25, 3.0, 7.0, cx + back * 4.0, 1.5, 0.0);
            cuboid(c, 8.0, 3.0, 0.25, cx, 1.5, -3.5);
            cuboid(c, 8.0, 3.0, 0.25, cx, 1.5, 3.5);
            cuboid(c, 8.8, 0.25, 7.8, cx, 3.1, 0.0);
            for z in [-3.4, 3.4] {
                column(c, 0.12, 3.0, cx - back * 4.0, 0.0, z);
            }
        }
        // 가구 (높이는 실제 가구 치수)
        cuboid(c, 0.9, 2.1, 0.5, -12.0, 1.05, -2.5); // 충전 독
        cuboid(c, 1.4, 0.84, 0.8, -7.0, 0.42, -2.5); // 식탁
        cuboid(c, 1.2, 0.81, 0.7, -7.0, 0.405, 2.5); // 책상
        cuboid(c, 1.2, 0.56, 2.2, -11.8, 0.28, 2.0); // 침대
        column(c, 0.62, 2.2, -12.3, 0.0, 0.0); // 동면 포드
        cuboid(c, 1.6, 1.6, 0.35, -9.5, 0.8, 3.2); // 진열장
        cuboid(c, 1.0, 1.1, 0.7, 11.0, 0.55, -2.5); // 분석기
        cuboid(c, 1.6, 0.94, 0.9, 11.0, 0.47, 2.5); // 작업대
        // 양자 공명탑
        column(c, 1.2, 1.0, 0.0, 0.0, 9.0);
        column(c, 0.4, 7.5, 0.0, 0.0, 9.0);
        // 전신 거울
        let (mx, mz) = MIRROR;
        let yaw = (SPAWN.0 - mx).atan2(SPAWN.2 - mz);
        fixed(c, ColliderBuilder::cuboid(0.62, 1.15, 0.12).translation(vector![mx, 1.15, mz]).rotation(vector![0.0, yaw, 0.0]));

        // 울타리: 기둥 사이 레일 (높이 1.3m — 사람의 제자리 점프로는 넘을 수 없다)
        let segs = 64;
        for k in 0..segs {
            let a0 = k as f32 / segs as f32 * std::f32::consts::TAU;
            let a1 = (k + 1) as f32 / segs as f32 * std::f32::consts::TAU;
            let (x0, z0) = (FENCE_RADIUS * a0.cos(), FENCE_RADIUS * a0.sin());
            let (x1, z1) = (FENCE_RADIUS * a1.cos(), FENCE_RADIUS * a1.sin());
            let (mx, mz) = ((x0 + x1) / 2.0, (z0 + z1) / 2.0);
            if mx.abs() < GATE_HALF_WIDTH && mz < 0.0 {
                continue;
            }
            let len = (x1 - x0).hypot(z1 - z0);
            let yaw = -(z1 - z0).atan2(x1 - x0);
            fixed(c, ColliderBuilder::cuboid(len / 2.0 + 0.05, 0.65, 0.05).translation(vector![mx, 0.65, mz]).rotation(vector![0.0, yaw, 0.0]));
        }
        // 문: 외출 시간인 플레이어만 통과 (플레이어별 필터)
        c.insert(
            ColliderBuilder::cuboid(GATE_HALF_WIDTH, 0.7, 0.05)
                .translation(vector![0.0, 0.7, -FENCE_RADIUS])
                .collision_groups(InteractionGroups::new(G_GATE, Group::ALL))
                .build(),
        );

        // 하람의 오두막과 전신주
        let (hx, hz) = HARAM_HOUSE;
        cuboid(c, 5.0, 2.6, 0.25, hx, 1.3, hz - 2.5);
        cuboid(c, 0.25, 2.6, 5.0, hx - 2.5, 1.3, hz);
        cuboid(c, 5.0, 2.6, 0.25, hx, 1.3, hz + 2.5);
        let (nx, nz) = station_pos(target::HARAM).unwrap();
        column(c, 0.3, 1.6, nx, 0.0, nz);
        for i in 0..5 {
            column(c, 0.12, 5.0, -2.0 - i as f32 * 4.6, 0.0, -20.0 - i as f32 * 3.4);
        }

        // 숲과 바위: 지형 높이에 맞춰 배치
        let mut rng = Lcg(TERRAIN_SEED as u64 * 7919);
        while self.trees.len() < TREE_COUNT {
            let r = 21.0 + rng.next() * 37.0;
            let a = rng.next() * std::f32::consts::TAU;
            let (x, z) = (r * a.cos(), r * a.sin());
            if (x - hx).hypot(z - hz) < 7.5 || dist_to_segment(x, z, 0.0, -FENCE_RADIUS, hx + 4.0, hz + 2.0) < 3.5 {
                continue;
            }
            let scale = 0.8 + rng.next() * 0.7;
            let y = self.terrain.height_at(x, z);
            column(c, 0.22 * scale, 2.5 * scale, x, y - 0.2, z);
            self.trees.push(Tree { x, y, z, scale });
        }
        for _ in 0..ROCK_COUNT {
            let r = 20.0 + rng.next() * 40.0;
            let a = rng.next() * std::f32::consts::TAU;
            let (x, z) = (r * a.cos(), r * a.sin());
            if dist_to_segment(x, z, 0.0, -FENCE_RADIUS, hx + 4.0, hz + 2.0) < 3.0 {
                continue;
            }
            let size = 0.3 + rng.next() * 0.6;
            let y = self.terrain.height_at(x, z);
            fixed(c, ColliderBuilder::ball(size).translation(vector![x, y + size * 0.35, z]));
            self.rocks.push(Rock { x, y, z, size });
        }
    }

    /// 광장의 물리 소품: 실제 크기·질량
    fn build_props(&mut self) {
        let mut id = PROP_ID_BASE;
        let mut add = |p: &mut Physics, kind: u8, x: f32, y: f32, z: f32| {
            let (collider, size) = match kind {
                PROP_CRATE => (ColliderBuilder::cuboid(0.225, 0.225, 0.225).density(DENSITY_WOOD * 0.6), [0.45, 0.45, 0.45]),
                PROP_BARREL => (ColliderBuilder::cylinder(0.43, 0.29).density(DENSITY_WOOD * 0.35), [0.58, 0.86, 0.58]),
                PROP_BALL => (ColliderBuilder::ball(0.11).mass(0.43).restitution(0.7), [0.22, 0.22, 0.22]),
                _ => (ColliderBuilder::ball(0.33).mass(1.2).restitution(0.55), [0.66, 0.66, 0.66]),
            };
            let body = p.bodies.insert(
                RigidBodyBuilder::dynamic().translation(vector![x, y, z]).linear_damping(0.05).angular_damping(0.3).build(),
            );
            p.colliders.insert_with_parent(
                collider.friction(0.6).collision_groups(InteractionGroups::new(G_PROP, Group::ALL)),
                body,
                &mut p.bodies,
            );
            p.props.insert(id, Prop { kind, size, body });
            id += 1;
        };
        // 연구소 앞 나무 상자 더미
        for (i, (x, z)) in [(4.0, -5.0), (4.5, -5.0), (4.25, -4.5), (4.0, -4.5)].iter().enumerate() {
            add(self, PROP_CRATE, *x, 0.25 + (i / 2) as f32 * 0.0, *z);
        }
        add(self, PROP_CRATE, 4.25, 0.72, -4.75);
        add(self, PROP_BARREL, -4.2, 0.45, -5.2);
        add(self, PROP_BARREL, -3.5, 0.45, -5.6);
        add(self, PROP_BALL, 1.5, 0.3, -2.0);
        add(self, PROP_BIG_BALL, -1.5, 0.5, 3.0);
    }

    pub fn add_avatar(&mut self, id: u32, gender: u8, feet: [f32; 3]) {
        self.remove_avatar(id);
        let (radius, half) = if gender == 1 { (0.27, 0.62) } else { (0.24, 0.59) };
        let center = vector![feet[0], feet[1] + half + radius + 0.02, feet[2]];
        let body = self.bodies.insert(RigidBodyBuilder::kinematic_position_based().translation(center).build());
        let collider = self.colliders.insert_with_parent(
            ColliderBuilder::capsule_y(half, radius).collision_groups(InteractionGroups::new(G_PLAYER, Group::ALL)),
            body,
            &mut self.bodies,
        );
        self.avatars.insert(
            id,
            Avatar { body, collider, half, radius, vel: Vector::zeros(), grounded: false, input: Input::default(), jump_latch: false },
        );
    }

    pub fn remove_avatar(&mut self, id: u32) {
        if let Some(a) = self.avatars.remove(&id) {
            self.bodies.remove(a.body, &mut self.islands, &mut self.colliders, &mut self.impulse_joints, &mut self.multibody_joints, true);
        }
    }

    pub fn feet(&self, id: u32) -> Option<[f32; 3]> {
        let a = self.avatars.get(&id)?;
        let t = self.bodies[a.body].translation();
        Some([t.x, t.y - a.half - a.radius, t.z])
    }

    pub fn teleport(&mut self, id: u32, feet: [f32; 3]) {
        if let Some(a) = self.avatars.get_mut(&id) {
            let center = vector![feet[0], feet[1] + a.half + a.radius + 0.02, feet[2]];
            self.bodies[a.body].set_translation(center, true);
            self.bodies[a.body].set_next_kinematic_translation(center);
            a.vel = Vector::zeros();
        }
    }

    /// 자원 노드: 실제 밀도의 동적 강체로 지면 위에 떨어뜨린다.
    pub fn add_node(&mut self, id: u32, kind: u8, x: f32, z: f32) -> f32 {
        let y = self.terrain.height_at(x, z) + 0.6;
        let (collider, size) = match kind {
            0 => (ColliderBuilder::ball(0.32).density(DENSITY_TITANIUM), 0.32),
            1 => (ColliderBuilder::ball(0.25).density(DENSITY_QUARTZ), 0.25),
            2 => (ColliderBuilder::ball(0.28).density(DENSITY_COPPER), 0.28),
            _ => (ColliderBuilder::ball(0.18).density(DENSITY_HERB), 0.18),
        };
        let body = self.bodies.insert(RigidBodyBuilder::dynamic().translation(vector![x, y, z]).angular_damping(1.5).build());
        self.colliders.insert_with_parent(
            collider.friction(0.9).collision_groups(InteractionGroups::new(G_PROP, Group::ALL)),
            body,
            &mut self.bodies,
        );
        self.props.insert(id, Prop { kind, size: [size; 3], body });
        y
    }

    pub fn remove_prop(&mut self, id: u32) {
        if let Some(p) = self.props.remove(&id) {
            self.bodies.remove(p.body, &mut self.islands, &mut self.colliders, &mut self.impulse_joints, &mut self.multibody_joints, true);
        }
    }

    pub fn prop_pos(&self, id: u32) -> Option<[f32; 3]> {
        let p = self.props.get(&id)?;
        let t = self.bodies[p.body].translation();
        Some([t.x, t.y, t.z])
    }

    /// (위치, 회전 사원수, 깨어 있는지)
    pub fn prop_pose(&self, id: u32) -> Option<([f32; 3], [f32; 4], bool)> {
        let p = self.props.get(&id)?;
        let b = &self.bodies[p.body];
        let t = b.translation();
        let q = b.rotation();
        Some(([t.x, t.y, t.z], [q.i, q.j, q.k, q.w], !b.is_sleeping()))
    }

    /// 한 스텝: 캐릭터 이동(입력 → 가속 → 충돌 해결) 후 강체 시뮬레이션.
    /// gate_open(id) 가 true 인 플레이어는 문을 통과한다.
    pub fn step(&mut self, gate_open: impl Fn(u32) -> bool) {
        let gravity = vector![0.0, -GRAVITY, 0.0];
        let ids: Vec<u32> = self.avatars.keys().copied().collect();
        for id in ids {
            let a = self.avatars.get_mut(&id).unwrap();
            let inp = a.input;
            let len = inp.mx.hypot(inp.mz);
            let (dx, dz) = if len > 1.0 { (inp.mx / len, inp.mz / len) } else { (inp.mx, inp.mz) };
            let speed = if inp.run { RUN_SPEED } else { WALK_SPEED };
            let (tx, tz) = (dx * speed, dz * speed);
            let accel = if a.grounded { GROUND_ACCEL } else { AIR_ACCEL } * DT;
            let (ex, ez) = (tx - a.vel.x, tz - a.vel.z);
            let e = ex.hypot(ez);
            let k = if e > accel { accel / e } else { 1.0 };
            a.vel.x += ex * k;
            a.vel.z += ez * k;
            if a.grounded && inp.jump && !a.jump_latch {
                a.vel.y = JUMP_SPEED;
                a.jump_latch = true;
            }
            if !inp.jump {
                a.jump_latch = false;
            }
            a.vel.y -= GRAVITY * DT;

            let body = a.body;
            let shape = SharedShape::capsule_y(a.half, a.radius);
            let pos = *self.bodies[body].position();
            let mask = if gate_open(id) { Group::ALL ^ G_GATE } else { Group::ALL };
            let filter = QueryFilter::new()
                .exclude_rigid_body(body)
                .groups(InteractionGroups::new(G_PLAYER, mask));
            let desired = a.vel * DT;
            let mut hits: Vec<CharacterCollision> = Vec::new();
            let moved = self.controller.move_shape(
                DT, &self.bodies, &self.colliders, &self.query, shape.as_ref(), &pos, desired, filter,
                |c| hits.push(c),
            );
            for hit in &hits {
                self.controller.solve_character_collision_impulses(
                    DT, &mut self.bodies, &self.colliders, &self.query, shape.as_ref(), BODY_MASS, hit, filter,
                );
            }
            let a = self.avatars.get_mut(&id).unwrap();
            a.grounded = moved.grounded;
            if a.grounded && a.vel.y < 0.0 {
                a.vel.y = 0.0;
            }
            if a.vel.y > 0.0 && moved.translation.y < desired.y * 0.5 {
                a.vel.y = 0.0; // 천장에 부딪힘
            }
            // 벽에 막힌 수평 성분은 사라진다
            a.vel.x = moved.translation.x / DT;
            a.vel.z = moved.translation.z / DT;
            let mut next = pos.translation.vector + moved.translation;
            if next.y < -30.0 {
                next = vector![SPAWN.0, SPAWN.1 + 2.0, SPAWN.2];
                a.vel = Vector::zeros();
            }
            self.bodies[body].set_next_kinematic_translation(next);
        }

        self.pipeline.step(
            &gravity,
            &self.params,
            &mut self.islands,
            &mut self.broad,
            &mut self.narrow,
            &mut self.bodies,
            &mut self.colliders,
            &mut self.impulse_joints,
            &mut self.multibody_joints,
            &mut self.ccd,
            Some(&mut self.query),
            &(),
            &(),
        );
        // 월드 밖으로 굴러간 소품은 광장으로 되돌린다
        for p in self.props.values() {
            let b = &mut self.bodies[p.body];
            if b.translation().y < -30.0 {
                b.set_translation(vector![0.0, 2.0, -4.0], true);
                b.set_linvel(Vector::zeros(), true);
            }
        }
    }

    pub fn set_input(&mut self, id: u32, input: Input) {
        if let Some(a) = self.avatars.get_mut(&id) {
            a.input = input;
        }
    }

    pub fn velocity(&self, id: u32) -> Option<(Vector<Real>, bool)> {
        self.avatars.get(&id).map(|a| (a.vel, a.grounded))
    }

    pub fn collider_of(&self, id: u32) -> Option<ColliderHandle> {
        self.avatars.get(&id).map(|a| a.collider)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn settle(p: &mut Physics, id: u32, steps: usize, input: Input) {
        p.set_input(id, input);
        for _ in 0..steps {
            p.step(|_| false);
        }
    }

    #[test]
    fn avatar_falls_to_ground_and_stands() {
        let mut p = Physics::new();
        p.add_avatar(1, 0, [0.0, 3.0, -6.0]);
        settle(&mut p, 1, 120, Input::default());
        let f = p.feet(1).unwrap();
        assert!(f[1].abs() < 0.08, "feet y = {}", f[1]);
        assert!(p.velocity(1).unwrap().1, "grounded");
    }

    #[test]
    fn jump_height_is_human() {
        let mut p = Physics::new();
        p.add_avatar(1, 1, [0.0, 0.0, -6.0]);
        settle(&mut p, 1, 30, Input::default());
        let mut peak: f32 = 0.0;
        p.set_input(1, Input { jump: true, ..Default::default() });
        for _ in 0..90 {
            p.step(|_| false);
            peak = peak.max(p.feet(1).unwrap()[1]);
        }
        assert!((0.4..0.6).contains(&peak), "jump peak = {peak}");
    }

    #[test]
    fn walking_speed_matches_input() {
        let mut p = Physics::new();
        p.add_avatar(1, 0, [0.0, 0.0, -2.0]);
        settle(&mut p, 1, 30, Input::default());
        let x0 = p.feet(1).unwrap()[0];
        settle(&mut p, 1, 120, Input { mx: 1.0, ..Default::default() });
        let v = (p.feet(1).unwrap()[0] - x0) / 2.0;
        assert!((1.2..1.7).contains(&v), "walk speed = {v}");
    }

    #[test]
    fn gate_blocks_unless_open() {
        for open in [false, true] {
            let mut p = Physics::new();
            p.add_avatar(1, 0, [0.0, 0.0, -16.5]);
            p.set_input(1, Input { mz: -1.0, run: true, ..Default::default() });
            for _ in 0..180 {
                p.step(|_| open);
            }
            let z = p.feet(1).unwrap()[2];
            assert_eq!(z < -FENCE_RADIUS - 0.5, open, "open={open} z={z}");
        }
    }

    #[test]
    fn player_kicks_ball_but_not_titanium() {
        let mut p = Physics::new();
        let ball = PROP_ID_BASE + 7;
        assert_eq!(p.props[&ball].kind, PROP_BALL);
        let b0 = p.prop_pos(ball).unwrap();
        p.add_avatar(1, 1, [b0[0] - 1.5, 0.0, b0[2]]);
        settle(&mut p, 1, 150, Input { mx: 1.0, run: true, ..Default::default() });
        let b1 = p.prop_pos(ball).unwrap();
        assert!((b1[0] - b0[0]) > 0.5, "ball moved {:?} -> {:?}", b0, b1);

        let mut p = Physics::new();
        p.add_node(5000, 0, 30.0, 0.0);
        for _ in 0..120 {
            p.step(|_| true);
        }
        let n0 = p.prop_pos(5000).unwrap();
        p.add_avatar(1, 1, [n0[0] - 2.0, n0[1], n0[2]]);
        settle(&mut p, 1, 150, Input { mx: 1.0, run: true, ..Default::default() });
        let n1 = p.prop_pos(5000).unwrap();
        assert!((n1[0] - n0[0]).abs() < 0.05, "titanium moved {:?} -> {:?}", n0, n1);
    }
}
