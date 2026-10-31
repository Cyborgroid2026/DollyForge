using Silk.NET.OpenGL;
using System.Numerics;

namespace DollyForge;

/// <summary>정적 메시 GPU 버퍼: 위치(3)·법선(3)·색(4)·빛(4) = 14 float. 인스턴스(자리 xyz + 크기)로 여러 번 그릴 수도 있다</summary>
sealed class StaticMesh : IDisposable
{
    readonly GL _gl;
    readonly uint _vao, _vbo, _ebo;
    uint _inst;
    int _instCap;
    public readonly int IndexCount;

    public unsafe StaticMesh(GL gl, StaticMeshData m)
    {
        _gl = gl;
        IndexCount = m.Idx.Count;
        var buf = new float[m.VertexCount * 14];
        for (var i = 0; i < m.VertexCount; i++)
        {
            var o = i * 14;
            var p = m.Pos[i]; var n = m.Nrm[i]; var c = m.Col[i]; var l = i < m.Light.Count ? m.Light[i] : new Vector4(1, 1, 1, 0);
            buf[o] = p.X; buf[o + 1] = p.Y; buf[o + 2] = p.Z; buf[o + 3] = n.X; buf[o + 4] = n.Y; buf[o + 5] = n.Z;
            buf[o + 6] = c.X; buf[o + 7] = c.Y; buf[o + 8] = c.Z; buf[o + 9] = c.W;
            buf[o + 10] = l.X; buf[o + 11] = l.Y; buf[o + 12] = l.Z; buf[o + 13] = l.W;
        }
        var idx = m.Idx.Select(i => (uint)i).ToArray();
        _vao = gl.GenVertexArray(); _vbo = gl.GenBuffer(); _ebo = gl.GenBuffer();
        gl.BindVertexArray(_vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* p = buf) gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(buf.Length * 4), p, BufferUsageARB.StaticDraw);
        const uint stride = 14 * 4;
        gl.EnableVertexAttribArray(0); gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        gl.EnableVertexAttribArray(1); gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * 4));
        gl.EnableVertexAttribArray(2); gl.VertexAttribPointer(2, 4, VertexAttribPointerType.Float, false, stride, (void*)(6 * 4));
        gl.EnableVertexAttribArray(3); gl.VertexAttribPointer(3, 4, VertexAttribPointerType.Float, false, stride, (void*)(10 * 4));
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        fixed (uint* p = idx) gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(idx.Length * 4), p, BufferUsageARB.StaticDraw);
        gl.BindVertexArray(0);
    }

    public unsafe void Draw()
    {
        if (IndexCount == 0) return;
        _gl.BindVertexArray(_vao);
        _gl.DrawElements(PrimitiveType.Triangles, (uint)IndexCount, DrawElementsType.UnsignedInt, (void*)0);
        _gl.BindVertexArray(0);
    }

    /// <summary>인스턴스 자리 (x, y, z, 크기) 를 올리고 그린다</summary>
    public unsafe void DrawInstanced(List<Vector4> inst)
    {
        if (IndexCount == 0 || inst.Count == 0) return;
        _gl.BindVertexArray(_vao);
        if (_inst == 0)
        {
            _inst = _gl.GenBuffer();
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _inst);
            _gl.EnableVertexAttribArray(4);
            _gl.VertexAttribPointer(4, 4, VertexAttribPointerType.Float, false, 16, (void*)0);
            _gl.VertexAttribDivisor(4, 1);
        }
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _inst);
        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(inst);
        fixed (Vector4* p = span)
        {
            if (inst.Count > _instCap) { _instCap = inst.Count * 2; _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(_instCap * 16), null, BufferUsageARB.DynamicDraw); }
            _gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, (nuint)(inst.Count * 16), p);
        }
        _gl.DrawElementsInstanced(PrimitiveType.Triangles, (uint)IndexCount, DrawElementsType.UnsignedInt, (void*)0, (uint)inst.Count);
        _gl.BindVertexArray(0);
    }

    public void Dispose() { _gl.DeleteVertexArray(_vao); _gl.DeleteBuffer(_vbo); _gl.DeleteBuffer(_ebo); if (_inst != 0) _gl.DeleteBuffer(_inst); }
}

