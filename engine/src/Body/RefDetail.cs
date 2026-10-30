using System.Numerics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DollyForge;

/// <summary>
/// 레퍼런스 그림의 근육 조각을 인형 법선으로 옮긴다.
/// assets/body/ref-detail-{front,back}.png = 레퍼런스 앞·뒤 그림을 Marigold 로 법선 추정한 뒤 근육 크기 이하만 남긴 디테일
/// (tools/refdetail/make_detail.py, 카메라 공간 x 오른쪽·y 위·z 화면 밖, 값 = 디테일 × 2 + 0.5).
/// 쉼 자세 메시를 그림 속 각 인형 자리(정수리·발끝·중심선)에 직교 투영하고, CPU 깊이 버퍼로 보이는 정점만 골라
/// 그 픽셀의 디테일을 법선에 더한다. 그림에서 여성형이 남성형 앞에 서 있어 겹친 곳은 여성형 차지다.
/// 형태(정점)는 그대로라 실루엣·체형 피팅·스키닝은 바뀌지 않는다.
/// </summary>
public sealed partial class CyborgBody
{
    // 레퍼런스 픽셀 기준점 (992×1072): 정수리 y, 발끝 y, 몸 중심선 x
    static readonly (float Top, float Bottom, float Cx)[,] RefPlace =
    {
        { (78, 985, 457), (72, 990, 672) },   // 정면: 여, 남
        { (90, 985, 465), (80, 990, 670) },   // 후면: 여, 남
    };
    const float RefDetailGain = 1.2f;
    /// <summary>몸 안쪽(정면을 향한 곳)에서 레퍼런스 전체 법선을 섞는 비율: 근육 디테일 위에 큰 입체감까지</summary>
    static float RefFullBlend = float.TryParse(Environment.GetEnvironmentVariable("DOLLY_REF_BLEND"), System.Globalization.CultureInfo.InvariantCulture, out var rb) ? rb : 0.7f;
    static bool _refDetailDone;

    sealed class DetailMap
    {
        public int W, H;
        public Vector3[] D = [];
        public Vector3 Sample(float x, float y)
        {
            x = Math.Clamp(x, 0, W - 1.001f); y = Math.Clamp(y, 0, H - 1.001f);
            int x0 = (int)x, y0 = (int)y; float fx = x - x0, fy = y - y0;
            Vector3 P(int a, int b) => D[b * W + a];
            return Vector3.Lerp(Vector3.Lerp(P(x0, y0), P(x0 + 1, y0), fx), Vector3.Lerp(P(x0, y0 + 1), P(x0 + 1, y0 + 1), fx), fy);
        }
    }

