using Godot;
using DollyForge.Core;

namespace DollyForge.Game;

/// <summary>
/// 1인칭 사이보그. 걷기·달리기는 사람 기준 실제 값이고, 점프 이륙 속도와 들 수 있는 힘은
/// 지금 끼운 몸 모듈의 관절 토크·질량에서 계산한다 (표준 여성형: 이륙 3.1 m/s, 약 240 N).
/// 그보다 무거운 물건은 들리지 않고 끌기만 된다.
/// </summary>
public partial class Player : CharacterBody3D
{
    const float Walk = 1.6f, Run = 4.0f, Gravity = 9.81f, GroundAccel = 8f, AirAccel = 1.5f;
    const float Reach = 2.5f, PushForce = 600f, HoldTorque = 20f;

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
    public Action? OpenCatalog, OpenStorage, OpenSurgery, ExportBody, BodyChanged;
    public Action<PartBody>? Stow, EditDrum;
    public Action<string>? Notify;

    float _pitch;
    float _headBobPhase;
    float _landingVelocity;
    Vector3 _headOffset;
    Vector3 _headOffsetVelocity;
    bool _wasOnFloor;
    int _tool;

    // ------------------------------------------------------------------ 시점: 1인칭 → 3인칭(뒤) → 3인칭(앞), V 로 바꾼다

    public enum ViewMode { FirstPerson, ThirdBack, ThirdFront }
    public ViewMode View { get; private set; } = ViewMode.FirstPerson;
    /// <summary>3인칭 카메라: 눈(Camera)에서 뒤나 앞으로 뻗은 팔 끝. 벽에 닿으면 몸 쪽으로 당겨진다.</summary>
    SpringArm3D _viewArm = null!;
    Camera3D _viewCamera = null!;
    /// <summary>3인칭에서 보이는 내 몸 (1인칭에서는 숨긴다: 카메라가 머리 안에 있다)</summary>
    CyborgBody? _avatar;
    byte _avatarGender = byte.MaxValue;

    public void CycleView() => SetView(View switch
    {
        ViewMode.FirstPerson => ViewMode.ThirdBack,
        ViewMode.ThirdBack => ViewMode.ThirdFront,
        _ => ViewMode.FirstPerson,
    });

    public void SetView(ViewMode mode)
    {
        View = mode;
        // 조준·들기는 늘 눈(Camera)에서 계산하고, 화면만 3인칭 카메라로 보여 준다
        _viewArm.Rotation = new Vector3(0, mode == ViewMode.ThirdFront ? Mathf.Pi : 0, 0);
        _viewCamera.Current = mode != ViewMode.FirstPerson;
        Camera.Current = mode == ViewMode.FirstPerson;
        if (_avatar is not null) _avatar.Visible = mode != ViewMode.FirstPerson;
    }

    /// <summary>체형이 바뀌면 3인칭용 몸을 다시 만든다 (몸 앞(+Z)이 플레이어 앞(-Z)을 보게)</summary>
    void RebuildAvatar()
    {
        if (_viewArm is null || Body.Gender == _avatarGender) return;
        _avatar?.QueueFree();
        _avatarGender = Body.Gender;
        _avatar = CyborgBody.Build(Body.Gender);
        _avatar.Rotation = new Vector3(0, Mathf.Pi, 0);
        _avatar.Visible = View != ViewMode.FirstPerson;
        AddChild(_avatar);
    }
    PartBody? _held, _selected;
    float _holdDistance;
    Basis _holdRelative;
    Label3D _tag = null!;
    PartBody? _aimBody;
    Vector3 _aimPoint;
    GodotObject? _aimObject;

    public string ToolName => Tools[_tool].NameKo;
    /// <summary>눈 높이 (m). 접속 뒤 체형이 정해지면 몸통 충돌체와 카메라도 따라간다.</summary>
    public float EyeHeight
    {
        get => _eyeHeight;
        set
        {
            _eyeHeight = value;
            if (_capsule is null) return;
            ((CapsuleShape3D)_capsule.Shape).Height = value + 0.11f;
            _capsule.Position = new Vector3(0, (value + 0.11f) / 2, 0);
            Camera.Position = new Vector3(0, value, 0);
        }
    }
    float _eyeHeight = 1.55f;
    CollisionShape3D? _capsule;

    // ------------------------------------------------------------------ Android Tool Environment: 몸에 찬 기기·비행