/// <summary>
/// 세계 그리기: 지형(가까운·먼 섹션) · 길가 집 다섯 채 · 내 집 가구 · 나무(인스턴스, 가까운 것은 고운 메시) · 풀잎 · 유리.
/// 빛은 정점에 구운 값(그늘·해·하늘·전등)에 해 방향 노출을 곱한다. 표면 종류(u_kind)마다 화면에서 무늬를 더한다:
///   0 지형·가구 (a = 풀 정도, 풀 얼룩) · 1 집 골격 (벽돌 쌓기·기와 줄·마루 널, 재질 색으로 고른다) · 2 나무 (a = 잎: 잎 색 얼룩과 빛 투과, 자작나무 껍질 무늬)
/// 멀수록 지평선 하늘빛 공기에 잠긴다.
/// </summary>
sealed class WorldRenderer : IDisposable
{
    readonly GL _gl;
    readonly Shader _shader;
    readonly Dictionary<(int X, int Z), StaticMesh> _fine = [];
    readonly StaticMesh _coarse, _shell, _furniture, _glass;
    readonly Dictionary<(Vector3 Half, float R, string Item), StaticMesh> _bodyMeshes = [];
    readonly StaticMesh[] _treeNear, _treeFar;
    readonly List<Vector4>[] _instNear, _instFar;
    readonly GrassRenderer _grass;
    readonly WorldScene _scene;
    Vector3 _instAt = new(float.MaxValue);
    const float TreeNearDist = 75, TreeFarDist = 1100;

    const string Vert = """
        #version 450 core
        layout(location=0) in vec3 a_pos;
        layout(location=1) in vec3 a_nrm;
        layout(location=2) in vec4 a_col;
        layout(location=3) in vec4 a_light;
        layout(location=4) in vec4 a_inst;
        uniform mat4 u_vp;
        uniform vec3 u_offset;
        uniform int u_instanced;
        uniform float u_time;
        uniform mat4 u_model;
        out vec3 v_world; out vec3 v_local; out vec3 v_nrm; out vec4 v_col; out vec4 v_light;
        void main() {
            vec3 p = a_pos;
            if (u_instanced == 1) {
                // leaves sway a little in the wind (more toward the top of the crown)
                float sway = a_col.a * clamp(a_pos.y / 9.0, 0.0, 1.0);
                p.x += sin(u_time * 1.3 + a_inst.x * 0.37 + a_pos.y * 0.6) * 0.06 * sway;
                p.z += cos(u_time * 1.1 + a_inst.z * 0.29 + a_pos.x * 0.5) * 0.06 * sway;
                v_local = p;
                p = p * a_inst.w + a_inst.xyz;
            } else if (u_instanced == 2) {
                v_local = p;
                p = (u_model * vec4(p, 1.0)).xyz;
            } else {
                v_local = p;
                p += u_offset;
            }
            v_world = p; v_nrm = u_instanced == 2 ? mat3(u_model) * a_nrm : a_nrm; v_col = a_col; v_light = a_light;
            gl_Position = u_vp * vec4(v_world, 1.0);
        }
        """;

