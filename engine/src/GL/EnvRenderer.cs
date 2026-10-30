using Silk.NET.OpenGL;
using System.Numerics;
using System.Collections.Generic;

namespace DollyForge;

/// <summary>하늘 + 도로 + 건물 환경 렌더러 (Google Street View 스타일)</summary>
sealed class EnvRenderer : IDisposable
{
    readonly GL   _gl;
    readonly uint _skyProg, _worldProg;
    readonly uint _skyVao,  _skyVbo;
    readonly uint _worldVao, _worldVbo;
    readonly int  _worldVerts;

    // ── Sky: 카메라 방향 벡터로 광선 계산 ──────────────────────────────
    const string SkyV = """
        #version 450 core
        layout(location=0) in vec2 a_pos;
        uniform vec3  u_fwd;
        uniform vec3  u_right;
        uniform vec3  u_up;
        uniform float u_tan;
        uniform float u_asp;
        out vec3 v_ray;
        void main() {
            v_ray = u_fwd
                  + u_right * (a_pos.x * u_asp * u_tan)
                  + u_up    * (a_pos.y * u_tan);
            gl_Position = vec4(a_pos, 0.0, 1.0);
        }
        """;

    const string SkyF = """
        #version 450 core
        in  vec3 v_ray;
        out vec4 frag;
        void main() {
            vec3 d  = normalize(v_ray);
            float t = clamp(d.y * 2.0 + 0.12, 0.0, 1.0);
            vec3 col = mix(vec3(0.73, 0.86, 0.97), vec3(0.15, 0.42, 0.80), t * t);
            vec3 sun = normalize(vec3(0.6, 1.0, 0.4));
            col += vec3(1.05, 0.95, 0.80) * pow(max(dot(d, sun), 0.0), 512.0);
            col += vec3(0.90, 0.85, 0.70) * pow(max(dot(d, sun), 0.0),  16.0) * 0.10;
            col = pow(col / (col + 1.0), vec3(1.0 / 2.2));
            frag = vec4(col, 1.0);
        }
        """;

    // ── World: 지면 + 건물 Lambert 조명 ────────────────────────────────
    const string WorldV = """
        #version 450 core
        layout(location=0) in vec3 a_pos;
        layout(location=1) in vec3 a_norm;
        layout(location=2) in vec3 a_col;
        uniform mat4 u_mvp;
        out vec3  v_norm;
        out vec3  v_col;
        out float v_dist;
        void main() {
            v_norm  = a_norm;
            v_col   = a_col;
            v_dist  = length(a_pos.xz);
            gl_Position = u_mvp * vec4(a_pos, 1.0);
        }
        """;

    const string WorldF = """
        #version 450 core
        in vec3  v_norm;
        in vec3  v_col;
        in float v_dist;
        out vec4 frag;
        uniform vec3 u_light;
        uniform vec3 u_ambient;
        void main() {
            float ndl = max(dot(normalize(v_norm), normalize(u_light)), 0.0);
            vec3 col  = v_col * (u_ambient + vec3(1.8, 1.65, 1.40) * ndl);
            float fog = pow(clamp(v_dist / 75.0, 0.0, 1.0), 2.0);
            col = mix(col, vec3(0.72, 0.83, 0.95), fog);
            col = pow(col / (col + 1.0), vec3(1.0 / 2.2));
            frag = vec4(col, 1.0);
        }
        """;

