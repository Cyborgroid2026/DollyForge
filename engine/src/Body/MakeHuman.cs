using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace DollyForge;

/// <summary>
/// MakeHuman CC0 인체로 만든 크롬 사이보그.
/// 해부학적으로 만든 기본 메시(hm08)에 공식 모프 타깃을 섞어 체형을 정하고(BodyFit 이 레퍼런스에 맞춘 값),
/// 팔을 내리고, 얼굴을 달걀형 머리로 투영하고, 163뼈 가중치를 14뼈로 접어 스키닝한다.
/// 자산: tools/fetch-makehuman.sh → engine/assets/makehuman (CC0 1.0)
/// </summary>
public sealed partial class CyborgBody
{
    sealed class MhAssets
    {
        public Vector3[] Base = [];                    // 모든 정점 (dm, 도우미 포함)
        public int[] Tris = [];                        // body 그룹 삼각형
        public int[] Used = [];                        // body 면이 쓰는 정점 (원래 번호)
        public Dictionary<string, int[]> Joints = [];
        public float[] W14 = [];                       // 정점 × 14 뼈
        public int[][] Adj = [];                       // body 정점 이웃 (원래 번호)
        public readonly List<(string Key, string? Neg, string? Pos)> Sliders = [];
        readonly string _dir;
        readonly Dictionary<string, (int[] I, Vector3[] D)?> _cache = [];

        MhAssets(string dir) => _dir = dir;

        public static string DefaultDir => Path.Combine(AppContext.BaseDirectory, "assets", "makehuman");
        static MhAssets? _shared;
        static bool _tried;

        public static MhAssets? Shared
        {
            get
            {
                if (_tried) return _shared;
                _tried = true;
                try { _shared = Load(DefaultDir); }
                catch (Exception ex) { Console.Error.WriteLine($"[mh] 자산 읽기 실패: {ex.Message}"); }
                return _shared;
            }
        }

        static MhAssets? Load(string dir)
        {
            var obj = Path.Combine(dir, "3dobjs", "base.obj");
            if (!File.Exists(obj)) return null;
            var a = new MhAssets(dir);
            a.LoadObj(obj);
            a.LoadRig(Path.Combine(dir, "rigs", "default.mhskel"), Path.Combine(dir, "rigs", "default_weights.mhw"));
            a.FindSliders();
            Console.WriteLine($"[mh] 기본 메시 {a.Used.Length:N0} 정점 · 삼각형 {a.Tris.Length / 3:N0} · 슬라이더 {a.Sliders.Count}");
            return a;
        }

        void LoadObj(string path)
        {
            var verts = new List<Vector3>(); var tris = new List<int>(); var group = "";
            foreach (var line in File.ReadLines(path))
            {
                if (line.StartsWith("v "))
                {
                    var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    verts.Add(new Vector3(F(p[1]), F(p[2]), F(p[3])));
                }
                else if (line.StartsWith("g ")) group = line[2..].Trim();
                else if (line.StartsWith("f ") && group == "body")
                {
                    var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    var idx = p.Skip(1).Select(t => int.Parse(t.Split('/')[0], CultureInfo.InvariantCulture) - 1).ToArray();
                    for (var k = 1; k + 1 < idx.Length; k++) tris.AddRange([idx[0], idx[k], idx[k + 1]]);
                }
            }
            Base = verts.ToArray(); Tris = tris.ToArray();
            Used = Tris.Distinct().OrderBy(i => i).ToArray();
            var sets = new HashSet<int>[Base.Length];
            foreach (var u in Used) sets[u] = [];
            for (var t = 0; t < Tris.Length; t += 3)
                for (var k = 0; k < 3; k++)
                {
                    int x = Tris[t + k], y = Tris[t + (k + 1) % 3];
                    sets[x].Add(y); sets[y].Add(x);
                }
            Adj = sets.Select(s => s?.ToArray() ?? []).ToArray();
        }

        static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

        void LoadRig(string skel, string weights)
        {
            using (var doc = JsonDocument.Parse(File.ReadAllText(skel)))
                foreach (var j in doc.RootElement.GetProperty("joints").EnumerateObject())
                    Joints[j.Name] = j.Value.EnumerateArray().Select(e => e.GetInt32()).ToArray();

            W14 = new float[Base.Length * BodyMesh.BoneCount];
            using (var doc = JsonDocument.Parse(File.ReadAllText(weights)))
                foreach (var b in doc.RootElement.GetProperty("weights").EnumerateObject())
                {
                    var ours = (int)MapBone(b.Name);
                    foreach (var pair in b.Value.EnumerateArray())
                    {
                        var v = pair[0].GetInt32(); var w = pair[1].GetSingle();
                        if (v < Base.Length) W14[v * BodyMesh.BoneCount + ours] += w;
                    }
                }
        }

        // MakeHuman 163뼈 → 우리 14뼈
        static Bone MapBone(string n)
        {
            var left = n.EndsWith(".L");
            Bone S(Bone l, Bone r) => left ? l : r;
            if (n.StartsWith("upperarm")) return S(Bone.UpperL, Bone.UpperR);
            if (n.StartsWith("lowerarm")) return S(Bone.ForeL, Bone.ForeR);
            if (n.StartsWith("wrist") || n.StartsWith("metacarpal") || n.StartsWith("finger")) return S(Bone.HandL, Bone.HandR);
            if (n.StartsWith("upperleg")) return S(Bone.ThighL, Bone.ThighR);
            if (n.StartsWith("lowerleg")) return S(Bone.ShinL, Bone.ShinR);
            if (n.StartsWith("foot") || n.StartsWith("toe")) return S(Bone.FootL, Bone.FootR);
            if (n is "root" || n.StartsWith("spine") || n.StartsWith("pelvis") || n.StartsWith("clavicle")
                || n.StartsWith("shoulder") || n.StartsWith("breast")) return Bone.Root;
            return Bone.Head;   // 목·머리·얼굴 근육
        }

        public Vector3 Joint(Vector3[] v, string name)
        {
            var ids = Joints[name]; var s = Vector3.Zero;
            foreach (var i in ids) s += v[i];
            return s / ids.Length;
        }