    static readonly string Frag = """
        #version 450 core
        in vec3 v_world; in vec3 v_local; in vec3 v_nrm; in vec4 v_col; in vec4 v_light;
        out vec4 frag;
        uniform vec3 u_cam_pos;
        uniform vec3 u_sun;
        uniform vec4 u_hole;
        uniform float u_alpha;
        uniform int u_kind;
        uniform vec3 u_brick, u_roof, u_floor;
        """ + EnvRenderer.EnvGlsl + """

        float vnoise(vec2 x) {
            vec2 i = floor(x); vec2 f = fract(x); f = f * f * (3.0 - 2.0 * f);
            return mix(mix(hash12(i), hash12(i + vec2(1, 0)), f.x), mix(hash12(i + vec2(0, 1)), hash12(i + vec2(1, 1)), f.x), f.y);
        }
        float vnoise3(vec3 p) { return 0.5 * (vnoise(p.xy + p.z * 7.13) + vnoise(p.zx * 1.07 + p.y * 3.71)); }

        // fade a procedural pattern out where it would alias (cell smaller than ~2 pixels)
        float patternFade(vec2 uv, float cell) { vec2 w = fwidth(uv); return 1.0 - smoothstep(cell * 0.12, cell * 0.35, max(w.x, w.y)); }

        // standard brick 215 x 65 mm, 10 mm joints, stretcher bond
        vec3 bricks(vec3 lp, vec3 n, vec3 base) {
            float u = abs(n.x) > abs(n.z) ? lp.z : lp.x;
            vec2 uv = vec2(u, lp.y);
            float row = floor(uv.y / 0.075);
            float bu = uv.x / 0.225 + 0.5 * mod(row, 2.0);
            vec2 id = vec2(floor(bu), row);
            vec2 f = vec2(fract(bu) * 0.225, fract(uv.y / 0.075) * 0.075);
            vec2 aa = fwidth(uv) + 0.001;
            float joint = 1.0 - smoothstep(0.004, 0.004 + aa.x, min(f.x, 0.225 - f.x)) * smoothstep(0.004, 0.004 + aa.y, min(f.y, 0.075 - f.y));
            float h = hash12(id), h2 = hash12(id + 13.7);
            vec3 b = base * (0.82 + 0.34 * h) * vec3(1.0 + 0.08 * (h2 - 0.5), 1.0, 1.0 - 0.08 * (h2 - 0.5));
            b *= 0.92 + 0.16 * vnoise(uv * 40.0);
            vec3 mortar = vec3(0.60, 0.58, 0.54);
            vec3 pat = mix(b, mortar, joint);
            vec3 avg = mix(base, mortar, 0.2);
            return mix(avg, pat, patternFade(uv, 0.075));
        }

        // clay roof tiles: courses up the slope, each course shades darker at its lower lip
        vec3 tiles(vec3 lp, vec3 n, vec3 base) {
            vec3 t = normalize(vec3(0.0, 1.0, 0.0) - n * n.y);
            float along = dot(lp, t) / 0.30;
            float row = floor(along);
            float tu = lp.x / 0.24 + 0.5 * mod(row, 2.0);
            vec2 id = vec2(floor(tu), row);
            float fv = fract(along), fu = fract(tu);
            float lip = mix(0.62, 1.06, smoothstep(0.0, 0.35, fv));
            float side = mix(0.8, 1.0, smoothstep(0.0, 0.08, min(fu, 1.0 - fu)));
            float h = hash12(id);
            vec3 pat = base * lip * side * (0.88 + 0.24 * h);
            return mix(base * 0.93, pat, patternFade(vec2(lp.x, along * 0.30), 0.24));
        }

        // oak floor boards 140 mm wide, staggered lengths, fine grain
        vec3 boards(vec3 lp, vec3 base) {
            float row = floor(lp.z / 0.14);
            float off = hash12(vec2(row, 3.1)) * 1.2;
            float bx = (lp.x + off) / 1.2;
            vec2 id = vec2(floor(bx), row);
            float fz = fract(lp.z / 0.14) * 0.14, fx = fract(bx) * 1.2;
            vec2 aa = fwidth(lp.xz) + 0.001;
            float joint = 1.0 - smoothstep(0.002, 0.002 + aa.y, min(fz, 0.14 - fz)) * smoothstep(0.002, 0.002 + aa.x, min(fx, 1.2 - fx));
            float h = hash12(id);
            float grain = vnoise(vec2(lp.x * 3.0, lp.z * 60.0)) * 0.5 + vnoise(vec2(lp.x * 1.0, lp.z * 140.0)) * 0.5;
            vec3 pat = base * (0.85 + 0.25 * h) * (0.9 + 0.2 * grain);
            pat = mix(pat, base * 0.45, joint * 0.8);
            return mix(base, pat, patternFade(lp.xz, 0.14));
        }

        void main() {
            if (u_hole.z > u_hole.x && v_world.x > u_hole.x && v_world.x < u_hole.z && v_world.z > u_hole.y && v_world.z < u_hole.w) discard;
            vec3 n = normalize(v_nrm);
            vec3 base = v_col.rgb;
            vec3 V = normalize(u_cam_pos - v_world);
            float ao = v_light.x, sunv = v_light.y, skyv = v_light.z, lamp = v_light.w;
            float ndl = max(dot(n, u_sun), 0.0);
            float leaf = 0.0;
            if (u_kind == 0) {
                if (v_col.a > 1.5) { frag = vec4(tonemap(base * 3.5 * u_exposure), 1.0); return; }      // glowing lamp shade
                float grass = clamp(v_col.a, 0.0, 1.0);
                if (grass > 0.0) {
                    float g1 = vnoise(v_world.xz * 0.35), g2 = vnoise(v_world.xz * 2.7), g3 = vnoise(v_world.xz * 11.0);
                    base *= mix(1.0, 0.78 + 0.32 * g1 + 0.14 * g2 + 0.10 * g3, grass);
                    base = mix(base, base * vec3(1.08, 1.04, 0.80), grass * smoothstep(0.55, 0.9, g2) * 0.6);
                    base *= mix(1.0, 0.74, grass);       // the turf seen from afar: shaded blade roots, matches the blade field at its fade ring
                }
            } else if (u_kind == 1) {
                // material by colour: the shell mesh carries the content-pack colours of brick, roof and floor
                float wb = 1.0 - smoothstep(0.02, 0.06, distance(base, u_brick));
                float wr = 1.0 - smoothstep(0.02, 0.06, distance(base, u_roof));
                float wf = 1.0 - smoothstep(0.02, 0.06, distance(base, u_floor));
                if (wb > 0.0 && abs(n.y) < 0.7) base = mix(base, bricks(v_local, n, u_brick), wb);
                if (wr > 0.0 && n.y > 0.3) base = mix(base, tiles(v_local, n, u_roof), wr);
                if (wf > 0.0 && n.y > 0.7) base = mix(base, boards(v_local, u_floor), wf);
            } else {
                leaf = v_col.a;
                if (leaf > 0.5) {
                    float l1 = vnoise3(v_world * 1.3), l2 = vnoise3(v_world * 5.0);
                    base *= 0.75 + 0.35 * l1 + 0.2 * l2;
                    base = mix(base, base * vec3(1.15, 1.1, 0.7), smoothstep(0.6, 0.9, l1) * 0.5);
                } else if (dot(base, vec3(0.333)) > 0.6) {
                    // birch bark: thin dark horizontal lenticels, a few dark patches, darker toward the root
                    float ang = atan(v_local.z, v_local.x);
                    float m = vnoise(vec2(ang * 2.0, v_local.y * 22.0));
                    float m2 = vnoise(vec2(ang * 1.2, v_local.y * 1.6) + 7.0);
                    float dark = smoothstep(0.78, 0.84, m) * 0.8 + smoothstep(0.8, 0.88, m2) * 0.55 + (1.0 - smoothstep(0.2, 0.9, v_local.y)) * 0.6;
                    base = mix(base, vec3(0.12, 0.11, 0.10), clamp(dark, 0.0, 1.0));
                } else {
                    base *= 0.8 + 0.4 * vnoise(vec2(atan(v_local.z, v_local.x) * 5.0, v_local.y * 1.5));
                }
            }
            vec3 sunCol = vec3(3.1, 2.85, 2.5);
            vec3 skyCol = vec3(0.46, 0.58, 0.82);
            vec3 lit;
            if (leaf > 0.5) {
                // foliage: wrapped diffuse + light shining through the leaves when the sun is behind them
                float wrap = max((dot(n, u_sun) + 0.45) / 1.45, 0.0);
                float through = pow(max(dot(-V, u_sun), 0.0), 3.0) * 0.6;
                lit = sunCol * (wrap * 0.85 + through) * sunv
                    + skyCol * (0.25 + 0.75 * skyv) * ao * (0.6 + 0.4 * n.y)
                    + vec3(0.30, 0.26, 0.20) * ao * 0.25;
            } else {
                lit = sunCol * ndl * sunv
                    + skyCol * (0.18 + 0.82 * skyv) * ao * (0.55 + 0.45 * n.y)
                    + vec3(0.30, 0.26, 0.20) * ao * (0.3 + 0.7 * skyv) * (0.5 - 0.5 * n.y)
                    + vec3(2.6, 2.1, 1.5) * lamp * ao;
            }
            vec3 col = base * lit * u_exposure;
            if (u_alpha < 0.99) {                       // glass: sky reflection by fresnel
                float fr = 0.04 + 0.96 * pow(1.0 - max(dot(n, V), 0.0), 5.0);
                col = mix(col * 0.3, envColor(v_world, reflect(-V, n), 0.0), fr);
            }
            float dist = length(v_world - u_cam_pos);
            col = mix(col, outdoorSky(normalize(vec3(v_world.x - u_cam_pos.x, 0.02, v_world.z - u_cam_pos.z))), 1.0 - exp(-dist / 1100.0));   // aerial perspective = horizon sky
            frag = vec4(tonemap(col), u_alpha < 0.99 ? max(u_alpha, 0.25) : 1.0);
        }
        """;