    /// <summary>지금 몸 구성 (체형·부위별 모듈). 찬 기기는 Worn 에 따로 둔다.</summary>
    public BodyConfig Body { get; private set; } = BodyConfig.Default(0);
    /// <summary>몸 구성 + 찬 기기에서 계산한 능력</summary>
    public BodyCapabilities Caps { get; private set; } = Cyborg.Capabilities(BodyConfig.Default(0));
    /// <summary>몸 질량 (kg): Android Cyborg 모듈 합 = 실제 체중</summary>
    public float BodyMassKg { get; private set; } = Cyborg.Female.Mass;
    public List<string> WornSkus => [.. Worn.Select(w => w.Gear.Sku!)];
    public BodyConfig FullBody => Body with { Worn = WornSkus };

    /// <summary>몸 구성을 바꾼다 (접속 시 복원, 정비 수술대에서 모듈 교체)</summary>
    public void ApplyBody(BodyConfig body)
    {
        Body = body with { Worn = [] };
        EyeHeight = body.Gender == 1 ? 1.67f : 1.55f;
        RebuildAvatar();
        BodyMassKg = Cyborg.Assemble(Body, System.Numerics.Vector3.Zero).TotalMass;
        RefreshCaps();
    }

    /// <summary>서버에 저장된 찬 기기를 다시 찬다 (연료는 가득)</summary>
    public void RestoreWorn(IEnumerable<string> skus)
    {
        Worn.Clear();
        foreach (var def in skus.Select(s => Catalog.Get(s).Build()!))
            Worn.Add((def, def.ThrustN > 0 && !def.Fictional ? JetFuelKg : 0));
        RefreshCaps();
    }

    void RefreshCaps() => Caps = Cyborg.Capabilities(FullBody);
    /// <summary>몸에 찬 기기들과 남은 연료 (kg, 제트 터빈만)</summary>
    public List<(PartDef Gear, float FuelKg)> Worn { get; } = new();
    /// <summary>자동 시험용: 참이면 추력 키를 누른 것으로 친다</summary>
    public bool ThrottleOverride { get; set; }
    /// <summary>자동 시험용: 다음 물리 틱에 점프 키를 누른 것으로 친다</summary>
    public bool JumpRequest { get; set; }
    readonly RandomNumberGenerator _wobble = new();
    const float JetFuelKg = 1.2f, JetBurnKgPerSec = 0.01f; // 등유 1.5 L, 최대 추력에서 분당 0.6 kg

    public float TotalMassKg => BodyMassKg + Worn.Sum(w => w.Gear.Mass + w.FuelKg);

    public string Wear(PartBody gear)
    {
        var def = gear.Part.Def;
        if (def.Wear is not { } slot) return "몸에 찰 수 있는 기기가 아닙니다.";
        var limit = slot == WearSlot.Wrist ? 2 : 1;
        if (Worn.Count(w => w.Gear.Wear == slot) >= limit)
            return slot == WearSlot.Wrist ? "두 손목이 모두 차 있습니다. (H 로 벗기)" : "이미 머리에 쓰고 있습니다. (H 로 벗기)";
        Worn.Add((def, def.Role == PartRole.Wearable && def.ThrustN > 0 && !def.Fictional ? JetFuelKg : 0));
        Devices.Remove(gear);
        RefreshCaps();
        BodyChanged?.Invoke();
        var lift = Cyborg.LiftAcceleration(BodyMassKg, Worn.Select(w => w.Gear));
        var thrust = Worn.Sum(w => w.Gear.ThrustN);
        return $"{def.NameKo} 착용 ({(slot == WearSlot.Wrist ? "손목" : "머리")}) — 추력 합 {thrust:0} N, 몸무게 {TotalMassKg * 9.81f:0} N → " +
               (thrust <= 0 ? "비행 불가" : lift > 0 ? $"상승 가속 +{lift:0.0} m/s², Space 를 누르고 있으면 난다" : $"떠오르지 못함 (가속 {lift:0.0} m/s²) — 낙하만 늦춘다");
    }

    public string TakeOffAll()
    {
        if (Worn.Count == 0) return "찬 기기가 없습니다.";
        var front = GlobalPosition - GlobalBasis.Z * 0.8f + Vector3.Up * 0.6f;
        var i = 0;
        foreach (var (gear, _) in Worn) Devices.Spawn(gear, new Transform3D(Basis.Identity, front + GlobalBasis.X * (i++ * 0.25f - 0.25f)));
        var n = Worn.Count;
        Worn.Clear();
        RefreshCaps();
        BodyChanged?.Invoke();
        return $"기기 {n}개를 벗어 내려놓았습니다.";
    }

