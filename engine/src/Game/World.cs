using System.Numerics;

namespace DollyForge;

/// <summary>카메라 + 씬 상태</summary>
sealed class World
{
    public const float FovY = 0.9f;   // 배경과 몸이 같은 화각을 써야 방 시차가 맞는다

    // 카메라 (남녀 두 인형을 함께 본다)
    public Vector3 CamPos   = new(0, 1.15f, 3.2f);
    public float   CamYaw   = 0f;
    public float   CamPitch = -0.12f;
    public float   CamDist  = 3.2f;   // 휠로 1.0 ~ 6.0 m

    // 인형: Body(여성형)가 이동 기준, Partner(남성형)는 옆에서 같이 걷는다
    public CyborgBody? Body;
    public CyborgBody? Partner;
    public Vector3     BodyPos  = Vector3.Zero;
    public float       BodyYaw  = 0f;
    public float       Speed    = 0f;

    // 입력 상태
    readonly HashSet<string> _keys = [];

    public void KeyDown(string code) => _keys.Add(code);
    public void KeyUp(string code)   => _keys.Remove(code);
    public bool IsDown(string code)  => _keys.Contains(code);

    public Vector3 CamFwd() => new(
        MathF.Sin(CamYaw) * MathF.Cos(CamPitch),
        MathF.Sin(CamPitch),
        -MathF.Cos(CamYaw) * MathF.Cos(CamPitch));

    public Matrix4x4 View()
    {
        var dir = CamFwd();
        return Matrix4x4.CreateLookAt(CamPos, CamPos + dir, Vector3.UnitY);
    }

    public Matrix4x4 Proj(float aspect, float fovY = FovY)
        => Matrix4x4.CreatePerspectiveFieldOfView(fovY, aspect, 0.05f, 200f);

    public Matrix4x4 BodyModel()
        => Matrix4x4.CreateRotationY(BodyYaw) * Matrix4x4.CreateTranslation(BodyPos);

    // 인형 기준 옆 방향(+X 가 쉼 자세의 오른쪽 화면)으로 offset 만큼
    public Vector3 Side(float offset)
        => new(MathF.Cos(BodyYaw) * offset, 0, -MathF.Sin(BodyYaw) * offset);

    public void Update(float dt, BrowserStream stream)
    {
        // 입력 큐 드레인
        lock (stream.InputQueue)
        {
            while (stream.InputQueue.Count > 0)
            {
                var ev = stream.InputQueue.Dequeue();
                switch (ev.Type)
                {
                    case "keydown":   KeyDown(ev.Key); break;
                    case "keyup":     KeyUp(ev.Key);   break;
                    case "wheel":     CamDist = M.Clamp(CamDist * MathF.Exp(ev.Dy * 0.001f), 1.0f, 6.0f); break;
                    case "mousemove": CamYaw += ev.Dx * 0.003f; CamPitch = M.Clamp(CamPitch - ev.Dy * 0.003f, -1.4f, 1.4f); break;
                }
            }
        }

        // WASD 이동
        const float walkSpeed = 1.6f;
        var wishDir = Vector3.Zero;
        if (IsDown("KeyW") || IsDown("ArrowUp"))    wishDir += new Vector3( MathF.Sin(CamYaw), 0, -MathF.Cos(CamYaw));
        if (IsDown("KeyS") || IsDown("ArrowDown"))  wishDir += new Vector3(-MathF.Sin(CamYaw), 0,  MathF.Cos(CamYaw));
        if (IsDown("KeyA") || IsDown("ArrowLeft"))  wishDir += new Vector3(-MathF.Cos(CamYaw), 0, -MathF.Sin(CamYaw));
        if (IsDown("KeyD") || IsDown("ArrowRight")) wishDir += new Vector3( MathF.Cos(CamYaw), 0,  MathF.Sin(CamYaw));
        // 몸은 +Z 를 본다: RotationY(yaw) 가 +Z 를 (sin, 0, cos) 로 돌린다
        if (wishDir.LengthSquared() > 0) { wishDir = Vector3.Normalize(wishDir); BodyYaw = MathF.Atan2(wishDir.X, wishDir.Z); }

        Speed = wishDir.LengthSquared() > 0 ? walkSpeed : 0f;
        BodyPos += wishDir * walkSpeed * dt;

        // 스튜디오 방 안에 머문다
        var lo = EnvRenderer.RoomMin; var hi = EnvRenderer.RoomMax;
        BodyPos = new Vector3(M.Clamp(BodyPos.X, lo.X + 1f, hi.X - 1f), 0, M.Clamp(BodyPos.Z, lo.Z + 1f, hi.Z - 3.5f));

        // 카메라: 인형 뒤(처음엔 정면 앞)에서 따라간다
        var follow = BodyPos + new Vector3(MathF.Sin(CamYaw), 0, -MathF.Cos(CamYaw)) * -CamDist + new Vector3(0, 1.15f, 0);
        follow = Vector3.Clamp(follow, lo + new Vector3(0.3f), hi - new Vector3(0.3f));
        CamPos = Vector3.Lerp(CamPos, follow, dt * 5f);

        Body?.Animate(dt, Speed);
        Partner?.Animate(dt, Speed);
    }
}
