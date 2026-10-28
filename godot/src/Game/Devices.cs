using Godot;
using DollyForge.Core;
using N = System.Numerics;

namespace DollyForge.Game;

/// <summary>
/// 작업장의 모든 부품과 관절. 코어 조립품(World)과 Godot 물리 노드를 함께 관리한다.
/// 결합은 현실 규칙(Rules) → 체결 부품 재고 → 접촉 확인 → 관절 스냅(Fusion 360 의 joint 처럼 위치 맞춤) 순서로 진행된다.
/// </summary>
public partial class Devices : Node3D
{
    public Assembly World { get; } = new("Workshop");
    public ShopAccount Shop { get; set; } = new(0);

    readonly Dictionary<int, PartBody> _bodies = new();
    readonly Dictionary<int, (Joint3D Node, Transform3D LocalFrame)> _joints = new();
    /// <summary>켜진 모터 (부품 id) → 배선 방향 (+1 / -1)</summary>
    readonly Dictionary<int, int> _wiring = new();
    readonly HashSet<int> _running = new();
    /// <summary>서보 증폭기 손잡이 (부품 id → −1…+1)</summary>
    readonly Dictionary<int, float> _knob = new();
    /// <summary>캠 드럼 프로그램 (부품 id → 8단, 각 +1/0/−1)</summary>
    readonly Dictionary<int, sbyte[]> _program = new();
    double _clock, _readoutTimer;

    public static readonly sbyte[] DefaultProgram = [1, 1, 1, 1, 0, -1, -1, 0];
    public float Knob(int id) => _knob.GetValueOrDefault(id, 1f);
    public sbyte[] Program(int id) => _program.TryGetValue(id, out var p) ? p : DefaultProgram;
    public void SetProgram(int id, sbyte[] program) => _program[id] = program;

    public string TurnKnob(PartBody amp, float delta)
    {
        if (amp.Part.Def.Role != PartRole.Controller) return "서보 증폭기가 아닙니다.";
        var v = Mathf.Clamp(MathF.Round((Knob(amp.Part.Id) + delta) * 10) / 10, -1, 1);
        _knob[amp.Part.Id] = v;
        return $"서보 증폭기 출력 {v * 100:+0;-0;0} %";
    }

    /// <summary>
    /// 기기 안 모터 하나의 지령 (−1…+1): 배선 극성 × 서보 손잡이 × 캠 드럼의 현재 단.
    /// 아날로그 회로처럼 곱해서 정해진다.
    /// </summary>
    float Command(int motorId, HashSet<int> group)
    {
        float cmd = _wiring.GetValueOrDefault(motorId, 1);
        foreach (var id in group)
        {
            var role = _bodies[id].Part.Def.Role;
            if (role == PartRole.Controller) cmd *= Knob(id);
            else if (role == PartRole.Sequencer) cmd *= Program(id)[(int)_clock % 8];
        }
        return cmd;
    }

    public IEnumerable<PartBody> Bodies => _bodies.Values;

    /// <summary>체결 소모품 사용·회수 알림 (온라인이면 서버 재고에 반영)</summary>
    public Action<string, int>? OnConsume, OnRefund;

    public PartBody Spawn(PartDef def, Transform3D xform)
    {
        var part = World.AddPart(def, xform.Pose());
        var body = PartBody.Create(part);
        AddChild(body);
        _bodies[part.Id] = body;
        return body;
    }

    /// <summary>미리 조립된 코어 조립품(예: 견본)을 그대로 월드에 놓는다.</summary>
    public void Adopt(Assembly a)
    {
        var map = new Dictionary<int, PartBody>();
        foreach (var p in a.Parts) map[p.Id] = Spawn(p.Def, p.Pose.Xform());
        foreach (var j in a.Joints)
        {
            var parent = map[j.Parent];
            var child = map[j.Child];
            var nj = World.AddJoint(j.Kind, parent.Part, child.Part, j.Pivot, j.Axis, j.Lower, j.Upper, j.Secondary);
            foreach (var (k, v) in j.Consumed) nj.Consumed[k] = v;
            if (j.DrivenBy is { } m) { nj.DrivenBy = map[m].Part.Id; nj.Effort = j.Effort; nj.Velocity = j.Velocity; }
            CreateJointNode(nj, parent, child);
        }
    }