    public WorldRenderer(GL gl, WorldScene scene)
    {
        _gl = gl; _scene = scene;
        _shader = new Shader(gl, Vert, Frag);
        foreach (var (k, m) in scene.FineSections) _fine[k] = new StaticMesh(gl, m);
        _coarse = new StaticMesh(gl, scene.TerrainCoarse);
        _shell = new StaticMesh(gl, scene.Shell);
        _furniture = new StaticMesh(gl, scene.Furniture);
        _glass = new StaticMesh(gl, scene.Glass);
        var vs = scene.Flora.Variants;
        _treeNear = vs.Select(v => new StaticMesh(gl, v.Near)).ToArray();
        _treeFar = vs.Select(v => new StaticMesh(gl, v.Far)).ToArray();
        _instNear = vs.Select(_ => new List<Vector4>()).ToArray();
        _instFar = vs.Select(_ => new List<Vector4>()).ToArray();
        _grass = new GrassRenderer(gl, scene);
    }

    /// <summary>카메라가 2 m 넘게 움직이면 나무를 가까운(고운 메시)·먼(거친 메시) 목록으로 다시 나눈다</summary>
    void SplitTrees(Vector3 cam)
    {
        if (Vector3.DistanceSquared(cam, _instAt) < 4) return;
        _instAt = cam;
        foreach (var l in _instNear) l.Clear();
        foreach (var l in _instFar) l.Clear();
        foreach (var t in _scene.Flora.Trees)
        {
            var d = Vector2.Distance(new(t.Pos.X, t.Pos.Z), new(cam.X, cam.Z));
            if (d > TreeFarDist) continue;
            (d < TreeNearDist ? _instNear : _instFar)[t.Variant].Add(new Vector4(t.Pos, t.Scale));
        }
    }