    static DetailMap? LoadDetail(string path, bool full = false)
    {
        if (!File.Exists(path)) return null;
        using var img = Image.Load<Rgb48>(path);
        var m = new DetailMap { W = img.Width, H = img.Height, D = new Vector3[img.Width * img.Height] };
        img.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
            {
                var row = rows.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                    m.D[y * m.W + x] = (new Vector3(row[x].R, row[x].G, row[x].B) / 65535f - new Vector3(0.5f)) * (full ? 2f : 0.5f);
            }
        });
        return m;
    }

    /// <summary>두 인형(여·남)에 레퍼런스 디테일을 한 번 입힌다. 자산이 없으면 아무것도 하지 않는다.</summary>
    public static void ApplyReferenceDetail(CyborgBody female, CyborgBody male)
    {
        if (_refDetailDone) return;
        _refDetailDone = true;
        var dir = Path.Combine(AppContext.BaseDirectory, "assets", "body");
        var maps = new[] { LoadDetail(Path.Combine(dir, "ref-detail-front.png")), LoadDetail(Path.Combine(dir, "ref-detail-back.png")) };
        var fulls = new[] { LoadDetail(Path.Combine(dir, "ref-normals-front.png"), true), LoadDetail(Path.Combine(dir, "ref-normals-back.png"), true) };
        if (maps[0] is null || maps[1] is null) return;
        var bodies = new[] { female.Mesh, male.Mesh };
        // 정점마다 가장 정면으로 보이는 쪽(앞/뒤)의 디테일과 그 가중치
        var best = bodies.Select(b => new (Vector3 D, float W, Vector3 Full)[b.Vertices.Length]).ToArray();

        for (var view = 0; view < 2; view++)
        {
            var map = maps[view]!;
            var flip = view == 0 ? 1f : -1f;             // 후면: 카메라가 반대편 → x·z 뒤집힘
            var depth = new float[map.W * map.H]; Array.Fill(depth, float.NegativeInfinity);
            var owner = new sbyte[map.W * map.H]; Array.Fill(owner, (sbyte)-1);
            var proj = new Vector3[2][];
            for (var f = 0; f < 2; f++)
            {
                var b = bodies[f];
                proj[f] = Place(b, view, f);
                Rasterize(proj[f], b.Indices, depth, owner, map.W, map.H, (sbyte)f);
            }
            // 남성형 정점은 여성형 실루엣 근처(8 px)도 피한다: 그림의 두 몸 경계 디테일이 섞이지 않게
            var nearFemale = Dilate(owner, map.W, map.H, 8);

            for (var f = 0; f < 2; f++)
            {
                var b = bodies[f]; var p = proj[f];
                for (var i = 0; i < p.Length; i++)
                {
                    var facing = flip * b.Normals[i].Z;
                    var w = SmoothStep(0.15f, 0.5f, facing);
                    if (w <= best[f][i].W) continue;
                    int px = (int)MathF.Round(p[i].X), py = (int)MathF.Round(p[i].Y);
                    if (px < 1 || py < 1 || px >= map.W - 1 || py >= map.H - 1) continue;
                    if (f == 1 && nearFemale[py * map.W + px]) continue;
                    // 보이는가: 주변 3×3 중 같은 몸의 깊이가 이 정점과 2 cm 안
                    var seen = false;
                    for (var dy = -1; dy <= 1 && !seen; dy++)
                        for (var dx = -1; dx <= 1 && !seen; dx++)
                        {
                            var k = (py + dy) * map.W + px + dx;
                            seen = owner[k] == f && depth[k] - p[i].Z < 0.02f;
                        }
                    if (!seen) continue;
                    var d = map.Sample(p[i].X, p[i].Y);
                    var full = fulls[view]?.Sample(p[i].X, p[i].Y) ?? Vector3.Zero;
                    best[f][i] = (new Vector3(flip * d.X, d.Y, flip * d.Z), w, new Vector3(flip * full.X, full.Y, flip * full.Z));
                }
            }
        }

        for (var f = 0; f < 2; f++)
        {
            var n = bodies[f].Normals;
            var hit = 0;
            for (var i = 0; i < n.Length; i++)
            {
                if (best[f][i].W <= 0) continue;
                var detailed = Vector3.Normalize(n[i] + best[f][i].D * (RefDetailGain * best[f][i].W));
                // 큰 입체감: 정면을 향한 안쪽일수록 레퍼런스 전체 법선 쪽으로 (가장자리는 메시 법선 그대로 → 실루엣과 어긋나지 않게)
                var full = best[f][i].Full;
                n[i] = full.LengthSquared() > 0.25f
                    ? Vector3.Normalize(Vector3.Lerp(detailed, Vector3.Normalize(full), RefFullBlend * best[f][i].W))
                    : detailed;
                hit++;
            }
            Console.WriteLine($"[detail] {(f == 0 ? "여성형" : "남성형")}: 레퍼런스 근육 디테일 {hit:N0}/{n.Length:N0} 정점");
        }
    }

    /// <summary>
    /// 비교용 (개발): 두 인형을 레퍼런스 그림 자리에 직교 투영해 카메라 공간 법선 색 지도로 저장한다.
    /// dolly-engine --refnormals 출력폴더 [--nodetail] → ours-front.png, ours-back.png (배경 검정)
    /// tools/refdetail/compare.py 가 Marigold 법선과 각도 차·디테일 상관을 잰다.
    /// </summary>
    public static void RefNormalsCli(string[] args)
    {
        var outDir = args.Length > 1 ? args[1] : ".";
        Directory.CreateDirectory(outDir);
        var female = Build(0); var male = Build(1);
        if (!args.Contains("--nosil")) for (var pass = 0; pass < SilPasses; pass++) FitReferenceSilhouette(female, male);
        if (!args.Contains("--nodetail")) ApplyReferenceDetail(female, male);
        var bodies = new[] { female.Mesh, male.Mesh };
        const int W = 992, H = 1072;
        for (var view = 0; view < 2; view++)
        {
            var flip = view == 0 ? 1f : -1f;
            var depth = new float[W * H]; Array.Fill(depth, float.NegativeInfinity);
            var normal = new Vector3[W * H];
            var who = new sbyte[W * H];
            for (var f = 0; f < 2; f++)
            {
                var b = bodies[f];
                var p = Place(b, view, f);
                var n = b.Normals.Select(v => new Vector3(flip * v.X, v.Y, flip * v.Z)).ToArray();
                for (var t = 0; t < b.Indices.Length; t += 3)
                {
                    int i0 = b.Indices[t], i1 = b.Indices[t + 1], i2 = b.Indices[t + 2];
                    Vector3 a = p[i0], bb = p[i1], c = p[i2];
                    var area = (bb.X - a.X) * (c.Y - a.Y) - (bb.Y - a.Y) * (c.X - a.X);
                    if (MathF.Abs(area) < 1e-6f) continue;
                    int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(bb.X, c.X)))), x1 = Math.Min(W - 1, (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(bb.X, c.X))));
                    int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(bb.Y, c.Y)))), y1 = Math.Min(H - 1, (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(bb.Y, c.Y))));
                    for (var y = y0; y <= y1; y++)
                        for (var x = x0; x <= x1; x++)
                        {
                            float w0 = ((bb.X - x) * (c.Y - y) - (bb.Y - y) * (c.X - x)) / area;
                            float w1 = ((c.X - x) * (a.Y - y) - (c.Y - y) * (a.X - x)) / area;
                            var w2 = 1 - w0 - w1;
                            if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                            var z = w0 * a.Z + w1 * bb.Z + w2 * c.Z;
                            var k = y * W + x;
                            if (z > depth[k]) { depth[k] = z; who[k] = (sbyte)f; normal[k] = Vector3.Normalize(w0 * n[i0] + w1 * n[i1] + w2 * n[i2]); }
                        }
                }
            }
            using var img = new Image<Rgb24>(W, H);
            using var ids = new Image<Rgb24>(W, H);   // 인형별 영역: R = 여성형, G = 남성형
            for (var y = 0; y < H; y++)
                for (var x = 0; x < W; x++)
                {
                    var k = y * W + x;
                    if (float.IsNegativeInfinity(depth[k])) continue;
                    ids[x, y] = who[k] == 0 ? new Rgb24(255, 0, 0) : new Rgb24(0, 255, 0);
                    var c = (normal[k] * 0.5f + new Vector3(0.5f)) * 255f;
                    img[x, y] = new Rgb24((byte)Math.Clamp(c.X, 1, 255), (byte)Math.Clamp(c.Y, 1, 255), (byte)Math.Clamp(c.Z, 1, 255));
                }
            var file = Path.Combine(outDir, view == 0 ? "ours-front.png" : "ours-back.png");
            img.SaveAsPng(file);
            ids.SaveAsPng(Path.Combine(outDir, view == 0 ? "ours-id-front.png" : "ours-id-back.png"));
            Console.WriteLine($"[refnormals] {file}");
        }
    }

    // ─── 실루엣 맞춤 (정점 이동) ───────────────────────────────────────────

    const float SilMaxShift = 0.03f;   // 최대 이동 (m)
    const int SilSearch = 30;          // 윤곽을 찾아 걷는 최대 거리 (px)
    const int SilSpread = 40;          // 이동량을 메시로 퍼뜨리는 횟수
    public const int SilPasses = 2;
    const int SilSmooth = 15;          // 이동량 필드 전체 평활 횟수 (옆구리 줄무늬 방지)    // 맞춘 뒤 다시 재서 남은 차이를 한 번 더

    /// <summary>
    /// 두 인형의 윤곽을 레퍼런스 그림 속 윤곽(assets/body/ref-silhouette-*.png, SAM 인형별 영역)에 맞춘다.
    /// 앞·뒤 그림 각각에서 메시 윤곽 정점(시선과 거의 수직인 보이는 정점)이 그림 윤곽까지 바깥·안쪽으로 몇 px 인지 재고,
    /// 2D 정렬의 역변환으로 쉼 자세 이동량(m)으로 되돌려 메시를 따라 퍼뜨린다. 다른 인형에 가려 그림 윤곽을 알 수 없는 곳은 건너뛴다.
    /// 머리·손·발은 맞추지 않는다. 정점을 옮긴 만큼 법선도 고친다 (근육 디테일 이식보다 먼저).
    /// </summary>
    public static void FitReferenceSilhouette(CyborgBody female, CyborgBody male)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "assets", "body");
        var sil = new bool[2][][];
        int W = 0, H = 0;
        for (var view = 0; view < 2; view++)
        {
            var path = Path.Combine(dir, view == 0 ? "ref-silhouette-front.png" : "ref-silhouette-back.png");
            if (!File.Exists(path)) return;
            using var img = Image.Load<Rgb24>(path);
            W = img.Width; H = img.Height;
            var fm = new bool[W * H]; var mm = new bool[W * H];
            img.ProcessPixelRows(rows =>
            {
                for (var y = 0; y < rows.Height; y++)
                {
                    var row = rows.GetRowSpan(y);
                    for (var x = 0; x < row.Length; x++) { fm[y * W + x] = row[x].R > 127; mm[y * W + x] = row[x].G > 127; }
                }
            });
            sil[view] = [fm, mm];
        }
        var bodies = new[] { female.Mesh, male.Mesh };
        for (var f = 0; f < 2; f++)
        {
            var b = bodies[f];
            var shift = new Vector3[b.Vertices.Length]; var count = new float[b.Vertices.Length];
            for (var view = 0; view < 2; view++)
            {
                var flip = view == 0 ? 1f : -1f;
                var p = Place(b, view, f, out var lin);
                var own = sil[view][f]; var other = sil[view][1 - f];
                var (top, bottom, _) = RefPlace[view, f];
                var s = (bottom - top) / b.Vertices.Max(v => v.Y);
                // 이 인형만 래스터화: 윤곽 정점이 다른 몸 부위에 가리지 않았는지 (같은 인형 안 자기 가림)
                var depth = new float[W * H]; Array.Fill(depth, float.NegativeInfinity);
                var owner = new sbyte[W * H]; Array.Fill(owner, (sbyte)-1);
                Rasterize(p, b.Indices, depth, owner, W, H, 0);
                bool Own(int x, int y) => x >= 0 && y >= 0 && x < W && y < H && own[y * W + x];
                bool Other(int x, int y) => x >= 0 && y >= 0 && x < W && y < H && other[y * W + x];
                for (var i = 0; i < p.Length; i++)
                {
                    // 머리(매끈한 달걀)·손가락·발가락은 그림 윤곽과 맞추면 오히려 흐트러진다
                    var ext = 0f;
                    for (var k = 0; k < 4; k++)
                        if (b.Bones[i * 4 + k] is (int)Bone.Head or (int)Bone.HandL or (int)Bone.HandR or (int)Bone.FootL or (int)Bone.FootR)
                            ext += b.Weights[i * 4 + k];
                    if (ext > 0.3f) continue;
                    var n = b.Normals[i];
                    var nc = new Vector2(flip * n.X, -n.Y);                 // 화면 방향 (y 아래)
                    if (MathF.Abs(flip * n.Z) > 0.3f || nc.Length() < 0.5f) continue;   // 윤곽 정점만
                    nc = Vector2.Normalize(nc);
                    int px = (int)MathF.Round(p[i].X), py = (int)MathF.Round(p[i].Y);
                    if (px < 1 || py < 1 || px >= W - 1 || py >= H - 1) continue;
                    // 실제로 보이는 윤곽인가: 바깥쪽 2 px 은 우리 몸이 아니어야
                    var ox = (int)MathF.Round(p[i].X + nc.X * 2); var oy = (int)MathF.Round(p[i].Y + nc.Y * 2);
                    if (ox >= 0 && oy >= 0 && ox < W && oy < H && !float.IsNegativeInfinity(depth[oy * W + ox])) continue;
                    // 그림 윤곽까지 걷기: 안이면 바깥으로(+), 밖이면 안으로(−)
                    var inside = Own(px, py);
                    float d = float.NaN;
                    for (var step = 1; step <= SilSearch; step++)
                    {
                        var q = p[i].AsVector2() + nc * (inside ? step : -step);
                        int qx = (int)MathF.Round(q.X), qy = (int)MathF.Round(q.Y);
                        if (Other(qx, qy)) break;                                   // 다른 인형에 닿음: 윤곽 모름
                        if (Own(qx, qy) != inside) { d = inside ? step - 0.5f : -(step - 0.5f); break; }
                    }
                    if (float.IsNaN(d)) continue;
                    // 그림 윤곽 너머가 다른 인형이면(가려진 경계) 믿지 않는다
                    var bx = (int)MathF.Round(p[i].X + nc.X * (d + 2)); var by = (int)MathF.Round(p[i].Y + nc.Y * (d + 2));
                    if (Other(bx, by)) continue;
                    // 픽셀 이동 → 쉼 자세 이동: 2D 변환 선형부의 역 → 크기 s → 3D (후면은 x 뒤집힘, 화면 y 는 아래)
                    var L = lin[i]; var det = L.X * L.W - L.Y * L.Z;
                    if (MathF.Abs(det) < 1e-4f) continue;
                    var dv = nc * d;
                    var bx2 = (L.W * dv.X - L.Y * dv.Y) / det; var by2 = (-L.Z * dv.X + L.X * dv.Y) / det;
                    var move = new Vector3(flip * bx2 / s, -by2 / s, 0);
                    if (move.Length() > SilMaxShift) move = Vector3.Normalize(move) * SilMaxShift;
                    shift[i] += move; count[i] += 1;
                }
            }
            // 퍼뜨리기: 정한 정점은 그 값으로 두고, 나머지는 이웃 평균 (단면이 통째로 자연스럽게)
            var adj = Adjacency(b);
            var fixedV = count.Select(c => c > 0).ToArray();
            for (var i = 0; i < shift.Length; i++) if (fixedV[i]) shift[i] /= count[i];
            var tmp = new Vector3[shift.Length];
            for (var it = 0; it < SilSpread; it++)
            {
                for (var i = 0; i < shift.Length; i++)
                {
                    if (fixedV[i] || adj[i].Length == 0) { tmp[i] = shift[i]; continue; }
                    var a = Vector3.Zero; foreach (var j in adj[i]) a += shift[j];
                    tmp[i] = a / adj[i].Length;
                }
                (shift, tmp) = (tmp, shift);
            }
            // 윤곽 값 자체도 이웃과 고르게: 앞·뒤 그림 윤곽이 만나는 옆구리에서 정점마다 들쭉날쭉하면 줄무늬가 된다
            for (var it = 0; it < SilSmooth; it++)
            {
                for (var i = 0; i < shift.Length; i++)
                {
                    if (adj[i].Length == 0) { tmp[i] = shift[i]; continue; }
                    var a = shift[i]; foreach (var j in adj[i]) a += shift[j];
                    tmp[i] = a / (adj[i].Length + 1);
                }
                (shift, tmp) = (tmp, shift);
            }
            tmp = shift;
            var maxMove = 0f;
            var before = GeometricNormals(b);
            for (var i = 0; i < b.Vertices.Length; i++) { b.Vertices[i] += tmp[i]; maxMove = MathF.Max(maxMove, tmp[i].Length()); }
            // 법선: 다시 계산하지 않고 이동 전후 기하 법선의 차이만 더한다 (달걀 머리 같은 해석적 법선 보존)
            var after = GeometricNormals(b);
            for (var i = 0; i < b.Normals.Length; i++)
                b.Normals[i] = Vector3.Normalize(b.Normals[i] + (after[i] - before[i]));
            Console.WriteLine($"[silhouette] {(f == 0 ? "여성형" : "남성형")}: 윤곽 정점 {fixedV.Count(x => x):N0}개 기준, 최대 이동 {maxMove * 100:0.0} cm");
        }
    }

    static int[][] Adjacency(BodyMesh b)
    {
        var sets = new HashSet<int>[b.Vertices.Length];
        for (var i = 0; i < sets.Length; i++) sets[i] = [];
        for (var t = 0; t < b.Indices.Length; t += 3)
            for (var e = 0; e < 3; e++)
            {
                int a = b.Indices[t + e], c = b.Indices[t + (e + 1) % 3];
                sets[a].Add(c); sets[c].Add(a);
            }
        return sets.Select(x => x.ToArray()).ToArray();
    }

    static Vector3[] GeometricNormals(BodyMesh b)
    {
        var n = new Vector3[b.Vertices.Length];
        for (var t = 0; t < b.Indices.Length; t += 3)
        {
            int a = b.Indices[t], c = b.Indices[t + 1], d = b.Indices[t + 2];
            var fn = Vector3.Cross(b.Vertices[c] - b.Vertices[a], b.Vertices[d] - b.Vertices[a]);
            n[a] += fn; n[c] += fn; n[d] += fn;
        }
        for (var i = 0; i < n.Length; i++) n[i] = n[i].LengthSquared() > 1e-20f ? Vector3.Normalize(n[i]) : Vector3.Zero;
        return n;
    }

    // ─── 그림 자세에 겹치기 ────────────────────────────────────────────────

    static Dictionary<string, Vector2>[][]? _refPose;   // [앞/뒤][여/남] 관절 이름 → 픽셀

    static Dictionary<string, Vector2>[][]? RefPose()
    {
        if (_refPose is not null) return _refPose;
        var path = Path.Combine(AppContext.BaseDirectory, "assets", "body", "ref-pose.json");
        if (!File.Exists(path)) return null;
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        Dictionary<string, Vector2> Person(System.Text.Json.JsonElement e) =>
            e.EnumerateObject().ToDictionary(p => p.Name, p => new Vector2(p.Value[0].GetSingle(), p.Value[1].GetSingle()));
        return _refPose = new[] { "front", "back" }.Select(v => doc.RootElement.GetProperty(v).EnumerateArray().Select(Person).ToArray()).ToArray();
    }

    /// <summary>2D 변환: 행렬 (a b; c d) + 이동</summary>
    readonly record struct Affine2(float A, float B, float C, float D, Vector2 T)
    {
        public Vector2 Apply(Vector2 p) => new(A * p.X + B * p.Y + T.X, C * p.X + D * p.Y + T.Y);
        /// <summary>마디 a→b 를 a2→b2 로: 길이 방향만 늘이고 굵기(수직 방향)는 그대로</summary>
        public static Affine2 Segment(Vector2 a, Vector2 b, Vector2 a2, Vector2 b2)
        {
            var d = b - a; var d2 = b2 - a2;
            var l = d.Length(); var l2 = d2.Length();
            if (l < 1e-3f || l2 < 1e-3f) return new(1, 0, 0, 1, a2 - a);
            d /= l; d2 /= l2;
            Vector2 n = new(-d.Y, d.X), n2 = new(-d2.Y, d2.X);
            var k = l2 / l;
            // p → a2 + (p−a)·d·k·d2 + (p−a)·n·n2
            float A = k * d.X * d2.X + n.X * n2.X, B = k * d.Y * d2.X + n.Y * n2.X;
            float C = k * d.X * d2.Y + n.X * n2.Y, D = k * d.Y * d2.Y + n.Y * n2.Y;
            return new(A, B, C, D, a2 - new Vector2(A * a.X + B * a.Y, C * a.X + D * a.Y));
        }
        /// <summary>점 쌍들에 최소제곱으로 맞춘 아핀 변환</summary>
        public static Affine2 Fit(Vector2[] p, Vector2[] q)
        {
            var pc = p.Aggregate(Vector2.Zero, (s, v) => s + v) / p.Length;
            var qc = q.Aggregate(Vector2.Zero, (s, v) => s + v) / q.Length;
            float sxx = 0, sxy = 0, syy = 0, qxx = 0, qxy = 0, qyx = 0, qyy = 0;
            for (var i = 0; i < p.Length; i++)
            {
                var a = p[i] - pc; var b = q[i] - qc;
                sxx += a.X * a.X; sxy += a.X * a.Y; syy += a.Y * a.Y;
                qxx += b.X * a.X; qxy += b.X * a.Y; qyx += b.Y * a.X; qyy += b.Y * a.Y;
            }
            var det = sxx * syy - sxy * sxy;
            if (MathF.Abs(det) < 1e-6f) return new(1, 0, 0, 1, qc - pc);
            float i00 = syy / det, i01 = -sxy / det, i11 = sxx / det;
            float A = qxx * i00 + qxy * i01, B = qxx * i01 + qxy * i11;
            float C = qyx * i00 + qyy * i01, D = qyx * i01 + qyy * i11;
            return new(A, B, C, D, qc - new Vector2(A * pc.X + B * pc.Y, C * pc.X + D * pc.Y));
        }
    }

    /// <summary>
    /// 쉼 자세 정점 → 레퍼런스 그림 픽셀 (x, y) 와 깊이 z (클수록 앞, 여성형은 +100 으로 늘 앞).
    /// 먼저 정수리·발끝·중심선으로 크기·자리를 맞추고, 그림의 관절(RTMPose)이 있으면
    /// 뼈마다 2D 변환(몸통: 어깨·골반 4점 아핀, 팔·다리: 마디 방향·길이)을 스키닝 가중치로 섞어 그림 자세에 겹친다.
    /// </summary>
    static Vector3[] Place(BodyMesh b, int view, int f) => Place(b, view, f, out _);

    /// <param name="lin">정점별 2D 변환의 선형부 (a b c d) — 픽셀 이동을 쉼 자세 공간으로 되돌릴 때</param>
    static Vector3[] Place(BodyMesh b, int view, int f, out Vector4[] lin)
    {
        var flip = view == 0 ? 1f : -1f;
        var (top, bottom, cx) = RefPlace[view, f];
        var height = b.Vertices.Max(v => v.Y);
        var s = (bottom - top) / height;
        var lift = f == 0 ? 100f : 0f;
        Vector2 P(Vector3 v) => new(cx + flip * v.X * s, bottom - v.Y * s);
        var basePos = b.Vertices.Select(P).ToArray();
        var pose = RefPose()?[view][f];
        lin = Enumerable.Repeat(new Vector4(1, 0, 0, 1), b.Vertices.Length).ToArray();
        if (pose is null) return b.Vertices.Select((v, i) => new Vector3(basePos[i], flip * v.Z + lift)).ToArray();

        Vector2 J(Bone bone) => P(b.Global[(int)bone].Origin);
        Vector2 Q(string name) => pose[name];
        // 우리 L 은 +x (MakeHuman), 그림의 L 은 그 인물의 왼쪽
        var t = new Affine2[BodyMesh.BoneCount];
        t[(int)Bone.Root] = Affine2.Fit([J(Bone.UpperL), J(Bone.UpperR), J(Bone.ThighL), J(Bone.ThighR)],
                                        [Q("shoulderL"), Q("shoulderR"), Q("hipL"), Q("hipR")]);
        var neck = (J(Bone.UpperL) + J(Bone.UpperR)) * 0.5f; var neck2 = (Q("shoulderL") + Q("shoulderR")) * 0.5f;
        t[(int)Bone.Head] = Affine2.Segment(neck, P(new Vector3(0, height, 0)), neck2, new Vector2(cx, top));
        foreach (var (L, side) in new[] { (true, "L"), (false, "R") })
        {
            Bone U = L ? Bone.UpperL : Bone.UpperR, Fo = L ? Bone.ForeL : Bone.ForeR, Ha = L ? Bone.HandL : Bone.HandR;
            Bone Th = L ? Bone.ThighL : Bone.ThighR, Sh = L ? Bone.ShinL : Bone.ShinR, Ft = L ? Bone.FootL : Bone.FootR;
            t[(int)U] = Affine2.Segment(J(U), J(Fo), Q("shoulder" + side), Q("elbow" + side));
            t[(int)Fo] = t[(int)Ha] = Affine2.Segment(J(Fo), J(Ha), Q("elbow" + side), Q("wrist" + side));
            t[(int)Th] = Affine2.Segment(J(Th), J(Sh), Q("hip" + side), Q("knee" + side));
            t[(int)Sh] = t[(int)Ft] = Affine2.Segment(J(Sh), J(Ft), Q("knee" + side), Q("ankle" + side));
        }
        var res = new Vector3[b.Vertices.Length];
        for (var i = 0; i < res.Length; i++)
        {
            var q = Vector2.Zero; var l = Vector4.Zero;
            for (var k = 0; k < 4; k++)
            {
                var w = b.Weights[i * 4 + k];
                if (w <= 0) continue;
                var tk = t[b.Bones[i * 4 + k]];
                q += tk.Apply(basePos[i]) * w;
                l += new Vector4(tk.A, tk.B, tk.C, tk.D) * w;
            }
            lin[i] = l;
            res[i] = new Vector3(q, flip * b.Vertices[i].Z + lift);
        }
        return res;
    }

    static float SmoothStep(float a, float b, float x) { var t = Math.Clamp((x - a) / (b - a), 0, 1); return t * t * (3 - 2 * t); }

    /// <summary>삼각형 래스터화 (깊이 큰 쪽이 앞)</summary>
    static void Rasterize(Vector3[] p, int[] idx, float[] depth, sbyte[] owner, int W, int H, sbyte id)
    {
        for (var t = 0; t < idx.Length; t += 3)
        {
            Vector3 a = p[idx[t]], b = p[idx[t + 1]], c = p[idx[t + 2]];
            var area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
            if (MathF.Abs(area) < 1e-6f) continue;
            int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X)))), x1 = Math.Min(W - 1, (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))));
            int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y)))), y1 = Math.Min(H - 1, (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))));
            for (var y = y0; y <= y1; y++)
                for (var x = x0; x <= x1; x++)
                {
                    float w0 = ((b.X - x) * (c.Y - y) - (b.Y - y) * (c.X - x)) / area;
                    float w1 = ((c.X - x) * (a.Y - y) - (c.Y - y) * (a.X - x)) / area;
                    var w2 = 1 - w0 - w1;
                    if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                    var z = w0 * a.Z + w1 * b.Z + w2 * c.Z;
                    var k = y * W + x;
                    if (z > depth[k]) { depth[k] = z; owner[k] = id; }
                }
        }
    }

    static bool[] Dilate(sbyte[] owner, int W, int H, int r)
    {
        var res = new bool[W * H];
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                if (owner[y * W + x] != 0) continue;
                for (var dy = -r; dy <= r; dy++)
                    for (var dx = -r; dx <= r; dx++)
                    {
                        int xx = x + dx, yy = y + dy;
                        if (xx >= 0 && yy >= 0 && xx < W && yy < H) res[yy * W + xx] = true;
                    }
            }
        return res;
    }
}
