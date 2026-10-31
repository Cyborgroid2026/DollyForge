using System.Numerics;

namespace DollyForge;

/// <summary>스튜디오 안에 서 있는 인형 한 기 (위치·방향·걷기 속도)</summary>
sealed class Actor(string name, CyborgBody body, Vector3 pos)
{
    public readonly string     Name = name;
    public readonly CyborgBody Body = body;
    public Vector3 Pos   = pos;
    public float   Yaw   = 0f;     // 0 = +Z(카메라 쪽)를 본다
    public float   Speed = 0f;

    public Vector3   Facing => new(MathF.Sin(Yaw), 0, MathF.Cos(Yaw));
    public Matrix4x4 Model  => Matrix4x4.CreateRotationY(Yaw) * Matrix4x4.CreateTranslation(Pos);
    public float     EyeHeight => Body.Height - 0.11f;
}

/// <summary>
/// 카메라 + 씬 상태.
/// 처음엔 캐릭터 선택 화면(두 인형 정면). 1/2 또는 화면의 카드로 고르면 그 인형을 조종한다.
/// V = 1인칭 → 3인칭(뒤) → 3인칭(앞), C = 다시 선택.
/// </summary>
sealed class World
{
    public const float FovY = 0.9f;   // 배경과 몸이 같은 화각을 써야 방 시차가 맞는다

    public enum ViewMode { FirstPerson, ThirdBack, ThirdFront }

    public readonly Actor[] Actors;
    public int      Selected = -1;               // -1 = 선택 화면
    public ViewMode CamView  = ViewMode.ThirdBack;
    public Actor?   Player   => Selected >= 0 ? Actors[Selected] : null;

    // 카메라
    public Vector3 CamPos  = new(0, 1.15f, 3.2f);
    public Vector3 LookDir = -Vector3.UnitZ;
    public float   CamYaw   = 0f;      // 마우스 좌우 (0 = -Z 를 본다)
    public float   CamPitch = -0.12f;
    public float   CamDist  = 2.6f;    // 3인칭 거리, 휠로 1.0 ~ 6.0 m

    /// <summary>화면 위 안내 줄이 바뀌면 브라우저로 보낸다</summary>
    public string Status { get; private set; } = "";

    readonly HashSet<string> _keys = [];

    public World(CyborgBody female, CyborgBody male)
    {
        Actors = [new Actor("여성형", female, Home[0]), new Actor("남성형", male, Home[1])];
        UpdateStatus();
    }

    public bool IsDown(string code) => _keys.Contains(code);

    public Matrix4x4 View() => Matrix4x4.CreateLookAt(CamPos, CamPos + LookDir, Vector3.UnitY);

    public Matrix4x4 Proj(float aspect, float fovY = FovY)
        => Matrix4x4.CreatePerspectiveFieldOfView(fovY, aspect, 0.03f, 200f);

    // 마우스 시점 방향 (yaw 0 = -Z)
    Vector3 Aim => new(MathF.Sin(CamYaw) * MathF.Cos(CamPitch), MathF.Sin(CamPitch), -MathF.Cos(CamYaw) * MathF.Cos(CamPitch));

    /// <summary>선택 화면에서는 둘 다, 조종 중에는 고른 인형만 씬에 있다</summary>
    public bool IsVisible(Actor a) => Selected < 0 || a == Player;

    // 레퍼런스 구도: 둘이 어깨가 닿을 만큼 붙어 서고, 여성형이 반걸음 앞
    static readonly Vector3[] Home = [new(-0.24f, 0, 0.14f), new(0.26f, 0, -0.14f)];
    // 걷는 동작의 한 순간 (위상, 보폭): 둘 다 한 발을 내딛은 자세
    static readonly (float Phase, float Stride)[] ShowcaseStep = [(MathF.PI / 2, 0.33f), (MathF.PI / 2, 0.38f)];

