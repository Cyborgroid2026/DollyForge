using Silk.NET.OpenGL;

namespace DollyForge;

/// <summary>
/// 화면 후처리: 레퍼런스 일러스트의 공기감.
///   블룸 = 밝은 하이라이트만 뽑아(문턱) 1/4 해상도에서 가우스 흐림 2회 → 원본에 더한다 (크롬 하이라이트·조명 띠의 은은한 번짐)
///   색보정 = 어두운 곳은 살짝 푸르게, 밝은 곳은 그대로 (스플릿 톤)
///   비네트 = 가장자리를 조금 어둡게 해서 시선을 인형으로
/// 장면은 이미 톤매핑된 LDR(RGBA8) 이므로 문턱도 0..1 값 기준이다.
/// </summary>
sealed class PostFx : IDisposable
{
    readonly GL _gl;
    readonly uint _vao, _vbo;
    readonly uint _bright, _blur, _final;
    uint _outFbo, _outTex, _fboA, _texA, _fboB, _texB;
    int _w, _h;

    public uint OutputFbo => _outFbo;

    const string Vert = """
        #version 330 core
        layout(location=0) in vec2 a_pos;
        out vec2 v_uv;
        void main() { v_uv = a_pos * 0.5 + 0.5; gl_Position = vec4(a_pos, 0.0, 1.0); }
        """;

    const string BrightFrag = """
        #version 330 core
        in vec2 v_uv; out vec4 frag;
        uniform sampler2D u_src;
        uniform vec2 u_texel;
        void main() {
            // 4 taps box downsample, then soft threshold on luminance
            vec3 c = 0.25 * (texture(u_src, v_uv + u_texel * vec2(-1, -1)).rgb + texture(u_src, v_uv + u_texel * vec2(1, -1)).rgb
                           + texture(u_src, v_uv + u_texel * vec2(-1, 1)).rgb + texture(u_src, v_uv + u_texel * vec2(1, 1)).rgb);
            float l = dot(c, vec3(0.2126, 0.7152, 0.0722));
            frag = vec4(c * smoothstep(0.55, 0.9, l), 1.0);
        }
        """;

    const string BlurFrag = """
        #version 330 core
        in vec2 v_uv; out vec4 frag;
        uniform sampler2D u_src;
        uniform vec2 u_dir;
        void main() {
            const float w[5] = float[](0.2270, 0.1945, 0.1216, 0.0541, 0.0162);
            vec3 c = texture(u_src, v_uv).rgb * w[0];
            for (int i = 1; i < 5; i++) {
                c += texture(u_src, v_uv + u_dir * float(i)).rgb * w[i];
                c += texture(u_src, v_uv - u_dir * float(i)).rgb * w[i];
            }
            frag = vec4(c, 1.0);
        }
        """;

    const string FinalFrag = """
        #version 330 core
        in vec2 v_uv; out vec4 frag;
        uniform sampler2D u_scene;
        uniform sampler2D u_bloom;
        uniform float u_aspect;
        void main() {
            vec3 c = texture(u_scene, v_uv).rgb;
            c += texture(u_bloom, v_uv).rgb * 0.35;
            float l = dot(c, vec3(0.2126, 0.7152, 0.0722));
            // split tone: cool blue in the shadows, neutral highlights
            c = mix(c * vec3(0.95, 0.99, 1.06), c, smoothstep(0.05, 0.6, l));
            // vignette
            vec2 q = (v_uv - 0.5) * vec2(u_aspect, 1.0);
            c *= mix(1.0, 0.72, smoothstep(0.35, 1.05, length(q)));
            frag = vec4(clamp(c, 0.0, 1.0), 1.0);
        }
        """;