    public PartBody? BodyOf(GodotObject? o) => o as PartBody;

    // ------------------------------------------------------------------ 결합

    public (bool Ok, string Message) Join(JoinMethod method, PartBody a, PartBody b, Vector3 hitOnB)
    {
        if (a == b) return (false, "같은 부품입니다.");
        var check = Rules.Check(method, a.Part.Def, b.Part.Def);
        if (!check.Ok) return (false, check.ReasonKo);
        if (!Shop.Has(check.Consumes))
        {
            var need = string.Join(", ", check.Consumes.Select(kv => $"{Catalog.Get(kv.Key).NameKo} {kv.Value}개"));
            return (false, $"체결 부품이 부족합니다: {need}. 카탈로그에서 주문하세요.");
        }

        Vector3 pivot, axis;
        if (method == JoinMethod.Axle)
        {
            var (shaft, hole) = a.Part.Def.ShaftMm > 0 ? (a, b) : (b, a);
            var snap = SnapOntoShaft(shaft, hole);
            if (!snap.Ok) return (false, snap.Message);
            pivot = hole.GlobalPosition;
            // 모터는 출력축 방향(로컬 +Z)이 구동 축의 양의 방향이다
            axis = shaft.Part.Def.Role == PartRole.Motor ? shaft.GlobalBasis.Z.Normalized() : shaft.PrincipalAxis();
        }
        else
        {
            if (!Touching(a, b)) return (false, "두 부품이 맞닿아 있지 않습니다. 붙여 놓고 다시 시도하세요.");
            pivot = method is JoinMethod.Hinge or JoinMethod.Ball ? hitOnB : b.GlobalPosition;
            axis = method == JoinMethod.Slide
                ? (a.Part.Def.Role == PartRole.Shaft ? a : b).PrincipalAxis()
                : b.PrincipalAxis();
        }

        var lower = check.Kind == JointKind.Revolute ? -Mathf.Pi / 2 : check.Kind == JointKind.Prismatic ? -0.5f : -Mathf.Pi;
        var upper = -lower;
        var joint = World.AddJoint(check.Kind, a.Part, b.Part, pivot.N(), axis.N(), lower, upper);
        foreach (var (k, v) in check.Consumes) joint.Consumed[k] = v;
        Shop.Consume(check.Consumes);
        foreach (var (k, v) in check.Consumes) OnConsume?.Invoke(k, v);
        if (check.Driven)
        {
            var motor = a.Part.Def.Role == PartRole.Motor ? a : b;
            joint.DrivenBy = motor.Part.Id;
            joint.Effort = motor.Part.Def.Actuator!.MaxTorque;
            joint.Velocity = motor.Part.Def.Actuator.MaxSpeed;
        }
        CreateJointNode(joint, a, b);
        return (true, $"{check.ReasonKo} 완료: {a.Part.Def.NameKo} ↔ {b.Part.Def.NameKo}");
    }

    /// <summary>두 부품이 1 cm 이내로 맞닿아 있는지</summary>
    bool Touching(PartBody a, PartBody b)
    {
        var (shape, xform) = a.Grown(0.01f);
        var q = new PhysicsShapeQueryParameters3D { Shape = shape, Transform = xform, CollisionMask = PartBody.LayerParts };
        q.Exclude = [a.GetRid()];
        foreach (var hit in GetWorld3D().DirectSpaceState.IntersectShape(q, 32))
            if (hit["collider"].AsGodotObject() == b) return true;
        return false;
    }

