using Silk.NET.OpenGL;
using System.Numerics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DollyForge;

/// <summary>
/// 배경 + 반사 환경.
/// 모드 0 = 절차적 사이버 스튜디오(회로 패턴 벽·도시 야경 창·천장 조명·뒤쪽 소프트박스).
///   벽은 assets/studio/walls.jpg (로컬 FLUX.1-schnell 로 생성한 회로 패널, tools/gen-studio-walls.sh) 가 있으면 그 그림.
///   창밖은 assets/studio/city_window.jpg (로컬 FLUX.1-schnell 로 생성한 저녁 도시, tools/imagegen.py) 가 있으면 그 사진, 없으면 절차적 야경.
/// 모드 1 = 등장방형 파노라마 사진(CC0 Poly Haven). N 키로 순환한다.
/// 같은 GLSL(EnvGlsl)을 몸·바닥 셰이더가 공유해서 크롬 반사가 배경과 일치한다.
/// </summary>
sealed class EnvRenderer : IDisposable
{
    readonly GL   _gl;
    readonly uint _prog;
    readonly uint _vao, _vbo;
    readonly uint _tex;
    readonly uint _cityTex, _wallTex, _matcapTex;
    readonly bool _cityOn, _wallOn, _matcapOn;

    // 풀스크린 쿼드 (NDC −1..1)
    static readonly float[] Quad = [-1f,-1f, 1f,-1f, -1f,1f, 1f,1f];

    // 스튜디오 방 크기 (World 가 카메라·인형을 이 안에 묶는다)
    public static readonly Vector3 RoomMin = new(-7f, 0f, -5f);
    public static readonly Vector3 RoomMax = new( 7f, 4.2f, 7f);

