using Silk.NET.OpenGL;
using System.Numerics;

namespace DollyForge;

sealed class Shader : IDisposable
{
    readonly GL _gl;
    readonly uint _prog;

    public Shader(GL gl, string vert, string frag)
    {
        _gl   = gl;
        _prog = gl.CreateProgram();
        uint CompileStage(GLEnum type, string src)
        {
            var sh = gl.CreateShader(type);
            gl.ShaderSource(sh, src);
            gl.CompileShader(sh);
            gl.GetShader(sh, GLEnum.CompileStatus, out var ok);
            if (ok == 0) throw new Exception($"Shader compile: {gl.GetShaderInfoLog(sh)}");
            return sh;
        }
        var vs = CompileStage(GLEnum.VertexShader,   vert);
        var fs = CompileStage(GLEnum.FragmentShader, frag);
        gl.AttachShader(_prog, vs); gl.AttachShader(_prog, fs);
        gl.LinkProgram(_prog);
        gl.GetProgram(_prog, GLEnum.LinkStatus, out var lok);
        if (lok == 0) throw new Exception($"Shader link: {gl.GetProgramInfoLog(_prog)}");
        gl.DeleteShader(vs); gl.DeleteShader(fs);
    }

    public void Use() => _gl.UseProgram(_prog);

    public void Set(string name, int v)        => _gl.Uniform1(_gl.GetUniformLocation(_prog, name), v);
    public void Set(string name, float v)      => _gl.Uniform1(_gl.GetUniformLocation(_prog, name), v);
    public void Set(string name, Vector3 v)    => _gl.Uniform3(_gl.GetUniformLocation(_prog, name), v.X, v.Y, v.Z);
    public void Set(string name, Vector4 v)    => _gl.Uniform4(_gl.GetUniformLocation(_prog, name), v.X, v.Y, v.Z, v.W);
    public unsafe void Set(string name, Matrix4x4 m)
    {
        var copy = m;
        _gl.UniformMatrix4(_gl.GetUniformLocation(_prog, name), 1, false, (float*)&copy);
    }
    public unsafe void SetArray(string name, Matrix4x4[] ms)
    {
        fixed (Matrix4x4* pm = ms)
        {
            var p = (float*)pm;
            _gl.UniformMatrix4(_gl.GetUniformLocation(_prog, name), (uint)ms.Length, false, p);
        }
    }

    public void Dispose() => _gl.DeleteProgram(_prog);

    // ── GLSL ソース ────────────────────────────────────────────────────

    public static readonly string BodyVert = """
        #version 450 core
        layout(location=0) in vec3 a_pos;
        layout(location=1) in vec3 a_norm;
        layout(location=2) in vec3 a_color;
        layout(location=3) in ivec4 a_bones;
        layout(location=4) in vec4 a_weights;

        uniform mat4 u_mvp;
        uniform mat4 u_model;
        uniform mat4 u_bones[14];

        out vec3 v_pos;
        out vec3 v_norm;
        out vec3 v_color;

        void main() {
            mat4 skin = a_weights.x * u_bones[a_bones.x]
                      + a_weights.y * u_bones[a_bones.y]
                      + a_weights.z * u_bones[a_bones.z]
                      + a_weights.w * u_bones[a_bones.w];
            vec4 wp   = skin * vec4(a_pos, 1.0);
            v_pos     = (u_model * wp).xyz;
            v_norm    = normalize(mat3(u_model) * (mat3(skin) * a_norm));
            v_color   = a_color;
            gl_Position = u_mvp * wp;
        }
        """;

    public static readonly string BodyFrag = """
        #version 450 core
        in vec3 v_pos;
        in vec3 v_norm;
        in vec3 v_color;
        out vec4 frag_color;

        uniform vec3 u_cam_pos;
        uniform vec3 u_l0_dir;
        uniform vec3 u_l0_col;
        uniform vec3 u_l1_dir;
        uniform vec3 u_l1_col;
        uniform vec3 u_l2_dir;
        uniform vec3 u_l2_col;
        uniform vec3 u_ambient;
        uniform float u_metallic;
        uniform float u_roughness;

        const float PI = 3.14159265;

        float D_GGX(float NdH, float rough) {
            float a = rough * rough;
            float a2 = a * a;
            float d = (NdH*NdH*(a2-1.0)+1.0);
            return a2 / (PI * d * d);
        }
        float G_Smith(float NdV, float NdL, float rough) {
            float r = rough + 1.0;
            float k = (r*r)/8.0;
            float g1 = NdV / (NdV*(1.0-k)+k);
            float g2 = NdL / (NdL*(1.0-k)+k);
            return g1 * g2;
        }
        vec3 F_Schlick(float cosT, vec3 F0) {
            return F0 + (1.0-F0)*pow(clamp(1.0-cosT,0.0,1.0),5.0);
        }

        vec3 PBR(vec3 N, vec3 V, vec3 L, vec3 lcolor, vec3 F0) {
            vec3 H    = normalize(V + L);
            float NdL = max(dot(N, L), 0.0);
            float NdV = max(dot(N, V), 0.001);
            float NdH = max(dot(N, H), 0.0);
            float HdV = max(dot(H, V), 0.0);
            vec3  F   = F_Schlick(HdV, F0);
            float D   = D_GGX(NdH, u_roughness);
            float G   = G_Smith(NdV, NdL, u_roughness);
            vec3 spec = (D * G * F) / max(4.0 * NdV * NdL, 0.001);
            vec3 kD   = (1.0 - F) * (1.0 - u_metallic);
            return (kD * v_color / PI + spec) * lcolor * NdL;
        }

        void main() {
            vec3 N   = normalize(v_norm);
            vec3 L0  = normalize(u_l0_dir);
            float d0 = max(dot(N, L0), 0.0);
            float d1 = max(dot(N, normalize(u_l1_dir)), 0.0);
            float d2 = max(dot(N, normalize(u_l2_dir)), 0.0);
            vec3 lit = v_color * (u_ambient + u_l0_col * d0 + u_l1_col * d1 + u_l2_col * d2);
            lit = lit / (lit + 1.0);
            lit = pow(lit, vec3(1.0/2.2));
            frag_color = vec4(lit, 1.0);
        }
        """;
}