    /// <summary>추력: 몸에 찬 추력기의 합력 / 전체 질량. 자세 안정기가 없으면 추력 방향이 흔들린다.</summary>
    Vector3 ThrustAcceleration(float dt, Vector3 wish)
    {
        var thrusting = ThrottleOverride || Input.IsActionPressed("jump");
        if (!thrusting || Worn.Count == 0) return Vector3.Zero;
        var force = 0f;
        for (var i = 0; i < Worn.Count; i++)
        {
            var (gear, fuel) = Worn[i];
            if (gear.ThrustN <= 0) continue;
            if (!gear.Fictional)
            {
                if (fuel <= 0) continue; // 연료가 떨어진 터빈
                Worn[i] = (gear, Mathf.Max(0, fuel - JetBurnKgPerSec * dt));
            }
            force += gear.ThrustN;
        }
        if (force <= 0) return Vector3.Zero;
        // 추력 방향: 대부분 위, 이동 입력 쪽으로 약간 기울인다
        var dir = (Vector3.Up * 0.88f + wish.LimitLength(1) * 0.35f).Normalized();
        if (!Worn.Any(w => w.Gear.Wear == WearSlot.Head))
            dir = (dir + new Vector3(_wobble.RandfRange(-0.35f, 0.35f), 0, _wobble.RandfRange(-0.35f, 0.35f))).Normalized();
        return dir * force / TotalMassKg;
    }

