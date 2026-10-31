using Silk.NET.OpenGL;
using System.Numerics;

namespace DollyForge;

/// <summary>
/// 풀잎: 카메라 둘레 반지름 26 m 를 8.5 cm 격자로 덮고, 칸마다 풀잎 하나를 GPU 가 그린다 (정점 버퍼 없이 인스턴스 번호로).
/// 칸은 세계 격자에 고정이라 걸어도 풀이 따라 미끄러지지 않는다. 지형 높이 캐시(0.25 m)와 풀 지도(0.5 m: 풀색·양·해 그림자·하늘빛·키·꽃)를 텍스처로 읽는다.
/// 풀 양이 적은 곳(길·밭·집 바닥 아래)은 풀잎이 없고, 멀어질수록 드물고 낮아져 지형의 풀 얼룩으로 자연스럽게 넘어간다. 바람에 끝이 흔들린다.
/// </summary>
sealed class GrassRenderer : IDisposable
{
    readonly GL _gl;
    readonly Shader _shader;
    readonly uint _vao, _texHeight, _texA, _texB;
    readonly int _heightCount;
    const float Spacing = 0.085f, Radius = 26f;
    static readonly int N = (int)(2 * Radius / Spacing);

    const string Vert = """
        #version 450 core
        uniform mat4 u_vp;
        uniform vec3 u_cam;
        uniform vec2 u_snap;
        uniform int u_n;
        uniform float u_spacing, u_radius, u_time;
        uniform sampler2D u_height, u_ga, u_gb;
        uniform vec3 u_hmap;   // origin, step, count
        uniform vec3 u_gmap;
        out vec3 v_world; out vec3 v_col; out vec3 v_light; out float v_t; out vec3 v_nrm; out float v_flower; out vec3 v_fcol;

        float h21(vec2 p) { p = fract(p * vec2(123.34, 456.21)); p += dot(p, p + 45.32); return fract(p.x * p.y); }
        vec2 uvOf(vec2 xz, vec3 m) { return ((xz - m.x) / m.y + 0.5) / m.z; }

        void main() {
            int id = gl_InstanceID;
            vec2 cell = u_snap + vec2(id % u_n, id / u_n) * u_spacing;
            vec2 ci = floor(cell / u_spacing + 0.5);
            float h1 = h21(ci), h2 = h21(ci + 17.3), h3 = h21(ci + 41.7), h4 = h21(ci + 73.1), h5 = h21(ci + 91.9);
            vec2 xz = cell + (vec2(h1, h2) - 0.5) * u_spacing * 0.95;
            float dist = length(xz - u_cam.xz);
            vec2 guv = uvOf(xz, u_gmap);
            vec4 ga = texture(u_ga, guv), gb = texture(u_gb, guv);
            float keep = ga.a * (1.0 - smoothstep(u_radius * 0.3, u_radius, dist));
            if (h3 > keep * 1.15 - 0.1 || any(lessThan(guv, vec2(0.0))) || any(greaterThan(guv, vec2(1.0)))) { gl_Position = vec4(2.0, 2.0, 2.0, 1.0); return; }
            float flower = h5 < gb.a ? 1.0 : 0.0;
            float H = mix(0.07, 0.65, gb.b) * (0.55 + 0.8 * h4) * mix(1.0, 0.4, smoothstep(u_radius * 0.5, u_radius, dist));
            if (flower > 0.5) H = max(H, 0.18) * 1.15;
            float y0 = texture(u_height, uvOf(xz, u_hmap)).r - 0.02;

            int k = gl_VertexID;
            float t = k >= 6 ? 1.0 : float(k / 2) / 3.0;
            float side = k >= 6 ? 0.0 : (k % 2 == 0 ? -1.0 : 1.0);
            float w = (flower > 0.5 ? 0.016 : 0.012 + 0.008 * h1) * (1.0 - t * 0.85) * (1.0 + smoothstep(8.0, 20.0, dist) * 2.0);
            float yaw = h2 * 6.2831;
            vec2 toCam = normalize(u_cam.xz - xz + 1e-4);
            vec2 sd = normalize(mix(vec2(cos(yaw), sin(yaw)), vec2(-toCam.y, toCam.x), 0.45));
            vec2 lean = vec2(sin(yaw * 1.7), cos(yaw * 1.3)) * 0.35;
            vec2 wind = vec2(sin(u_time * 1.7 + xz.x * 0.35 + xz.y * 0.21), cos(u_time * 1.3 + xz.y * 0.31)) * 0.18 + vec2(0.12, 0.05);
            vec2 bend = (lean + wind) * t * t * H;
            vec3 p = vec3(xz.x + sd.x * w * side + bend.x, y0 + t * H * (1.0 - 0.15 * t), xz.y + sd.y * w * side + bend.y);
            v_world = p;
            v_t = t;
            v_nrm = normalize(vec3(-sd.y, 0.6, sd.x) * (h4 > 0.5 ? 1.0 : -1.0) + vec3(bend.x, 0.0, bend.y));
            v_col = ga.rgb * (0.82 + 0.36 * h4) * vec3(1.0 + 0.1 * (h1 - 0.5), 1.0, 1.0 - 0.15 * (h2 - 0.5));
            v_light = gb.rgb;
            v_flower = flower;
            float fc = h21(ci + 5.5);
            v_fcol = fc < 0.45 ? vec3(0.95, 0.94, 0.88) : fc < 0.75 ? vec3(0.95, 0.82, 0.25) : fc < 0.9 ? vec3(0.62, 0.48, 0.85) : vec3(0.88, 0.35, 0.40);
            gl_Position = u_vp * vec4(p, 1.0);
        }
        """;

