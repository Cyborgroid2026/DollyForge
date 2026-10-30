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
        if (!args.Contains("--nodetail")) ApplyReferenceDetail(female, male);
        var bodies = new[] { female.Mesh, male.Mesh };
        const int W = 992, H = 1072;
        for (var view = 0; view < 2; view++)
        {
            var flip = view == 0 ? 1f : -1f;
            var depth = new float[W * H]; Array.Fill(depth, float.NegativeInfinity);
            var normal = new Vector3[W * H];
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
                            if (z > depth[k]) { depth[k] = z; normal[k] = Vector3.Normalize(w0 * n[i0] + w1 * n[i1] + w2 * n[i2]); }
                        }
                }
            }
            using var img = new Image<Rgb24>(W, H);
            for (var y = 0; y < H; y++)
                for (var x = 0; x < W; x++)
                {
                    var k = y * W + x;
                    if (float.IsNegativeInfinity(depth[k])) continue;
                    var c = (normal[k] * 0.5f + new Vector3(0.5f)) * 255f;
                    img[x, y] = new Rgb24((byte)Math.Clamp(c.X, 1, 255), (byte)Math.Clamp(c.Y, 1, 255), (byte)Math.Clamp(c.Z, 1, 255));
                }
            var file = Path.Combine(outDir, view == 0 ? "ours-front.png" : "ours-back.png");
            img.SaveAsPng(file);
            Console.WriteLine($"[refnormals] {file}");
        }
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
    static Vector3[] Place(BodyMesh b, int view, int f)
    {
        var flip = view == 0 ? 1f : -1f;
        var (top, bottom, cx) = RefPlace[view, f];
        var height = b.Vertices.Max(v => v.Y);
        var s = (bottom - top) / height;
        var lift = f == 0 ? 100f : 0f;
        Vector2 P(Vector3 v) => new(cx + flip * v.X * s, bottom - v.Y * s);
        var basePos = b.Vertices.Select(P).ToArray();
        var pose = RefPose()?[view][f];
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
            var q = Vector2.Zero;
            for (var k = 0; k < 4; k++)
            {
                var w = b.Weights[i * 4 + k];
                if (w > 0) q += t[b.Bones[i * 4 + k]].Apply(basePos[i]) * w;
            }
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
