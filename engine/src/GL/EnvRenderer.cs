using Silk.NET.OpenGL;
using System.Numerics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DollyForge;

/// <summary>
/// 등장방형(equirectangular) 파노라마 텍스처를 스카이박스로 렌더링한다.
/// 실사 거리 사진(CC0 Poly Haven 등)을 배경으로 사용.
/// </summary>
sealed class EnvRenderer : IDisposable
{
    readonly GL   _gl;
    readonly uint _prog;
    readonly uint _vao, _vbo;
    readonly uint _tex;

    // 풀스크린 쿼드 (NDC −1..1)
    static readonly float[] Quad = [-1f,-1f, 1f,-1f, -1f,1f, 1f,1f];

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
        uniform sampler2D u_pano;
        uniform vec3 u_fwd;
        uniform vec3 u_right;
        uniform vec3 u_up;
        uniform float u_tan;
        uniform float u_asp;
        const float PI = 3.14159265;
        void main() {
            vec3 dir = normalize(u_fwd
                     + u_right * (v_pos.x * u_asp * u_tan)
                     + u_up    * (v_pos.y * u_tan));
            float u = atan(dir.z, dir.x) / (2.0 * PI) + 0.5;
            float v = asin(clamp(dir.y, -0.9999, 0.9999)) / PI + 0.5;
            frag = texture(u_pano, vec2(u, 1.0 - v));
        }
        """;

    int _loc_pano, _loc_fwd, _loc_right, _loc_up, _loc_tan, _loc_asp;

    string[] _files = [];
    int      _cur   = 0;

    public unsafe EnvRenderer(GL gl, string assetDir)
    {
        _gl = gl;

        // 셰이더 컴파일
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

        _loc_pano  = gl.GetUniformLocation(_prog, "u_pano");
        _loc_fwd   = gl.GetUniformLocation(_prog, "u_fwd");
        _loc_right = gl.GetUniformLocation(_prog, "u_right");
        _loc_up    = gl.GetUniformLocation(_prog, "u_up");
        _loc_tan   = gl.GetUniformLocation(_prog, "u_tan");
        _loc_asp   = gl.GetUniformLocation(_prog, "u_asp");

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

        // 텍스처 슬롯 할당
        _tex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, _tex);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS,     (int)GLEnum.Repeat);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT,     (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.LinearMipmapLinear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        gl.BindTexture(TextureTarget.Texture2D, 0);

        // 파노라마 파일 목록 로드
        if (Directory.Exists(assetDir))
        {
            _files = Directory.GetFiles(assetDir, "*.jpg")
                               .Concat(Directory.GetFiles(assetDir, "*.png"))
                               .OrderBy(f => f)
                               .ToArray();
            Console.WriteLine($"[env] 파노라마 {_files.Length}개 발견");
        }

        if (_files.Length > 0)
            LoadPanorama(_files[0]);
        else
            LoadFallbackGradient();
    }

    // 다음 파노라마로 전환 (N 키)
    public void NextPanorama()
    {
        if (_files.Length == 0) return;
        _cur = (_cur + 1) % _files.Length;
        LoadPanorama(_files[_cur]);
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
            Console.WriteLine($"[env] 로드 완료 {w}x{h}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[env] 파노라마 로드 실패: {ex.Message}");
            LoadFallbackGradient();
        }
    }

    unsafe void LoadFallbackGradient()
    {
        // 파노라마 없을 때: 하늘+땅 그라디언트 (2x2 텍스처)
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

    // 스카이박스 렌더링 (depth write 끄고 가장 먼저)
    public void Draw(Vector3 fwd, Vector3 right, Vector3 up, float fovTan, float aspect)
    {
        _gl.UseProgram(_prog);
        _gl.Disable(EnableCap.DepthTest);
        _gl.DepthMask(false);

        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _tex);
        _gl.Uniform1(_loc_pano, 0);
        _gl.Uniform3(_loc_fwd,   fwd.X,   fwd.Y,   fwd.Z);
        _gl.Uniform3(_loc_right, right.X, right.Y, right.Z);
        _gl.Uniform3(_loc_up,    up.X,    up.Y,    up.Z);
        _gl.Uniform1(_loc_tan,  fovTan);
        _gl.Uniform1(_loc_asp,  aspect);

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
    }
}
