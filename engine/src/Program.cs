using DollyForge;
using System.Numerics;

// ── 레퍼런스 체형 피팅 (창 없이): dolly-engine --fit female|male|both --out engine/assets/body
if (args.Length > 0 && args[0] == "--fit") { CyborgBody.FitCli(args); return; }
// ── 레퍼런스와 형태 비교 (개발): dolly-engine --refnormals 출력폴더 [--nodetail]
if (args.Length > 0 && args[0] == "--refnormals") { CyborgBody.RefNormalsCli(args); return; }

// ── 부품 조립체 메시 검사 (개발): dolly-engine --parts 아이템[+아이템…] [결합] — 부품을 위로 쌓아 잇고 메시의 정점 수·색·법선을 본다
if (args.Length > 0 && args[0] == "--parts") { PartsCli(args); return; }

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
Quaternion? pickedTurn = null;                        // 주우려는 물건이 놓여 있던 자세
if (link is not null && scene is not null)
{
    scene.Bodies = () => link.Solids;                 // 걷기 물리·시선이 놓인 물건에도 닿는다
    // 주운 물건은 놓여 있던 자세(90° 단위)를 내가 보는 방향 기준으로 그대로 든다. 모르면 기본 자세 (판·봉은 눕힌다)
    link.HeldChanged += () =>
    {
        world.HeldTurn = link.Held is not { } h ? Quaternion.Identity
            : pickedTurn is { } q && world.Player is { } me ? Quaternion.Normalize(Quaternion.Conjugate(YawTurn(me)) * Parts.Snap90(q))
            : Parts.Rest(scene.Pack, h);
        pickedTurn = null;
    };
}
Placement? place = null;                              // 들고 있는 것을 놓을 자리 (미리보기·Q)
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
        place = world.Player is { } holder0 && link.Held is { } h0 ? FindPlacement(scene, h0, holder0) : null;
        HandleGestures(link, scene);
        var heldName = link.Held is null ? "" : link.HeldMass > 0 ? FormattableString.Invariant($"{link.HeldName} ({link.HeldMass:0.0} kg)") : link.HeldName;
        world.SetHud(heldName, link.Note);
    }
    if (world.Status != lastStatus) stream.SetStatus(lastStatus = world.Status);
    if (world.Player is { } pl) stream.Debug = FormattableString.Invariant($"{{\"x\":{pl.Pos.X:F3},\"y\":{pl.Pos.Y:F3},\"z\":{pl.Pos.Z:F3},\"camYaw\":{world.CamYaw:F4},\"camPitch\":{world.CamPitch:F4},\"held\":{System.Text.Json.JsonSerializer.Serialize(world.Held)},\"note\":{System.Text.Json.JsonSerializer.Serialize(world.Note)},\"view\":\"{world.CamView}\",\"bodies\":{link?.Bodies.Count ?? 0},\"aim\":{(link?.Raycast(world.CamPos, world.LookDir, 6) is { } ah ? ah.Id : -1)},\"place\":{(place is not { } pv ? "null" : FormattableString.Invariant($"[{pv.P.X:F3},{pv.P.Y:F3},{pv.P.Z:F3},{(pv.Valid ? 1 : 0)}]"))},\"things\":{ThingsJson()},\"edits\":{scene?.Terrain.EditCount ?? 0}}}");

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
    if (world.InWorld && worldGfx is not null && !maskMode)
    {
        worldGfx.SetOccluders(link is null ? [] : link.Bodies.Values
            .Select(b => ShapeBox(scene!, b.Shape, b.Model))
            .OrderBy(o => Vector3.DistanceSquared(o.C, world.CamPos)).Take(24).Select(o => (o.C, o.H)).ToList());
        worldGfx.Draw(env, vp, world.CamPos, (float)(now - started).TotalSeconds);
    }
    // 1-c. 물건 (서버 Rapier 강체 = 부품 조립체) + 손에 든 것 + 놓을 자리 미리보기
    if (world.InWorld && worldGfx is not null && link is not null && !maskMode)
    {
        var list = new List<(AssemblyDef, Matrix4x4, Vector3, Vector4)>();
        foreach (var b in link.Bodies.Values)
        {
            if (!(Vector3.Distance(b.P, b.LightAt) < 0.02f)) { b.Light = scene!.LightAt(b.P + new Vector3(0, 0.05f, 0)); b.LightAt = b.P; }
            list.Add((b.Shape, b.Model, b.Light, Vector4.Zero));
        }
        if (link.Held is { } held && world.Player is { } holder)
        {
            var q = HeldRotation(holder);
            var (c, hh) = ShapeBox(scene!, held, Matrix4x4.CreateFromQuaternion(q));
            // 3인칭에서는 가슴 앞에 든 모습, 1인칭은 시야를 가리지 않게 숨긴다 (놓을 자리 미리보기가 보인다)
            if (world.CamView != World.ViewMode.FirstPerson && !world.PlayerHidden)
            {
                var at = holder.Pos + holder.Facing * (0.28f + MathF.Min(hh.X, hh.Z)) + new Vector3(0, 1.0f, 0) - c;
                list.Add((held, Matrix4x4.CreateFromQuaternion(q) * Matrix4x4.CreateTranslation(at), scene!.LightAt(holder.Pos + new Vector3(0, 1, 0)), Vector4.Zero));
            }
            if (place is { } spot)
                list.Add((held, Matrix4x4.CreateFromQuaternion(spot.Q) * Matrix4x4.CreateTranslation(spot.P), Vector3.One,
                    spot.Valid ? new Vector4(0.35f, 0.95f, 0.45f, 0.42f) : new Vector4(1f, 0.32f, 0.25f, 0.42f)));
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

/// <summary>조립체를 m 으로 옮겼을 때의 경계 상자 (가운데, 반 치수)</summary>
static (Vector3 C, Vector3 H) ShapeBox(WorldScene scene, AssemblyDef a, Matrix4x4 m)
{
    Vector3 lo = new(float.MaxValue), hi = new(float.MinValue);
    foreach (var part in a.Parts)
    {
        var pm = Matrix4x4.CreateFromQuaternion(part.Q) * Matrix4x4.CreateTranslation(part.P) * m;
        var h = Parts.Half(scene.Pack, part.Item);
        var e = new Vector3(
            MathF.Abs(pm.M11) * h.X + MathF.Abs(pm.M21) * h.Y + MathF.Abs(pm.M31) * h.Z,
            MathF.Abs(pm.M12) * h.X + MathF.Abs(pm.M22) * h.Y + MathF.Abs(pm.M32) * h.Z,
            MathF.Abs(pm.M13) * h.X + MathF.Abs(pm.M23) * h.Y + MathF.Abs(pm.M33) * h.Z);
        lo = Vector3.Min(lo, pm.Translation - e); hi = Vector3.Max(hi, pm.Translation + e);
    }
    return ((lo + hi) * 0.5f, (hi - lo) * 0.5f);
}

/// <summary>내가 보는 쪽을 90° 단위로 맞춘 세로축 회전 (물건의 앞이 나를 보게)</summary>
static Quaternion YawTurn(Actor holder)
{
    var f = holder.Facing;
    var yaw = MathF.Round(MathF.Atan2(f.X, f.Z) / (MathF.PI / 2)) * (MathF.PI / 2) + MathF.PI;
    return Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
}

/// <summary>손에 든 것의 자세: 보는 쪽 회전 × R·T 로 돌린 만큼</summary>
Quaternion HeldRotation(Actor holder) => Quaternion.Normalize(YawTurn(holder) * world.HeldTurn);

/// <summary>
/// 놓을 자리: 시선이 닿은 면(손이 닿는 3 m 안)에 조립체의 경계 상자가 맞닿게 놓는다. 부품 상자 안쪽 점들이 벽·땅·다른 물건 밖이어야 놓을 수 있다.
/// 최종 판정은 세계 서버가 실제 충돌 모양으로 다시 한다.
/// </summary>
Placement? FindPlacement(WorldScene scene, AssemblyDef held, Actor holder)
{
    var t = scene.Raycast(world.CamPos, world.LookDir, 7);
    if (t >= 7) return null;
    var hit = world.CamPos + world.LookDir * t;
    if (Vector3.Distance(hit, world.EyePos) > scene.Pack.Layout.EditReach) return null;
    var n = scene.Gradient(hit);
    if (n.Y > 0.8f) n = Vector3.UnitY;                    // 바닥·상판 위는 똑바로 세운다
    var q = HeldRotation(holder);
    var rot = Matrix4x4.CreateFromQuaternion(q);
    var (c, h) = ShapeBox(scene, held, rot);
    var support = MathF.Abs(n.X) * h.X + MathF.Abs(n.Y) * h.Y + MathF.Abs(n.Z) * h.Z;
    var pos = hit + n * (support + 0.004f) - c;
    // 상자가 비었나: 가운데 거리가 반 대각선보다 크면 비었고, 0 보다 작으면 걸리고, 그 사이면 여덟으로 나눠 다시 본다 (벽 두께보다 고운 데까지)
    bool Free(Matrix4x4 pm, Vector3 c, Vector3 h, int depth)
    {
        var d = scene.Sdf(Vector3.Transform(c, pm));
        if (d >= h.Length()) return true;
        if (d < 0) return false;
        if (depth == 0) return true;
        var q = h * 0.5f;
        for (var i = 0; i < 8; i++)
            if (!Free(pm, c + new Vector3((i & 1) == 0 ? -q.X : q.X, (i & 2) == 0 ? -q.Y : q.Y, (i & 4) == 0 ? -q.Z : q.Z), q, depth - 1)) return false;
        return true;
    }
    bool Fits(Vector3 at) => held.Parts.All(part =>
    {
        var pm = Matrix4x4.CreateFromQuaternion(part.Q) * Matrix4x4.CreateTranslation(part.P) * rot * Matrix4x4.CreateTranslation(at);
        return Free(pm, Vector3.Zero, Vector3.Max(Parts.Half(scene.Pack, part.Item) - new Vector3(0.01f), new Vector3(0.0005f)), 5);
    });
    var ok = Fits(pos);
    // 걸리면 내 쪽으로 5 cm 씩 당겨 본다: 벽 밑동을 겨냥하면 벽에 붙여 놓인다
    var back = new Vector3(-world.LookDir.X, 0, -world.LookDir.Z);
    if (!ok && back.LengthSquared() > 1e-4f)
    {
        back = Vector3.Normalize(back);
        for (var k = 1; k <= 16 && !ok; k++)
            if (Fits(pos + back * (0.05f * k))) { pos += back * (0.05f * k); ok = true; }
    }
    // 몸과 겹치지 않게
    if (ok && Vector2.Distance(new(pos.X + c.X, pos.Z + c.Z), new(holder.Pos.X, holder.Pos.Z)) < 0.3f + MathF.Min(h.X, h.Z) && pos.Y + c.Y - h.Y < holder.Pos.Y + 1.7f) ok = false;
    return new Placement(pos, q, ok);
}

string ThingsJson() => link is null ? "[]" : "[" + string.Join(",", link.Bodies.Values.Select(b => FormattableString.Invariant(
    $"{{\"id\":{b.Id},\"items\":\"{string.Join("+", b.Shape.Parts.Select(p => p.Item))}\",\"joints\":\"{string.Join(",", b.Shape.Joints.Select(j => j.Method))}\",\"p\":[{b.P.X:F3},{b.P.Y:F3},{b.P.Z:F3}]}}"))) + "]";

/// <summary>손짓 → 세계 서버 요청. 겨냥은 화면 가운데(카메라 시선)가 닿는 곳, 손이 닿는지는 서버가 다시 판정한다</summary>
void HandleGestures(WorldLink link, WorldScene scene)
{
    while (world.Gestures.Count > 0)
    {
        var g = world.Gestures.Dequeue();
        var player = world.Player;
        if (player is null) continue;
        var aimed = link.Raycast(world.CamPos, world.LookDir, 7);
        switch (g)
        {
            case World.Gesture.Dig or World.Gesture.Pour:
            {
                var t = scene.Raycast(world.CamPos, world.LookDir, 10);
                var p = world.CamPos + world.LookDir * t;
                if (g == World.Gesture.Dig && aimed is { } ab && ab.T <= t + 0.02f) { world.SetHud(world.Held, "✕ 물건이다 (E 줍기 · F 잇기 · X 풀기)"); break; }
                if (t >= 10 || Vector3.Distance(p, world.EyePos) > scene.Pack.Layout.EditReach + 0.3f) { world.SetHud(world.Held, "✕ 손이 닿는 곳(3 m 안)의 땅을 겨냥한다"); break; }
                if (g == World.Gesture.Dig) link.Dig(p, scene.Gradient(p), world.EyePos);
                else link.Pour(p, world.EyePos);
                break;
            }
            case World.Gesture.Pickup:
                pickedTurn = aimed is { } pa && link.Bodies.TryGetValue(pa.Id, out var pb) ? pb.Q : null;
                link.Pickup(aimed?.Id, player.Pos + new Vector3(0, 0.3f, 0), world.EyePos);
                break;
            case World.Gesture.Drop:
                if (link.Held is null) { link.Pickup(null, player.Pos, world.EyePos); break; }   // 서버가 "들고 있는 것이 없다"
                if (place is { Valid: true } pl) link.Drop(pl.P, pl.Q, Vector3.Zero);
                else if (place is not null) world.SetHud(world.Held, "✕ 그 자리에는 들어가지 않는다 (R·T 로 돌리거나 다른 곳을 겨냥한다)");
                else link.Drop(player.Pos + player.Facing * 0.7f + new Vector3(0, 1.0f, 0), HeldRotation(player), player.Facing * 1.2f + new Vector3(0, 0.5f, 0));
                break;
            case World.Gesture.Join or World.Gesture.Split:
                if (aimed is not { } a) { world.SetHud(world.Held, g == World.Gesture.Join ? "✕ 이을 물건을 겨냥한다 (맞닿은 내 물건과 이어진다)" : "✕ 풀 조립체를 겨냥한다"); break; }
                var hitP = world.CamPos + world.LookDir * a.T;
                if (g == World.Gesture.Join) link.Join(a.Id, hitP, world.EyePos); else link.Split(a.Id, hitP, world.EyePos);
                break;
        }
    }
}

static void PartsCli(string[] args)
{
    var pack = ContentPack.Load();
    var items = args[1].Split('+');
    var method = args.Length > 2 ? args[2] : "weld";
    var y = 0f; var parts = new List<string>();
    var json = new System.Text.StringBuilder("{\"parts\":[");
    for (var i = 0; i < items.Length; i++)
    {
        var h = Parts.Half(pack, items[i]);
        if (i > 0) y += h.Y;
        parts.Add(FormattableString.Invariant($"{{\"item\":\"{items[i]}\",\"p\":[0.03,{y},0.01],\"q\":[0,0,0,1]}}"));
        y += h.Y;
    }
    json.Append(string.Join(",", parts)).Append("],\"joints\":[");
    json.Append(string.Join(",", Enumerable.Range(1, items.Length - 1).Select(i => $"{{\"a\":{i - 1},\"b\":{i},\"method\":\"{method}\"}}"))).Append("]}");
    var a = AssemblyDef.Parse(System.Text.Json.JsonDocument.Parse(json.ToString()).RootElement);
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var m = Parts.Mesh(pack, a);
    Console.WriteLine($"[parts] {args[1]} ({method}): 정점 {m.VertexCount:N0} · 삼각형 {m.Idx.Count / 3:N0} · {sw.Elapsed.TotalSeconds:0.00} 초");
    var cols = m.Col.GroupBy(c => (MathF.Round(c.X, 2), MathF.Round(c.Y, 2), MathF.Round(c.Z, 2), c.W)).OrderByDescending(g => g.Count()).Take(5);
    foreach (var g in cols) Console.WriteLine($"  색 {g.Key} × {g.Count()}");
    var up = m.Nrm.Where((n, i) => m.Pos[i].Y > y - 0.002f).ToList();
    Console.WriteLine($"  윗면 정점 {up.Count} · 법선 y 평균 {(up.Count > 0 ? up.Average(n => n.Y) : 0):0.000} · 최소 {(up.Count > 0 ? up.Min(n => n.Y) : 0):0.000}");
    var bad = Enumerable.Range(0, m.VertexCount).Where(i => m.Pos[i].Y > y - 0.002f && m.Nrm[i].Y < 0.98f && MathF.Abs(m.Pos[i].X - 0.03f) < 0.4f && MathF.Abs(m.Pos[i].Z - 0.01f) < 0.05f).Take(8);
    foreach (var i in bad) Console.WriteLine(FormattableString.Invariant($"  기운 법선 {m.Pos[i].X:F4},{m.Pos[i].Y:F4},{m.Pos[i].Z:F4} → {m.Nrm[i].X:F3},{m.Nrm[i].Y:F3},{m.Nrm[i].Z:F3}"));
    Console.WriteLine($"  그늘(AO) 평균 {m.Light.Average(l => l.X):0.000} · 최소 {m.Light.Min(l => l.X):0.000}");
}

readonly record struct Placement(Vector3 P, Quaternion Q, bool Valid);
