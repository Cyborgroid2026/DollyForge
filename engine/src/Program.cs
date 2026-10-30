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
        env        = new EnvRenderer(gl, assetDir);
        Console.WriteLine("[engine] GL 로드 완료. N 키: 파노라마 전환");
    }
    catch (Exception ex) { Console.Error.WriteLine($"[engine] GL 로드 실패: {ex.Message}"); }
};

ctx.Window.Initialize();
stream.Start();

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

    if (bodyShader is null || femaleGpu is null || env is null) return;

    ctx.BeginFrame();

    var aspect = (float)ctx.Width / ctx.Height;
    const float fovY = MathF.PI / 4f;
    var fovTan = MathF.Tan(fovY * 0.5f);

    // 카메라 방향 벡터 (스카이박스 등장방형 샘플링에 사용)
    var fwd   = world.CamFwd();
    var right = Vector3.Normalize(Vector3.Cross(fwd, Vector3.UnitY));
    var up    = Vector3.Cross(right, fwd);

    // 1. 스카이박스: 실사 거리 파노라마
    env.Draw(fwd, right, up, fovTan, aspect);

    // 2. 캐릭터
    var view  = world.View();
    var proj  = world.Proj(aspect);
    var model = world.BodyModel();
    var mvp   = model * view * proj;

    bodyShader.Use();
    bodyShader.Set("u_model",       model);
    bodyShader.Set("u_mvp",         mvp);
    bodyShader.Set("u_cam_pos",     world.CamPos);
    bodyShader.Set("u_light_dir",   Vector3.Normalize(new Vector3(0.6f, 1.0f, 0.4f)));
    bodyShader.Set("u_light_color", new Vector3(2.4f, 2.2f, 1.9f));
    bodyShader.Set("u_ambient",     new Vector3(0.18f, 0.16f, 0.20f));
    bodyShader.Set("u_metallic",    0.68f);
    bodyShader.Set("u_roughness",   0.58f);

    world.Body!.GetSkinMatrices(skinBuf.AsSpan());
    bodyShader.SetArray("u_bones", skinBuf);
    femaleGpu.Draw();

    stream.PushFrame(ctx.ReadPixels(), ctx.Width, ctx.Height);
});

ctx.Dispose();
env?.Dispose();
stream.Dispose();
