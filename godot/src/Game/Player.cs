using Godot;
using DollyForge.Core;

namespace DollyForge.Game;

/// <summary>
/// 1인칭 사이보그. 이동·점프는 사람 기준 실제 값, 들기·밀기는 사람이 낼 수 있는 힘으로 제한된다.
/// 무거운 물건(약 25 kg 이상)은 들리지 않고 끌기만 된다.
/// </summary>
public partial class Player : CharacterBody3D
{
    const float Walk = 1.6f, Run = 4.0f, JumpSpeed = 3.1f, Gravity = 9.81f, GroundAccel = 8f, AirAccel = 1.5f;
    const float Reach = 2.5f, LiftForce = 250f, PushForce = 600f, HoldTorque = 20f;

    public enum Tool { Hand, Bolt, Weld, Screw, Axle, Hinge, Slide, Ball, Wrench }

    static readonly (Tool Tool, string NameKo, JoinMethod? Method)[] Tools =
    [
        (Tool.Hand, "손 — 집기·옮기기", null),
        (Tool.Bolt, "렌치·볼트 — 볼트 체결", JoinMethod.Bolt),
        (Tool.Weld, "용접기 — 같은 금속 용접", JoinMethod.Weld),
        (Tool.Screw, "전동 드라이버 — 목재 나사", JoinMethod.Screw),
        (Tool.Axle, "축 끼우기 — 회전 관절", JoinMethod.Axle),
        (Tool.Hinge, "경첩 달기", JoinMethod.Hinge),
        (Tool.Slide, "리니어 부싱 — 직선 이동", JoinMethod.Slide),
        (Tool.Ball, "로드엔드 — 볼 조인트", JoinMethod.Ball),
        (Tool.Wrench, "해체 공구 — 관절 풀기", null),
    ];

    public Devices Devices { get; set; } = null!;
    public Camera3D Camera { get; private set; } = null!;
    public Action? OpenCatalog;
    public Action<string>? Notify;

    float _pitch;
    int _tool;
    PartBody? _held, _selected;
    float _holdDistance;
    Basis _holdRelative;
    Label3D _tag = null!;
    PartBody? _aimBody;
    Vector3 _aimPoint;
    GodotObject? _aimObject;

    public string ToolName => Tools[_tool].NameKo;
    public float EyeHeight { get; set; } = 1.55f;

