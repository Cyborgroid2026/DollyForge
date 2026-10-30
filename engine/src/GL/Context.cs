using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using Silk.NET.Windowing.Sdl;
using System.Numerics;

namespace DollyForge;

/// <summary>SDL2 offscreen 창 + OpenGL 4.5 컨텍스트 + FBO (오프스크린 렌더링)</summary>
sealed class GlContext : IDisposable
{
    public  readonly IWindow Window;
    public  GL Gl { get; private set; } = null!;

    uint _fbo, _colorTex, _depthRbo;
    public int Width  { get; private set; }
    public int Height { get; private set; }

    bool _ready;

    public GlContext(int w, int h)
    {
        Width = w; Height = h;
        // SDL2 offscreen: 디스플레이 없이 OpenGL 컨텍스트 생성
        SdlWindowing.Use();
        var opts = WindowOptions.Default with
        {
            Title          = "DollyForge",
            Size           = new Vector2D<int>(w, h),
            IsVisible      = false,
            API            = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(4, 5)),
            VSync          = false,
            ShouldSwapAutomatically = false,
        };
        // SDL_VIDEODRIVER=offscreen 환경 변수로 디스플레이 없이 실행
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) &&
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
            Environment.SetEnvironmentVariable("SDL_VIDEODRIVER", "offscreen");
        Window = Silk.NET.Windowing.Window.Create(opts);
        Window.Load   += OnLoad;
        Window.Resize += OnResize;
    }

    void OnLoad()
    {
        Gl = Window.CreateOpenGL();
        Gl.Enable(EnableCap.DepthTest);
        // 컬링 끔: Surface Nets 메시 와인딩 검증 전까지 양면 렌더링
        Gl.Disable(EnableCap.CullFace);
        CreateFbo(Width, Height);
        _ready = true;
    }

    void OnResize(Vector2D<int> size)
    {
        Width = size.X; Height = size.Y;
        if (_ready) { DeleteFbo(); CreateFbo(Width, Height); }
    }

    unsafe void CreateFbo(int w, int h)
    {
        _fbo = Gl.GenFramebuffer();
        Gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);

        _colorTex = Gl.GenTexture();
        Gl.BindTexture(TextureTarget.Texture2D, _colorTex);
        Gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)w, (uint)h, 0, PixelFormat.Rgba, PixelType.UnsignedByte, null);
        Gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        Gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        Gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _colorTex, 0);

        _depthRbo = Gl.GenRenderbuffer();
        Gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _depthRbo);
        Gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.Depth24Stencil8, (uint)w, (uint)h);
        Gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthStencilAttachment, RenderbufferTarget.Renderbuffer, _depthRbo);

        var status = Gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete) throw new Exception($"FBO incomplete: {status}");
        Gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    void DeleteFbo()
    {
        Gl.DeleteFramebuffer(_fbo);
        Gl.DeleteTexture(_colorTex);
        Gl.DeleteRenderbuffer(_depthRbo);
    }

    public void BeginFrame()
    {
        Gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        Gl.Viewport(0, 0, (uint)Width, (uint)Height);
        Gl.ClearColor(0f, 0f, 0f, 1f);
        Gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
    }

    public unsafe byte[] ReadPixels()
    {
        Gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        var buf = new byte[Width * Height * 4];
        fixed (byte* p = buf)
            Gl.ReadPixels(0, 0, (uint)Width, (uint)Height, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        Gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        return buf;
    }

    public void Dispose()
    {
        if (_ready) DeleteFbo();
        Window.Dispose();
    }
}