    /// <summary>
    /// 구멍 있는 부품(바퀴·베어링)을 축 위로 끼운다: 축선에 중심을 맞추고 구멍 축을 축 방향과 정렬한다.
    /// 구멍 부품에 이미 붙어 있는 부품들도 함께 강체처럼 옮긴다.
    /// </summary>
    (bool Ok, string Message) SnapOntoShaft(PartBody shaft, PartBody hole)
    {
        var axis = shaft.PrincipalAxis();
        var def = shaft.Part.Def;
        var half = def.Shape.Kind == ShapeKind.Cylinder ? def.Shape.Length / 2 : def.Shape.Size.Z / 2;
        Vector3 target;
        if (def.Role == PartRole.Motor)
        {
            // 모터 출력축은 하우징 앞면(로컬 +Z)으로만 나와 있다
            var holeHalf = hole.Part.Def.Shape.Kind == ShapeKind.Cylinder ? hole.Part.Def.Shape.Length / 2 : hole.Part.Def.Shape.Size.Z / 2;
            axis = shaft.GlobalBasis.Z.Normalized();
            target = shaft.GlobalPosition + axis * (half + holeHalf + 0.003f);
        }
        else
        {
            var t = Mathf.Clamp(axis.Dot(hole.GlobalPosition - shaft.GlobalPosition), -half, half);
            target = shaft.GlobalPosition + axis * t;
        }
        if (hole.GlobalPosition.DistanceTo(target) > 0.2f)
            return (false, "축에서 너무 멉니다. 바퀴·베어링을 축 끝에 가져다 대고 다시 시도하세요.");

        var group = World.ConnectedTo(hole.Part.Id);
        if (group.Contains(shaft.Part.Id))
        {
            // 이미 같은 기기 안이면 움직일 수 없다 → 이미 정렬되어 있어야 한다
            var aligned = Mathf.Abs(hole.GlobalBasis.Z.Normalized().Dot(axis)) > 0.995f && hole.GlobalPosition.DistanceTo(target) < 0.005f;
            return aligned ? (true, "") : (false, "같은 기기 안의 부품은 움직일 수 없어 축에 맞출 수 없습니다. 먼저 분해하세요.");
        }
        var holeZ = hole.GlobalBasis.Z.Normalized();
        var rot = new Quaternion(holeZ, holeZ.Dot(axis) >= 0 ? axis : -axis);
        var pivotFrom = hole.GlobalPosition;
        foreach (var id in group)
        {
            var body = _bodies[id];
            var rel = body.GlobalPosition - pivotFrom;
            body.GlobalTransform = new Transform3D(new Basis(rot) * body.GlobalBasis, target + rot * rel);
            body.LinearVelocity = Vector3.Zero;
            body.AngularVelocity = Vector3.Zero;
        }
        return (true, "");
    }

    void CreateJointNode(JointInstance j, PartBody parent, PartBody child)
    {
        var axis = j.Axis.G();
        var frame = new Transform3D(new Basis(j.FrameRotation.G()), j.Pivot.G()); // X = 관절 축
        Joint3D node;
        switch (j.Kind)
        {
            case JointKind.Continuous:
            case JointKind.Revolute:
            {
                // Godot 경첩은 노드의 Z 축을 중심으로 돈다
                var hinge = new HingeJoint3D { Transform = new Transform3D(BasisWithZ(axis), j.Pivot.G()) };
                if (j.Kind == JointKind.Revolute)
                {
                    hinge.SetFlag(HingeJoint3D.Flag.UseLimit, true);
                    hinge.SetParam(HingeJoint3D.Param.LimitLower, j.Lower);
                    hinge.SetParam(HingeJoint3D.Param.LimitUpper, j.Upper);
                }
                node = hinge;
                break;
            }
            case JointKind.Prismatic:
            {
                var slider = new SliderJoint3D { Transform = frame }; // 노드 X 축을 따라 미끄러진다
                slider.SetParam(SliderJoint3D.Param.LinearLimitLower, j.Lower);
                slider.SetParam(SliderJoint3D.Param.LinearLimitUpper, j.Upper);
                node = slider;
                break;
            }
            case JointKind.Ball:
                node = new PinJoint3D { Transform = frame };
                break;
            default:
                node = new Generic6DofJoint3D { Transform = frame }; // 기본값: 6 자유도 모두 잠김 = 고정
                break;
        }
        node.Name = j.Name;
        AddChild(node);
        node.NodeA = parent.GetPath();
        node.NodeB = child.GetPath();
        _joints[j.Id] = (node, parent.GlobalTransform.AffineInverse() * frame);
    }

