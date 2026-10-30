using Silk.NET.OpenGL;
using System.Numerics;

namespace DollyForge;

sealed class GpuMesh : IDisposable
{
    readonly GL   _gl;
    readonly uint _vao, _vbo, _bbo, _ebo;  // vbo=float, bbo=bone-int, ebo=index
    public int IndexCount { get; }

    public unsafe GpuMesh(GL gl, BodyMesh mesh)
    {
        _gl  = gl;
        var n = mesh.Vertices.Length;

        // float VBO: pos(3) + norm(3) + color(3) + weights(4) + seam(1) = 14 floats/vert
        const int fStride = 14 * sizeof(float);
        var fBuf = new float[n * 14];
        for (var i = 0; i < n; i++)
        {
            var v = mesh.Vertices[i];
            var nm = mesh.Normals[i];
            var c  = mesh.Colors[i];
            var b0 = i * 4; var off = i * 14;
            fBuf[off+0]=v.X;  fBuf[off+1]=v.Y;  fBuf[off+2]=v.Z;
            fBuf[off+3]=nm.X; fBuf[off+4]=nm.Y; fBuf[off+5]=nm.Z;
            fBuf[off+6]=c.X;  fBuf[off+7]=c.Y;  fBuf[off+8]=c.Z;
            fBuf[off+9] =mesh.Weights[b0];
            fBuf[off+10]=mesh.Weights[b0+1];
            fBuf[off+11]=mesh.Weights[b0+2];
            fBuf[off+12]=mesh.Weights[b0+3];
            fBuf[off+13]=mesh.Seams.Length > i ? mesh.Seams[i] : 1f;
        }

        // int VBO: bones(4 int) — 별도 버퍼로 비트패턴 보존
        var iBuf = new int[n * 4];
        for (var i = 0; i < n; i++)
        {
            var b0 = i * 4;
            iBuf[b0]   = mesh.Bones[b0];
            iBuf[b0+1] = mesh.Bones[b0+1];
            iBuf[b0+2] = mesh.Bones[b0+2];
            iBuf[b0+3] = mesh.Bones[b0+3];
        }

        _vao = gl.GenVertexArray();
        _vbo = gl.GenBuffer();
        _bbo = gl.GenBuffer();
        _ebo = gl.GenBuffer();
        gl.BindVertexArray(_vao);

        // float 버퍼 바인딩
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* p = fBuf)
            gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(fBuf.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);

        // layout=0: pos
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, fStride, (void*)0);
        // layout=1: norm
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, fStride, (void*)(3*4));
        // layout=2: color
        gl.EnableVertexAttribArray(2);
        gl.VertexAttribPointer(2, 3, VertexAttribPointerType.Float, false, fStride, (void*)(6*4));
        // layout=4: weights (bones는 별도 버퍼이므로 먼저 weights)
        gl.EnableVertexAttribArray(4);
        gl.VertexAttribPointer(4, 4, VertexAttribPointerType.Float, false, fStride, (void*)(9*4));
        // layout=5: seam distance
        gl.EnableVertexAttribArray(5);
        gl.VertexAttribPointer(5, 1, VertexAttribPointerType.Float, false, fStride, (void*)(13*4));

        // int 버퍼 바인딩 (bones)
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _bbo);
        fixed (int* p = iBuf)
            gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(iBuf.Length * sizeof(int)), p, BufferUsageARB.StaticDraw);

        // layout=3: bones (ivec4, 실제 int 비트패턴)
        gl.EnableVertexAttribArray(3);
        gl.VertexAttribIPointer(3, 4, VertexAttribIType.Int, 4 * sizeof(int), (void*)0);

        // 인덱스 버퍼
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        fixed (int* p = mesh.Indices)
            gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(mesh.Indices.Length * sizeof(int)), p, BufferUsageARB.StaticDraw);

        gl.BindVertexArray(0);
        IndexCount = mesh.Indices.Length;
    }

    public unsafe void Draw()
    {
        _gl.BindVertexArray(_vao);
        // (void*)0 = EBO byte offset; a bare 0 binds Silk's `in T` overload and passes a stack address
        _gl.DrawElements(PrimitiveType.Triangles, (uint)IndexCount, DrawElementsType.UnsignedInt, (void*)0);
        _gl.BindVertexArray(0);
    }

    public void Dispose()
    {
        _gl.DeleteVertexArray(_vao);
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteBuffer(_bbo);
        _gl.DeleteBuffer(_ebo);
    }
}