        public (int[] I, Vector3[] D)? Target(string rel)
        {
            if (_cache.TryGetValue(rel, out var t)) return t;
            var path = Path.Combine(_dir, "targets", rel + ".target");
            if (!File.Exists(path)) return _cache[rel] = null;
            var idx = new List<int>(); var d = new List<Vector3>();
            foreach (var line in File.ReadLines(path))
            {
                if (line.Length == 0 || line[0] == '#') continue;
                var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 4) continue;
                idx.Add(int.Parse(p[0], CultureInfo.InvariantCulture)); d.Add(new Vector3(F(p[1]), F(p[2]), F(p[3])));
            }
            return _cache[rel] = (idx.ToArray(), d.ToArray());
        }

        // 국소 슬라이더: 음수 → 앞 파일, 양수 → 뒤 파일
        void FindSliders()
        {
            (string N, string P)[] pairs = [("decr", "incr"), ("down", "up"), ("in", "out"), ("backward", "forward"), ("less", "more")];
            foreach (var dir in new[] { "measure", "hip", "neck", "torso", "stomach", "buttocks", "breast" })
            {
                var full = Path.Combine(_dir, "targets", dir);
                if (!Directory.Exists(full)) continue;
                var names = Directory.GetFiles(full, "*.target").Select(Path.GetFileNameWithoutExtension).ToHashSet();
                foreach (var n in names.Order())
                    foreach (var (neg, pos) in pairs)
                    {
                        if (!n!.EndsWith("-" + neg)) continue;
                        var stem = n[..^(neg.Length + 1)];
                        if (!names.Contains(stem + "-" + pos)) continue;
                        if (stem.Contains("trans") || stem.Contains("pregnant") || stem.Contains("double") || stem.Contains("navel")) continue;
                        if (dir == "breast" && !stem.StartsWith("breast-")) continue;
                        Sliders.Add(($"{dir}/{stem}", $"{dir}/{n}", $"{dir}/{stem}-{pos}"));
                    }
            }
            var al = Path.Combine(_dir, "targets", "armslegs");
            if (Directory.Exists(al))
            {
                var names = Directory.GetFiles(al, "*.target").Select(Path.GetFileNameWithoutExtension).ToHashSet();
                foreach (var n in names.Where(x => x!.StartsWith("l-") && x.EndsWith("-decr")).Order())
                {
                    var stem = n![2..^5];
                    Sliders.Add(($"armslegs/{stem}", $"armslegs/l-{stem}-decr|armslegs/r-{stem}-decr", $"armslegs/l-{stem}-incr|armslegs/r-{stem}-incr"));
                }
                foreach (var h in new[] { "upperlegs-height", "lowerlegs-height" })
                    if (names.Contains(h + "-decr")) Sliders.Add(($"armslegs/{h}", $"armslegs/{h}-decr", $"armslegs/{h}-incr"));
            }
            var bs = Path.Combine(_dir, "targets", "bodyshapes");
            if (Directory.Exists(bs))
                foreach (var n in Directory.GetFiles(bs, "*.target").Select(Path.GetFileNameWithoutExtension).Order())
                    Sliders.Add(($"bodyshapes/{n}", null, $"bodyshapes/{n}"));   // 한쪽만: 0..1
        }

        public void Add(Vector3[] v, string rels, float w)
        {
            if (w == 0) return;
            foreach (var rel in rels.Split('|'))
                if (Target(rel) is { } t)
                    for (var k = 0; k < t.I.Length; k++)
                        if (t.I[k] < v.Length) v[t.I[k]] += t.D[k] * w;
        }