    public override void _Ready()
    {
        CollisionLayer = PartBody.LayerPlayer;
        CollisionMask = PartBody.LayerWorld | PartBody.LayerParts;
        FloorSnapLength = 0.3f;
        FloorMaxAngle = Mathf.DegToRad(38);
        AddChild(new CollisionShape3D
        {
            Shape = new CapsuleShape3D { Radius = 0.24f, Height = EyeHeight + 0.11f },
            Position = new Vector3(0, (EyeHeight + 0.11f) / 2, 0),
        });
        Camera = new Camera3D { Position = new Vector3(0, EyeHeight, 0), Fov = 75, Near = 0.03f, Current = true };
        AddChild(Camera);
        _tag = Look.Tag("", 0.0011f);
        _tag.Billboard = BaseMaterial3D.BillboardModeEnum.Enabled;
        _tag.Modulate = new Color(0.95f, 0.93f, 0.86f);
        _tag.OutlineModulate = new Color(0.05f, 0.05f, 0.05f, 0.9f);
        _tag.OutlineSize = 10;
        _tag.NoDepthTest = true;
        _tag.Visible = false;
        _tag.TopLevel = true;
        AddChild(_tag);
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (Input.MouseMode != Input.MouseModeEnum.Captured)
        {
            if (e is InputEventMouseButton { Pressed: true }) Input.MouseMode = Input.MouseModeEnum.Captured;
            return;
        }
        switch (e)
        {
            case InputEventMouseMotion m:
                RotateY(-m.Relative.X * 0.0022f);
                _pitch = Mathf.Clamp(_pitch - m.Relative.Y * 0.0022f, -1.45f, 1.45f);
                Camera.Rotation = new Vector3(_pitch, 0, 0);
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelUp, Pressed: true } when _held is not null:
                _holdDistance = Mathf.Min(_holdDistance + 0.1f, Reach);
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelDown, Pressed: true } when _held is not null:
                _holdDistance = Mathf.Max(_holdDistance - 0.1f, 0.5f);
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } b:
                if (b.Pressed) UsePrimary(); else _held = null;
                break;
            case InputEventKey { Pressed: true, Echo: false } k:
                OnKey(k.PhysicalKeycode);
                break;
        }
    }

    void OnKey(Key key)
    {
        if (key is >= Key.Key1 and <= Key.Key9)
        {
            _tool = (int)(key - Key.Key1);
            _selected = null;
            _held = null;
            Notify?.Invoke(ToolName);
            return;
        }
        switch (key)
        {
            case Key.Escape:
                if (_selected is not null) { _selected = null; Notify?.Invoke("선택 취소"); }
                else Input.MouseMode = Input.MouseModeEnum.Visible;
                break;
            case Key.E:
                if (_aimObject is Node n && n.HasMeta("interact") && n.GetMeta("interact").AsString() == "catalog") OpenCatalog?.Invoke();
                break;
            case Key.Q when _aimBody is not null:
                Notify?.Invoke(Devices.TogglePower(_aimBody));
                break;
            case Key.R when _aimBody is not null:
                Notify?.Invoke(Devices.FlipWiring(_aimBody));
                break;
            case Key.F8 when _aimBody is not null:
                Notify?.Invoke(Devices.ExportDevice(_aimBody));
                break;
            case Key.F9 when _aimBody is not null:
                Notify?.Invoke(Devices.Properties(_aimBody));
                break;
            case Key.Z when _held is not null:
                _holdRelative = new Basis(Vector3.Up, Mathf.Pi / 2) * _holdRelative;
                break;
            case Key.X when _held is not null:
                _holdRelative = new Basis(Vector3.Right, Mathf.Pi / 2) * _holdRelative;
                break;
        }
    }

    void UsePrimary()
    {
        var tool = Tools[_tool];
        if (_aimBody is null) return;
        if (tool.Tool == Tool.Hand)
        {
            _held = _aimBody;
            _holdDistance = Mathf.Clamp(Camera.GlobalPosition.DistanceTo(_aimBody.GlobalPosition), 0.5f, Reach);
            _holdRelative = Camera.GlobalBasis.Inverse() * _held.GlobalBasis.Orthonormalized();
            return;
        }
        if (tool.Tool == Tool.Wrench)
        {
            Notify?.Invoke(Devices.Disassemble(_aimBody));
            return;
        }
        if (_selected is null)
        {
            _selected = _aimBody;
            Notify?.Invoke($"기준 부품: {_aimBody.Part.Def.NameKo} — 붙일 부품을 선택하세요");
            return;
        }
        var (_, msg) = Devices.Join(tool.Method!.Value, _selected, _aimBody, _aimPoint);
        Notify?.Invoke(msg);
        _selected = null;
    }

    public override void _PhysicsProcess(double delta)
    {
        var dt = (float)delta;
        Aim();

        // 이동: 입력 방향으로 가속 (사람의 보행·달리기 속도)
        var input = Input.GetVector("move_left", "move_right", "move_forward", "move_back");
        var wish = (GlobalBasis * new Vector3(input.X, 0, input.Y)) with { Y = 0 };
        var speed = Input.IsActionPressed("run") ? Run : Walk;
        var v = Velocity;
        var target = wish.LimitLength(1) * speed;
        var horizontal = new Vector3(v.X, 0, v.Z).MoveToward(target, (IsOnFloor() ? GroundAccel : AirAccel) * dt);
        v = new Vector3(horizontal.X, v.Y, horizontal.Z);
        if (IsOnFloor() && Input.IsActionJustPressed("jump")) v.Y = JumpSpeed;
        if (!IsOnFloor()) v.Y -= Gravity * dt;
        Velocity = v;
        MoveAndSlide();

        // 밀기: 사람이 낼 수 있는 힘 이하로만
        for (var i = 0; i < GetSlideCollisionCount(); i++)
        {
            var c = GetSlideCollision(i);
            if (c.GetCollider() is RigidBody3D rb)
            {
                var n = -c.GetNormal() with { Y = 0 };
                if (n.LengthSquared() < 1e-4f) continue;
                n = n.Normalized();
                var closing = horizontal.Dot(n) - rb.LinearVelocity.Dot(n);
                if (closing > 0)
                    rb.ApplyImpulse(n * Mathf.Min(PushForce * dt, rb.Mass * closing), c.GetPosition() - rb.GlobalPosition);
            }
        }

        if (_held is not null) Hold(dt);
    }

    /// <summary>들고 있는 부품을 손 위치로 당긴다. 힘·토크는 사람 한계로 제한된다.</summary>
    void Hold(float dt)
    {
        var b = _held!;
        if (!IsInstanceValid(b) || Camera.GlobalPosition.DistanceTo(b.GlobalPosition) > Reach + 1)
        {
            _held = null;
            return;
        }
        var target = Camera.GlobalPosition - Camera.GlobalBasis.Z * _holdDistance;
        var err = target - b.GlobalPosition;
        // 질량에 비례한 임계 감쇠 스프링 + 중력 보상, 전체는 LiftForce 로 제한
        var k = 60f * b.Mass;
        var c = 2 * Mathf.Sqrt(k * b.Mass);
        var f = err * k - b.LinearVelocity * c + Vector3.Up * b.Mass * Gravity;
        f = f.LimitLength(LiftForce);
        b.ApplyCentralForce(f);

        var want = Camera.GlobalBasis * _holdRelative;
        var q = (want * b.GlobalBasis.Orthonormalized().Inverse()).GetRotationQuaternion();
        var angle = q.GetAngle();
        if (angle > Mathf.Pi) angle -= Mathf.Tau;
        var axis = angle > 1e-4f ? q.GetAxis() : Vector3.Zero;
        var I = Mathf.Max(b.Inertia.X, Mathf.Max(b.Inertia.Y, b.Inertia.Z));
        var torque = (axis * angle * 40f * I - b.AngularVelocity * 2f * Mathf.Sqrt(40f) * I).LimitLength(HoldTorque);
        b.ApplyTorque(torque);
    }

    void Aim()
    {
        var from = Camera.GlobalPosition;
        var to = from - Camera.GlobalBasis.Z * Reach;
        var q = PhysicsRayQueryParameters3D.Create(from, to, PartBody.LayerWorld | PartBody.LayerParts, [GetRid()]);
        var hit = GetWorld3D().DirectSpaceState.IntersectRay(q);
        _aimObject = hit.Count > 0 ? hit["collider"].AsGodotObject() : null;
        _aimBody = _aimObject as PartBody;
        _aimPoint = hit.Count > 0 ? hit["position"].AsVector3() : to;

        // 꼬리표: 조준한 부품의 이름·질량·가격, 또는 상호작용 안내
        if (_aimBody is not null)
        {
            var d = _aimBody.Part.Def;
            var extra = d.Role == PartRole.Battery ? $"\n잔량 {_aimBody.EnergyJ / 3600:0.0} / {d.Electrical!.CapacityWh:0} Wh" :
                        d.ShaftMm > 0 ? $"\n축 Ø{d.ShaftMm} mm" : d.BoreMm > 0 ? $"\n구멍 Ø{d.BoreMm} mm" : "";
            var sel = _selected == _aimBody ? "\n[기준 부품]" : "";
            _tag.Text = $"{d.NameKo}\n{d.Mass:0.###} kg · ₩{d.PriceKrw:N0}{extra}{sel}";
            _tag.GlobalPosition = _aimPoint + (from - _aimPoint).Normalized() * 0.08f + Vector3.Up * 0.06f;
            _tag.Visible = true;
        }
        else if (_aimObject is Node n && n.HasMeta("interact"))
        {
            _tag.Text = "E — 카탈로그 펼치기";
            _tag.GlobalPosition = _aimPoint + Vector3.Up * 0.12f;
            _tag.Visible = true;
        }
        else _tag.Visible = false;
    }
}