    static readonly string Frag = """
        #version 450 core
        in vec3 v_world; in vec3 v_col; in vec3 v_light; in float v_t; in vec3 v_nrm; in float v_flower; in vec3 v_fcol;
        out vec4 frag;
        uniform vec3 u_cam_pos;
        uniform vec3 u_sun;
        """ + EnvRenderer.EnvGlsl + """

        void main() {
            vec3 n = normalize(v_nrm);
            if (!gl_FrontFacing) n = -n;
            vec3 V = normalize(u_cam_pos - v_world);
            float sunv = v_light.x, skyv = v_light.y;
            vec3 base = v_col * mix(0.42, 1.12, smoothstep(0.0, 0.8, v_t));        // darker toward the root (self shadow of the turf)
            if (v_flower > 0.5) base = mix(base, v_fcol, smoothstep(0.78, 0.86, v_t));
            float wrap = max((dot(n, u_sun) + 0.5) / 1.5, 0.0);
            float through = pow(max(dot(-V, u_sun), 0.0), 4.0) * 0.5;
            vec3 lit = vec3(3.1, 2.85, 2.5) * (wrap * 0.8 + through) * sunv
                     + vec3(0.46, 0.58, 0.82) * (0.3 + 0.7 * skyv) * mix(0.55, 1.0, v_t);
            vec3 col = base * lit * u_exposure;
            float dist = length(v_world - u_cam_pos);
            col = mix(col, outdoorSky(normalize(vec3(v_world.x - u_cam_pos.x, 0.02, v_world.z - u_cam_pos.z))), 1.0 - exp(-dist / 1100.0));
            frag = vec4(tonemap(col), 1.0);
        }
        """;

    public unsafe GrassRenderer(GL gl, WorldScene scene)
    {
        _gl = gl;
        _shader = new Shader(gl, Vert, Frag);
        _vao = gl.GenVertexArray();
        _heightCount = scene.Terrain.CacheCount;
        _texHeight = Tex(PixelFormat.Red, InternalFormat.R32f, _heightCount, scene.Terrain.HeightCache.ToArray());
        _texA = Tex(PixelFormat.Rgba, InternalFormat.Rgba16f, WorldScene.GroundCount, scene.GroundA);
        _texB = Tex(PixelFormat.Rgba, InternalFormat.Rgba16f, WorldScene.GroundCount, scene.GroundB);
    }

    unsafe uint Tex(PixelFormat fmt, InternalFormat ifmt, int n, float[] data)
    {
        var t = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, t);
        fixed (float* p = data) _gl.TexImage2D(TextureTarget.Texture2D, 0, ifmt, (uint)n, (uint)n, 0, fmt, PixelType.Float, p);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        return t;
    }

    public void Draw(EnvRenderer env, Matrix4x4 vp, Vector3 cam, float time)
    {
        _shader.Use();
        env.Bind(_shader);
        _shader.Set("u_vp", vp);
        _shader.Set("u_cam", cam);
        _shader.Set("u_cam_pos", cam);
        _shader.Set("u_sun", WorldScene.SunDir);
        _shader.Set("u_time", time);
        _shader.Set("u_n", N);
        _shader.Set("u_spacing", Spacing);
        _shader.Set("u_radius", Radius);
        // 격자 원점은 세계 격자에 맞춘다 (걸어도 풀잎 자리가 바뀌지 않는다)
        _shader.Set("u_snap", new Vector2(MathF.Floor((cam.X - Radius) / Spacing) * Spacing, MathF.Floor((cam.Z - Radius) / Spacing) * Spacing));
        _shader.Set("u_hmap", new Vector3(-Terrain.CacheHalf, Terrain.CacheStep, _heightCount));
        _shader.Set("u_gmap", new Vector3(-WorldScene.GroundHalf, WorldScene.GroundStep, WorldScene.GroundCount));
        _gl.ActiveTexture(TextureUnit.Texture6); _gl.BindTexture(TextureTarget.Texture2D, _texHeight); _shader.Set("u_height", 6);
        _gl.ActiveTexture(TextureUnit.Texture7); _gl.BindTexture(TextureTarget.Texture2D, _texA); _shader.Set("u_ga", 7);
        _gl.ActiveTexture(TextureUnit.Texture8); _gl.BindTexture(TextureTarget.Texture2D, _texB); _shader.Set("u_gb", 8);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindVertexArray(_vao);
        _gl.DrawArraysInstanced(PrimitiveType.TriangleStrip, 0, 7, (uint)(N * N));
        _gl.BindVertexArray(0);
    }

    public void Dispose() { _shader.Dispose(); _gl.DeleteVertexArray(_vao); _gl.DeleteTexture(_texHeight); _gl.DeleteTexture(_texA); _gl.DeleteTexture(_texB); }
}
