using System.Numerics;

namespace DollyForge;

/// <summary>카메라 + 씬 상태</summary>
sealed class World
{
    // 카메라
    public Vector3 CamPos   = new(0, 1.1f, 2.8f);
    public float   CamYaw   = 0f;
    public float   CamPitch = -0.08f;

    // 인형
    public CyborgBody? Body;
    public Vector3     BodyPos  = Vector3.Zero;
    public float       BodyYaw  = 0f;
    public float       Speed    = 0f;

    // 입력 상태
    readonly HashSet<string> _keys = [];

    public void KeyDown(string code) => _keys.Add(code);
    public void KeyUp(string code)   => _keys.Remove(code);
    public bool IsDown(string code)  => _keys.Contains(code);

    public Matrix4x4 View()
    {
        var dir = new Vector3(
            MathF.Sin(CamYaw) * MathF.Cos(CamPitch),
            MathF.Sin(CamPitch),
            -MathF.Cos(CamYaw) * MathF.Cos(CamPitch));
        return Matrix4x4.CreateLookAt(CamPos, CamPos + dir, Vector3.UnitY);
    }

    public Matrix4x4 Proj(float aspect, float fovY = 1.05f)
        => Matrix4x4.CreatePerspectiveFieldOfView(fovY, aspect, 0.05f, 200f);

    public Matrix4x4 BodyModel()
        => Matrix4x4.CreateRotationY(BodyYaw) * Matrix4x4.CreateTranslation(BodyPos);

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
                    case "mousemove": CamYaw += ev.Dx * 0.003f; CamPitch = M.Clamp(CamPitch - ev.Dy * 0.003f, -1.4f, 1.4f); break;
                }
            }
        }

        // WASD 이동
        const float walkSpeed = 2.4f;
        var wishDir = Vector3.Zero;
        if (IsDown("KeyW") || IsDown("ArrowUp"))    wishDir += new Vector3( MathF.Sin(CamYaw), 0, -MathF.Cos(CamYaw));
        if (IsDown("KeyS") || IsDown("ArrowDown"))  wishDir += new Vector3(-MathF.Sin(CamYaw), 0,  MathF.Cos(CamYaw));
        if (IsDown("KeyA") || IsDown("ArrowLeft"))  wishDir += new Vector3(-MathF.Cos(CamYaw), 0, -MathF.Sin(CamYaw));
        if (IsDown("KeyD") || IsDown("ArrowRight")) wishDir += new Vector3( MathF.Cos(CamYaw), 0,  MathF.Sin(CamYaw));
        if (wishDir.LengthSquared() > 0) { wishDir = Vector3.Normalize(wishDir); BodyYaw = MathF.Atan2(wishDir.X, -wishDir.Z); }

        Speed = wishDir.LengthSquared() > 0 ? walkSpeed : 0f;
        BodyPos += wishDir * walkSpeed * dt;

        // 카메라: 캐릭터 뒤를 따라간다
        var follow = BodyPos + new Vector3(MathF.Sin(CamYaw), 0, -MathF.Cos(CamYaw)) * -2.5f + new Vector3(0, 1.2f, 0);
        CamPos = Vector3.Lerp(CamPos, follow, dt * 5f);

        Body?.Animate(dt, Speed);
    }
}
