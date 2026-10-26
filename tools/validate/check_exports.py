#!/usr/bin/env python3
"""URDF / USDA 내보내기 독립 검증기 (표준 라이브러리만 사용).

C# 내보내기 코드와 별개로 구현한 수학으로 다음을 확인한다.
  1. URDF: 트리 구조, 질량·관성 양수, 관성 삼각부등식, 순운동학으로 구한 각 부품 형상의 월드 자세
  2. USDA: 각 강체의 절대 자세, 질량
  3. 교차 검증: URDF 순운동학 결과와 USDA 절대 자세가 일치 (기준 링크 오프셋 보정)
  4. USDA 관절: body0 의 localPos0/Rot0 와 body1 의 localPos1/Rot1 이 월드에서 같은 프레임
사용법: check_exports.py <export_dir>
"""
import math, re, sys, glob, os
import xml.etree.ElementTree as ET

def qmul(a, b):
    aw, ax, ay, az = a; bw, bx, by, bz = b
    return (aw*bw - ax*bx - ay*by - az*bz, aw*bx + ax*bw + ay*bz - az*by,
            aw*by - ax*bz + ay*bw + az*bx, aw*bz + ax*by - ay*bx + az*bw)

def qrot(q, v):
    w, x, y, z = q
    p = qmul(qmul(q, (0, *v)), (w, -x, -y, -z))
    return p[1:]

def rpy_to_q(r, p, y):
    # URDF: 고정축 X(roll) → Y(pitch) → Z(yaw) = Rz * Ry * Rx
    def axis(a, ang):
        s = math.sin(ang / 2)
        return (math.cos(ang / 2), *(s * c for c in a))
    return qmul(axis((0, 0, 1), y), qmul(axis((0, 1, 0), p), axis((1, 0, 0), r)))

def compose(a, b):  # (pos, quat)
    return (tuple(pa + pb for pa, pb in zip(a[0], qrot(a[1], b[0]))), qmul(a[1], b[1]))

def origin(el):
    o = el.find('origin') if el is not None else None
    xyz = tuple(map(float, (o.get('xyz', '0 0 0') if o is not None else '0 0 0').split()))
    rpy = tuple(map(float, (o.get('rpy', '0 0 0') if o is not None else '0 0 0').split()))
    return (xyz, rpy_to_q(*rpy))

def same_pose(a, b, tol=1e-4):
    dp = math.dist(a[0], b[0])
    dq = abs(sum(x*y for x, y in zip(a[1], b[1])))  # |cos(θ/2)|
    return dp < tol and dq > 1 - 1e-6, dp, dq

def check_urdf(path):
    root = ET.parse(path).getroot()
    links = {l.get('name'): l for l in root.findall('link')}
    joints = root.findall('joint')
    children = [j.find('child').get('link') for j in joints]
    assert len(children) == len(set(children)), '링크가 두 부모를 가짐'
    roots = [n for n in links if n not in children]
    assert roots == ['base_link'], f'루트가 base_link 하나가 아님: {roots}'
    assert len(joints) == len(links) - 1, '트리가 아님'
    total = 0.0
    for name, l in links.items():
        inert = l.find('inertial')
        if inert is None:
            continue
        m = float(inert.find('mass').get('value'))
        i = inert.find('inertia')
        ixx, iyy, izz = (float(i.get(k)) for k in ('ixx', 'iyy', 'izz'))
        assert m > 0 and ixx > 0 and iyy > 0 and izz > 0, f'{name}: 질량/관성이 양수가 아님'
        tol = 1e-9 + 1e-6 * (ixx + iyy + izz)
        assert ixx + iyy >= izz - tol and iyy + izz >= ixx - tol and ixx + izz >= iyy - tol, f'{name}: 관성 삼각부등식 위반'
        total += m
    # 순운동학 (관절 값 0)
    world = {'base_link': ((0.0, 0.0, 0.0), (1.0, 0.0, 0.0, 0.0))}
    pending = list(joints)
    while pending:
        progressed = False
        for j in list(pending):
            parent = j.find('parent').get('link')
            if parent in world:
                world[j.find('child').get('link')] = compose(world[parent], origin(j))
                pending.remove(j)
                progressed = True
        assert progressed, '도달할 수 없는 관절'
    geoms = {}
    for name, l in links.items():
        v = l.find('visual')
        if v is not None:
            geoms[name] = compose(world[name], origin(v))
    return total, geoms