    static Basis BasisWithZ(Vector3 z)
    {
        z = z.Normalized();
        var x = Mathf.Abs(z.Y) < 0.9f ? Vector3.Up.Cross(z).Normalized() : Vector3.Right.Cross(z).Normalized();
        var y = z.Cross(x);
        return new Basis(x, y, z);
    }

    /// <summary>분해: 부품의 모든 관절을 푼다. 볼트·나사·경첩 등 기계 체결 부품은 회수, 용접은 절단(회수 없음).</summary>
    public string Disassemble(PartBody b)
    {
        var removed = World.Joints.Where(j => j.Parent == b.Part.Id || j.Child == b.Part.Id).ToList();
        if (removed.Count == 0) return "풀 관절이 없습니다.";
        var back = new Dictionary<string, int>();
        foreach (var j in removed)
        {
            foreach (var (k, v) in j.Consumed) back[k] = back.GetValueOrDefault(k) + v;
            if (_joints.Remove(j.Id, out var n)) n.Node.QueueFree();
            World.Joints.Remove(j);
        }
        foreach (var (k, v) in back)
        {
            Shop.Consumables[k] = Shop.Consumables.GetValueOrDefault(k) + v;
            OnRefund?.Invoke(k, v);
        }
        var welds = removed.Count(j => j.Kind == JointKind.Fixed && j.Consumed.Count == 0);
        return $"관절 {removed.Count}개 해체" + (welds > 0 ? $" (용접 {welds}곳 절단)" : "") +
               (back.Count > 0 ? $", 회수: {string.Join(", ", back.Select(kv => $"{Catalog.Get(kv.Key).NameKo} {kv.Value}"))}" : "");
    }

    // ------------------------------------------------------------------ 전기 구동

    public string TogglePower(PartBody any)
    {
        var group = World.ConnectedTo(any.Part.Id);
        var motors = group.Where(id => _bodies[id].Part.Def.Role == PartRole.Motor).ToList();
        if (motors.Count == 0) return "이 기기에는 모터가 없습니다.";
        var on = !motors.Any(_running.Contains);
        foreach (var m in motors) { if (on) _running.Add(m); else _running.Remove(m); }
        if (!on) return "전원 끔";
        return PowerSource(group) is { } bat
            ? $"전원 켬 — {bat.Part.Def.NameKo} 잔량 {bat.EnergyJ / 3600:0.0} Wh"
            : "전원 켬 — 하지만 전압이 맞는 배터리가 연결되어 있지 않습니다.";
    }

    public string FlipWiring(PartBody motor)
    {
        if (motor.Part.Def.Role != PartRole.Motor) return "모터가 아닙니다.";
        var dir = -_wiring.GetValueOrDefault(motor.Part.Id, 1);
        _wiring[motor.Part.Id] = dir;
        return $"배선 극성 반전: {(dir > 0 ? "정회전" : "역회전")}";
    }

    PartBody? PowerSource(HashSet<int> group) =>
        group.Select(id => _bodies[id]).FirstOrDefault(b => b.Part.Def.Role == PartRole.Battery && b.EnergyJ > 0);