        /// <summary>기본 메시 + 매크로(근육·체중·키·비율·인종) + 국소 슬라이더</summary>
        public Vector3[] Compose(bool male, Func<string, float, float> P)
        {
            var v = (Vector3[])Base.Clone();
            var g = male ? "male" : "female";
            static float[] Tri(float x) => x < 0.5f ? [1 - x * 2, x * 2, 0] : [0, 2 - x * 2, x * 2 - 1];
            string[] mus = ["minmuscle", "averagemuscle", "maxmuscle"], wei = ["minweight", "averageweight", "maxweight"];
            var mw = Tri(P("muscle", 0.5f)); var ww = Tri(P("weight", 0.5f));
            var h = P("height", 0.5f); var pr = P("proportions", 0.5f);
            for (var i = 0; i < 3; i++)
                for (var j = 0; j < 3; j++)
                {
                    var w = mw[i] * ww[j];
                    if (w <= 0) continue;
                    Add(v, $"macrodetails/universal-{g}-young-{mus[i]}-{wei[j]}", w);
                    Add(v, $"macrodetails/height/{g}-young-{mus[i]}-{wei[j]}-maxheight", w * MathF.Max(0, h * 2 - 1));
                    Add(v, $"macrodetails/height/{g}-young-{mus[i]}-{wei[j]}-minheight", w * MathF.Max(0, 1 - h * 2));
                    Add(v, $"macrodetails/proportions/{g}-young-{mus[i]}-{wei[j]}-idealproportions", w * MathF.Max(0, pr * 2 - 1));
                    Add(v, $"macrodetails/proportions/{g}-young-{mus[i]}-{wei[j]}-uncommonproportions", w * MathF.Max(0, 1 - pr * 2));
                }
            float ra = P("african", 0.333f), rs = P("asian", 0.333f), rc = P("caucasian", 0.334f), sum = MathF.Max(ra + rs + rc, 1e-3f);
            Add(v, $"macrodetails/african-{g}-young", ra / sum);
            Add(v, $"macrodetails/asian-{g}-young", rs / sum);
            Add(v, $"macrodetails/caucasian-{g}-young", rc / sum);
            if (!male)
            {
                // 레퍼런스 그림처럼 높고 둥근 가슴: 크기(cup)는 피팅값 그대로, 탄력은 최대 타깃으로
                var cup = P("breast/cup", 0);
                Add(v, "breast/female-young-averagemuscle-averageweight-maxcup-maxfirmness", MathF.Max(0, cup));
                Add(v, "breast/female-young-averagemuscle-averageweight-mincup-maxfirmness", MathF.Max(0, -cup));
                // 가슴 위치: 레퍼런스처럼 겨드랑이 쪽으로 높게 (피팅 대상이 아닌 디자인 값, trans 는 슬라이더에서 빠져 있다)
                var lift = P("breast/lift", 0.7f);
                Add(v, lift >= 0 ? "breast/breast-trans-up" : "breast/breast-trans-down", MathF.Abs(lift));
            }
            foreach (var (key, neg, pos) in Sliders)
            {
                var s = P(key, 0);
                if (s > 0 && pos is not null) Add(v, pos, s);
                if (s < 0 && neg is not null) Add(v, neg, -s);
            }
            return v;
        }
    }

    /// <summary>MakeHuman 몸 한 벌 (미터, 발바닥 y=0). 메시·피팅이 함께 쓴다.</summary>
    sealed class MhBody
    {
        public Vector3[] V = [];          // 모든 정점 (원래 번호)
        public Vector3[] N = [];
        public Vector3[] Joints = new Vector3[BodyMesh.BoneCount];
        public float Navel, Height;
        public float[] EggT = [];          // 달걀 투영 정도 (0..1)
        public Bone[] Owner = [];          // 정점의 주된 뼈
    }

    static float GetP(BodyShape s, string k, float def) => s.Values.TryGetValue(k, out var v) ? v : def;

    /// <summary>
    /// 디자인 보정: 실루엣 피팅값 위에 더하는 모프 값. 피팅의 다리 목표 치수는 레퍼런스 그림보다 가늘게 잡혀 있어서
    /// (허벅지·종아리가 키 대비 20~30 % 가늘었다) 렌더링 화면에서 직접 잰 너비(키 대비)에 맞춰 둔다.
    /// 여: 허벅지 0.105·종아리 0.063·어깨 0.221 / 남: 허벅지 0.097·종아리 0.065·어깨 0.287 (tools/bodyfit/ref 정면).
    /// </summary>
    static float StyleOffset(bool male, string k) => (male ? MaleStyle : FemaleStyle).TryGetValue(k, out var o) ? o : 0;
    static readonly Dictionary<string, float> FemaleStyle = new()
    {
        ["measure/measure-thigh-circ"] = 1.2f, ["armslegs/upperleg-scale-horiz"] = 0.45f,
        ["measure/measure-calf-circ"] = 0.6f, ["armslegs/lowerleg-scale-horiz"] = 0.45f,
        ["measure/measure-waist-circ"] = 0.7f,
        ["armslegs/upperarm-shoulder-muscle"] = 0.1f,
    };
    static readonly Dictionary<string, float> MaleStyle = new()
    {
        // 피팅은 허벅지를 짧게·정강이를 길게 잡아 무릎이 키의 63 % 높이 → 레퍼런스처럼 긴 허벅지(무릎 ~70 %)로
        ["armslegs/upperlegs-height"] = 1.0f, ["armslegs/lowerlegs-height"] = -1.0f,
        ["measure/measure-upperleg-height"] = 0.6f, ["measure/measure-lowerleg-height"] = -0.6f,
        ["measure/measure-thigh-circ"] = 0.3f, ["armslegs/upperleg-muscle"] = 0.3f,
        ["measure/measure-knee-circ"] = -0.4f,
        ["measure/measure-waist-circ"] = 1.0f,
        ["armslegs/upperarm-shoulder-muscle"] = 0.4f,
    };

    static MhBody MhBuild(MhAssets A, bool male, BodyShape shape, bool normals)
    {
        float P(string k, float d) => GetP(shape, k, d) + StyleOffset(male, k);
        var v = A.Compose(male, P);
        var B = BodyMesh.BoneCount;

        // 우리 14뼈 관절 (dm)
        var j = new Vector3[B];
        j[(int)Bone.Root] = A.Joint(v, "root____head");
        j[(int)Bone.Head] = A.Joint(v, "neck01____head");
        foreach (var (s, L) in new[] { ("L", true), ("R", false) })
        {
            int I(Bone l, Bone r) => (int)(L ? l : r);
            j[I(Bone.UpperL, Bone.UpperR)] = A.Joint(v, $"upperarm01.{s}____head");
            j[I(Bone.ForeL, Bone.ForeR)]   = A.Joint(v, $"lowerarm01.{s}____head");
            j[I(Bone.HandL, Bone.HandR)]   = A.Joint(v, $"wrist.{s}____head");
            j[I(Bone.ThighL, Bone.ThighR)] = A.Joint(v, $"upperleg01.{s}____head");
            j[I(Bone.ShinL, Bone.ShinR)]   = A.Joint(v, $"lowerleg01.{s}____head");
            j[I(Bone.FootL, Bone.FootR)]   = A.Joint(v, $"foot.{s}____head");
        }

        // 팔 내리기: 기본 A 자세 → 레퍼런스처럼 몸 옆에 거의 붙인다 (어깨 축 회전, 팔 가중치만큼)
        var armDeg = P("armAngle", male ? 11f : 7f);
        foreach (var (L, sd) in new[] { (true, 1f), (false, -1f) })
        {
            int u = (int)(L ? Bone.UpperL : Bone.UpperR), f = (int)(L ? Bone.ForeL : Bone.ForeR), h = (int)(L ? Bone.HandL : Bone.HandR);
            var S = j[u]; var dir = Vector3.Normalize(j[h] - S);
            var want = new Vector3(sd * MathF.Sin(armDeg * MathF.PI / 180), -MathF.Cos(armDeg * MathF.PI / 180), dir.Z);
            want = Vector3.Normalize(want);
            var axis = Vector3.Cross(dir, want); var sn = axis.Length();
            if (sn < 1e-6f) continue;
            var q = Quaternion.CreateFromAxisAngle(axis / sn, MathF.Atan2(sn, Vector3.Dot(dir, want)));
            foreach (var i in A.Used)
            {
                var w = A.W14[i * B + u] + A.W14[i * B + f] + A.W14[i * B + h];
                if (w <= 0) continue;
                var r = Vector3.Transform(v[i] - S, q) + S;
                v[i] += (r - v[i]) * MathF.Min(w, 1);
            }
            j[f] = Vector3.Transform(j[f] - S, q) + S;
            j[h] = Vector3.Transform(j[h] - S, q) + S;
        }

        // 다리 모으기: 기본 메시는 다리를 벌리고 서 있다 → 엉덩이 관절 축으로 돌려 발목을 레퍼런스처럼 모은다
        var legDeg = P("legAngle", 1.5f);
        foreach (var (L, sd) in new[] { (true, 1f), (false, -1f) })
        {
            int t = (int)(L ? Bone.ThighL : Bone.ThighR), sn = (int)(L ? Bone.ShinL : Bone.ShinR), ft = (int)(L ? Bone.FootL : Bone.FootR);
            var Hj = j[t]; var dir = Vector3.Normalize(j[ft] - Hj);
            var want = Vector3.Normalize(new Vector3(sd * MathF.Sin(legDeg * MathF.PI / 180), -MathF.Cos(legDeg * MathF.PI / 180), dir.Z));
            var axis = Vector3.Cross(dir, want); var sl = axis.Length();
            if (sl < 1e-6f) continue;
            var q = Quaternion.CreateFromAxisAngle(axis / sl, MathF.Atan2(sl, Vector3.Dot(dir, want)));
            foreach (var i in A.Used)
            {
                var w = A.W14[i * B + t] + A.W14[i * B + sn] + A.W14[i * B + ft];
                if (w <= 0 || v[i].X * sd < 0) continue;
                var r = Vector3.Transform(v[i] - Hj, q) + Hj;
                v[i] += (r - v[i]) * MathF.Min(w, 1);
            }
            j[sn] = Vector3.Transform(j[sn] - Hj, q) + Hj;
            j[ft] = Vector3.Transform(j[ft] - Hj, q) + Hj;
        }

        // 남성 다리: 허벅지 중간·종아리가 굵은 근육 윤곽 (모프는 사타구니 쪽만 키워서 축 기준 단면을 직접 키운다)
        if (male) ThickenLegs(A, v, j, thigh: 1.6f, knee: 0.9f, calf: 1.02f);

        // 주된 뼈
        var owner = new Bone[v.Length];
        foreach (var i in A.Used)
        {
            var best = 0; var bw = -1f;
            for (var b = 0; b < B; b++) if (A.W14[i * B + b] > bw) { bw = A.W14[i * B + b]; best = b; }
            owner[i] = (Bone)best;
        }

        // 달걀형 머리: 얼굴(눈·코·입·귀)을 매끈한 달걀 표면으로 투영, 목 쪽으로 갈수록 서서히
        var head = A.Used.Where(i => A.W14[i * B + (int)Bone.Head] > 0.05f).ToArray();
        var top = head.Max(i => v[i].Y);
        var cz = (head.Max(i => v[i].Z) + head.Min(i => v[i].Z)) * 0.5f;
        var chin = head.Where(i => A.W14[i * B + (int)Bone.Head] > 0.9f && v[i].Z > cz + 0.3f).Min(i => v[i].Y);
        // 해석적 달걀: 타원체인데 아래(턱)로 갈수록 좁아지고 턱 끝이 살짝 앞으로 — 중심에서 방사 투영
        var upper = head.Where(i => v[i].Y > chin + 0.45f * (top - chin)).ToArray();
        var rx = upper.Max(i => MathF.Abs(v[i].X)) * P("headW", 1f);
        var rz = (upper.Max(i => v[i].Z) - upper.Min(i => v[i].Z)) * 0.5f;
        var ry = (top - chin) * 0.5f * P("headH", 1f);   // 레퍼런스 머리는 키의 약 14 %: 정수리를 위로 늘인다
        var ec = new Vector3(0, chin + ry, cz);
        var taper = male ? 0.22f : 0.30f;
        float EggF(Vector3 p)   // < 1 안, > 1 밖
        {
            var q = p - ec;
            var u = q.Y / ry;
            var k = 1 - taper * Smooth(0, 1, -u);                  // 아래로 좁아짐
            var zc = rz * 0.12f * Smooth(0, 1, -u);               // 턱이 살짝 앞으로
            var x = q.X / (rx * k); var y = u; var z = (q.Z - zc) / (rz * k);
            return x * x + y * y + z * z;
        }
        Vector3 EggProject(Vector3 p)
        {
            var d = p - ec;
            if (d.LengthSquared() < 1e-8f) d = Vector3.UnitY;
            float lo = 0, hi = 4;                                  // 중심에서 d 방향으로 표면까지 (배수)
            for (var it = 0; it < 30; it++) { var mid = (lo + hi) * 0.5f; if (EggF(ec + d * mid) < 1) lo = mid; else hi = mid; }
            return ec + d * lo;
        }
        Vector3 EggN(Vector3 p)
        {
            const float e = 0.005f;
            var g = new Vector3(EggF(p + new Vector3(e, 0, 0)) - EggF(p - new Vector3(e, 0, 0)),
                                EggF(p + new Vector3(0, e, 0)) - EggF(p - new Vector3(0, e, 0)),
                                EggF(p + new Vector3(0, 0, e)) - EggF(p - new Vector3(0, 0, e)));
            return g.LengthSquared() > 1e-12f ? Vector3.Normalize(g) : Vector3.UnitY;
        }
        var eggT = new float[v.Length];
        var neckTop = chin - 0.12f * (top - chin);
        foreach (var i in head)
        {
            var t = Smooth(neckTop, chin + 0.04f * (top - chin), v[i].Y) * M.Clamp(A.W14[i * B + (int)Bone.Head] * 1.5f, 0, 1);
            if (t <= 0) continue;
            v[i] = Vector3.Lerp(v[i], EggProject(v[i]), t);
            eggT[i] = t;
        }

        // 젖꼭지 (레퍼런스에는 없다): MakeHuman 젖꼭지 크기 타깃이 움직이는 정점들의 좌우 중심.
        // (breast.L/R 관절은 기본 리그에서 가운데 x 0 으로 잡혀 위치로 쓸 수 없다)
        var nipples = A.Target("breast/nipple-size-incr") is { } nt
            ? new[] { 1f, -1f }.Select(sx => nt.I.Where(i => i < v.Length && v[i].X * sx > 0).Select(i => v[i]).Aggregate(Vector3.Zero, (acc, p) => acc + p)
                                             / Math.Max(1, nt.I.Count(i => i < v.Length && v[i].X * sx > 0))).ToArray()
            : [];
        float NearNipple(int i) => nipples.Length == 0 ? 0 : nipples.Max(c => M.Clamp(1.5f - Vector3.Distance(v[i], c) / 0.4f, 0, 1));

        // 레퍼런스처럼 조각 같은 근육: 기본 메시의 근육 굴곡(원본 − 저역 통과)을 법선 방향으로 키운다. 머리·손·발·젖꼭지는 그대로.
        Define(A, v, male ? 2.0f : 0.7f, i => (1 - A.W14[i * B + (int)Bone.Head] - A.W14[i * B + (int)Bone.HandL] - A.W14[i * B + (int)Bone.HandR]
                                                - A.W14[i * B + (int)Bone.FootL] - A.W14[i * B + (int)Bone.FootR]) * (1 - eggT[i]) * (1 - NearNipple(i)));

        // 젖꼭지 같은 잔 형태는 크롬에서 튀므로 국소 평활
        foreach (var c in nipples)
            Dome(A, v, c, 0.45f);

        // 샅: 해부학적 굴곡 대신 마네킹처럼 매끈한 면 (레퍼런스, 선정적이지 않게)
        var crotchY = A.Used.Where(i => MathF.Abs(v[i].X) < 0.2f && A.W14[i * B + (int)Bone.Root] > 0.5f).Min(i => v[i].Y);
        var pubis = v[A.Used.Where(i => MathF.Abs(v[i].X) < 0.15f && v[i].Y > crotchY && v[i].Y < crotchY + 0.6f).MaxBy(i => v[i].Z)];
        Dome(A, v, pubis, 0.55f);

        // 남성 복근: 기본 메시에 없는 식스팩 홈을 조각한다 (배꼽 ~ 가슴 아래, 가운데 세로선·가로 3줄·바깥 경계)
        if (male && nipples.Length == 2 && A.Target("stomach/stomach-navel-in") is { } navT)
            CarveAbs(A, v, navT.I.Where(i => i < v.Length).Aggregate(Vector3.Zero, (acc, i) => acc + v[i]) / navT.I.Length, nipples[0].Y);

        // 미터로, 발바닥 y = 0
        var minY = A.Used.Min(i => v[i].Y);
        var height = A.Used.Max(i => v[i].Y) - minY;
        var target = male ? 1.876f : 1.766f;
        var scale = target / height;
        Vector3 ToM(Vector3 p) => new Vector3(p.X, p.Y - minY, p.Z) * scale;
        for (var i = 0; i < v.Length; i++) v[i] = ToM(v[i]);
        for (var b = 0; b < B; b++) j[b] = ToM(j[b]);

        var body = new MhBody { V = v, Joints = j, Height = target, EggT = eggT, Owner = owner };
        // 배꼽: stomach-navel 타깃이 움직이는 정점들의 중심
        if (A.Target("stomach/stomach-navel-in") is { } nav)
            body.Navel = nav.I.Where(i => i < v.Length).Select(i => v[i].Y).Average();

        if (normals)
        {
            var n = new Vector3[v.Length];
            for (var t = 0; t < A.Tris.Length; t += 3)
            {
                int a = A.Tris[t], b = A.Tris[t + 1], c = A.Tris[t + 2];
                var fn = Vector3.Cross(v[b] - v[a], v[c] - v[a]);
                n[a] += fn; n[b] += fn; n[c] += fn;
            }
            for (var i = 0; i < n.Length; i++)
            {
                n[i] = n[i].LengthSquared() > 1e-20f ? Vector3.Normalize(n[i]) : Vector3.UnitY;
                if (eggT[i] > 0)
                {
                    var p = v[i] / scale + new Vector3(0, minY, 0);
                    n[i] = Vector3.Normalize(Vector3.Lerp(n[i], EggN(p), eggT[i]));
                }
            }
            body.N = n;
        }
        return body;
    }

    /// <summary>
    /// 식스팩: 배 앞면(정면 +Z)을 안쪽으로 눌러 홈을 판다 (dm 단위).
    /// 세로 백선(가운데), 가로 건획 3줄(배꼽 위 0.3·0.78·1.22 dm, 위로 갈수록 바깥이 살짝 올라감), 바깥 반달선(|x| 0.95 dm).
    /// 홈 단면은 가우스(σ 1.6 cm), 깊이 ~10 mm. 배꼽 아래와 가슴 아래 경계에서 부드럽게 사라진다.
    /// </summary>
    static void CarveAbs(MhAssets A, Vector3[] v, Vector3 navel, float nippleY)
    {
        const float depth = 0.10f, w = 0.16f;          // 홈 폭은 기본 메시 간격(~1.5 cm)보다 넓어야 표현된다
        var top = nippleY - 0.75f;                     // 흉근 아래
        float G(float d) => MathF.Exp(-d * d / (2 * w * w));
        foreach (var i in A.Used)
        {
            var p = v[i];
            var x = p.X - navel.X; var y = p.Y - navel.Y;
            if (p.Z < navel.Z - 0.6f || y < -0.6f || y > top - navel.Y + 0.3f || MathF.Abs(x) > 1.3f) continue;
            var region = Smooth(-0.6f, -0.1f, y) * (1 - Smooth(top - navel.Y - 0.1f, top - navel.Y + 0.3f, y)) * (1 - Smooth(1.0f, 1.3f, MathF.Abs(x)));
            if (region <= 0) continue;
            var ax = MathF.Abs(x);
            var groove = G(x) * 0.9f;                                                     // 백선
            foreach (var row in new[] { 0.3f, 0.78f, 1.22f })
                if (row < top - navel.Y) groove = MathF.Max(groove, G(y - row - 0.12f * ax * ax) * (1 - Smooth(0.7f, 0.95f, ax)));
            groove = MathF.Max(groove, G(ax - 0.95f) * 0.8f * Smooth(-0.3f, 0.2f, y));      // 반달선
            v[i].Z -= depth * groove * region;
        }
    }

    /// <summary>
    /// 다리 단면을 뼈 축(엉덩이 → 무릎 → 발목)에서 바깥으로 키운다. 축 위치 u: 0 엉덩이, 1 무릎, 2 발목.
    /// 배율 윤곽: 엉덩이 쪽은 70 %, 허벅지 위쪽(u 0.3)에서 thigh(레퍼런스는 위가 가장 굵은 역삼각형), 무릎(u 0.9~1.05)에서 knee, 종아리(u 1.35)에서 calf, 발목에서 1.
    /// 다리 안쪽(몸 가운데 쪽)은 20 %만 키워 두 허벅지가 붙지 않게 한다. 다리 뼈 가중치만큼만.
    /// </summary>
    static void ThickenLegs(MhAssets A, Vector3[] v, Vector3[] j, float thigh, float knee, float calf)
    {
        var B = BodyMesh.BoneCount;
        (float u, float f)[] prof = [(0f, 1 + (thigh - 1) * 0.7f), (0.3f, thigh), (0.9f, knee), (1.05f, knee), (1.35f, calf), (1.8f, 1 + (calf - 1) * 0.3f), (2f, 1f)];
        float F(float u)
        {
            for (var k = 1; k < prof.Length; k++)
                if (u <= prof[k].u) return prof[k - 1].f + (prof[k].f - prof[k - 1].f) * Smooth(prof[k - 1].u, prof[k].u, u);
            return 1;
        }
        foreach (var (L, sd) in new[] { (true, 1f), (false, -1f) })
        {
            int t = (int)(L ? Bone.ThighL : Bone.ThighR), sn = (int)(L ? Bone.ShinL : Bone.ShinR), ft = (int)(L ? Bone.FootL : Bone.FootR);
            Vector3 hip = j[t], kn = j[sn], an = j[ft];
            foreach (var i in A.Used)
            {
                var w = MathF.Min(1, A.W14[i * B + t] + A.W14[i * B + sn]);
                if (w <= 0 || v[i].X * sd < 0) continue;
                var p = v[i];
                float Seg(Vector3 a, Vector3 b, out Vector3 c) { var ab = b - a; var x = M.Clamp(Vector3.Dot(p - a, ab) / ab.LengthSquared(), 0, 1); c = a + ab * x; return x; }
                var a1 = Seg(hip, kn, out var c1); var a2 = Seg(kn, an, out var c2);
                var upper = Vector3.DistanceSquared(p, c1) <= Vector3.DistanceSquared(p, c2);
                var u = upper ? a1 : 1 + a2; var c = upper ? c1 : c2;
                var d = p - c;
                var f = F(u);
                if (d.X * sd < 0) f = 1 + (f - 1) * 0.2f;      // 안쪽: 레퍼런스처럼 허벅지 사이 틈이 남게
                v[i] = c + d * (1 + (f - 1) * w);
            }
        }
    }

    static float Smooth(float a, float b, float x)
    {
        var t = M.Clamp((x - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>
    /// 근육 굴곡 강조 (기하 대역 통과): 레퍼런스 그림처럼 근육 덩어리만 또렷하고 표면은 매끈하게.
    ///   fine   = Taubin 평활 3쌍: 피부 수준 잔 굴곡(갈비뼈 자국, 힘줄, 요철)을 지운 면
    ///   coarse = Taubin 평활 24쌍: 근육 한 덩이 폭(약 5 cm) 아래를 걸러 낸 큰 형태
    /// 정점을 fine 으로 옮기고 (fine − coarse) 의 법선 성분을 gain 배 더한다.
    /// Taubin 은 팔·다리 같은 가는 원통을 수축시키지 않아서 큰 실루엣(체형 피팅)이 흔들리지 않는다.
    /// </summary>
    static void Define(MhAssets A, Vector3[] v, float gain, Func<int, float> weight)
    {
        var ids = A.Used;
        Vector3[] Taubin(Vector3[] src, int pairs)
        {
            var s = (Vector3[])src.Clone();
            var tmp = new Vector3[s.Length];
            for (var it = 0; it < pairs * 2; it++)
            {
                var step = it % 2 == 0 ? 0.5f : -0.53f;
                foreach (var i in ids)
                {
                    var nb = A.Adj[i];
                    if (nb.Length == 0) { tmp[i] = s[i]; continue; }
                    var avg = Vector3.Zero;
                    foreach (var x in nb) avg += s[x];
                    tmp[i] = s[i] + (avg / nb.Length - s[i]) * step;
                }
                foreach (var i in ids) s[i] = tmp[i];
            }
            return s;
        }
        var fine = Taubin(v, 3);
        var coarse = Taubin(v, 24);
        var n = new Vector3[v.Length];
        for (var t = 0; t < A.Tris.Length; t += 3)
        {
            int a = A.Tris[t], b = A.Tris[t + 1], c = A.Tris[t + 2];
            var fn = Vector3.Cross(coarse[b] - coarse[a], coarse[c] - coarse[a]);
            n[a] += fn; n[b] += fn; n[c] += fn;
        }
        foreach (var i in ids)
        {
            var w = M.Clamp(weight(i), 0, 1);
            if (w <= 0 || n[i].LengthSquared() < 1e-20f) continue;
            var nn = Vector3.Normalize(n[i]);
            var target = fine[i] + nn * (Vector3.Dot(fine[i] - coarse[i], nn) * gain);
            v[i] = Vector3.Lerp(v[i], target, w);
        }
    }

    /// <summary>
    /// 반지름 r 안의 잔 형태(젖꼭지)를 지우고 둘레 곡면을 이어 붙인 돔으로 바꾼다:
    /// r~1.8r 고리 정점들로 국소 높이장 h = a + bx + cy + dx² + exy + fy² 를 최소제곱으로 맞추고, 안쪽 정점을 그 높이로 옮긴다.
    /// 평활은 원뿔처럼 뾰족한 가슴 끝을 그대로 남기지만, 맞춘 곡면은 가슴의 둥근 형태만 잇는다.
    /// </summary>
    static void Dome(MhAssets A, Vector3[] v, Vector3 c, float r)
    {
        var inner = A.Used.Where(i => Vector3.Distance(v[i], c) < r).ToArray();
        var ring = A.Used.Where(i => Vector3.Distance(v[i], c) is var d && d >= r && d < r * 1.8f).ToArray();
        if (inner.Length == 0 || ring.Length < 12) return;
        var mid = ring.Aggregate(Vector3.Zero, (s, i) => s + v[i]) / ring.Length;
        var n = Vector3.Normalize(c - mid);                     // 고리 평면에서 젖꼭지 쪽 = 바깥
        var t1 = Vector3.Normalize(Vector3.Cross(n, MathF.Abs(n.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
        var t2 = Vector3.Cross(n, t1);
        float[] Basis(Vector3 p) { var q = p - c; float x = Vector3.Dot(q, t1), y = Vector3.Dot(q, t2); return [1, x, y, x * x, x * y, y * y]; }
        // 정규방정식 (6×6) 가우스 소거
        var m = new double[6, 7];
        foreach (var i in ring)
        {
            var f = Basis(v[i]); var h = Vector3.Dot(v[i] - c, n);
            for (var a = 0; a < 6; a++) { for (var b = 0; b < 6; b++) m[a, b] += f[a] * f[b]; m[a, 6] += f[a] * h; }
        }
        for (var a = 0; a < 6; a++) m[a, a] += 1e-9;
        for (var col = 0; col < 6; col++)
        {
            var piv = Enumerable.Range(col, 6 - col).MaxBy(row => Math.Abs(m[row, col]));
            for (var k = 0; k < 7; k++) (m[col, k], m[piv, k]) = (m[piv, k], m[col, k]);
            for (var row = 0; row < 6; row++)
            {
                if (row == col) continue;
                var fct = m[row, col] / m[col, col];
                for (var k = col; k < 7; k++) m[row, k] -= fct * m[col, k];
            }
        }
        var coef = Enumerable.Range(0, 6).Select(k => (float)(m[k, 6] / m[k, k])).ToArray();
        foreach (var i in inner)
        {
            var f = Basis(v[i]);
            var h = Vector3.Dot(v[i] - c, n);
            var fit = 0f; for (var k = 0; k < 6; k++) fit += coef[k] * f[k];
            var w = Smooth(r, r * 0.6f, Vector3.Distance(v[i], c));   // 가장자리에서 고리와 매끄럽게
            v[i] += n * ((fit - h) * w);
        }
    }

    /// <summary>MakeHuman 몸 → 엔진 메시 (body 정점만, 14뼈 가중치 상위 4, 이음선)</summary>
    static BodyMesh MhMesh(MhAssets A, bool male, BodyShape shape)
    {
        var body = MhBuild(A, male, shape, true);
        var B = BodyMesh.BoneCount;
        var remap = new int[body.V.Length]; Array.Fill(remap, -1);
        for (var k = 0; k < A.Used.Length; k++) remap[A.Used[k]] = k;

        var geo = new BodyMesh
        {
            Vertices = A.Used.Select(i => body.V[i]).ToArray(),
            Normals  = A.Used.Select(i => body.N[i]).ToArray(),
            Indices  = A.Tris.Select(i => remap[i]).ToArray(),
            Bones    = new int[A.Used.Length * 4],
            Weights  = new float[A.Used.Length * 4],
            Seams    = new float[A.Used.Length],
        };
        for (var k = 0; k < A.Used.Length; k++)
        {
            var i = A.Used[k];
            var top = Enumerable.Range(0, B).OrderByDescending(b => A.W14[i * B + b]).Take(4).ToArray();
            var sum = top.Sum(b => A.W14[i * B + b]);
            for (var s = 0; s < 4; s++)
            {
                geo.Bones[k * 4 + s] = top[s];
                geo.Weights[k * 4 + s] = sum > 0 ? A.W14[i * B + top[s]] / sum : (s == 0 ? 1 : 0);
            }
            geo.Seams[k] = MhSeam(body, male, body.V[i], body.Owner[i]);
        }

        // 크롬은 다각형 면이 그대로 비친다 → Loop 세분 (가중치·이음선·머리 법선도 함께 나눈다)
        var eggT = A.Used.Select(i => body.EggT[i]).ToArray();
        LoopSubdivide(geo, ref eggT);
        LoopSubdivide(geo, ref eggT);   // 2회: 정점 간격 ~3 mm → 레퍼런스 그림(1 px ≈ 2 mm)의 근육 디테일이 뭉개지지 않게

        int[] parent = [-1, 0, 0, 0, (int)Bone.UpperR, (int)Bone.UpperL, (int)Bone.ForeR, (int)Bone.ForeL,
                        0, 0, (int)Bone.ThighR, (int)Bone.ThighL, (int)Bone.ShinR, (int)Bone.ShinL];
        var jt = body.Joints;
        for (var b = 0; b < B; b++)
        {
            geo.Parent[b] = parent[b];
            geo.Global[b] = Transform3D.FromTranslation(jt[b]);
            geo.Rest[b]   = Transform3D.FromTranslation(parent[b] < 0 ? jt[b] : jt[b] - jt[parent[b]]);
        }
        return geo;
    }

    // 레퍼런스의 외피 이음선 (평면과 표면이 만나는 곡선, 부호 거리)
    static float MhSeam(MhBody body, bool male, Vector3 p, Bone owner)
    {
        var d = 1f;
        void Cut(Vector3 o, Vector3 n, bool region)
        {
            if (!region) return;
            var s = Vector3.Dot(p - o, Vector3.Normalize(n));
            if (M.Abs(s) < M.Abs(d)) d = s;
        }
        var j = body.Joints;
        var torsoOrLeg = owner is Bone.Root or Bone.ThighL or Bone.ThighR;
        foreach (var (L, sd) in new[] { (true, 1f), (false, -1f) })
        {
            if (p.X * sd <= 0) continue;
            var S = j[(int)(L ? Bone.UpperL : Bone.UpperR)];
            var E = j[(int)(L ? Bone.ForeL : Bone.ForeR)];
            var W = j[(int)(L ? Bone.HandL : Bone.HandR)];
            var H = j[(int)(L ? Bone.ThighL : Bone.ThighR)];
            // 하이컷: 골반 뼈 위 → 샅
            var top = new Vector3(H.X * 1.55f, H.Y + 0.13f, 0);
            var bot = new Vector3(sd * 0.028f, H.Y - 0.075f, 0);
            // 법선을 늘 위쪽으로: 좌우 평면의 부호가 가운데에서 뒤집혀 가짜 선이 생기지 않게
            var hn = Vector3.Cross(bot - top, Vector3.UnitZ); if (hn.Y < 0) hn = -hn;
            Cut(top, hn, torsoOrLeg && p.Y > bot.Y - 0.01f && p.Y < top.Y + 0.02f);
            var dir = Vector3.Normalize(W - E);
            Cut(W - dir * 0.012f, dir, owner is Bone.ForeL or Bone.ForeR or Bone.HandL or Bone.HandR && Vector3.Distance(p, W) < 0.05f);
            if (!male)
                Cut(S + new Vector3(-sd * 0.03f, 0, 0), new Vector3(sd, -0.45f, 0),
                    owner is Bone.Root or Bone.UpperL or Bone.UpperR && p.Y > S.Y - 0.09f && p.Y < S.Y + 0.08f && M.Abs(p.X) > M.Abs(S.X) * 0.55f);
        }
        if (!male)
        {
            var nb = j[(int)Bone.Head];
            Cut(nb + new Vector3(0, 0.075f, 0), new Vector3(0, 1, -0.4f), p.Y > nb.Y + 0.03f && p.Y < nb.Y + 0.14f && p.X * p.X + (p.Z - nb.Z) * (p.Z - nb.Z) < 0.0049f);
        }
        return d;
    }

    /// <summary>
    /// Loop 세분: 삼각형 하나를 넷으로, 정점은 Loop 가중으로 매끈하게 옮긴다.
    /// 뼈 가중치는 (뼈별로 합친 뒤) 상위 4개, 이음선 거리는 선형으로 섞는다.
    /// </summary>
    static void LoopSubdivide(BodyMesh g, ref float[] eggT)
    {
        var v = g.Vertices; var idx = g.Indices; var n0 = v.Length;
        var edgeId = new Dictionary<long, int>();
        var edgeOpp = new Dictionary<long, List<int>>();
        long Key(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        var nb = new HashSet<int>[n0];
        for (var i = 0; i < n0; i++) nb[i] = [];
        for (var t = 0; t < idx.Length; t += 3)
            for (var k = 0; k < 3; k++)
            {
                int a = idx[t + k], b = idx[t + (k + 1) % 3], c = idx[t + (k + 2) % 3];
                var key = Key(a, b);
                if (!edgeOpp.TryGetValue(key, out var l)) edgeOpp[key] = l = [];
                l.Add(c);
                nb[a].Add(b); nb[b].Add(a);
            }

        var nv = new List<Vector3>(n0 + edgeOpp.Count);
        var B = BodyMesh.BoneCount;
        var w = new List<float[]>(n0 + edgeOpp.Count);
        var seam = new List<float>(n0 + edgeOpp.Count);
        var egg = new List<float>(n0 + edgeOpp.Count);
        float[] Dense(int i)
        {
            var d = new float[B];
            for (var s = 0; s < 4; s++) d[g.Bones[i * 4 + s]] += g.Weights[i * 4 + s];
            return d;
        }
        var dense = Enumerable.Range(0, n0).Select(Dense).ToArray();

        // 원래 정점: Loop 가중 이동 (경계는 그대로)
        var boundary = new bool[n0];
        foreach (var (key, opp) in edgeOpp)
            if (opp.Count == 1) { boundary[(int)(key >> 32)] = true; boundary[(int)(key & 0xffffffff)] = true; }
        for (var i = 0; i < n0; i++)
        {
            var k = nb[i].Count;
            if (k == 0 || boundary[i]) nv.Add(v[i]);
            else
            {
                var beta = k == 3 ? 3f / 16 : 3f / (8 * k);
                var sum = Vector3.Zero; foreach (var j in nb[i]) sum += v[j];
                nv.Add(v[i] * (1 - k * beta) + sum * beta);
            }
            w.Add(dense[i]); seam.Add(g.Seams[i]); egg.Add(eggT[i]);
        }
        // 모서리 점
        foreach (var (key, opp) in edgeOpp)
        {
            int a = (int)(key >> 32), b = (int)(key & 0xffffffff);
            var p = opp.Count == 2 ? (v[a] + v[b]) * 0.375f + (v[opp[0]] + v[opp[1]]) * 0.125f : (v[a] + v[b]) * 0.5f;
            edgeId[key] = nv.Count;
            nv.Add(p);
            var d = new float[B]; for (var bb = 0; bb < B; bb++) d[bb] = (dense[a][bb] + dense[b][bb]) * 0.5f;
            w.Add(d); seam.Add((g.Seams[a] + g.Seams[b]) * 0.5f); egg.Add((eggT[a] + eggT[b]) * 0.5f);
        }
        var ni = new int[idx.Length * 4]; var o = 0;
        for (var t = 0; t < idx.Length; t += 3)
        {
            int a = idx[t], b = idx[t + 1], c = idx[t + 2];
            int ab = edgeId[Key(a, b)], bc = edgeId[Key(b, c)], ca = edgeId[Key(c, a)];
            foreach (var x in new[] { a, ab, ca, ab, b, bc, ca, bc, c, ab, bc, ca }) ni[o++] = x;
        }

        // 법선: 새 면에서 다시, 머리(달걀) 쪽은 원래 해석 법선을 섞는다
        var verts = nv.ToArray();
        var normals = new Vector3[verts.Length];
        for (var t = 0; t < ni.Length; t += 3)
        {
            var fn = Vector3.Cross(verts[ni[t + 1]] - verts[ni[t]], verts[ni[t + 2]] - verts[ni[t]]);
            normals[ni[t]] += fn; normals[ni[t + 1]] += fn; normals[ni[t + 2]] += fn;
        }
        var oldN = g.Normals;
        for (var i = 0; i < normals.Length; i++)
            normals[i] = normals[i].LengthSquared() > 1e-20f ? Vector3.Normalize(normals[i]) : Vector3.UnitY;
        // 면 감기 방향이 안쪽이면 전부 뒤집는다 (원래 정점 법선과 다수결)
        var agree = 0; for (var i = 0; i < n0; i++) agree += Vector3.Dot(normals[i], oldN[i]) >= 0 ? 1 : -1;
        if (agree < 0) for (var i = 0; i < normals.Length; i++) normals[i] = -normals[i];
        // 달걀 머리: 얼굴 자리를 눌러 담은 면이라 면 법선이 고르지 않다 → 원래(해석) 법선을 보간해 섞는다
        var interp = new Vector3[verts.Length];
        for (var i = 0; i < n0; i++) interp[i] = oldN[i];
        foreach (var (key, id) in edgeId) interp[id] = oldN[(int)(key >> 32)] + oldN[(int)(key & 0xffffffff)];
        for (var i = 0; i < normals.Length; i++)
            if (egg[i] > 0 && interp[i].LengthSquared() > 1e-12f)
                normals[i] = Vector3.Normalize(Vector3.Lerp(normals[i], Vector3.Normalize(interp[i]), M.Clamp(egg[i] * 1.5f, 0, 1)));

        g.Vertices = verts; g.Normals = normals; g.Indices = ni;
        g.Bones = new int[verts.Length * 4]; g.Weights = new float[verts.Length * 4];
        for (var i = 0; i < verts.Length; i++)
        {
            var d = w[i];
            var top = Enumerable.Range(0, B).OrderByDescending(bb => d[bb]).Take(4).ToArray();
            var sum = top.Sum(bb => d[bb]);
            for (var s2 = 0; s2 < 4; s2++)
            {
                g.Bones[i * 4 + s2] = top[s2];
                g.Weights[i * 4 + s2] = sum > 0 ? d[top[s2]] / sum : (s2 == 0 ? 1 : 0);
            }
        }
        g.Seams = seam.ToArray();
        eggT = egg.ToArray();
    }
}
