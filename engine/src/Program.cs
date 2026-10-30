using DollyForge;
using System.Numerics;

// ── 인형 빌드 ─────────────────────────────────────────────────────────────
Console.WriteLine("[engine] 캐릭터 메시 빌드 중…");
var female = CyborgBody.Build(0);
var male   = CyborgBody.Build(1);
Console.WriteLine($"[engine] 여성형 {female.Mesh.Vertices.Length:N0}  남성형 {male.Mesh.Vertices.Length:N0}");

// ── GL 컨텍스트 + 스트리밍 ────────────────────────────────────────────────
var ctx    = new GlContext(1600, 900);
var stream = new BrowserStream();
var world  = new World();
world.Body = female;

Shader?      bodyShader = null;
EnvRenderer? env        = null;
GpuMesh?     femaleGpu  = null;
GpuMesh?     maleGpu    = null;

var skinBuf  = new Matrix4x4[BodyMesh.BoneCount];
var last     = DateTime.UtcNow;
var assetDir = Path.Combine(AppContext.BaseDirectory, "assets", "panoramas");
bool nWas    = false;

ctx.Window.Load += () =>
{
    var gl = ctx.Gl;
    try
    {
        Console.WriteLine($"[engine] GL: {gl.GetStringS(Silk.NET.OpenGL.GLEnum.Renderer)}");
        bodyShader = new Shader(gl, Shader.BodyVert, Shader.BodyFrag);
        femaleGpu  = new GpuMesh(gl, female.Mesh);
        maleGpu    = new GpuMesh(gl, male.Mesh);
        env        = new EnvRenderer(gl, assetDir);
        Console.WriteLine("[engine] GL 로드 완료. N 키: 파노라마 전환");
    }
    catch (Exception ex) { Console.Error.WriteLine($"[engine] GL 로드 실패: {ex.Message}"); }
};

ctx.Window.Initialize();
stream.Start();

// 키 조명 방향 3개 (크롬 반사용 다방향 조명)
var lights = new (Vector3 Dir, Vector3 Color)[]
{
    (Vector3.Normalize(new(0.5f,  1.0f,  0.6f)), new(2.8f, 2.5f, 2.2f)),  // 메인 (따뜻한 흰빛)
    (Vector3.Normalize(new(-0.8f, 0.4f,  0.5f)), new(0.6f, 0.7f, 1.0f)),  // 보조 (차가운 파랑)
    (Vector3.Normalize(new( 0.0f, 0.2f, -1.0f)), new(0.5f, 0.5f, 0.6f)),  // 림 (역광)
};

ctx.Window.Run(() =>
{
    var now = DateTime.UtcNow;
    var dt  = (float)(now - last).TotalSeconds;
    last    = now;
    dt      = Math.Clamp(dt, 0f, 0.1f);

    world.Update(dt, stream);

    // N 키 엣지 감지 → 파노라마 전환
    bool nNow = world.IsDown("KeyN");
    if (nNow && !nWas) env?.NextPanorama();
    nWas = nNow;

    if (bodyShader is null || femaleGpu is null || maleGpu is null || env is null) return;

    ctx.BeginFrame();

    var aspect = (float)ctx.Width / ctx.Height;
    const float fovY = MathF.PI / 4f;
    var fovTan = MathF.Tan(fovY * 0.5f);

    var fwd   = world.CamFwd();
    var right = Vector3.Normalize(Vector3.Cross(fwd, Vector3.UnitY));
    var up    = Vector3.Cross(right, fwd);

    // 1. 스카이박스
    env.Draw(fwd, right, up, fovTan, aspect);

    // 2. 공통 셰이더 파라미터
    var view = world.View();
    var proj = world.Proj(aspect);

    bodyShader.Use();
    bodyShader.Set("u_cam_pos",      world.CamPos);
    bodyShader.Set("u_ambient",      new Vector3(0.22f, 0.20f, 0.25f));
    bodyShader.Set("u_metallic",     1.0f);
    bodyShader.Set("u_roughness",    0.07f);
    for (int i = 0; i < lights.Length; i++)
    {
        bodyShader.Set($"u_light_dir[{i}]",   lights[i].Dir);
        bodyShader.Set($"u_light_color[{i}]", lights[i].Color);
    }

    // 3. 여성형 (왼쪽 -0.45)
    var fPos   = world.BodyPos + new Vector3(-0.45f, 0, 0);
    var fModel = Matrix4x4.CreateRotationY(world.BodyYaw) * Matrix4x4.CreateTranslation(fPos);
    bodyShader.Set("u_model", fModel);
    bodyShader.Set("u_mvp",   fModel * view * proj);
    female.GetSkinMatrices(skinBuf.AsSpan());
    bodyShader.SetArray("u_bones", skinBuf);
    femaleGpu.Draw();

    // 4. 남성형 (오른쪽 +0.45)
    var mPos   = world.BodyPos + new Vector3( 0.45f, 0, 0);
    var mModel = Matrix4x4.CreateRotationY(world.BodyYaw) * Matrix4x4.CreateTranslation(mPos);
    bodyShader.Set("u_model", mModel);
    bodyShader.Set("u_mvp",   mModel * view * proj);
    male.GetSkinMatrices(skinBuf.AsSpan());
    bodyShader.SetArray("u_bones", skinBuf);
    maleGpu.Draw();

    stream.PushFrame(ctx.ReadPixels(), ctx.Width, ctx.Height);
});

ctx.Dispose();
env?.Dispose();
maleGpu?.Dispose();
femaleGpu?.Dispose();
stream.Dispose();