def parse_usda(path):
    text = open(path, encoding='utf-8').read()
    bodies, joints = {}, {}
    for m in re.finditer(r'def Xform "(\w+)" \(\s*prepend apiSchemas = \["PhysicsRigidBodyAPI".*?\n    \}', text, re.S):
        block = m.group(0)
        t = tuple(map(float, re.search(r'xformOp:translate = \(([^)]*)\)', block).group(1).split(',')))
        q = tuple(map(float, re.search(r'xformOp:orient = \(([^)]*)\)', block).group(1).split(',')))
        mass = float(re.search(r'physics:mass = ([-\d.eE+]+)', block).group(1))
        bodies[m.group(1)] = ((t, q), mass)
    for m in re.finditer(r'def (Physics\w*Joint) "(\w+)"[^{]*\{(.*?)\n        \}', text, re.S):
        block = m.group(3)
        get = lambda k: tuple(map(float, re.search(k + r' = \(([^)]*)\)', block).group(1).split(',')))
        b0 = re.search(r'physics:body0 = </\w+/(\w+)>', block).group(1)
        b1 = re.search(r'physics:body1 = </\w+/(\w+)>', block).group(1)
        joints[m.group(2)] = (m.group(1), b0, b1, (get('physics:localPos0'), get('physics:localRot0')),
                              (get('physics:localPos1'), get('physics:localRot1')))
    assert 'upAxis = "Z"' in text and 'metersPerUnit = 1' in text and 'kilogramsPerUnit = 1' in text
    return bodies, joints

def main(d):
    failures = 0
    for urdf in sorted(glob.glob(os.path.join(d, '*.urdf'))):
        name = os.path.splitext(os.path.basename(urdf))[0]
        usda = urdf[:-5] + '.usda'
        total, geoms = check_urdf(urdf)
        bodies, joints = parse_usda(usda)
        usd_mass = sum(m for _, m in bodies.values())
        print(f'[{name}] URDF 링크 {len(geoms)}개 질량 {total:.3f} kg / USDA 강체 {len(bodies)}개 질량 {usd_mass:.3f} kg, 관절 {len(joints)}개')
        # URDF 의 base_link 는 루트 부품 위치(회전 없음)에 놓인다 → USDA 와 비교할 때 그만큼 평행이동
        dummy = [n for n in geoms if n not in bodies]
        assert not dummy, f'USDA 에 없는 형상 링크: {dummy}'
        before = failures
        offsets = []
        for n, g in geoms.items():
            u = bodies[n][0]
            offsets.append(tuple(a - b for a, b in zip(u[0], g[0])))
        off = offsets[0]
        worst = 0.0
        for n, g in geoms.items():
            shifted = (tuple(a + b for a, b in zip(g[0], off)), g[1])
            ok, dp, dq = same_pose(shifted, bodies[n][0])
            worst = max(worst, dp)
            if not ok:
                failures += 1
                print(f'  ✘ {n}: URDF 순운동학과 USDA 자세 불일치 (거리 {dp:.2e} m, |cos| {dq:.8f})')
        if failures == before:
            print(f'  ✔ URDF 순운동학 = USDA 절대 자세: 부품 {len(geoms)}개 (최대 오차 {worst:.1e} m)')
        jworst = 0.0
        for jn, (kind, b0, b1, l0, l1) in joints.items():
            w0 = compose(bodies[b0][0], l0)
            w1 = compose(bodies[b1][0], l1)
            ok, dp, dq = same_pose(w0, w1)
            jworst = max(jworst, dp)
            if not ok:
                failures += 1
                print(f'  ✘ {jn} ({kind}): body0/body1 관절 프레임 불일치 (거리 {dp:.2e} m, |cos| {dq:.8f})')
        if joints:
            print(f'  ✔ USDA 관절 {len(joints)}개: 양쪽 몸체의 관절 프레임이 월드에서 일치 (최대 오차 {jworst:.1e} m)')
        if abs(total - 0.001 * sum(1 for l in ET.parse(urdf).getroot().findall('link') if '_dof' in (l.get('name') or '')) - usd_mass) > 1e-3:
            failures += 1
            print('  ✘ URDF/USDA 총질량 불일치')
        else:
            print('  ✔ 총질량 일치 (URDF 중간 링크 1 g 제외)')
    print('검증 실패 %d건' % failures if failures else '모든 검증 통과')
    return 1 if failures else 0

if __name__ == '__main__':
    sys.exit(main(sys.argv[1]))
