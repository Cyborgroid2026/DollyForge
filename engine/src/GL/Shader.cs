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
        layout(location=5) in float a_seam;

        uniform mat4 u_mvp;
        uniform mat4 u_model;
        uniform mat4 u_bones[14];

        out vec3 v_pos;
        out vec3 v_norm;
        out vec3 v_color;
        out float v_seam;

        void main() {
            v_seam    = a_seam;
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

    // 크롬: 환경 반사(EnvGlsl) x 슐릭 프레넬 + GGX 하이라이트 3개.
    // 바닥 반사 패스에서는 u_cam_pos 에 바닥 아래로 뒤집은 카메라를 넣는다.
    public static readonly string BodyFrag = """
        #version 450 core
        in vec3 v_pos;
        in vec3 v_norm;
        in vec3 v_color;
        in float v_seam;
        out vec4 frag_color;

        uniform vec3 u_cam_pos;
        uniform vec3 u_l0_dir;
        uniform vec3 u_l0_col;
        uniform vec3 u_l1_dir;
        uniform vec3 u_l1_col;
        uniform vec3 u_l2_dir;
        uniform vec3 u_l2_col;
        uniform vec3 u_ambient;
        uniform float u_roughness;
        uniform vec3 u_cam_back;
        uniform int u_mask;
        uniform float u_matcap_gain;
        uniform float u_matcap_mix;
        uniform float u_curv_gain;
        """ + EnvRenderer.EnvGlsl + """

        float D_GGX(float NdH, float rough) {
            float a = rough * rough;
            float a2 = a * a;
            float d = NdH * NdH * (a2 - 1.0) + 1.0;
            return a2 / (PI_E * d * d);
        }

        vec3 keyLight(vec3 N, vec3 V, vec3 L, vec3 lcol, vec3 F) {
            L = normalize(L);
            vec3 H = normalize(V + L);
            float NdL = max(dot(N, L), 0.0);
            return D_GGX(max(dot(N, H), 0.0), u_roughness) * 0.25 * F * lcol * NdL;
        }

        // illustrated chrome of the reference art (matcap-style, view space: x right, y up, z toward viewer).
        // the look does not depend on what is around the body: soft grey front, dark core band at ~50 deg,
        // bright rim strips near the silhouette, lit shoulders/tops, dark undersides, cool blue shadows.
        vec3 illusChrome(vec3 n) {
            float side = abs(n.x);
            float lum = mix(0.012, 0.11, smoothstep(-0.75, 0.85, n.y));             // sky above, floor below
            lum += 0.11 * pow(max(n.z, 0.0), 2.5);                                     // soft front fill
            lum *= mix(1.0, 0.38, smoothstep(0.35, 0.65, side) * (1.0 - smoothstep(0.78, 0.9, side)));   // deep core shadow band
            float rim = smoothstep(0.72, 0.88, side) * (1.0 - smoothstep(0.95, 1.0, side));
            lum += rim * mix(0.14, 0.60, smoothstep(-0.5, 0.6, n.y));                 // side strip lights
            lum += 0.60 * smoothstep(0.66, 0.94, n.y) * smoothstep(0.0, 0.5, n.z + 0.4); // top softbox on shoulders
            float k = max(dot(n, normalize(vec3(-0.30, 0.50, 0.81))), 0.0);
            lum += 0.22 * pow(k, 9.0) + 1.6 * pow(k, 70.0);                            // key highlight (upper left), crisp
            vec3 shadowTint = vec3(0.62, 0.70, 0.88), lightTint = vec3(1.00, 1.00, 1.02);
            return lum * mix(shadowTint, lightTint, smoothstep(0.03, 0.35, lum));
        }

        void main() {
            vec3 N = normalize(v_norm);
            vec3 V = normalize(u_cam_pos - v_pos);
            float NdV = max(dot(N, V), 0.001);
            vec3 R = reflect(-V, N);
            vec3 F = v_color + (1.0 - v_color) * pow(1.0 - NdV, 5.0);
            vec3 col;
            if (u_env_mode == 0) {
                vec3 fwd = -u_cam_back;
                vec3 right = normalize(cross(fwd, vec3(0.0, 1.0, 0.0)));
                vec3 up = cross(right, fwd);
                vec3 nv = vec3(dot(N, right), dot(N, up), dot(N, u_cam_back));
                // v_color carries module tint x baked cavity; 0.65 = standard chrome luminance
                float albedo = dot(v_color, vec3(1.0 / 3.0)) / 0.65;
                if (u_matcap_on == 1) {
                    // matcap: the AI-made silver sphere (assets/studio/matcap.png, ZBrush-style material capture).
                    // view-space normal -> point on the sphere photo; outer 4 % skipped (sphere rim / background)
                    vec2 muv = vec2(nv.x, -nv.y) * 0.48 + 0.5;
                    vec3 mc = pow(textureLod(u_matcap, muv, 2.0).rgb, vec3(2.2));          // slightly soft: satin streaks, not mirror
                    mc = mix(vec3(dot(mc, vec3(0.2126, 0.7152, 0.0722))), mc, 0.55);         // less blue than the photo
                    // blend with the soft illustrated studio: its tone spread matches the art, the matcap adds the liquid streaks
                    col = mix(illusChrome(nv) * 0.62, mc * u_matcap_gain, u_matcap_mix) * albedo;
                } else {
                    col = illusChrome(nv) * albedo * 0.72;
                }
                // curvature shading: convex muscle ridges catch light, grooves fall dark (1/m from screen derivatives)
                vec3 dPx = dFdx(v_pos), dPy = dFdy(v_pos);
                float curv = 0.5 * (dot(dFdx(N), dPx) / max(dot(dPx, dPx), 1e-10) + dot(dFdy(N), dPy) / max(dot(dPy, dPy), 1e-10));
                col *= 1.0 + u_curv_gain * clamp(curv * 0.04, -1.0, 1.0);
                col += envGlossy(v_pos + N * 0.003, R, u_cam_back, 0.0) * F * 0.10;    // a hint of the room
            } else {
                col = envGlossy(v_pos + N * 0.003, R, u_cam_back, 0.08) * F;
                col += keyLight(N, V, u_l0_dir, u_l0_col, F);
                col += keyLight(N, V, u_l1_dir, u_l1_col, F);
                col += keyLight(N, V, u_l2_dir, u_l2_col, F);
                col += v_color * u_ambient * (0.55 + 0.45 * N.y) * 0.07;
            }
            // panel seam: thin anti-aliased groove where the baked plane distance crosses zero
            float sw = min(fwidth(v_seam) * 1.5, 0.003);
            float seam = 1.0 - smoothstep(0.0007, 0.0007 + sw, abs(v_seam));
            col *= 1.0 - 0.5 * seam;   // the art draws seams as faint hairlines
            if (u_mask == 2) {
                // debug: camera-space normal as color (same convention as the Marigold maps: x right, y up, z to viewer)
                vec3 fw = -u_cam_back, rt = normalize(cross(fw, vec3(0.0, 1.0, 0.0))), upv = cross(rt, fw);
                frag_color = vec4(vec3(dot(N, rt), dot(N, upv), dot(N, u_cam_back)) * 0.5 + 0.5, 1.0);
                return;
            }
            frag_color = u_mask == 1 ? vec4(1.0) : vec4(tonemap(col), 1.0);
        }
        """;

    // 광택 검정 바닥. 패스 0 = 반사층(환경), 패스 1 = 바닥 자체(알파 = 1 - 반사율).
    public static readonly string FloorVert = """
        #version 450 core
        layout(location=0) in vec2 a_xz;
        uniform mat4 u_vp;
        out vec3 v_pos;
        void main() {
            v_pos = vec3(a_xz.x, 0.0, a_xz.y);
            gl_Position = u_vp * vec4(v_pos, 1.0);
        }
        """;

    public static readonly string FloorFrag = """
        #version 450 core
        in vec3 v_pos;
        out vec4 frag_color;
        uniform vec3 u_cam_pos;
        uniform int u_pass;
        uniform int u_star_n;
        uniform vec3 u_star0;
        uniform vec3 u_star1;

        // four-pointed sparkle lying on the floor under each doll (select screen, as in the reference art):
        // |x|^(1/2) + |z|^(1/2) < 1: four thin arms (long sideways), bright core and soft halo
        float sparkle(vec2 p, vec3 c) {
            vec2 q = (p - c.xz) / vec2(0.42, 0.11);
            float a = sqrt(abs(q.x)) + sqrt(abs(q.y));              // sharper than an astroid: thin lens-flare arms
            float body = 1.0 - smoothstep(0.8, 1.0, a);
            float core = exp(-dot(q, q) * 40.0);
            float halo = exp(-length(q) * 5.0) * 0.12;
            return body * 0.75 + core * 1.0 + halo;
        }
        """ + EnvRenderer.EnvGlsl + """

        void main() {
            vec3 V = normalize(u_cam_pos - v_pos);
            if (u_pass == 0) {
                vec3 R = reflect(-V, vec3(0.0, 1.0, 0.0));
                frag_color = vec4(tonemap(envColor(v_pos + vec3(0.0, 0.001, 0.0), R, 0.03) * 0.55), 1.0);
                return;
            }
            float F = 0.04 + 0.96 * pow(1.0 - max(V.y, 0.0), 5.0);
            float refl = mix(0.40, 0.92, F);
            vec2 g = abs(fract(v_pos.xz / 1.5) - 0.5) * 1.5;
            float aa = fwidth(v_pos.x) + fwidth(v_pos.z);
            float seam = band(0.75 - max(g.x, g.y), 0.003, aa);
            vec3 base = vec3(0.004, 0.0045, 0.006) + vec3(0.03) * seam;
            float st = 0.0;
            if (u_star_n > 0) st += sparkle(v_pos.xz, u_star0);
            if (u_star_n > 1) st += sparkle(v_pos.xz, u_star1);
            base += st * vec3(1.6, 1.7, 1.9);
            frag_color = vec4(tonemap(base), max(1.0 - refl * (1.0 - seam), clamp(st, 0.0, 1.0)));
        }
        """;
}
