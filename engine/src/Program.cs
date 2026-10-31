using DollyForge;
using System.Numerics;

// ── 레퍼런스 체형 피팅 (창 없이): dolly-engine --fit female|male|both --out engine/assets/body
if (args.Length > 0 && args[0] == "--fit") { CyborgBody.FitCli(args); return; }
// ── 레퍼런스와 형태 비교 (개발): dolly-engine --refnormals 출력폴더 [--nodetail]
if (args.Length > 0 && args[0] == "--refnormals") { CyborgBody.RefNormalsCli(args); return; }

// ── 인형 빌드 ─────────────────────────────────────────────────────────────
Console.WriteLine("[engine] 캐릭터 메시 빌드 중…");
var female = CyborgBody.Build(0);
var male   = CyborgBody.Build(1);
Console.WriteLine($"[engine] 여성형 {female.Mesh.Vertices.Length:N0}  남성형 {male.Mesh.Vertices.Length:N0}");
// 레퍼런스 그림의 근육 조각(Marigold 법선 디테일)을 두 인형 법선에 입힌다
for (var pass = 0; pass < CyborgBody.SilPasses; pass++) CyborgBody.FitReferenceSilhouette(female, male);   // 윤곽을 그림 윤곽에 (정점 이동)
CyborgBody.ApplyReferenceDetail(female, male);

// ── 세계 (0장 재설계 M1): 콘텐츠 팩의 집 설계도로 평범한 방과 동네, 곡면 지형 ───────────────
WorldScene? scene = null;
try { scene = new WorldScene(ContentPack.Load()); }
catch (Exception ex) { Console.Error.WriteLine($"[world] 세계를 짓지 못해 스튜디오에서 걷습니다: {ex.Message}"); }

// ── GL 컨텍스트 + 스트리밍 ────────────────────────────────────────────────
var ctx    = new GlContext(1600, 900);
var stream = new BrowserStream();
var world  = new World(female, male) { Scene = scene };
// 세계 서버 (Rust, world/): 파기·붓기·물건 물리·존중 규칙의 권한
var link   = scene is not null ? new WorldLink(scene) : null;
WorldRenderer? worldGfx = null;

Shader?        bodyShader = null;
EnvRenderer?   env        = null;
FloorRenderer? floor      = null;
PostFx?        post       = null;
GpuMesh?       femaleGpu  = null;
GpuMesh?       maleGpu    = null;
var            lastStatus = "";