    public override void _PhysicsProcess(double delta)
    {
        foreach (var j in World.Joints)
        {
            if (j.DrivenBy is not { } motorId || !_joints.TryGetValue(j.Id, out var jn) || jn.Node is not HingeJoint3D hinge) continue;
            var motor = _bodies[motorId];
            var group = World.ConnectedTo(motorId);
            var battery = _running.Contains(motorId) ? PowerSource(group) : null;
            var spec = motor.Part.Def.Electrical!;
            var powered = battery is not null && Mathf.Abs(battery.Part.Def.Electrical!.Volts - spec.Volts) <= spec.Volts * 0.2f;
            var cmd = powered ? Command(motorId, group) : 0;
            // 지령 0 이면 모터를 풀어 관성으로 구른다 (DC 모터에 전압이 없을 때와 같다)
            hinge.SetFlag(HingeJoint3D.Flag.EnableMotor, powered && MathF.Abs(cmd) > 1e-3f);
            if (!powered || MathF.Abs(cmd) <= 1e-3f) continue;
            // 정극성: 하우징에 대해 출력축(자식)이 축 방향으로 +회전. 모터가 자식 쪽이면 부호를 뒤집는다.
            var dir = cmd * (j.Parent == motorId ? 1 : -1);
            hinge.SetParam(HingeJoint3D.Param.MotorTargetVelocity, dir * j.Velocity);
            hinge.SetParam(HingeJoint3D.Param.MotorMaxImpulse, j.Effort * (float)delta);
            // 소비 전력: 정격 전류 기준, 부하(상대 회전 속도)에 따라 15–100 %
            var parent = _bodies[j.Parent];
            var child = _bodies[j.Child];
            var rel = Mathf.Abs((child.AngularVelocity - parent.AngularVelocity).Dot(hinge.GlobalBasis.Z));
            var load = 0.15 + 0.85 * Math.Clamp(1 - rel / Math.Max(j.Velocity, 1e-3), 0, 1);
            battery!.EnergyJ = Math.Max(0, battery.EnergyJ - spec.Volts * spec.RatedCurrentA * load * MathF.Abs(cmd) * delta);
        }

        // 진공관·캠 드럼·닉시관의 대기 전력 (배터리가 있는 기기에서 항상)
        foreach (var b in _bodies.Values)
        {
            if (b.Part.Def.Role is not (PartRole.Controller or PartRole.Sequencer or PartRole.Display)) continue;
            var bat = PowerSource(World.ConnectedTo(b.Part.Id));
            var e = b.Part.Def.Electrical!;
            if (bat is not null) bat.EnergyJ = Math.Max(0, bat.EnergyJ - e.Volts * e.RatedCurrentA * delta);
        }
    }

    public override void _Process(double delta)
    {
        _clock += delta;
        if ((_readoutTimer -= delta) > 0) return;
        _readoutTimer = 0.2;
        foreach (var b in _bodies.Values)
        {
            var role = b.Part.Def.Role;
            if (role is not (PartRole.Controller or PartRole.Sequencer or PartRole.Display)) continue;
            var group = World.ConnectedTo(b.Part.Id);
            var bat = PowerSource(group);
            switch (role)
            {
                case PartRole.Display when b.Readout is not null:
                    if (bat is null) { b.Readout.Text = ""; break; }
                    var e = bat.Part.Def.Electrical!;
                    var soc = bat.EnergyJ / (e.CapacityWh * 3600);
                    // 단자 전압: 만충 시 정격의 105 %, 방전 끝에서 90 % 로 내려가는 근사
                    var volts = e.Volts * (0.9 + 0.15 * soc);
                    b.Readout.Text = $"{volts:00.0}V {soc * 100:00}%";
                    break;
                case PartRole.Controller:
                    var on = bat is not null && group.Any(_running.Contains);
                    if (b.Glow is not null) b.Glow.EmissionEnergyMultiplier = on ? 2.5f : 0;
                    if (b.Readout is not null) b.Readout.Text = $"출력 {Knob(b.Part.Id) * 100:+0;-0;0}%";
                    break;
                case PartRole.Sequencer when b.Readout is not null:
                    var step = (int)_clock % 8;
                    var v = Program(b.Part.Id)[step];
                    b.Readout.Text = $"단 {step + 1}/8 [{(v > 0 ? "+" : v < 0 ? "−" : "0")}]";
                    break;
            }
        }
    }

    // ------------------------------------------------------------------ 물성·내보내기

