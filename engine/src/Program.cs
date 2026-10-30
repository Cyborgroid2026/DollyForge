using DollyForge;
using System.Numerics;

// ── 레퍼런스 체형 피팅 (창 없이): dolly-engine --fit female|male|both --out engine/assets/body
if (args.Length > 0 && args[0] == "--fit") { CyborgBody.FitCli(args); return; }

// ── 인형 빌드 ─────────────────────────────────────────────────────────────
Console.WriteLine("[engine] 캐릭터 메시 빌드 중…");
var female = CyborgBody.Build(0);
var male   = CyborgBody.Build(1);
Console.WriteLine($"[engine] 여성형 {female.Mesh.Vertices.Length:N0}  남성형 {male.Mesh.Vertices.Length:N0}");

// ── GL 컨텍스트 + 스트리밍 ────────────────────────────────────────────────
var ctx    = new GlContext(1600, 900);
var stream = new BrowserStream();
var world  = new World(female, male);

Shader?        bodyShader = null;
EnvRenderer?   env        = null;
FloorRenderer? floor      = null;
GpuMesh?       femaleGpu  = null;
GpuMesh?       maleGpu    = null;
var            lastStatus = "";

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
        floor      = new FloorRenderer(gl);
        Console.WriteLine("[engine] GL 로드 완료. N 키: 스튜디오/파노라마 전환");
    }
    catch (Exception ex) { Console.Error.WriteLine($"[engine] GL 로드 실패: {ex.Message}"); }
};

ctx.Window.Initialize();
stream.Start();

// 하이라이트용 키 조명 3개 (크롬의 반짝임은 주로 환경 반사가 만든다)
var lights = new (Vector3 Dir, Vector3 Color)[]
{
    (Vector3.Normalize(new(0.4f,  1.0f,  0.8f)), new(1.0f, 1.0f, 1.08f)), // 앞 위 (소프트박스 쪽, 레퍼런스처럼 정면은 은은하게)
    (Vector3.Normalize(new(-0.9f, 0.5f,  0.2f)), new(0.7f, 0.8f, 1.0f)),  // 왼쪽 (차가운 파랑)
    (Vector3.Normalize(new( 0.6f, 0.3f, -1.0f)), new(0.9f, 0.9f, 1.0f)),  // 뒤 림
};

ctx.Window.Run(() =>
{
    var now = DateTime.UtcNow;
    var dt  = (float)(now - last).TotalSeconds;
    last    = now;
    dt      = Math.Clamp(dt, 0f, 0.1f);

    world.Update(dt, stream);
    if (world.Status != lastStatus) stream.SetStatus(lastStatus = world.Status);

    // N 키 엣지 감지 → 환경 전환
    bool nNow = world.IsDown("KeyN");
    if (nNow && !nWas) env?.NextPanorama();
    nWas = nNow;

    if (bodyShader is null || femaleGpu is null || maleGpu is null || env is null || floor is null) return;

    ctx.BeginFrame();

    var aspect = (float)ctx.Width / ctx.Height;
    var fovTan = MathF.Tan(World.FovY * 0.5f);

    var fwd   = world.LookDir;
    var right = Vector3.Normalize(Vector3.Cross(fwd, Vector3.UnitY));
    var up    = Vector3.Cross(right, fwd);

    // 1. 배경
    env.Draw(world.CamPos, fwd, right, up, fovTan, aspect, ctx.Height);

    var vp       = world.View() * world.Proj(aspect);
    var mirror   = Matrix4x4.CreateScale(1, -1, 1);
    var mirCam   = world.CamPos with { Y = -world.CamPos.Y };
    var studio   = env.Mode == 0;
    var gpus     = new[] { femaleGpu, maleGpu };

    // reflection = 바닥에 비친 상: 1인칭이어도 내 몸이 바닥에 비친다
    void DrawBodies(Matrix4x4 extra, Vector3 cam, bool reflection)
    {
        bodyShader.Use();
        env.Bind(bodyShader);
        bodyShader.Set("u_cam_pos",   cam);
        bodyShader.Set("u_cam_back",  -fwd);
        bodyShader.Set("u_ambient",   new Vector3(0.50f, 0.52f, 0.58f));
        bodyShader.Set("u_roughness", 0.20f);   // 새틴 크롬: 하이라이트가 점이 아니라 넓게 번진다
        bodyShader.Set("u_l0_dir", lights[0].Dir); bodyShader.Set("u_l0_col", lights[0].Color);
        bodyShader.Set("u_l1_dir", lights[1].Dir); bodyShader.Set("u_l1_col", lights[1].Color);
        bodyShader.Set("u_l2_dir", lights[2].Dir); bodyShader.Set("u_l2_col", lights[2].Color);
        for (var i = 0; i < world.Actors.Length; i++)
        {
            var a = world.Actors[i];
            if (!world.IsVisible(a)) continue;
            if (!reflection && a == world.Player && world.CamView == World.ViewMode.FirstPerson) continue;   // 눈이 머리 안에 있다
            var model = a.Model;
            bodyShader.Set("u_model", model);
            bodyShader.Set("u_mvp",   model * extra * vp);
            a.Body.GetSkinMatrices(skinBuf.AsSpan());
            bodyShader.SetArray("u_bones", skinBuf);
            gpus[i].Draw();
        }
    }

    // 2. 광택 바닥: 반사층 → 뒤집힌 인형 → 바닥 표면
    if (studio)
    {
        floor.DrawReflection(env, vp, world.CamPos);
        DrawBodies(mirror, mirCam, true);
        floor.DrawSurface(env, vp, world.CamPos);
    }

    // 3. 인형
    DrawBodies(Matrix4x4.Identity, world.CamPos, false);

    stream.PushFrame(ctx.ReadPixels(), ctx.Width, ctx.Height);
});

ctx.Dispose();
floor?.Dispose();
env?.Dispose();
maleGpu?.Dispose();
femaleGpu?.Dispose();
stream.Dispose();