    /// <summary>물건 (서버 Rapier 의 강체): 아이템 치수의 둥근 상자, 재료 색. 같은 모양은 메시를 한 번만 짓는다</summary>
    public void DrawBodies(IEnumerable<(Vector3 Half, float R, string Item, Matrix4x4 Model)> bodies)
    {
        _shader.Use();
        _shader.Set("u_instanced", 2);
        _shader.Set("u_kind", 0);
        foreach (var (half, r, item, model) in bodies)
        {
            if (!_bodyMeshes.TryGetValue((half, r, item), out var mesh))
            {
                var col = _scene.Pack.Items.TryGetValue(item, out var it) ? _scene.Pack.Color(it.Mat, new(0.5f, 0.45f, 0.4f)) : new Vector3(0.5f);
                if (item == "soil_sack") col = new Vector3(0.28f, 0.22f, 0.14f);   // 마대 포대 (속은 흙)
                var sc = new SdfScene { Blend = 0.001f }.Add(SdfPrim.Box(-half, half, r, col));
                _bodyMeshes[(half, r, item)] = mesh = new StaticMesh(_gl, WorldScene.MeshScenePublic(sc, Math.Clamp(r / 2, 0.006f, 0.02f)));
            }
            _shader.Set("u_model", model);
            mesh.Draw();
        }
        _shader.Set("u_instanced", 0);
    }

