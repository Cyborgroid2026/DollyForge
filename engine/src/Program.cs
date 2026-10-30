using DollyForge;
using System.Numerics;

// ── 인형 빌드 (GL 창 없이) ────────────────────────────────────────────
Console.WriteLine("[engine] 캐릭터 메시 빌드 중…");
var female = CyborgBody.Build(0);
var male   = CyborgBody.Build(1);
Console.WriteLine($"[engine] 여성형 정점 {female.Mesh.Vertices.Length:N0}  남성형 정점 {male.Mesh.Vertices.Length:N0}");

// ── GL 컨텍스트 + 스트리밍 ──────────────────────────────────────────────
var ctx    = new GlContext(1600, 900);
var stream = new BrowserStream();
var world  = new World();
world.Body = female;

Shader?  bodyShader = null;
GpuMesh? femaleGpu  = null;
GpuMesh? maleGpu    = null;
var skinBuf = new Matrix4x4[BodyMesh.BoneCount];
var last    = DateTime.UtcNow;

ctx.Window.Load += () =>
{
    var gl = ctx.Gl;
    try
    {
        var renderer = gl.GetStringS(Silk.NET.OpenGL.GLEnum.Renderer);
        var version  = gl.GetStringS(Silk.NET.OpenGL.GLEnum.Version);
        Console.WriteLine($"[engine] GL 렌더러: {renderer}");
        Console.WriteLine($"[engine] GL 버전:   {version}");
        bodyShader = new Shader(gl, Shader.BodyVert, Shader.BodyFrag);
        femaleGpu  = new GpuMesh(gl, female.Mesh);
        maleGpu    = new GpuMesh(gl, male.Mesh);
        Console.WriteLine("[engine] GL 로드 완료");
    }
    catch (Exception ex) { Console.Error.WriteLine($"[engine] GL 로드 실패: {ex.Message}"); }
};

// ── Silk.NET 2.21: Run(Action onFrame) ────────────────────────────────
ctx.Window.Initialize();
stream.Start();

ctx.Window.Run(() =>
{
    // Update
    var now = DateTime.UtcNow;
    var dt  = (float)(now - last).TotalSeconds;
    last    = now;
    dt      = Math.Clamp(dt, 0f, 0.1f);
    world.Update(dt, stream);

    // Render
    if (bodyShader is null || femaleGpu is null) return;
    ctx.BeginFrame();
    var aspect = (float)ctx.Width / ctx.Height;
    var view   = world.View();
    var proj   = world.Proj(aspect);
    var model  = world.BodyModel();
    var mvp    = model * view * proj;

    bodyShader.Use();
    bodyShader.Set("u_model",       model);
    bodyShader.Set("u_mvp",         mvp);
    bodyShader.Set("u_cam_pos",     world.CamPos);
    bodyShader.Set("u_light_dir",   Vector3.Normalize(new Vector3(0.6f, 1.0f, 0.4f)));
    bodyShader.Set("u_light_color", new Vector3(2.4f, 2.2f, 1.9f));
    bodyShader.Set("u_ambient",     new Vector3(0.08f, 0.09f, 0.12f));
    bodyShader.Set("u_metallic",    0.68f);
    bodyShader.Set("u_roughness",   0.58f);

    world.Body!.GetSkinMatrices(skinBuf.AsSpan());
    bodyShader.SetArray("u_bones", skinBuf);
    femaleGpu.Draw();

    var rgba = ctx.ReadPixels();
    stream.PushFrame(rgba, ctx.Width, ctx.Height);
});

ctx.Dispose();
stream.Dispose();