    public override void _Ready()
    {
        CollisionLayer = PartBody.LayerPlayer;
        CollisionMask = PartBody.LayerWorld | PartBody.LayerParts;
        FloorSnapLength = 0.3f;
        FloorMaxAngle = Mathf.DegToRad(38);
        Camera = new Camera3D { Fov = 75, Near = 0.03f, Current = true };
        AddChild(Camera);
        _capsule = new CollisionShape3D { Shape = new CapsuleShape3D { Radius = 0.24f } };
        AddChild(_capsule);
        EyeHeight = _eyeHeight;
        _viewArm = new SpringArm3D { SpringLength = 2.4f, Margin = 0.15f, CollisionMask = PartBody.LayerWorld, Position = new Vector3(0.0f, 0.15f, 0) };
        _viewArm.Shape = new SphereShape3D { Radius = 0.18f };
        _viewArm.AddExcludedObject(GetRid());
        Camera.AddChild(_viewArm);
        _viewCamera = new Camera3D { Fov = 70, Near = 0.05f };
        _viewArm.AddChild(_viewCamera);
        RebuildAvatar();
        SetView(View);
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
        if (!Pointer.Captured)
        {
            if (e is InputEventMouseButton { Pressed: true }) Pointer.Mode = Input.MouseModeEnum.Captured;
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
            case Key.V:
                CycleView();
                Notify?.Invoke(View switch
                {
                    ViewMode.FirstPerson => "1인칭 시점",
                    ViewMode.ThirdBack => "3인칭 시점 — 뒤에서",
                    _ => "3인칭 시점 — 앞에서",
                });
                break;
            case Key.Escape:
                if (_selected is not null) { _selected = null; Notify?.Invoke("선택 취소"); }
                else Pointer.Mode = Input.MouseModeEnum.Visible;
                break;
            case Key.E:
                if (_aimObject is Node n && n.HasMeta("interact") && n.GetMeta("interact").AsString() == "catalog") OpenCatalog?.Invoke();
                else if (_aimObject is Node s && s.HasMeta("interact") && s.GetMeta("interact").AsString() == "surgery") OpenSurgery?.Invoke();
                else if (_aimBody?.Part.Def.Role == PartRole.Sequencer) EditDrum?.Invoke(_aimBody);
                break;
            case Key.Bracketleft when _aimBody is not null:
                Notify?.Invoke(Devices.TurnKnob(_aimBody, -0.1f));
                break;
            case Key.Bracketright when _aimBody is not null:
                Notify?.Invoke(Devices.TurnKnob(_aimBody, 0.1f));
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
            case Key.G when _aimBody is not null:
                Notify?.Invoke(Wear(_aimBody));
                break;
            case Key.H:
                Notify?.Invoke(TakeOffAll());
                break;
            case Key.F5 when _aimBody is not null:
                Stow?.Invoke(_aimBody);
                break;
            case Key.F6:
                OpenStorage?.Invoke();
                break;
            case Key.F7:
                ExportBody?.Invoke();
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
        var jump = Input.IsActionJustPressed("jump") || JumpRequest;
        JumpRequest = false;
        if (IsOnFloor() && jump && Worn.All(w => w.Gear.ThrustN <= 0)) v.Y = Caps.JumpSpeed;
        var thrust = ThrustAcceleration(dt, wish);
        v += thrust * dt;
        if (!IsOnFloor() || thrust.Y > Gravity) v.Y -= Gravity * dt;
        Velocity = v;
        MoveAndSlide();

        UpdateHeadMotion(dt, new Vector3(Velocity.X, 0, Velocity.Z), v.Y);
        _avatar?.Animate(dt, new Vector2(Velocity.X, Velocity.Z).Length());

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

    void UpdateHeadMotion(float dt, Vector3 horizontalVelocity, float verticalVelocity)
    {
        var grounded = IsOnFloor();
        var speed = horizontalVelocity.Length();
        var firstPerson = View == ViewMode.FirstPerson;
        var intensity = firstPerson ? 1f : 0f;

        if (grounded && speed > 0.12f)
            _headBobPhase = Mathf.PosMod(_headBobPhase + speed * dt * Mathf.Tau / 1.35f, Mathf.Tau);

        var bob = grounded && speed > 0.12f
            ? new Vector3(
                Mathf.Sin(_headBobPhase) * 0.006f,
                (0.5f - 0.5f * Mathf.Cos(_headBobPhase * 2f)) * Mathf.Min(speed / Run, 1f) * 0.014f,
                0)
            : Vector3.Zero;

        if (!_wasOnFloor && grounded && verticalVelocity < 0)
            _landingVelocity += Mathf.Min(-verticalVelocity * 0.018f, 0.22f);
        _wasOnFloor = grounded;

        var landingAmount = _landingVelocity;
        _landingVelocity = Mathf.MoveToward(_landingVelocity, 0, dt * 0.9f);
        var target = (bob + Vector3.Down * landingAmount) * intensity;
        var acceleration = (target - _headOffset) * 42f - _headOffsetVelocity * 13f;
        _headOffsetVelocity += acceleration * dt;
        _headOffset += _headOffsetVelocity * dt;

        var localVelocity = GlobalBasis.Inverse() * horizontalVelocity;
        var roll = firstPerson ? -Mathf.Clamp(localVelocity.X / Run, -1f, 1f) * 0.012f : 0f;
        var pitchSway = firstPerson && grounded ? Mathf.Sin(_headBobPhase) * 0.003f * Mathf.Min(speed / Run, 1f) : 0f;
        Camera.Position = new Vector3(_headOffset.X, _eyeHeight + _headOffset.Y, _headOffset.Z);
        Camera.Rotation = new Vector3(_pitch + pitchSway, 0, roll);
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
        // 질량에 비례한 임계 감쇠 스프링 + 중력 보상, 전체는 팔 모듈이 낼 수 있는 힘으로 제한
        var k = 60f * b.Mass;
        var c = 2 * Mathf.Sqrt(k * b.Mass);
        var f = err * k - b.LinearVelocity * c + Vector3.Up * b.Mass * Gravity;
        f = f.LimitLength(Caps.LiftN);
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
            var extra = d.Fictional ? $"\n가상 부품 · 현실 대체: {d.RealEquivalentKo}" : "";
            extra += d.Role == PartRole.Battery ? $"\n잔량 {_aimBody.EnergyJ / 3600:0.0} / {d.Electrical!.CapacityWh:0} Wh" :
                        d.Role == PartRole.Controller ? $"\n출력 {Devices.Knob(_aimBody.Part.Id) * 100:+0;-0;0}% · [ ] 로 조절" :
                        d.Role == PartRole.Sequencer ? "\nE — 캠 핀 다시 꽂기" :
                        d.Wear is { } ws ? $"\nG — {(ws == WearSlot.Wrist ? "손목에 차기" : "머리에 쓰기")}" + (d.ThrustN > 0 ? $" · 추력 {d.ThrustN:0} N" : "") :
                        d.ShaftMm > 0 ? $"\n축 Ø{d.ShaftMm} mm" : d.BoreMm > 0 ? $"\n구멍 Ø{d.BoreMm} mm" : "";
            var sel = _selected == _aimBody ? "\n[기준 부품]" : "";
            _tag.Text = $"{d.NameKo}\n{d.Mass:0.###} kg · ₩{d.PriceKrw:N0}{extra}{sel}";
            _tag.GlobalPosition = _aimPoint + (from - _aimPoint).Normalized() * 0.08f + Vector3.Up * 0.06f;
            _tag.Visible = true;
        }
        else if (_aimObject is Node n && n.HasMeta("interact"))
        {
            _tag.Text = n.GetMeta("interact").AsString() == "surgery" ? "E — 정비 기록부 펼치기" : "E — 카탈로그 펼치기";
            _tag.GlobalPosition = _aimPoint + Vector3.Up * 0.12f;
            _tag.Visible = true;
        }
        else _tag.Visible = false;
    }
}
