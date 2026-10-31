using Silk.NET.OpenGL;
using System.Numerics;

namespace DollyForge;

/// <summary>정적 메시 GPU 버퍼: 위치(3)·법선(3)·색(4)·빛(4) = 14 float</summary>
sealed class StaticMesh : IDisposable
{
    readonly GL _gl;
    readonly uint _vao, _vbo, _ebo;
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

    public void Dispose() { _gl.DeleteVertexArray(_vao); _gl.DeleteBuffer(_vbo); _gl.DeleteBuffer(_ebo); }
}

/// <summary>
/// 세계 그리기: 지형(가까운·먼 섹션) · 길가 집 다섯 채 · 내 집 가구 · 유리.
/// 빛은 정점에 구운 값(그늘·해·하늘·전등)에 해 방향 노출을 곱한다. 풀은 화면에서 노이즈 무늬를 더하고, 멀수록 하늘빛 공기에 잠긴다.
/// </summary>
sealed class WorldRenderer : IDisposable
{
    readonly GL _gl;
    readonly Shader _shader;
    readonly StaticMesh _fine, _coarse, _shell, _furniture, _glass;
    readonly WorldScene _scene;

    const string Vert = """
        #version 450 core
        layout(location=0) in vec3 a_pos;
        layout(location=1) in vec3 a_nrm;
        layout(location=2) in vec4 a_col;
        layout(location=3) in vec4 a_light;
        uniform mat4 u_vp;
        uniform vec3 u_offset;
        out vec3 v_world; out vec3 v_nrm; out vec4 v_col; out vec4 v_light;
        void main() {
            v_world = a_pos + u_offset; v_nrm = a_nrm; v_col = a_col; v_light = a_light;
            gl_Position = u_vp * vec4(v_world, 1.0);
        }
        """;

    static readonly string Frag = """
        #version 450 core
        in vec3 v_world; in vec3 v_nrm; in vec4 v_col; in vec4 v_light;
        out vec4 frag;
        uniform vec3 u_cam_pos;
        uniform vec3 u_sun;
        uniform vec4 u_hole;
        uniform float u_alpha;
        """ + EnvRenderer.EnvGlsl + """

        float vnoise(vec2 x) {
            vec2 i = floor(x); vec2 f = fract(x); f = f * f * (3.0 - 2.0 * f);
            return mix(mix(hash12(i), hash12(i + vec2(1, 0)), f.x), mix(hash12(i + vec2(0, 1)), hash12(i + vec2(1, 1)), f.x), f.y);
        }

        void main() {
            if (u_hole.z > u_hole.x && v_world.x > u_hole.x && v_world.x < u_hole.z && v_world.z > u_hole.y && v_world.z < u_hole.w) discard;
            vec3 n = normalize(v_nrm);
            vec3 base = v_col.rgb;
            if (v_col.a > 1.5) { frag = vec4(tonemap(base * 3.5 * u_exposure), 1.0); return; }      // glowing lamp shade
            float grass = clamp(v_col.a, 0.0, 1.0);
            if (grass > 0.0) {
                float g1 = vnoise(v_world.xz * 0.35), g2 = vnoise(v_world.xz * 2.7), g3 = vnoise(v_world.xz * 11.0);
                base *= mix(1.0, 0.78 + 0.32 * g1 + 0.14 * g2 + 0.10 * g3, grass);
                base = mix(base, base * vec3(1.08, 1.04, 0.80), grass * smoothstep(0.55, 0.9, g2) * 0.6);
            }
            float ao = v_light.x, sunv = v_light.y, skyv = v_light.z, lamp = v_light.w;
            float ndl = max(dot(n, u_sun), 0.0);
            vec3 sunCol = vec3(3.1, 2.85, 2.5);
            vec3 skyCol = vec3(0.46, 0.58, 0.82);
            vec3 lit = sunCol * ndl * sunv
                     + skyCol * (0.18 + 0.82 * skyv) * ao * (0.55 + 0.45 * n.y)
                     + vec3(0.30, 0.26, 0.20) * ao * (0.3 + 0.7 * skyv) * (0.5 - 0.5 * n.y)
                     + vec3(2.6, 2.1, 1.5) * lamp * ao;
            vec3 col = base * lit * u_exposure;
            vec3 V = normalize(u_cam_pos - v_world);
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
        _fine = new StaticMesh(gl, scene.TerrainFine);
        _coarse = new StaticMesh(gl, scene.TerrainCoarse);
        _shell = new StaticMesh(gl, scene.Shell);
        _furniture = new StaticMesh(gl, scene.Furniture);
        _glass = new StaticMesh(gl, scene.Glass);
    }

    public void Draw(EnvRenderer env, Matrix4x4 vp, Vector3 camPos)
    {
        _shader.Use();
        env.Bind(_shader);
        _shader.Set("u_vp", vp);
        _shader.Set("u_cam_pos", camPos);
        _shader.Set("u_sun", WorldScene.SunDir);
        _shader.Set("u_alpha", 1f);
        _shader.Set("u_offset", Vector3.Zero);
        _shader.Set("u_hole", Vector4.Zero);
        _fine.Draw();
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
            _shell.Draw();
            if (i == WorldScene.OwnHouse) _furniture.Draw();
        }
        // 유리: 반투명, 깊이는 쓰지 않는다
        _gl.Enable(EnableCap.Blend); _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha); _gl.DepthMask(false);
        _shader.Set("u_alpha", 0.18f);
        for (var i = 0; i < _scene.HouseOrigins.Length; i++) { _shader.Set("u_offset", _scene.HouseOrigins[i]); _glass.Draw(); }
        _gl.DepthMask(true); _gl.Disable(EnableCap.Blend);
    }

    public void Dispose() { _shader.Dispose(); _fine.Dispose(); _coarse.Dispose(); _shell.Dispose(); _furniture.Dispose(); _glass.Dispose(); }
}
