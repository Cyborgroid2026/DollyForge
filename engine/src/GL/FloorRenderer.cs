using Silk.NET.OpenGL;
using System.Numerics;

namespace DollyForge;

/// <summary>
/// 스튜디오의 광택 검정 바닥 (y = 0, 방 크기만큼).
/// 그리는 순서: 반사층(패스 0) → 바닥 아래로 뒤집은 인형 → 바닥 자체(패스 1, 반투명).
/// </summary>
sealed class FloorRenderer : IDisposable
{
    readonly GL _gl;
    readonly Shader _shader;
    readonly uint _vao, _vbo;

    public unsafe FloorRenderer(GL gl)
    {
        _gl = gl;
        _shader = new Shader(gl, Shader.FloorVert, Shader.FloorFrag);
        Vector3 lo = EnvRenderer.RoomMin, hi = EnvRenderer.RoomMax;
        float[] quad = [lo.X, lo.Z, hi.X, lo.Z, lo.X, hi.Z, hi.X, hi.Z];

        _vao = gl.GenVertexArray();
        _vbo = gl.GenBuffer();
        gl.BindVertexArray(_vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* p = quad)
            gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(quad.Length * 4), p, BufferUsageARB.StaticDraw);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * 4, (void*)0);
        gl.BindVertexArray(0);
    }

    // 패스 0: 바닥에 비친 방 (깊이 기록 없음 — 뒤집힌 인형이 위에 덮인다)
    public void DrawReflection(EnvRenderer env, Matrix4x4 vp, Vector3 camPos)
    {
        Draw(env, vp, camPos, 0);
    }

    // 패스 1: 바닥 표면. 아래 그려진 반사층·뒤집힌 인형을 반사율만큼 비춰 보인다.
    public void DrawSurface(EnvRenderer env, Matrix4x4 vp, Vector3 camPos)
    {
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        Draw(env, vp, camPos, 1);
        _gl.Disable(EnableCap.Blend);
    }

    /// <summary>바닥에 빛나는 네 갈래 별을 그릴 자리 (최대 2개, 선택 화면의 인형 발밑)</summary>
    public Vector3[] Stars = [];

    void Draw(EnvRenderer env, Matrix4x4 vp, Vector3 camPos, int pass)
    {
        _shader.Use();
        env.Bind(_shader);
        _shader.Set("u_star_n", Math.Min(Stars.Length, 2));
        if (Stars.Length > 0) _shader.Set("u_star0", Stars[0]);
        if (Stars.Length > 1) _shader.Set("u_star1", Stars[1]);
        _shader.Set("u_vp", vp);
        _shader.Set("u_cam_pos", camPos);
        _shader.Set("u_pass", pass);
        _gl.DepthMask(pass == 1);
        _gl.BindVertexArray(_vao);
        _gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
        _gl.BindVertexArray(0);
        _gl.DepthMask(true);
    }

    public void Dispose()
    {
        _shader.Dispose();
        _gl.DeleteVertexArray(_vao);
        _gl.DeleteBuffer(_vbo);
    }
}
