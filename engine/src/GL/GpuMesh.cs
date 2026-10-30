using Silk.NET.OpenGL;
using System.Numerics;

namespace DollyForge;

sealed class GpuMesh : IDisposable
{
    readonly GL   _gl;
    readonly uint _vao, _vbo, _ebo;
    public int IndexCount { get; }

    public unsafe GpuMesh(GL gl, BodyMesh mesh)
    {
        _gl  = gl;
        var n = mesh.Vertices.Length;

        // 인터리브 버퍼: pos(3) + norm(3) + color(3) + bones(4i) + weights(4) = 17 floats + 4 ints per vertex
        // 정점당 stride = (3+3+3+4)*4 = 52 bytes float + 4*4 = 16 bytes int → 68 bytes
        // 간단히 float으로 bones도 저장 (정수 → float 캐스트, 셰이더에서 ivec4로 읽음)
        const int stride = (3 + 3 + 3 + 4 + 4) * sizeof(float);  // 68 bytes

        var buf = new float[n * 17];
        for (var i = 0; i < n; i++)
        {
            var v = mesh.Vertices[i]; var nm = mesh.Normals[i]; var c = mesh.Colors[i];
            var b0 = i*4; var off = i*17;
            buf[off+0]=v.X; buf[off+1]=v.Y; buf[off+2]=v.Z;
            buf[off+3]=nm.X; buf[off+4]=nm.Y; buf[off+5]=nm.Z;
            buf[off+6]=c.X; buf[off+7]=c.Y; buf[off+8]=c.Z;
            buf[off+9] =mesh.Bones[b0];   buf[off+10]=mesh.Bones[b0+1];
            buf[off+11]=mesh.Bones[b0+2]; buf[off+12]=mesh.Bones[b0+3];
            buf[off+13]=mesh.Weights[b0];   buf[off+14]=mesh.Weights[b0+1];
            buf[off+15]=mesh.Weights[b0+2]; buf[off+16]=mesh.Weights[b0+3];
        }

        _vao = gl.GenVertexArray(); _vbo = gl.GenBuffer(); _ebo = gl.GenBuffer();
        gl.BindVertexArray(_vao);

        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* p = buf)
            gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(buf.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);

        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        fixed (int* p = mesh.Indices)
            gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(mesh.Indices.Length * sizeof(int)), p, BufferUsageARB.StaticDraw);

        // layout=0: pos
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        // layout=1: norm
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3*4));
        // layout=2: color
        gl.EnableVertexAttribArray(2);
        gl.VertexAttribPointer(2, 3, VertexAttribPointerType.Float, false, stride, (void*)(6*4));
        // layout=3: bones (float 저장, ivec4로 읽기)
        gl.EnableVertexAttribArray(3);
        gl.VertexAttribIPointer(3, 4, VertexAttribIType.Int, (uint)stride, (void*)(9*4));
        // layout=4: weights
        gl.EnableVertexAttribArray(4);
        gl.VertexAttribPointer(4, 4, VertexAttribPointerType.Float, false, stride, (void*)(13*4));

        gl.BindVertexArray(0);
        IndexCount = mesh.Indices.Length;
    }

    public void Draw()
    {
        _gl.BindVertexArray(_vao);
        _gl.DrawElements(PrimitiveType.Triangles, (uint)IndexCount, DrawElementsType.UnsignedInt, 0);
        _gl.BindVertexArray(0);
    }

    public void Dispose()
    {
        _gl.DeleteVertexArray(_vao);
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteBuffer(_ebo);
    }
}