var skinBuf  = new Matrix4x4[BodyMesh.BoneCount];
var last     = DateTime.UtcNow;
var started  = last;
var assetDir = Path.Combine(AppContext.BaseDirectory, "assets", "panoramas");
// 측정용 (개발): DOLLY_MASK=1 이면 배경·바닥·후처리 없이 몸만 흰색으로 그린다 → 레퍼런스와 실루엣 비교
// DOLLY_MASK=2: 몸을 카메라 공간 법선 색으로 (레퍼런스 Marigold 법선 지도와 형태 비교)
var maskKind = int.TryParse(Environment.GetEnvironmentVariable("DOLLY_MASK"), out var mk) ? mk : 0;
var maskMode = maskKind > 0;
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
        post       = new PostFx(gl, ctx.Width, ctx.Height);
        if (scene is not null) worldGfx = new WorldRenderer(gl, scene);
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
    if (link is not null && scene is not null)
    {
        link.Pump();
        HandleGestures(link, scene);
        var heldName = link.Held is { } hid && scene.Pack.Items.TryGetValue(hid, out var hit) ? hit.Name : link.Held ?? "";
        world.SetHud(heldName, link.Note);
    }
    if (world.Status != lastStatus) stream.SetStatus(lastStatus = world.Status);
    if (world.Player is { } pl) stream.Debug = FormattableString.Invariant($"{{\"x\":{pl.Pos.X:F3},\"y\":{pl.Pos.Y:F3},\"z\":{pl.Pos.Z:F3},\"camYaw\":{world.CamYaw:F4},\"camPitch\":{world.CamPitch:F4},\"held\":{System.Text.Json.JsonSerializer.Serialize(world.Held)},\"note\":{System.Text.Json.JsonSerializer.Serialize(world.Note)},\"bodies\":{link?.Bodies.Count ?? 0},\"edits\":{scene?.Terrain.EditCount ?? 0}}}");

    // N 키 엣지 감지 → 환경 전환
    bool nNow = world.IsDown("KeyN");
    if (nNow && !nWas) env?.NextPanorama();
    nWas = nNow;

    if (bodyShader is null || femaleGpu is null || maleGpu is null || env is null || floor is null || post is null) return;

    ctx.BeginFrame();
    env.Outdoor = world.InWorld;   // 캐릭터를 고르면 세계(바깥 하늘), 선택 화면은 스튜디오
    post.BloomGain = world.InWorld ? 0.12f : 0.35f;
    // 눈 적응: 바깥 햇빛은 노출을 줄이고, 집 안에 들어오면 1 초쯤에 걸쳐 연다
    var exposureGoal = !world.InWorld ? 1f : scene!.Indoors(world.CamPos) ? 0.9f : 0.55f;
    env.Exposure += (exposureGoal - env.Exposure) * (world.InWorld ? 1 - MathF.Exp(-dt * 3f) : 1);

    var aspect = (float)ctx.Width / ctx.Height;
    var fovTan = MathF.Tan(World.FovY * 0.5f);

    var fwd   = world.LookDir;
    var right = Vector3.Normalize(Vector3.Cross(fwd, Vector3.UnitY));
    var up    = Vector3.Cross(right, fwd);

    // 1. 배경
    if (!maskMode) env.Draw(world.CamPos, fwd, right, up, fovTan, aspect, ctx.RenderHeight);

    var vp       = world.View() * world.Proj(aspect);
    var mirror   = Matrix4x4.CreateScale(1, -1, 1);
    var mirCam   = world.CamPos with { Y = -world.CamPos.Y };
    var studio   = env.Mode == 0;
    var gpus     = new[] { femaleGpu, maleGpu };

    // 1-b. 세계: 지형 · 집 · 가구 · 유리
    if (world.InWorld && worldGfx is not null && !maskMode) worldGfx.Draw(env, vp, world.CamPos, (float)(now - started).TotalSeconds);
    // 1-c. 물건 (서버 Rapier 강체) + 손에 든 것
    if (world.InWorld && worldGfx is not null && link is not null && !maskMode)
    {
        var list = link.Bodies.Values.Select(b => (b.Half, b.R, b.Item, Matrix4x4.CreateFromQuaternion(b.Q) * Matrix4x4.CreateTranslation(b.P))).ToList();
        if (link.Held is { } held && world.Player is { } holder && scene!.Pack.Items.TryGetValue(held, out var hi))
        {
            var (hh, hr) = BodyShape(hi);
            var at = holder.Pos + holder.Facing * 0.36f + new Vector3(0, 1.0f, 0);
            list.Add((hh, hr, held, Matrix4x4.CreateRotationY(holder.Yaw + MathF.PI / 2) * Matrix4x4.CreateTranslation(at)));
        }
        worldGfx.DrawBodies(list);
    }
    // 세계에서는 해가 키 조명, 하늘이 채움빛
    var keyLights = world.InWorld
        ? new (Vector3 Dir, Vector3 Color)[] { (WorldScene.SunDir, new(2.4f, 2.2f, 1.9f)), (Vector3.Normalize(new(-0.3f, 1, 0.4f)), new(0.35f, 0.42f, 0.6f)), (Vector3.Normalize(new(-0.5f, 0.2f, 0.8f)), new(0.2f, 0.2f, 0.22f)) }
        : lights;

    // reflection = 바닥에 비친 상: 1인칭이어도 내 몸이 바닥에 비친다
    void DrawBodies(Matrix4x4 extra, Vector3 cam, bool reflection)
    {
        bodyShader.Use();
        env.Bind(bodyShader);
        bodyShader.Set("u_cam_pos",   cam);
        bodyShader.Set("u_cam_back",  -fwd);
        bodyShader.Set("u_ambient",   new Vector3(0.50f, 0.52f, 0.58f));
        bodyShader.Set("u_roughness", 0.20f);   // 새틴 크롬: 하이라이트가 점이 아니라 넓게 번진다
        bodyShader.Set("u_mask", maskKind);
        bodyShader.Set("u_matcap_gain", 0.42f);
        bodyShader.Set("u_matcap_mix", 0.5f);   // 일러스트 스튜디오 톤 50 % + AI 크롬 구 매트캡 50 %
        bodyShader.Set("u_curv_gain", 0.1f);
        bodyShader.Set("u_l0_dir", keyLights[0].Dir); bodyShader.Set("u_l0_col", keyLights[0].Color);
        bodyShader.Set("u_l1_dir", keyLights[1].Dir); bodyShader.Set("u_l1_col", keyLights[1].Color);
        bodyShader.Set("u_l2_dir", keyLights[2].Dir); bodyShader.Set("u_l2_col", keyLights[2].Color);
        for (var i = 0; i < world.Actors.Length; i++)
        {
            var a = world.Actors[i];
            if (!world.IsVisible(a)) continue;
            if (!reflection && a == world.Player && (world.CamView == World.ViewMode.FirstPerson || world.PlayerHidden)) continue;   // 눈이 머리 안에 있다 (좁은 곳의 3인칭도)
            var model = a.Model;
            bodyShader.Set("u_model", model);
            bodyShader.Set("u_mvp",   model * extra * vp);
            a.Body.GetSkinMatrices(skinBuf.AsSpan());
            bodyShader.SetArray("u_bones", skinBuf);
            gpus[i].Draw();
        }
    }

    // 2. 광택 바닥: 반사층 → 뒤집힌 인형 → 바닥 표면
    if (studio && !maskMode)
    {
        // 선택 화면: 레퍼런스 그림처럼 두 인형 발밑에 빛나는 별
        floor.Stars = world.Selected < 0 ? world.Actors.Select(a => a.Pos).ToArray() : [];
        floor.DrawReflection(env, vp, world.CamPos);
        DrawBodies(mirror, mirCam, true);
        floor.DrawSurface(env, vp, world.CamPos);
    }

    // 3. 인형
    DrawBodies(Matrix4x4.Identity, world.CamPos, false);

    // 4. 후처리 (블룸·색보정·비네트): 레퍼런스 일러스트의 공기감
    // (측정용 마스크 모드는 효과 없이 축소만)
    post.Apply(ctx.ColorTex, ctx.Width, ctx.Height, plain: maskMode);
    stream.PushFrame(ctx.ReadPixels(post.OutputFbo), ctx.Width, ctx.Height);
});