    /// <summary>캐릭터를 고른 뒤 들어가는 세계 (0장 재설계: 평범한 방에서 시작). 없으면 예전처럼 스튜디오 안에서 걷는다</summary>
    public WorldScene? Scene;
    public bool InWorld => Selected >= 0 && Scene is not null;
    float _vy;
    bool _grounded;
    /// <summary>세계를 바꾸는 손짓 (Program 이 세계 서버에 요청한다): 왼쪽 클릭 파기 · 오른쪽 클릭 붓기 · E 줍기 · Q 내려놓기 · F 잇기 · X 풀기</summary>
    public enum Gesture { None, Dig, Pour, Pickup, Drop, Join, Split }
    /// <summary>손에 든 것의 자세: R 세로축 90° · T 앞으로 눕히기 90° (놓을 때 그대로 놓인다)</summary>
    public Quaternion HeldTurn = Quaternion.Identity;
    public readonly Queue<Gesture> Gestures = new();
    /// <summary>상태 줄에 함께 보이는 것: 들고 있는 것, 세계 서버의 알림</summary>
    public string Held = "", Note = "";
    public void SetHud(string held, string note) { if (held == Held && note == Note) return; Held = held; Note = note; UpdateStatus(); }
    public Vector3 EyePos => Player is { } p ? p.Pos + new Vector3(0, p.EyeHeight, 0) : CamPos;

    /// <summary>3인칭 카메라가 좁은 곳에서 머리에 붙어 몸을 그리지 않는다</summary>
    public bool PlayerHidden { get; private set; }

    public void Select(int i)
    {
        if (i < 0 || i >= Actors.Length) return;
        Selected = i;
        var p = Actors[i];
        if (Scene is not null)
        {
            // 내 집 침실, 충전 침대 옆에서 깨어난다
            p.Pos = Scene.Spawn; p.Yaw = Scene.SpawnYaw;
            CamYaw = MathF.PI - p.Yaw; CamPitch = -0.12f; CamDist = 1.6f;
            _vy = 0; _grounded = true;
            CamPos = p.Pos + new Vector3(0, p.EyeHeight, 0);
        }
        else
        {
            // 고른 인형 하나만 방 가운데에서 -Z(도시 창 쪽)를 보며 시작
            p.Pos = Vector3.Zero;
            p.Yaw = MathF.PI;
            CamYaw = 0; CamPitch = -0.10f;
        }
        UpdateStatus();
    }

    /// <summary>
    /// 세계 안 걷기: 중력, 그리고 몸을 세 개의 구(발목·허리·가슴 높이, 반지름 0.25 m)로 보고 거리장에서 밀어낸다.
    /// 지형과 집·가구가 같은 곡면이라, 턱과 경사는 아래 구가 곡면을 타고 올라서며 넘는다 (블록 단위로 뛰어오르지 않는다).
    /// </summary>
    void WalkPhysics(Actor p, float dt)
    {
        const float r = 0.25f;
        float[] heights = [0.3f, 0.95f, 1.5f];
        _vy -= 9.81f * dt;
        p.Pos += new Vector3(0, _vy * dt, 0);
        var grounded = false;
        for (var it = 0; it < 4; it++)
            foreach (var h in heights)
            {
                var c = p.Pos + new Vector3(0, h, 0);
                var d = Scene!.Sdf(c) - r;
                if (d >= 0) continue;
                var n = Scene.Gradient(c);
                if (h == heights[0] && n.Y > 0.55f) { grounded = true; n = new Vector3(0, 1, 0) * n.Y + new Vector3(n.X, 0, n.Z) * 0.2f; }
                p.Pos -= n * d;
            }
        // 내리막에서 땅에 붙어 걷기
        if (!grounded && _grounded && _vy <= 0)
        {
            var c = p.Pos + new Vector3(0, heights[0], 0);
            var gap = Scene!.Sdf(c) - r;
            if (gap < 0.2f) { p.Pos -= new Vector3(0, gap, 0); grounded = true; }
        }
        if (grounded) _vy = MathF.Max(_vy, 0) * 0;
        _grounded = grounded;
        if (p.Pos.Y < -50) { p.Pos = Scene!.Spawn; _vy = 0; }
    }