    public unsafe PostFx(GL gl, int w, int h)
    {
        _gl = gl;
        _bright = Program(BrightFrag);
        _blur   = Program(BlurFrag);
        _final  = Program(FinalFrag);
        float[] quad = [-1f, -1f, 1f, -1f, -1f, 1f, 1f, 1f];
        _vao = gl.GenVertexArray();
        _vbo = gl.GenBuffer();
        gl.BindVertexArray(_vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* p = quad)
            gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(quad.Length * 4), p, BufferUsageARB.StaticDraw);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * 4, (void*)0);
        gl.BindVertexArray(0);
        Resize(w, h);
    }

    uint Program(string frag)
    {
        uint Sh(GLEnum t, string src)
        {
            var s = _gl.CreateShader(t);
            _gl.ShaderSource(s, src);
            _gl.CompileShader(s);
            _gl.GetShader(s, GLEnum.CompileStatus, out var ok);
            if (ok == 0) throw new Exception($"PostFx shader: {_gl.GetShaderInfoLog(s)}");
            return s;
        }
        var p = _gl.CreateProgram();
        var v = Sh(GLEnum.VertexShader, Vert);
        var f = Sh(GLEnum.FragmentShader, frag);
        _gl.AttachShader(p, v); _gl.AttachShader(p, f);
        _gl.LinkProgram(p);
        _gl.GetProgram(p, GLEnum.LinkStatus, out var lok);
        if (lok == 0) throw new Exception($"PostFx link: {_gl.GetProgramInfoLog(p)}");
        _gl.DeleteShader(v); _gl.DeleteShader(f);
        return p;
    }

    unsafe (uint Fbo, uint Tex) Target(int w, int h)
    {
        var tex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, tex);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)w, (uint)h, 0, PixelFormat.Rgba, PixelType.UnsignedByte, null);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        var fbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, tex, 0);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        return (fbo, tex);
    }

    void Release()
    {
        foreach (var f in new[] { _outFbo, _fboA, _fboB }) if (f != 0) _gl.DeleteFramebuffer(f);
        foreach (var t in new[] { _outTex, _texA, _texB }) if (t != 0) _gl.DeleteTexture(t);
    }

    public void Resize(int w, int h)
    {
        Release();
        _w = w; _h = h;
        (_outFbo, _outTex) = Target(w, h);
        (_fboA, _texA) = Target(Math.Max(1, w / 4), Math.Max(1, h / 4));
        (_fboB, _texB) = Target(Math.Max(1, w / 4), Math.Max(1, h / 4));
    }

    void Pass(uint prog, uint fbo, int w, int h, uint src, Action? set = null)
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        _gl.Viewport(0, 0, (uint)w, (uint)h);
        _gl.UseProgram(prog);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, src);
        _gl.Uniform1(_gl.GetUniformLocation(prog, "u_src"), 0);
        set?.Invoke();
        _gl.BindVertexArray(_vao);
        _gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
    }

    /// <summary>장면 텍스처 → 후처리 결과 (OutputFbo). 창 크기가 바뀌었으면 대상도 다시 만든다.</summary>
    public void Apply(uint sceneTex, int w, int h)
    {
        if (w != _w || h != _h) Resize(w, h);
        _gl.Disable(EnableCap.DepthTest);
        int qw = Math.Max(1, _w / 4), qh = Math.Max(1, _h / 4);
        Pass(_bright, _fboA, qw, qh, sceneTex, () => _gl.Uniform2(_gl.GetUniformLocation(_bright, "u_texel"), 1f / _w, 1f / _h));
        for (var i = 0; i < 2; i++)
        {
            Pass(_blur, _fboB, qw, qh, _texA, () => _gl.Uniform2(_gl.GetUniformLocation(_blur, "u_dir"), 1.6f / qw, 0f));
            Pass(_blur, _fboA, qw, qh, _texB, () => _gl.Uniform2(_gl.GetUniformLocation(_blur, "u_dir"), 0f, 1.6f / qh));
        }
        Pass(_final, _outFbo, _w, _h, sceneTex, () =>
        {
            _gl.Uniform1(_gl.GetUniformLocation(_final, "u_scene"), 0);
            _gl.ActiveTexture(TextureUnit.Texture1);
            _gl.BindTexture(TextureTarget.Texture2D, _texA);
            _gl.Uniform1(_gl.GetUniformLocation(_final, "u_bloom"), 1);
            _gl.Uniform1(_gl.GetUniformLocation(_final, "u_aspect"), (float)_w / _h);
        });
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindVertexArray(0);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _gl.Enable(EnableCap.DepthTest);
    }

    public void Dispose()
    {
        Release();
        _gl.DeleteProgram(_bright); _gl.DeleteProgram(_blur); _gl.DeleteProgram(_final);
        _gl.DeleteVertexArray(_vao); _gl.DeleteBuffer(_vbo);
    }
}