    // GLSL 안에는 ASCII 만 (Mesa 파서가 한글 주석에서 실패한다)
    public const string EnvGlsl = """
        uniform sampler2D u_pano;
        uniform int u_env_mode;
        uniform sampler2D u_city;
        uniform int u_city_on;
        uniform sampler2D u_wall;
        uniform int u_wall_on;
        uniform sampler2D u_matcap;
        uniform int u_matcap_on;

        const float PI_E = 3.14159265;
        const vec3 ROOM_MIN = vec3(-7.0, 0.0, -5.0);
        const vec3 ROOM_MAX = vec3( 7.0, 4.2,  7.0);

        float hash12(vec2 p) {
            vec3 p3 = fract(vec3(p.xyx) * 0.1031);
            p3 += dot(p3, p3.yzx + 33.33);
            return fract((p3.x + p3.y) * p3.z);
        }
        float band(float d, float w, float aa) { return 1.0 - smoothstep(w, w + aa, abs(d)); }
        float rect(vec2 p, vec2 lo, vec2 hi, float aa) {
            vec2 a = smoothstep(lo - aa, lo + aa, p) * (1.0 - smoothstep(hi - aa, hi + aa, p));
            return a.x * a.y;
        }

        vec3 circuitWall(vec2 uv, float aa) {
            vec3 col = vec3(0.016, 0.018, 0.022);
            vec2 ps = vec2(1.4, 1.05);
            vec2 g = abs(fract(uv / ps) - 0.5) * ps;
            float seamD = min(ps.x * 0.5 - g.x, ps.y * 0.5 - g.y);
            col += vec3(0.035) * band(seamD, 0.006, aa);

            vec2 bc = uv / 0.9;
            vec2 bf = fract(bc);
            float hb = hash12(floor(bc) + 51.0);
            float chip = hb > 0.62 ? rect(bf, vec2(0.22, 0.30), vec2(0.62, 0.60), aa / 0.9 + 0.004) : 0.0;

            vec2 cs = uv / 0.3;
            vec2 ci = floor(cs);
            vec2 cf = fract(cs) - 0.5;
            float h1 = hash12(ci), h2 = hash12(ci + 7.31), h3 = hash12(ci + 19.7);
            float rowH = hash12(vec2(ci.y, 3.1));
            float a2 = aa / 0.3 + 0.01;
            float tr = 0.0;
            if (rowH > 0.45 && h1 > 0.25) tr = max(tr, band(cf.y - (rowH - 0.7) * 0.5, 0.03, a2));
            if (h2 > 0.70) tr = max(tr, band(cf.x - (h3 - 0.5) * 0.6, 0.03, a2) * step(cf.y, h1 - 0.5));
            if (h3 > 0.78) tr = max(tr, 1.0 - smoothstep(0.08, 0.08 + a2, length(cf - vec2((h3 - 0.5) * 0.6, (rowH - 0.7) * 0.5))));

            col = mix(col, vec3(0.008, 0.009, 0.011), chip);
            col += chip * band(bf.x - 0.22, 0.004, a2) * vec3(0.05);
            col += tr * (1.0 - chip) * vec3(0.030, 0.033, 0.040);
            float glowRow = step(0.94, hash12(vec2(ci.y, 9.7))) * step(0.5, hash12(vec2(floor(ci.x / 6.0), ci.y)));
            col += tr * (1.0 - chip) * glowRow * vec3(0.35, 0.45, 0.70);
            return col;
        }

        vec3 cityWindow(vec2 uv) {
            vec3 col = mix(vec3(0.06, 0.09, 0.18), vec3(0.015, 0.02, 0.05), uv.y);
            float ci = floor(uv.x / 0.045);
            float hgt = (0.25 + 0.65 * hash12(vec2(ci, 1.0))) * (0.55 + 0.45 * hash12(vec2(floor(uv.x / 0.16), 2.0)));
            if (uv.y < hgt) {
                col = vec3(0.012, 0.018, 0.035);
                vec2 wg = vec2(uv.x / 0.0075, uv.y / 0.011);
                float lit = step(0.6, hash12(floor(wg) + ci * 13.0));
                float win = rect(fract(wg), vec2(0.25), vec2(0.75), 0.1);
                col += lit * win * vec3(0.55, 0.65, 0.95);
            }
            if (uv.y < hgt + 0.03 && uv.y > hgt && hash12(vec2(ci, 5.0)) > 0.7)
                col += vec3(0.6, 0.7, 1.0) * band(uv.x - (ci + 0.5) * 0.045, 0.002, 0.002);
            return col;
        }

        // window view photo, treated as infinitely far: sampled by ray direction (pinhole aimed CITY_YAW right of -Z,
        // toward the window from mid-room), so it does not slide like a poster when the camera walks.
        // eye level maps to CITY_EYE of the image height, which keeps the ridge line and the lit blocks in the window.
        const float CITY_YAW = 0.5;
        const float CITY_TAN_X = 1.0;
        const float CITY_TAN_Y = 0.619;
        const float CITY_EYE = 0.57;
        vec3 cityPhoto(vec3 rd, float aa) {
            vec3 f = vec3(sin(CITY_YAW), 0.0, -cos(CITY_YAW));
            vec3 r = vec3(cos(CITY_YAW), 0.0, sin(CITY_YAW));
            float d = max(dot(rd, f), 0.05);
            vec2 q = vec2(dot(rd, r), rd.y) / d;
            vec2 uv = vec2(0.5 + q.x / (2.0 * CITY_TAN_X), CITY_EYE - q.y / (2.0 * CITY_TAN_Y));
            float lod = clamp(log2(aa * 672.0 / CITY_TAN_X + 1e-6), 0.0, 6.0);
            return pow(textureLod(u_city, uv, lod).rgb, vec3(2.2)) * 0.6;
        }

        // AI-generated wall panels (assets/studio/walls.jpg): top half = back wall (14 m), bottom half = side/front walls (12-14 m).
        // u spans the whole wall, v spans floor..ceiling, so each wall shows one painting without tiling.
        vec3 wallPhoto(float u, float y, bool back, float aa, float width) {
            float v = (1.0 - clamp(y / ROOM_MAX.y, 0.0, 1.0)) * 0.5 + (back ? 0.0 : 0.5);
            v = clamp(v, back ? 0.002 : 0.502, back ? 0.498 : 0.998);
            float lod = clamp(log2(aa * 1792.0 / width + 1e-6), 0.0, 6.0);
            return pow(textureLod(u_wall, vec2(clamp(u, 0.0, 1.0), v), lod).rgb, vec3(2.2)) * 1.8;
        }

        vec3 studio(vec3 ro, vec3 rd, float aa) {
            ro = clamp(ro, ROOM_MIN + 0.02, ROOM_MAX - 0.02);
            rd = normalize(rd + vec3(1e-5));
            vec3 inv = 1.0 / rd;
            vec3 tMax = max((ROOM_MIN - ro) * inv, (ROOM_MAX - ro) * inv);
            float t = min(min(tMax.x, tMax.y), tMax.z);
            vec3 p = ro + rd * t;
            float aw = aa * t + 0.002;
            vec3 col;
            if (t == tMax.y) {
                if (rd.y > 0.0) {
                    col = vec3(0.012, 0.013, 0.016);
                    float s = 0.0;
                    for (int i = -1; i <= 1; i++)
                        s = max(s, band(p.x - float(i) * 3.0, 0.10, aw) * step(abs(p.z - 1.0), 4.5));
                    col += s * vec3(3.2, 3.2, 3.5);
                } else {
                    vec2 g = abs(fract(p.xz / 1.5) - 0.5) * 1.5;
                    col = vec3(0.006, 0.0065, 0.008) + vec3(0.02) * band(0.75 - max(g.x, g.y), 0.004, aw);
                }
            } else if (t == tMax.x) {
                vec2 uv = vec2(p.z * sign(p.x), p.y);
                if (u_wall_on == 1) {
                    col = wallPhoto((p.z - ROOM_MIN.z) / (ROOM_MAX.z - ROOM_MIN.z), p.y, false, aw / t, ROOM_MAX.z - ROOM_MIN.z);
                } else {
                    col = circuitWall(uv, aw);
                    float s = max(band(p.z + 1.0, 0.05, aw), band(p.z - 3.0, 0.05, aw)) * step(0.3, p.y);
                    col += s * vec3(2.6, 2.7, 3.0);
                }
            } else if (rd.z < 0.0) {
                vec2 uv = p.xy;
                col = u_wall_on == 1 ? wallPhoto((p.x - ROOM_MIN.x) / (ROOM_MAX.x - ROOM_MIN.x), p.y, true, aw / t, ROOM_MAX.x - ROOM_MIN.x)
                                     : circuitWall(uv, aw);
                vec2 lo = vec2(1.6, 0.35), hi = vec2(5.8, 2.9);
                float w = rect(uv, lo, hi, aw);
                float fr = rect(uv, lo - 0.06, hi + 0.06, aw) * (1.0 - w);
                col = mix(col, vec3(0.05, 0.055, 0.06), fr);
                col = mix(col, u_city_on == 1 ? cityPhoto(rd, aa) : cityWindow((uv - lo) / (hi - lo)), w);
                float gl = band(p.x + 3.2, 0.02, aw) * step(1.1, p.y)
                         + band(p.y - 3.4, 0.02, aw) * step(-3.2, p.x) * step(p.x, 0.9)
                         + band(p.x - 0.9, 0.02, aw) * step(0.6, p.y) * step(p.y, 3.4);
                if (u_wall_on == 0) col += min(gl, 1.0) * vec3(1.6, 1.8, 2.3);
            } else {
                vec2 uv = p.xy;
                col = u_wall_on == 1 ? wallPhoto(1.0 - (p.x - ROOM_MIN.x) / (ROOM_MAX.x - ROOM_MIN.x), p.y, false, aw / t, ROOM_MAX.x - ROOM_MIN.x) * 0.7
                                     : circuitWall(uv, aw) * 0.6;
                if (u_wall_on == 0) col += rect(uv, vec2(-1.4, 1.2), vec2(1.4, 3.2), 0.25) * vec3(0.30, 0.32, 0.36);   // softbox (mirror chrome only)
            }
            return col;
        }

        vec3 envColor(vec3 ro, vec3 rd, float aa);

        // low-frequency studio light used for glossy reflections (large soft boxes)
        // camBack = direction from the subject toward the viewer, so the key box always sits behind the camera
        vec3 studioSoft(vec3 rd, vec3 camBack) {
            vec3 b = normalize(vec3(camBack.x, 0.0, camBack.z));
            vec3 side = vec3(-b.z, 0.0, b.x);
            float front = max(dot(rd, b), 0.0);
            // satin gunmetal look of the reference: dim front box, bright side strips -> rim highlights along limbs
            vec3 c = vec3(0.04, 0.043, 0.05);
            c += vec3(0.42, 0.44, 0.48) * pow(front, 3.0) * smoothstep(-0.6, 0.2, rd.y);
            c += vec3(0.62, 0.62, 0.66) * pow(max(rd.y, 0.0), 4.0);
            c += vec3(0.80, 0.88, 1.05) * pow(abs(dot(rd, side)), 6.0) * smoothstep(-0.1, 0.3, rd.y);
            c += vec3(0.12, 0.14, 0.20) * pow(max(-dot(rd, b), 0.0), 2.0) * smoothstep(-0.2, 0.2, rd.y);
            c *= mix(0.6, 1.0, smoothstep(-0.45, 0.05, rd.y));
            return c;
        }

        vec3 envGlossy(vec3 ro, vec3 rd, vec3 camBack, float sharp) {
            if (u_env_mode == 1) return envColor(ro, rd, 0.03);
            return mix(studioSoft(rd, camBack), studio(ro, rd, 0.03), sharp);
        }

        vec3 envColor(vec3 ro, vec3 rd, float aa) {
            if (u_env_mode == 1) {
                float u = atan(rd.z, rd.x) / (2.0 * PI_E) + 0.5;
                float v = asin(clamp(rd.y, -0.9999, 0.9999)) / PI_E + 0.5;
                vec3 c = textureLod(u_pano, vec2(u, 1.0 - v), clamp(aa * 300.0, 0.0, 6.0)).rgb;
                return pow(c, vec3(2.2)) * 1.25;
            }
            return studio(ro, rd, aa);
        }

        vec3 tonemap(vec3 c) {
            c = (c * (2.51 * c + 0.03)) / (c * (2.43 * c + 0.59) + 0.14);
            return pow(clamp(c, 0.0, 1.0), vec3(1.0 / 2.2));
        }
        """;