ctx.Dispose();
link?.Dispose();
floor?.Dispose();
worldGfx?.Dispose();
post?.Dispose();
env?.Dispose();
maleGpu?.Dispose();
femaleGpu?.Dispose();
stream.Dispose();

/// <summary>아이템 치수 → 서버와 같은 둥근 상자 (가장 얇은 치수가 높이: 바닥에 눕는다)</summary>
static (Vector3 Half, float R) BodyShape(ContentItem it)
{
    var d = it.Mm.Select(x => x / 1000f).Concat(Enumerable.Repeat(0.1f, 3)).Take(3).OrderByDescending(x => x).ToArray();
    var half = new Vector3(d[0], d[2], d[1]) * 0.5f;
    return (half, MathF.Min(it.Edge / 1000f, half.Y * 0.9f));
}

/// <summary>손짓 → 세계 서버 요청. 겨냥은 화면 가운데(카메라 시선)가 닿는 곳, 손이 닿는지는 서버가 다시 판정한다</summary>
void HandleGestures(WorldLink link, WorldScene scene)
{
    while (world.Gestures.Count > 0)
    {
        var g = world.Gestures.Dequeue();
        var player = world.Player;
        if (player is null) continue;
        switch (g)
        {
            case World.Gesture.Dig or World.Gesture.Pour:
            {
                var t = scene.Raycast(world.CamPos, world.LookDir, 10);
                var p = world.CamPos + world.LookDir * t;
                if (t >= 10 || Vector3.Distance(p, world.EyePos) > scene.Pack.Layout.EditReach + 0.3f) { world.SetHud(world.Held, "✕ 손이 닿는 곳(3 m 안)의 땅을 겨냥한다"); break; }
                if (g == World.Gesture.Dig) link.Dig(p, scene.Gradient(p), world.EyePos);
                else link.Pour(p, world.EyePos);
                break;
            }
            case World.Gesture.Pickup: link.Pickup(player.Pos + new Vector3(0, 0.3f, 0)); break;
            case World.Gesture.Drop: link.Drop(player.Pos + player.Facing * 0.6f + new Vector3(0, 1.0f, 0), player.Facing * 1.5f + new Vector3(0, 0.6f, 0)); break;
        }
    }
}