    void Press(string code)
    {
        switch (code)
        {
            case "Digit1": Select(0); break;
            case "Digit2": Select(1); break;
            case "KeyC": ShowSelection(); break;
            case "KeyE" when InWorld: Gestures.Enqueue(Gesture.Pickup); break;
            case "KeyQ" when InWorld: Gestures.Enqueue(Gesture.Drop); break;
            case "KeyF" when InWorld: Gestures.Enqueue(Gesture.Join); break;
            case "KeyX" when InWorld: Gestures.Enqueue(Gesture.Split); break;
            case "KeyR" when InWorld: HeldTurn = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2) * HeldTurn); break;
            case "KeyT" when InWorld: HeldTurn = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2) * HeldTurn); break;
            case "KeyV" when Selected >= 0:
                CamView = CamView switch
                {
                    ViewMode.FirstPerson => ViewMode.ThirdBack,
                    ViewMode.ThirdBack   => ViewMode.ThirdFront,
                    _                    => ViewMode.FirstPerson,
                };
                UpdateStatus(); break;
        }
    }

    /// <summary>선택 화면으로: 두 인형을 원위치에 세우고 정면에서 본다</summary>
    public void ShowSelection()
    {
        Selected = -1; CamYaw = 0; CamPitch = -0.12f;
        for (var k = 0; k < Actors.Length; k++) { Actors[k].Pos = Home[k]; Actors[k].Yaw = 0; }
        _keys.Clear();
        UpdateStatus();
    }

    void UpdateStatus()
    {
        var view = CamView switch
        {
            ViewMode.FirstPerson => "1인칭",
            ViewMode.ThirdBack   => "3인칭 뒤",
            _                    => "3인칭 앞",
        };
        Status = Selected < 0
            ? """{"state":"select"}"""
            : $$"""{"state":"play","who":"{{Actors[Selected].Name}}","view":"{{view}}","held":{{System.Text.Json.JsonSerializer.Serialize(Held)}},"note":{{System.Text.Json.JsonSerializer.Serialize(Note)}},"world":{{(InWorld ? "true" : "false")}}}""";
    }

    public void Update(float dt, BrowserStream stream)
    {
        lock (stream.InputQueue)
        {
            while (stream.InputQueue.Count > 0)
            {
                var ev = stream.InputQueue.Dequeue();
                switch (ev.Type)
                {
                    case "keydown":
                        if (_keys.Add(ev.Key)) Press(ev.Key);
                        break;
                    case "keyup":     _keys.Remove(ev.Key); break;
                    case "select":    Select(ev.Button); break;
                    case "mousedown" when InWorld: Gestures.Enqueue(ev.Button == 2 ? Gesture.Pour : ev.Button == 0 ? Gesture.Dig : Gesture.None); break;
                    case "hello":     ShowSelection(); break;
                    case "wheel":     CamDist = M.Clamp(CamDist * MathF.Exp(ev.Dy * 0.001f), 1.0f, 6.0f); break;
                    case "mousemove":
                        CamYaw += ev.Dx * 0.003f;
                        CamPitch = M.Clamp(CamPitch - ev.Dy * 0.003f, -1.4f, 1.4f);
                        break;
                }
            }
        }

        var lo = EnvRenderer.RoomMin; var hi = EnvRenderer.RoomMax;
        var player = Player;

        if (player is null)
        {
            // 선택 화면: 두 인형을 마우스 좌우로 돌려 보고(턴테이블), 휠로 거리 조절
            for (var k = 0; k < Actors.Length; k++) { Actors[k].Speed = 0; Actors[k].Body.Showcase(ShowcaseStep[k].Phase, ShowcaseStep[k].Stride); }
            var mid = (Actors[0].Pos + Actors[1].Pos) * 0.5f;
            var dist = 3.2f * CamDist / 2.6f;
            var target = mid + new Vector3(MathF.Sin(CamYaw) * dist, 1.15f, MathF.Cos(CamYaw) * dist);
            CamPos  = Vector3.Lerp(CamPos, Vector3.Clamp(target, lo + new Vector3(0.3f), hi - new Vector3(0.3f)), dt * 5f);
            LookDir = Vector3.Normalize(mid + new Vector3(0, 0.95f, 0) - CamPos);
            return;
        }

        // 몸은 마우스가 가리키는 수평 방향을 본다 (Actor.Yaw 0 = +Z, 시점 yaw 0 = -Z)
        var fwd   = new Vector3(MathF.Sin(CamYaw), 0, -MathF.Cos(CamYaw));
        var right = new Vector3(MathF.Cos(CamYaw), 0,  MathF.Sin(CamYaw));
        player.Yaw = MathF.Atan2(fwd.X, fwd.Z);

        const float walkSpeed = 1.6f;
        var wish = Vector3.Zero;
        if (IsDown("KeyW") || IsDown("ArrowUp"))    wish += fwd;
        if (IsDown("KeyS") || IsDown("ArrowDown"))  wish -= fwd;
        if (IsDown("KeyD") || IsDown("ArrowRight")) wish += right;
        if (IsDown("KeyA") || IsDown("ArrowLeft"))  wish -= right;
        if (wish.LengthSquared() > 0) wish = Vector3.Normalize(wish);
        var run = IsDown("ShiftLeft") || IsDown("ShiftRight") ? 1.8f : 1f;
        player.Speed = wish.LengthSquared() > 0 ? walkSpeed * run : 0f;
        player.Pos  += wish * player.Speed * dt;

        if (Scene is not null) WalkPhysics(player, dt);
        else
            // 방 경계 + 다른 인형과 겹치지 않기
            player.Pos = new Vector3(M.Clamp(player.Pos.X, lo.X + 0.4f, hi.X - 0.4f), 0, M.Clamp(player.Pos.Z, lo.Z + 0.4f, hi.Z - 0.4f));

        player.Body.Animate(dt, player.Speed);

        // 카메라
        var aim   = Aim;
        var eye   = player.Pos + new Vector3(0, player.EyeHeight, 0) + player.Facing * 0.075f;
        var pivot = player.Pos + new Vector3(0, player.EyeHeight - 0.15f, 0);
        Vector3 cam;
        switch (CamView)
        {
            case ViewMode.FirstPerson:
                PlayerHidden = false;
                cam = eye; LookDir = aim;
                CamPos = cam;   // 1인칭은 눈에 붙어 있다 (지연 없음)
                return;
            case ViewMode.ThirdBack:
                cam = pivot - aim * CamDist; LookDir = aim; break;
            default:
                // 앞에서 얼굴을 본다: 시선을 뒤집고 위아래도 거울처럼
                var front = new Vector3(aim.X, -aim.Y, aim.Z);
                cam = pivot + front * CamDist; LookDir = Vector3.Normalize(pivot - cam); break;
        }
        if (Scene is not null)
        {
            // 카메라가 벽·땅을 뚫지 않게: 머리 쪽에서 카메라 쪽으로 쏜 광선이 닿는 곳 앞에 멈춘다
            var toCam = cam - pivot; var len = toCam.Length();
            if (len > 1e-3f)
            {
                var avail = Scene.Raycast(pivot, toCam / len, len, 0.12f) - 0.05f;
                cam = pivot + toCam / len * MathF.Max(0.15f, avail);
                // 좁은 곳(벽·문틀 바로 앞)에서 카메라가 머리에 붙으면 몸을 감추고 그 자리에서 본다: 머리 속이 보이지 않게
                PlayerHidden = avail < 0.6f;
            }
        }
        else cam = Vector3.Clamp(cam, lo + new Vector3(0.25f, 0.15f, 0.25f), hi - new Vector3(0.25f));
        CamPos = Vector3.Lerp(CamPos, cam, 1 - MathF.Exp(-dt * 14f));
        if (CamView == ViewMode.ThirdFront) LookDir = Vector3.Normalize(pivot - CamPos);
    }
}