    public void Draw(EnvRenderer env, Matrix4x4 vp, Vector3 camPos, float time)
    {
        _shader.Use();
        env.Bind(_shader);
        _shader.Set("u_vp", vp);
        _shader.Set("u_cam_pos", camPos);
        _shader.Set("u_sun", WorldScene.SunDir);
        _shader.Set("u_alpha", 1f);
        _shader.Set("u_offset", Vector3.Zero);
        _shader.Set("u_hole", Vector4.Zero);
        _shader.Set("u_kind", 0);
        _shader.Set("u_instanced", 0);
        _shader.Set("u_time", time);
        _shader.Set("u_brick", _scene.House.Brick);
        _shader.Set("u_roof", _scene.House.Roof);
        _shader.Set("u_floor", _scene.House.Floor);
        // 편집으로 다시 지은 섹션을 바꿔 끼운다
        while (_scene.Rebuilt.TryDequeue(out var rb))
        {
            if (_fine.Remove(rb.Key, out var old)) old.Dispose();
            _fine[rb.Key] = new StaticMesh(_gl, rb.Mesh);
        }
        foreach (var m in _fine.Values) m.Draw();
        // 먼 섹션: 가까운 영역은 비우고, 겹친 띠에서는 깊이를 살짝 밀어 가까운 섹션이 이긴다
        var h = WorldScene.FineHalf - 0.5f;
        _shader.Set("u_hole", new Vector4(-h, -h, h, h));
        _gl.Enable(EnableCap.PolygonOffsetFill); _gl.PolygonOffset(2, 4);
        _coarse.Draw();
        _gl.Disable(EnableCap.PolygonOffsetFill);
        _shader.Set("u_hole", Vector4.Zero);
        for (var i = 0; i < _scene.HouseOrigins.Length; i++)
        {
            _shader.Set("u_offset", _scene.HouseOrigins[i]);
            _shader.Set("u_kind", 1);
            _shell.Draw();
            _shader.Set("u_kind", 0);
            if (i == WorldScene.OwnHouse) _furniture.Draw();
        }
        // 나무
        SplitTrees(camPos);
        _shader.Set("u_kind", 2);
        _shader.Set("u_instanced", 1);
        for (var v = 0; v < _treeNear.Length; v++) { _treeNear[v].DrawInstanced(_instNear[v]); _treeFar[v].DrawInstanced(_instFar[v]); }
        _shader.Set("u_instanced", 0);
        _shader.Set("u_kind", 0);
        // 풀잎
        _grass.Draw(env, vp, camPos, time);
        // 유리: 반투명, 깊이는 쓰지 않는다
        _shader.Use();
        _gl.Enable(EnableCap.Blend); _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha); _gl.DepthMask(false);
        _shader.Set("u_alpha", 0.18f);
        for (var i = 0; i < _scene.HouseOrigins.Length; i++) { _shader.Set("u_offset", _scene.HouseOrigins[i]); _glass.Draw(); }
        _gl.DepthMask(true); _gl.Disable(EnableCap.Blend);
        _shader.Set("u_alpha", 1f);
    }

    public void Dispose()
    {
        _shader.Dispose(); foreach (var m in _fine.Values) m.Dispose(); foreach (var m in _bodyMeshes.Values) m.Dispose(); _coarse.Dispose(); _shell.Dispose(); _furniture.Dispose(); _glass.Dispose();
        foreach (var m in _treeNear) m.Dispose();
        foreach (var m in _treeFar) m.Dispose();
        _grass.Dispose();
    }
}