    const string Vert = """
        #version 330 core
        layout(location=0) in vec2 a_pos;
        out vec2 v_pos;
        void main() { v_pos = a_pos; gl_Position = vec4(a_pos, 0.999, 1.0); }
        """;

    const string Frag = """
        #version 330 core
        in vec2 v_pos;
        out vec4 frag;
        uniform vec3 u_cam_pos;
        uniform vec3 u_fwd;
        uniform vec3 u_right;
        uniform vec3 u_up;
        uniform float u_tan;
        uniform float u_asp;
        uniform float u_pix;
        """ + EnvGlsl + """

        void main() {
            vec3 dir = normalize(u_fwd
                     + u_right * (v_pos.x * u_asp * u_tan)
                     + u_up    * (v_pos.y * u_tan));
            frag = vec4(tonemap(envColor(u_cam_pos, dir, u_pix)), 1.0);
        }
        """;

    string[] _files = [];
    int      _cur   = -1;   // -1 = 스튜디오, 0.. = 파노라마

    public int Mode => _cur < 0 ? 0 : 1;

    public unsafe EnvRenderer(GL gl, string assetDir)
    {
        _gl = gl;

        uint Sh(GLEnum t, string src)
        {
            var s = gl.CreateShader(t);
            gl.ShaderSource(s, src);
            gl.CompileShader(s);
            gl.GetShader(s, GLEnum.CompileStatus, out var ok);
            if (ok == 0) throw new Exception($"EnvShader: {gl.GetShaderInfoLog(s)}");
            return s;
        }
        _prog = gl.CreateProgram();
        var v = Sh(GLEnum.VertexShader,   Vert);
        var f = Sh(GLEnum.FragmentShader, Frag);
        gl.AttachShader(_prog, v); gl.AttachShader(_prog, f);
        gl.LinkProgram(_prog);
        gl.GetProgram(_prog, GLEnum.LinkStatus, out var lok);
        if (lok == 0) throw new Exception($"EnvProg: {gl.GetProgramInfoLog(_prog)}");
        gl.DeleteShader(v); gl.DeleteShader(f);

        // 풀스크린 쿼드 VAO
        _vao = gl.GenVertexArray();
        _vbo = gl.GenBuffer();
        gl.BindVertexArray(_vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* p = Quad)
            gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(Quad.Length * 4), p, BufferUsageARB.StaticDraw);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2*4, (void*)0);
        gl.BindVertexArray(0);

        _tex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, _tex);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS,     (int)GLEnum.Repeat);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT,     (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.LinearMipmapLinear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        gl.BindTexture(TextureTarget.Texture2D, 0);
        LoadFallbackGradient();

        var studioDir = Path.Combine(assetDir, "..", "studio");
        _cityTex = gl.GenTexture();
        _cityOn  = LoadStudioTexture(_cityTex, Path.Combine(studioDir, "city_window.jpg"));
        _wallTex = gl.GenTexture();
        _wallOn  = LoadStudioTexture(_wallTex, Path.Combine(studioDir, "walls.jpg"));
        _matcapTex = gl.GenTexture();
        _matcapOn  = LoadStudioTexture(_matcapTex, Path.Combine(studioDir, "matcap.png"));

        if (Directory.Exists(assetDir))
        {
            _files = Directory.GetFiles(assetDir, "*.jpg")
                               .Concat(Directory.GetFiles(assetDir, "*.png"))
                               .OrderBy(f => f)
                               .ToArray();
        }
        Console.WriteLine($"[env] 스튜디오{(_cityOn ? "(창밖 사진)" : "")}{(_wallOn ? "(벽 그림)" : "")}{(_matcapOn ? "(매트캡)" : "")} + 파노라마 {_files.Length}개 (N 키 순환)");
    }

    // 스튜디오용 사진 (가장자리 고정, 밉맵). 없으면 false → 셰이더가 절차적 그림을 쓴다
    unsafe bool LoadStudioTexture(uint tex, string path)
    {
        if (!File.Exists(path)) return false;
        using var img = Image.Load<Rgb24>(path);
        var buf = new byte[img.Width * img.Height * 3];
        img.CopyPixelDataTo(buf);
        _gl.BindTexture(TextureTarget.Texture2D, tex);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS,     (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT,     (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.LinearMipmapLinear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        fixed (byte* p = buf)
            _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgb8,
                (uint)img.Width, (uint)img.Height, 0, PixelFormat.Rgb, PixelType.UnsignedByte, p);
        _gl.GenerateMipmap(TextureTarget.Texture2D);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
        return true;
    }

    // 스튜디오 → 파노라마 1 → … → 스튜디오 (N 키)
    public void NextPanorama()
    {
        _cur = _cur + 1 >= _files.Length ? -1 : _cur + 1;
        if (_cur >= 0) LoadPanorama(_files[_cur]);
        else Console.WriteLine("[env] 사이버 스튜디오");
    }

    // 다른 셰이더가 EnvGlsl 을 쓸 때 텍스처·모드를 묶는다 (파노라마 unit, 창밖 사진 unit+1)
    public void Bind(Shader s, int unit = 1)
    {
        _gl.ActiveTexture(TextureUnit.Texture0 + unit);
        _gl.BindTexture(TextureTarget.Texture2D, _tex);
        _gl.ActiveTexture(TextureUnit.Texture0 + unit + 1);
        _gl.BindTexture(TextureTarget.Texture2D, _cityTex);
        s.Set("u_pano", unit);
        s.Set("u_city", unit + 1);
        s.Set("u_city_on", _cityOn ? 1 : 0);
        _gl.ActiveTexture(TextureUnit.Texture0 + unit + 2);
        _gl.BindTexture(TextureTarget.Texture2D, _wallTex);
        s.Set("u_wall", unit + 2);
        s.Set("u_wall_on", _wallOn ? 1 : 0);
        _gl.ActiveTexture(TextureUnit.Texture0 + unit + 3);
        _gl.BindTexture(TextureTarget.Texture2D, _matcapTex);
        s.Set("u_matcap", unit + 3);
        s.Set("u_matcap_on", _matcapOn ? 1 : 0);
        s.Set("u_env_mode", Mode);
    }

    unsafe void LoadPanorama(string path)
    {
        Console.WriteLine($"[env] 파노라마 로드: {Path.GetFileName(path)}");
        try
        {
            using var img = Image.Load<Rgb24>(path);
            var w = img.Width; var h = img.Height;
            var buf = new byte[w * h * 3];
            img.CopyPixelDataTo(buf);
            _gl.BindTexture(TextureTarget.Texture2D, _tex);
            fixed (byte* p = buf)
                _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgb8,
                    (uint)w, (uint)h, 0, PixelFormat.Rgb, PixelType.UnsignedByte, p);
            _gl.GenerateMipmap(TextureTarget.Texture2D);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[env] 파노라마 로드 실패: {ex.Message}");
            LoadFallbackGradient();
        }
    }

    unsafe void LoadFallbackGradient()
    {
        // 파노라마가 없을 때: 하늘+땅 그라디언트 (2x2 텍스처)
        byte[] px = [
            108,166,210,  108,166,210,
             72, 72, 74,   72, 72, 74
        ];
        _gl.BindTexture(TextureTarget.Texture2D, _tex);
        fixed (byte* p = px)
            _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgb8,
                2, 2, 0, PixelFormat.Rgb, PixelType.UnsignedByte, p);
        _gl.GenerateMipmap(TextureTarget.Texture2D);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
    }

    // 배경 렌더링 (depth write 끄고 가장 먼저)
    public void Draw(Vector3 camPos, Vector3 fwd, Vector3 right, Vector3 up, float fovTan, float aspect, int heightPx)
    {
        _gl.UseProgram(_prog);
        _gl.Disable(EnableCap.DepthTest);
        _gl.DepthMask(false);

        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _tex);
        _gl.Uniform1(_gl.GetUniformLocation(_prog, "u_pano"), 0);
        _gl.ActiveTexture(TextureUnit.Texture1);
        _gl.BindTexture(TextureTarget.Texture2D, _cityTex);
        _gl.Uniform1(_gl.GetUniformLocation(_prog, "u_city"), 1);
        _gl.Uniform1(_gl.GetUniformLocation(_prog, "u_city_on"), _cityOn ? 1 : 0);
        _gl.ActiveTexture(TextureUnit.Texture2);
        _gl.BindTexture(TextureTarget.Texture2D, _wallTex);
        _gl.Uniform1(_gl.GetUniformLocation(_prog, "u_wall"), 2);
        _gl.Uniform1(_gl.GetUniformLocation(_prog, "u_wall_on"), _wallOn ? 1 : 0);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.Uniform1(_gl.GetUniformLocation(_prog, "u_env_mode"), Mode);
        _gl.Uniform3(_gl.GetUniformLocation(_prog, "u_cam_pos"), camPos.X, camPos.Y, camPos.Z);
        _gl.Uniform3(_gl.GetUniformLocation(_prog, "u_fwd"),   fwd.X,   fwd.Y,   fwd.Z);
        _gl.Uniform3(_gl.GetUniformLocation(_prog, "u_right"), right.X, right.Y, right.Z);
        _gl.Uniform3(_gl.GetUniformLocation(_prog, "u_up"),    up.X,    up.Y,    up.Z);
        _gl.Uniform1(_gl.GetUniformLocation(_prog, "u_tan"), fovTan);
        _gl.Uniform1(_gl.GetUniformLocation(_prog, "u_asp"), aspect);
        _gl.Uniform1(_gl.GetUniformLocation(_prog, "u_pix"), 2f * fovTan / heightPx);

        _gl.BindVertexArray(_vao);
        _gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
        _gl.BindVertexArray(0);

        _gl.DepthMask(true);
        _gl.Enable(EnableCap.DepthTest);
    }

    public void Dispose()
    {
        _gl.DeleteProgram(_prog);
        _gl.DeleteVertexArray(_vao);
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteTexture(_tex);
        _gl.DeleteTexture(_cityTex);
        _gl.DeleteTexture(_wallTex);
        _gl.DeleteTexture(_matcapTex);
    }
}