    public unsafe EnvRenderer(GL gl)
    {
        _gl = gl;
        _skyProg   = Link(SkyV,   SkyF);
        _worldProg = Link(WorldV, WorldF);

        // Sky: NDC 풀스크린 쿼드 (vec2 only)
        var sq = new float[] { -1,-1, 1,-1, -1,1, 1,1 };
        _skyVao = gl.GenVertexArray();
        _skyVbo = gl.GenBuffer();
        gl.BindVertexArray(_skyVao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _skyVbo);
        fixed (float* p = sq)
            gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(sq.Length * 4), p, BufferUsageARB.StaticDraw);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2*4, (void*)0);
        gl.BindVertexArray(0);

        // World: pos(3) + norm(3) + col(3) = 9 floats/vert
        var wd = BuildWorld();
        _worldVerts = wd.Length / 9;
        _worldVao = gl.GenVertexArray();
        _worldVbo = gl.GenBuffer();
        gl.BindVertexArray(_worldVao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _worldVbo);
        fixed (float* p = wd)
            gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(wd.Length * 4), p, BufferUsageARB.StaticDraw);
        const uint sv = 9 * 4;
        gl.EnableVertexAttribArray(0); gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, sv, (void*)0);
        gl.EnableVertexAttribArray(1); gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, sv, (void*)(3*4));
        gl.EnableVertexAttribArray(2); gl.VertexAttribPointer(2, 3, VertexAttribPointerType.Float, false, sv, (void*)(6*4));
        gl.BindVertexArray(0);
    }

    // 하늘 렌더링: 카메라 방향 벡터 + FOV
    public void DrawSky(Vector3 fwd, Vector3 right, Vector3 up, float fovTan, float aspect)
    {
        _gl.UseProgram(_skyProg);
        _gl.Disable(EnableCap.DepthTest);
        _gl.DepthMask(false);
        U3("u_fwd", fwd); U3("u_right", right); U3("u_up", up);
        U1("u_tan", fovTan); U1("u_asp", aspect);
        _gl.BindVertexArray(_skyVao);
        _gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
        _gl.BindVertexArray(0);
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthMask(true);

        void U1(string n, float v) => _gl.Uniform1(_gl.GetUniformLocation(_skyProg, n), v);
        void U3(string n, Vector3 v) => _gl.Uniform3(_gl.GetUniformLocation(_skyProg, n), v.X, v.Y, v.Z);
    }

    // 지면 + 건물 렌더링
    public unsafe void DrawWorld(Matrix4x4 vp, Vector3 lightDir)
    {
        _gl.UseProgram(_worldProg);
        _gl.Disable(EnableCap.CullFace);  // 평면 쉐이딩, 컬링 불필요
        var copy = vp;
        _gl.UniformMatrix4(_gl.GetUniformLocation(_worldProg, "u_mvp"), 1, false, (float*)&copy);
        _gl.Uniform3(_gl.GetUniformLocation(_worldProg, "u_light"),   lightDir.X, lightDir.Y, lightDir.Z);
        _gl.Uniform3(_gl.GetUniformLocation(_worldProg, "u_ambient"), 0.12f, 0.14f, 0.19f);
        _gl.BindVertexArray(_worldVao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)_worldVerts);
        _gl.BindVertexArray(0);
        _gl.Enable(EnableCap.CullFace);
    }

    // ── 도시 거리 기하 생성 ────────────────────────────────────────────
    static float[] BuildWorld()
    {
        var buf = new List<float>(10000);

        // 도로 + 인도
        HQ(buf, -35f, -90f, 35f,  12f, 0f,      new(0.19f, 0.19f, 0.21f));  // 아스팔트
        HQ(buf, -7.0f,-90f, -3.5f,12f, 0.005f,  new(0.50f, 0.50f, 0.52f));  // 왼쪽 인도
        HQ(buf,  3.5f,-90f,  7.0f,12f, 0.005f,  new(0.50f, 0.50f, 0.52f));  // 오른쪽 인도

        // 건물 10쌍 (양쪽 거리)
        float[] hts = { 9f, 5f, 11f, 6f, 13f, 7f, 8f, 4f, 10f, 6f };
        float z = -4f;
        for (int i = 0; i < 10; i++)
        {
            float h  = hts[i % hts.Length];
            float bw = 8f;
            float s  = 0.28f + (i % 4) * 0.05f;
            var   bc = new Vector3(s, s + 0.01f, s + 0.025f);
            Box(buf, -14.5f, -7.0f, z - bw, z, 0f, h, bc);  // 왼쪽
            Box(buf,   7.0f, 14.5f, z - bw, z, 0f, h, bc);  // 오른쪽
            z -= bw + 1f;
        }
        return buf.ToArray();
    }

    // 수평 사각형 (y=const)
    static void HQ(List<float> b, float x0, float z0, float x1, float z1, float y, Vector3 c)
    {
        var n = Vector3.UnitY;
        Tri(b, new(x0,y,z0), new(x1,y,z0), new(x1,y,z1), n, c);
        Tri(b, new(x0,y,z0), new(x1,y,z1), new(x0,y,z1), n, c);
    }

    // 직사각형 건물 박스 (바닥 제외 5면)
    static void Box(List<float> b, float x0, float x1, float zF, float zN, float y0, float y1, Vector3 c)
    {
        // Front (+Z)
        Quad(b, new(x0,y0,zN),new(x1,y0,zN),new(x0,y1,zN),new(x1,y1,zN), new(0,0,1),  c);
        // Back (-Z)
        Quad(b, new(x1,y0,zF),new(x0,y0,zF),new(x1,y1,zF),new(x0,y1,zF), new(0,0,-1), c * 0.85f);
        // Left (-X)
        Quad(b, new(x0,y0,zF),new(x0,y0,zN),new(x0,y1,zF),new(x0,y1,zN), new(-1,0,0), c * 0.80f);
        // Right (+X)
        Quad(b, new(x1,y0,zN),new(x1,y0,zF),new(x1,y1,zN),new(x1,y1,zF), new(1,0,0),  c * 0.80f);
        // Top (+Y)
        Quad(b, new(x0,y1,zN),new(x1,y1,zN),new(x0,y1,zF),new(x1,y1,zF), new(0,1,0),  c * 1.15f);
    }

    static void Quad(List<float> b, Vector3 a, Vector3 c, Vector3 e, Vector3 g, Vector3 n, Vector3 col)
    {
        Tri(b, a, c, e, n, col);
        Tri(b, c, g, e, n, col);
    }

    static void Tri(List<float> b, Vector3 a, Vector3 v, Vector3 c, Vector3 n, Vector3 col)
    {
        Vtx(b, a, n, col); Vtx(b, v, n, col); Vtx(b, c, n, col);
    }

    static void Vtx(List<float> b, Vector3 p, Vector3 n, Vector3 c)
    {
        b.Add(p.X); b.Add(p.Y); b.Add(p.Z);
        b.Add(n.X); b.Add(n.Y); b.Add(n.Z);
        b.Add(c.X); b.Add(c.Y); b.Add(c.Z);
    }

    uint Link(string vs, string fs)
    {
        uint Sh(GLEnum t, string src)
        {
            var sh = _gl.CreateShader(t);
            _gl.ShaderSource(sh, src);
            _gl.CompileShader(sh);
            _gl.GetShader(sh, GLEnum.CompileStatus, out var ok);
            if (ok == 0) throw new Exception($"EnvShader: {_gl.GetShaderInfoLog(sh)}");
            return sh;
        }
        var p  = _gl.CreateProgram();
        var v  = Sh(GLEnum.VertexShader,   vs);
        var f  = Sh(GLEnum.FragmentShader, fs);
        _gl.AttachShader(p, v); _gl.AttachShader(p, f);
        _gl.LinkProgram(p);
        _gl.GetProgram(p, GLEnum.LinkStatus, out var lok);
        if (lok == 0) throw new Exception($"EnvProg: {_gl.GetProgramInfoLog(p)}");
        _gl.DeleteShader(v); _gl.DeleteShader(f);
        return p;
    }

    public void Dispose()
    {
        _gl.DeleteProgram(_skyProg);   _gl.DeleteProgram(_worldProg);
        _gl.DeleteVertexArray(_skyVao); _gl.DeleteBuffer(_skyVbo);
        _gl.DeleteVertexArray(_worldVao); _gl.DeleteBuffer(_worldVbo);
    }
}
