using System.Numerics;

namespace DollyForge;

/// <summary>정적 메시 (세계·집·가구): 위치·법선·색(rgb + 풀 표시 a)·빛(굽기: 그늘·해·하늘·전등)</summary>
sealed class StaticMeshData
{
    public List<Vector3> Pos = [], Nrm = [];
    public List<Vector4> Col = [];
    public List<Vector4> Light = [];     // x 오목 그늘(AO), y 해 보임, z 하늘 보임, w 전등 빛
    public List<int> Idx = [];
    public int VertexCount => Pos.Count;
}

/// <summary>
/// Surface Nets: 거리장의 부호가 바뀌는 칸마다 정점 하나(모서리 교차점의 평균)를 두고, 부호가 바뀌는 모서리마다 사각형을 잇는다.
/// 블록의 계단 없이 곡면이 나온다. 섹션 경계의 틈을 막으려고 표본을 −1 … n+1 까지 뽑고, 기준 표본이 [0, n) 인 모서리만 면을 만든다:
/// 이웃 섹션도 같은 결정적 표본으로 같은 정점 위치를 얻으므로 경계에서 정확히 이어진다.
/// 법선은 거리장 기울기, 색은 재질 함수에서 정점마다 계산한다 (재질 경계도 정점 사이에서 부드럽게 섞인다).
/// </summary>
static class SurfaceNets
{
    static readonly int[,] Corners = { { 0, 0, 0 }, { 1, 0, 0 }, { 0, 1, 0 }, { 1, 1, 0 }, { 0, 0, 1 }, { 1, 0, 1 }, { 0, 1, 1 }, { 1, 1, 1 } };
    static readonly int[,] Edges = { { 0, 1 }, { 2, 3 }, { 4, 5 }, { 6, 7 }, { 0, 2 }, { 1, 3 }, { 4, 6 }, { 5, 7 }, { 0, 4 }, { 1, 5 }, { 2, 6 }, { 3, 7 } };

    /// <param name="origin">표본 (0,0,0) 의 세계 위치</param>
    /// <param name="n">칸 수 (x, y, z)</param>
    public static StaticMeshData Mesh(Func<Vector3, float> sdf, Vector3 origin, float step, (int X, int Y, int Z) n,
                                      Func<Vector3, Vector3> normal, Func<Vector3, Vector4> color, bool parallel = true)
    {
        int sx = n.X + 3, sy = n.Y + 3, sz = n.Z + 3;           // 표본 −1 … n+1
        var g = new float[sx * sy * sz];
        void Slice(int k)
        {
            for (var j = 0; j < sy; j++)
                for (var i = 0; i < sx; i++)
                    g[(k * sy + j) * sx + i] = sdf(origin + new Vector3(i - 1, j - 1, k - 1) * step);
        }
        if (parallel) Parallel.For(0, sz, Slice); else for (var k = 0; k < sz; k++) Slice(k);
        float G(int i, int j, int k) => g[((k + 1) * sy + (j + 1)) * sx + (i + 1)];

        // 칸 −1 … n (아래 모서리 표본 번호) 마다 정점
        int cx = n.X + 2, cy = n.Y + 2, cz = n.Z + 2;
        var cellVert = new int[cx * cy * cz];
        Array.Fill(cellVert, -1);
        var mesh = new StaticMeshData();
        var corner = new float[8];
        for (var k = -1; k <= n.Z; k++)
            for (var j = -1; j <= n.Y; j++)
                for (var i = -1; i <= n.X; i++)
                {
                    var mask = 0;
                    for (var c = 0; c < 8; c++)
                    {
                        corner[c] = G(i + Corners[c, 0], j + Corners[c, 1], k + Corners[c, 2]);
                        if (corner[c] < 0) mask |= 1 << c;
                    }
                    if (mask == 0 || mask == 255) continue;
                    var sum = Vector3.Zero; var cnt = 0;
                    for (var e = 0; e < 12; e++)
                    {
                        int a = Edges[e, 0], b = Edges[e, 1];
                        if ((corner[a] < 0) == (corner[b] < 0)) continue;
                        var t = corner[a] / (corner[a] - corner[b]);
                        var pa = new Vector3(Corners[a, 0], Corners[a, 1], Corners[a, 2]);
                        var pb = new Vector3(Corners[b, 0], Corners[b, 1], Corners[b, 2]);
                        sum += pa + (pb - pa) * t; cnt++;
                    }
                    cellVert[((k + 1) * cy + (j + 1)) * cx + (i + 1)] = mesh.Pos.Count;
                    mesh.Pos.Add(origin + (new Vector3(i, j, k) + sum / cnt) * step);
                }
        int V(int i, int j, int k) => cellVert[((k + 1) * cy + (j + 1)) * cx + (i + 1)];

        // 면: 기준 표본이 [0, n) 인 모서리만 (이웃 섹션과 겹치지 않게)
        void Quad(int a, int b, int c, int d)
        {
            if (a < 0 || b < 0 || c < 0 || d < 0) return;
            mesh.Idx.Add(a); mesh.Idx.Add(b); mesh.Idx.Add(c);
            mesh.Idx.Add(a); mesh.Idx.Add(c); mesh.Idx.Add(d);
        }
        for (var k = 0; k < n.Z; k++)
            for (var j = 0; j < n.Y; j++)
                for (var i = 0; i < n.X; i++)
                {
                    var g0 = G(i, j, k);
                    if ((g0 < 0) != (G(i + 1, j, k) < 0)) Quad(V(i, j - 1, k - 1), V(i, j, k - 1), V(i, j, k), V(i, j - 1, k));
                    if ((g0 < 0) != (G(i, j + 1, k) < 0)) Quad(V(i - 1, j, k - 1), V(i - 1, j, k), V(i, j, k), V(i, j, k - 1));
                    if ((g0 < 0) != (G(i, j, k + 1) < 0)) Quad(V(i - 1, j - 1, k), V(i, j - 1, k), V(i, j, k), V(i - 1, j, k));
                }

        // 법선·색, 그리고 삼각형 감김을 법선 쪽으로 맞춤
        var nv = mesh.Pos.Count;
        var nrm = new Vector3[nv]; var col = new Vector4[nv];
        void Shade(int v) { nrm[v] = normal(mesh.Pos[v]); col[v] = color(mesh.Pos[v]); }
        if (parallel) Parallel.For(0, nv, Shade); else for (var v = 0; v < nv; v++) Shade(v);
        mesh.Nrm.AddRange(nrm); mesh.Col.AddRange(col);
        for (var t = 0; t < mesh.Idx.Count; t += 3)
        {
            int a = mesh.Idx[t], b = mesh.Idx[t + 1], c = mesh.Idx[t + 2];
            var fn = Vector3.Cross(mesh.Pos[b] - mesh.Pos[a], mesh.Pos[c] - mesh.Pos[a]);
            if (Vector3.Dot(fn, nrm[a] + nrm[b] + nrm[c]) < 0) { mesh.Idx[t + 1] = c; mesh.Idx[t + 2] = b; }
        }
        return mesh;
    }
}