    /// <summary>관절의 현재 월드 자세를 코어에 반영 (기기가 움직였으므로)</summary>
    void SyncJoints()
    {
        foreach (var j in World.Joints)
        {
            if (!_joints.TryGetValue(j.Id, out var jn)) continue;
            var f = _bodies[j.Parent].GlobalTransform * jn.LocalFrame;
            j.Pivot = f.Origin.N();
            j.Axis = f.Basis.X.Normalized().N();
            j.Secondary = f.Basis.Y.Normalized().N();
        }
    }

    public Assembly DeviceOf(PartBody any, string name)
    {
        SyncJoints();
        return World.Extract(World.ConnectedTo(any.Part.Id), name);
    }

    public string Properties(PartBody any)
    {
        var dev = DeviceOf(any, "device");
        var mp = dev.ComputeMassProperties();
        var bom = Export.Bom(dev).TotalKrw;
        return $"기기 물성 — 부품 {dev.Parts.Count}개 · 관절 {dev.Joints.Count}개 · 질량 {mp.Mass:0.###} kg · " +
               $"무게중심 ({mp.CenterOfMass.X:0.000}, {mp.CenterOfMass.Y:0.000}, {mp.CenterOfMass.Z:0.000}) m · " +
               $"관성 Ixx {mp.Inertia[0, 0]:0.####} Iyy {mp.Inertia[1, 1]:0.####} Izz {mp.Inertia[2, 2]:0.####} kg·m² · 재료비 ₩{bom:N0}";
    }

    /// <summary>URDF·USDA·STL·BOM 을 exports 폴더에 쓴다.</summary>
    public string ExportDevice(PartBody any) => ExportAssembly(DeviceOf(any, $"device_{Stamp()}"));

    static string Stamp() => Time.GetDatetimeStringFromSystem().Replace(":", "").Replace("-", "").Replace("T", "_");

    /// <summary>조립체를 exports/ 에 URDF·USDA·STL·BOM 으로 쓴다. 이름은 조립체 이름 (없으면 시각).</summary>
    public static string ExportAssembly(Assembly dev)
    {
        var name = string.Concat(dev.Name.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_'));
        if (name.Trim('_').Length == 0) name = $"device_{Stamp()}";
        var dir = ProjectSettings.GlobalizePath("res://exports");
        System.IO.Directory.CreateDirectory(dir);
        // 내보낸 파일은 게임 리소스가 아니다: Godot 가 BOM CSV 를 번역 파일로 가져오다 멈추지 않게 한다
        System.IO.File.WriteAllText(System.IO.Path.Combine(dir, ".gdignore"), "");
        var path = System.IO.Path.Combine(dir, name);
        System.IO.File.WriteAllText(path + ".urdf", Export.Urdf(dev));
        System.IO.File.WriteAllText(path + ".usda", Export.Usda(dev));
        System.IO.File.WriteAllText(path + ".stl", Export.Stl(dev));
        var (md, csv, total) = Export.Bom(dev);
        System.IO.File.WriteAllText(path + ".bom.md", md);
        System.IO.File.WriteAllText(path + ".bom.csv", csv);
        return $"내보냄: exports/{name}.(urdf|usda|stl|bom.md|bom.csv) — 부품 {dev.Parts.Count}개, {dev.TotalMass:0.###} kg, ₩{total:N0}";
    }

    /// <summary>
    /// 기기를 월드에서 통째로 뺀다 (창고에 넣을 때). 체결 부품은 기기에 그대로 붙어 있으므로 회수하지 않는다.
    /// </summary>
    public void RemoveDevice(IEnumerable<int> partIds)
    {
        var ids = partIds.ToHashSet();
        foreach (var j in World.Joints.Where(j => ids.Contains(j.Parent) || ids.Contains(j.Child)).ToList())
        {
            if (_joints.Remove(j.Id, out var n)) n.Node.QueueFree();
            World.Joints.Remove(j);
        }
        foreach (var id in ids)
        {
            _running.Remove(id);
            World.RemovePart(id);
            if (_bodies.Remove(id, out var b)) b.QueueFree();
        }
    }

    public void Remove(PartBody b)
    {
        Disassemble(b);
        World.RemovePart(b.Part.Id);
        _bodies.Remove(b.Part.Id);
        b.QueueFree();
    }
}
