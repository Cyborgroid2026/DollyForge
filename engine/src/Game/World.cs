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
        Actors = [new Actor("여성형", female, new Vector3(-0.45f, 0, 0)), new Actor("남성형", male, new Vector3(0.45f, 0, 0))];
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

    static readonly Vector3[] Home = [new(-0.45f, 0, 0), new(0.45f, 0, 0)];

    public void Select(int i)
    {
        if (i < 0 || i >= Actors.Length) return;
        Selected = i;
        // 고른 인형 하나만 방 가운데에서 -Z(도시 창 쪽)를 보며 시작
        var p = Actors[i];
        p.Pos = Vector3.Zero;
        p.Yaw = MathF.PI;
        CamYaw = 0; CamPitch = -0.10f;
        UpdateStatus();
    }

    void Press(string code)
    {
        switch (code)
        {
            case "Digit1": Select(0); break;
            case "Digit2": Select(1); break;
            case "KeyC": ShowSelection(); break;
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
            : $$"""{"state":"play","who":"{{Actors[Selected].Name}}","view":"{{view}}"}""";
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
            foreach (var a in Actors) { a.Speed = 0; a.Body.Animate(dt, 0); }
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
        cam = Vector3.Clamp(cam, lo + new Vector3(0.25f, 0.15f, 0.25f), hi - new Vector3(0.25f));
        CamPos = Vector3.Lerp(CamPos, cam, 1 - MathF.Exp(-dt * 14f));
        if (CamView == ViewMode.ThirdFront) LookDir = Vector3.Normalize(pivot - CamPos);
    }
}
