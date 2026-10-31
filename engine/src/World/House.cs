using System.Numerics;

namespace DollyForge;

/// <summary>
/// 집 설계도(content/dolly/houses.json) 한 채를 거리장으로 짓는다. 좌표는 집 기준: 건물 모서리 (0, 0, 0), x 오른쪽, z 안쪽(앞면 z = 0 이 길 쪽), y 위.
///   골격: 방 테두리마다 벽(바깥벽 0.2 m 벽돌, 칸막이 0.12 m 회벽), 바닥, 천장, 박공지붕(평면으로 자른 박공벽), 문·창 개구부
///   가구: 아이템의 실제 치수(mm)로 만든 둥근 기본형. 문짝은 방 안쪽으로 열어 둔다
/// 모든 기본형은 부드러운 합집합이라 벽과 천장, 다리와 상판의 이음이 곡면으로 이어진다.
/// </summary>
sealed class HouseModel
{
    public const float FloorY = 0.02f;
    public SdfScene Shell = new() { Blend = 0.03f };
    public readonly List<(string Item, SdfScene Scene)> Furniture = [];
    public readonly List<SdfPrim> Glass = [];
    public readonly List<Vector3> Lamps = [];
    public Vector3 Spawn;
    public float SpawnYaw;                 // Actor.Yaw (0 = +z 를 본다)
    public Vector2 Size;
    public float WallHeight;
    public SdfScene Collision = new() { Blend = 0.01f };

    static readonly Vector3 Plaster = new(0.74f, 0.72f, 0.68f);
    static readonly Vector3 FrameWhite = new(0.90f, 0.90f, 0.88f);

    /// <summary>골격 색: 바깥벽 안쪽 면은 회벽, 바닥 슬래브의 바깥에 드러난 곳은 콘크리트</summary>
    public Vector4 ShellColor(Vector3 p, Vector3 brick, Vector3 floor, Vector3 concrete)
    {
        Shell.EvalColor(p, out var c, out _);
        var inside = p.X > 0.1f && p.X < Size.X - 0.1f && p.Z > 0.1f && p.Z < Size.Y - 0.1f && p.Y < WallHeight + 0.05f;
        if (c == brick && inside) c = Plaster;
        if (c == floor && !inside) c = concrete;
        return new Vector4(c, 0);
    }

    public static HouseModel Build(ContentPack pack, HouseDef def, bool furniture)
    {
        var m = new HouseModel { Size = def.Footprint, WallHeight = def.WallHeight };
        Vector3 Mat(string key, Vector3 fb) => def.Shell.TryGetValue(key, out var id) ? pack.Color(id, fb) : fb;
        var brick = Mat("walls", new(0.62f, 0.30f, 0.22f));
        var roof = Mat("roof", new(0.70f, 0.42f, 0.30f)) * 0.75f;
        var floor = Mat("floor", new(0.55f, 0.40f, 0.25f));
        var concrete = pack.Color("concrete", new(0.66f, 0.65f, 0.62f));
        float W = def.Footprint.X, D = def.Footprint.Y, H = def.WallHeight, top = H + 0.15f;
        var s = m.Shell;

        // 바닥 (오크 마루, 바깥 둘레는 콘크리트 기초) · 천장
        s.Add(SdfPrim.Box(new(-0.1f, -0.35f, -0.1f), new(W + 0.1f, FloorY, D + 0.1f), 0.02f, floor));
        s.Add(SdfPrim.Box(new(0, H, 0), new(W, top, D), 0.02f, Plaster));

        // 벽: 방 테두리마다 (같은 선은 겹쳐도 합집합이라 하나)
        foreach (var r in def.Rooms)
        {
            float x0 = r.Pos.X, z0 = r.Pos.Y, x1 = r.Pos.X + r.Size.X, z1 = r.Pos.Y + r.Size.Y;
            void Wall(float ax, float az, float bx, float bz)
            {
                var exterior = (ax == bx && (ax <= 0.001f || ax >= W - 0.001f)) || (az == bz && (az <= 0.001f || az >= D - 0.001f));
                var t = exterior ? 0.1f : 0.06f;
                s.Add(SdfPrim.Box(new(MathF.Min(ax, bx) - t, -0.05f, MathF.Min(az, bz) - t), new(MathF.Max(ax, bx) + t, top, MathF.Max(az, bz) + t),
                    0.015f, exterior ? brick : Plaster));
            }
            Wall(x0, z0, x1, z0); Wall(x0, z1, x1, z1); Wall(x0, z0, x0, z1); Wall(x1, z0, x1, z1);
        }

        // 박공지붕: 앞·뒤 지붕판(기울인 둥근 상자)과, 지붕 아래면으로 자른 박공벽
        var ridgeH = 1.6f; var over = 0.45f; var slope = ridgeH / (D / 2);
        var nf = Vector3.Normalize(new Vector3(0, 1, -slope)); var nb = Vector3.Normalize(new Vector3(0, 1, slope));
        foreach (var (n, z0, dir) in new[] { (nf, -over, 1f), (nb, D + over, -1f) })
        {
            var u = Vector3.Normalize(new Vector3(0, slope, dir));             // 처마 → 용마루
            var horiz = D / 2 + over;
            var len = horiz * MathF.Sqrt(1 + slope * slope);
            var zMid = z0 + dir * horiz / 2;
            var under = new Vector3(W / 2, top + (dir > 0 ? zMid : D - zMid) * slope, zMid);
            s.Add(SdfPrim.Oriented(under + n * 0.06f, Vector3.UnitX, n, u, new(W / 2 + over, 0.06f, len / 2 + 0.03f), 0.03f, roof));
        }
        var cf = Vector3.Dot(nf, new Vector3(0, top, 0)); var cb = Vector3.Dot(nb, new Vector3(0, top, D));
        foreach (var x in new[] { 0f, W })
        {
            var g = SdfPrim.Box(new(x - 0.1f, H, 0), new(x + 0.1f, top + ridgeH + 0.1f, D), 0.015f, brick);
            g.Clip = [(nf, cf), (nb, cb)];
            s.Add(g);
        }

        // 문·창 개구부와 문짝·창틀, 그리고 가구
        foreach (var r in def.Rooms)
        {
            var roomCenter = new Vector2(r.Pos.X + r.Size.X / 2, r.Pos.Y + r.Size.Y / 2);
            foreach (var f in r.Furniture)
            {
                if (!pack.Items.TryGetValue(f.Item, out var item)) continue;
                if (f.Wall) { Opening(pack, m, item, r, f, roomCenter, furniture); continue; }
                if (!furniture) continue;
                var scene = Furnish(pack, item, r, f, m);
                if (scene is null) continue;
                m.Furniture.Add((f.Item, scene));
                if (f.SpawnPoint)
                {
                    // 침대 옆 (가구 앞쪽 +z 방향으로 0.45 m), 방 가운데를 본다
                    var c = (scene.Min + scene.Max) * 0.5f;
                    m.Spawn = new Vector3(c.X, FloorY, scene.Max.Z + 0.45f);
                    var look = roomCenter + new Vector2(0.6f, 0) - new Vector2(m.Spawn.X, m.Spawn.Z);
                    m.SpawnYaw = MathF.Atan2(look.X, look.Y);
                }
            }
        }

        // 충돌: 골격(개구부로 깎임) + 고체: 가구(카펫·전등 제외)·문짝·유리는 개구부에 깎이지 않는다
        m.Collision.Adds.AddRange(s.Adds); m.Collision.Subs.AddRange(s.Subs);
        m.Collision.Min = s.Min; m.Collision.Max = s.Max;
        foreach (var (item, sc) in m.Furniture)
            if (item is not ("carpet" or "electric_lamp_home"))
                foreach (var p in sc.Adds) m.Collision.AddSolid(p);
        foreach (var g in m.Glass) m.Collision.AddSolid(g);
        return m;
    }

    /// <summary>벽의 문·창: 개구부를 빼고, 문짝은 방 안쪽으로 열어 두고, 창은 흰 창틀 + 유리</summary>
    static void Opening(ContentPack pack, HouseModel m, ContentItem item, RoomDef r, FurnitureDef f, Vector2 roomCenter, bool furniture)
    {
        var isDoor = item.Id.StartsWith("door");
        var w = isDoor ? item.Mm[0] / 1000f : 1.2f;
        float y0 = isDoor ? FloorY + 0.012f : 0.9f, y1 = isDoor ? 2.1f : 2.1f;   // 문은 바닥을 파지 않고 1 cm 문턱을 남긴다
        var alongX = MathF.Abs(f.Yaw % 180) < 1;
        var a = new Vector2(r.Pos.X + f.Pos.X, r.Pos.Y + f.Pos.Y);          // 개구부 시작점 (벽 선 위)
        var b = a + (alongX ? new Vector2(w, 0) : new Vector2(0, w));
        Vector3 Lo(float t) => alongX ? new(a.X, y0, a.Y - t) : new(a.X - t, y0, a.Y);
        Vector3 Hi(float t) => alongX ? new(b.X, y1, b.Y + t) : new(b.X + t, y1, b.Y);
        // 깊이 ±0.6 m: 빼기 거리장은 근사라, 몸 구(반지름 0.25 m)가 벽면 앞에서 구멍을 보려면 상자가 벽 밖으로 충분히 나와야 한다
        m.Shell.Sub(SdfPrim.Box(Lo(0.6f), Hi(0.6f), 0.02f, Vector3.One));
        var frameCol = isDoor ? pack.Color("oak", new(0.55f, 0.4f, 0.25f)) : FrameWhite;
        // 둘레 틀 (문은 문틀, 창은 창틀): 4 변
        const float ft = 0.05f, fd = 0.08f;
        var lo = Lo(fd); var hi = Hi(fd);
        if (alongX)
        {
            m.Shell.Add(SdfPrim.Box(new(lo.X - ft, lo.Y, lo.Z), new(lo.X, hi.Y + ft, hi.Z), 0.01f, frameCol));
            m.Shell.Add(SdfPrim.Box(new(hi.X, lo.Y, lo.Z), new(hi.X + ft, hi.Y + ft, hi.Z), 0.01f, frameCol));
        }
        else
        {
            m.Shell.Add(SdfPrim.Box(new(lo.X, lo.Y, lo.Z - ft), new(hi.X, hi.Y + ft, lo.Z), 0.01f, frameCol));
            m.Shell.Add(SdfPrim.Box(new(lo.X, lo.Y, hi.Z), new(hi.X, hi.Y + ft, hi.Z + ft), 0.01f, frameCol));
        }
        m.Shell.Add(SdfPrim.Box(new(lo.X - (alongX ? ft : 0), hi.Y, lo.Z - (alongX ? 0 : ft)), new(hi.X + (alongX ? ft : 0), hi.Y + ft, hi.Z + (alongX ? 0 : ft)), 0.01f, frameCol));
        if (!isDoor)
        {
            m.Shell.Add(SdfPrim.Box(new(lo.X - (alongX ? ft : 0), lo.Y - ft, lo.Z - (alongX ? 0 : ft)), new(hi.X + (alongX ? ft : 0), lo.Y, hi.Z + (alongX ? 0 : ft)), 0.01f, frameCol));
            m.Glass.Add(SdfPrim.Box(alongX ? new(a.X, y0, a.Y - 0.004f) : new(a.X - 0.004f, y0, a.Y), alongX ? new(b.X, y1, b.Y + 0.004f) : new(b.X + 0.004f, y1, b.Y + 0.004f), 0.002f, new(0.8f, 0.9f, 0.95f)));
            return;
        }
        if (!furniture) return;
        // 문짝: 경첩(개구부 시작점)에서 +x·+z 쪽 방으로 90° 열림 (지나가는 길을 막지 않게)
        const float side = 1;
        var col = pack.Color(item.Mat, new(0.55f, 0.4f, 0.25f));
        var leaf = alongX
            ? SdfPrim.Box(new(a.X, FloorY, MathF.Min(a.Y, a.Y + side * w)), new(a.X + 0.04f, 2.08f, MathF.Max(a.Y, a.Y + side * w)), 0.012f, col)
            : SdfPrim.Box(new(MathF.Min(a.X, a.X + side * w), FloorY, a.Y), new(MathF.Max(a.X, a.X + side * w), 2.08f, a.Y + 0.04f), 0.012f, col);
        m.Furniture.Add((item.Id, new SdfScene { Blend = 0.01f }.Add(leaf)));
    }

    /// <summary>가구 한 점: 아이템 치수로 기본형을 짓고 방 안 자리로 옮긴다 (90° 단위 회전)</summary>
    static SdfScene? Furnish(ContentPack pack, ContentItem item, RoomDef r, FurnitureDef f, HouseModel m)
    {
        if (item.Mm.Length < 2) return null;
        float w = item.Mm[0] / 1000f, d, h;
        if (item.Shape == "cyl") { d = w; h = item.Mm[1] / 1000f; } else { d = item.Mm[1] / 1000f; h = item.Mm.Length > 2 ? item.Mm[2] / 1000f : 0.1f; }
        var col = pack.Color(item.Mat, new(0.6f));
        var parts = new List<SdfPrim>();
        void B(float x0, float y0, float z0, float x1, float y1, float z1, float rad, Vector3 c, float emis = 0) =>
            parts.Add(new SdfPrim { Shape = SdfPrim.Kind.Box, Center = new((x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2), Half = new((x1 - x0) / 2, (y1 - y0) / 2, (z1 - z0) / 2), R = rad, Color = c, Emissive = emis });
        void C(float x, float y, float z, float rad, float hh, float er, Vector3 c, float emis = 0) =>
            parts.Add(new SdfPrim { Shape = SdfPrim.Kind.Cylinder, Center = new(x, y + hh / 2, z), Half = new(rad, hh / 2, rad), R = er, Color = c, Emissive = emis });
        float hw = w / 2, hd = d / 2;
        var oak = pack.Color("oak", col); var steel = pack.Color("steel_s235", col);
        var fabric = new Vector3(0.86f, 0.87f, 0.89f);
        switch (item.Id)
        {
            case "charging_bed":
                B(-hw, 0.12f, -hd, hw, 0.30f, hd, 0.03f, oak);
                foreach (var sx in new[] { -1, 1 }) foreach (var sz in new[] { -1, 1 })
                    B(sx * (hw - 0.035f) - 0.035f, 0, sz * (hd - 0.035f) - 0.035f, sx * (hw - 0.035f) + 0.035f, 0.14f, sz * (hd - 0.035f) + 0.035f, 0.015f, oak);
                B(-hw + 0.05f, 0.30f, -hd + 0.03f, hw - 0.03f, 0.47f, hd - 0.03f, 0.07f, fabric);
                B(-hw, 0.12f, -hd, -hw + 0.07f, 0.95f, hd, 0.03f, oak);
                B(-hw + 0.12f, 0.46f, -hd + 0.12f, -hw + 0.55f, 0.56f, hd - 0.12f, 0.05f, new(0.94f, 0.94f, 0.95f));
                B(-hw + 0.65f, 0.45f, -hd + 0.01f, hw - 0.01f, 0.51f, hd - 0.01f, 0.025f, new(0.32f, 0.40f, 0.52f));
                B(-hw + 0.06f, 0.6f, -0.15f, -hw + 0.08f, 0.75f, 0.15f, 0.005f, pack.Color("copper", col));   // 충전 접점
                break;
            case "desk":
                B(-hw, h - 0.03f, -hd, hw, h, hd, 0.012f, oak);
                foreach (var sx in new[] { -1, 1 }) foreach (var sz in new[] { -1, 1 })
                    B(sx * (hw - 0.05f) - 0.0225f, 0, sz * (hd - 0.05f) - 0.0225f, sx * (hw - 0.05f) + 0.0225f, h - 0.03f, sz * (hd - 0.05f) + 0.0225f, 0.008f, oak);
                B(hw - 0.45f, h - 0.18f, -hd + 0.04f, hw - 0.06f, h - 0.03f, hd - 0.04f, 0.01f, oak * 0.92f);
                break;
            case "chair":
                B(-hw, 0.43f, -hd, hw, 0.46f, hd, 0.012f, oak);
                foreach (var sx in new[] { -1, 1 }) foreach (var sz in new[] { -1, 1 })
                    B(sx * (hw - 0.03f) - 0.0175f, 0, sz * (hd - 0.03f) - 0.0175f, sx * (hw - 0.03f) + 0.0175f, sz < 0 ? h : 0.43f, sz * (hd - 0.03f) + 0.0175f, 0.007f, oak);
                B(-hw + 0.01f, 0.62f, -hd + 0.01f, hw - 0.01f, h - 0.02f, -hd + 0.035f, 0.01f, oak);
                break;
            case "wardrobe":
                B(-hw, 0, -hd, hw, h, hd, 0.015f, col);
                foreach (var sx in new[] { -1, 1 }) C(sx * 0.05f, 1.0f, hd + 0.01f, 0.012f, 0.12f, 0.004f, steel);
                break;
            case "carpet":
                B(-hw, 0, -hd, hw, 0.02f, hd, 0.006f, new(0.55f, 0.27f, 0.22f));
                break;
            case "electric_lamp_home":
                var top = m.WallHeight - FloorY;
                C(0, top - 0.35f, 0, 0.006f, 0.35f, 0.002f, new(0.2f, 0.2f, 0.2f));
                C(0, top - 0.48f, 0, 0.13f, 0.14f, 0.01f, new(0.95f, 0.92f, 0.85f), 1f);
                break;
            case "bookshelf":
                B(-hw, 0, -hd, -hw + 0.025f, h, hd, 0.006f, oak); B(hw - 0.025f, 0, -hd, hw, h, hd, 0.006f, oak);
                B(-hw, 0, -hd, hw, h, -hd + 0.012f, 0.004f, oak * 0.85f);
                var rng = new Random(7);
                for (var k = 0; k < 5; k++)
                {
                    var y = k * (h - 0.03f) / 4;
                    B(-hw, y, -hd, hw, y + 0.025f, hd, 0.006f, oak);
                    if (k == 4) break;
                    for (var x = -hw + 0.04f; x < hw - 0.08f;)
                    {
                        var bw = 0.025f + (float)rng.NextDouble() * 0.03f; var bh = 0.2f + (float)rng.NextDouble() * 0.12f;
                        var bc = new Vector3(0.3f + (float)rng.NextDouble() * 0.5f, 0.2f + (float)rng.NextDouble() * 0.4f, 0.2f + (float)rng.NextDouble() * 0.4f);
                        B(x, y + 0.025f, -hd + 0.03f, x + bw, y + 0.025f + MathF.Min(bh, h / 4 - 0.05f), hd - 0.03f, 0.004f, bc);
                        x += bw + 0.004f;
                    }
                }
                break;
            case "record_player":
                B(-0.3f, 0, -0.22f, 0.3f, 0.6f, 0.22f, 0.015f, oak);
                B(-hw, 0.6f, -hd, hw, 0.6f + h, hd, 0.015f, new(0.12f, 0.11f, 0.10f));
                C(-0.05f, 0.6f + h, 0, 0.15f, 0.012f, 0.004f, new(0.06f, 0.06f, 0.07f));
                break;
            case "barrel":
                C(0, 0, 0, hw, h, 0.03f, col);
                foreach (var y in new[] { 0.1f, h - 0.13f }) C(0, y, 0, hw + 0.006f, 0.03f, 0.008f, steel * 0.7f);
                break;
            case "workbench":
                B(-hw, h - 0.06f, -hd, hw, h, hd, 0.015f, col);
                B(-hw + 0.06f, 0.15f, -hd + 0.06f, hw - 0.06f, 0.18f, hd - 0.06f, 0.01f, col * 0.9f);
                foreach (var sx in new[] { -1, 1 }) foreach (var sz in new[] { -1, 1 })
                    B(sx * (hw - 0.06f) - 0.04f, 0, sz * (hd - 0.06f) - 0.04f, sx * (hw - 0.06f) + 0.04f, h - 0.06f, sz * (hd - 0.06f) + 0.04f, 0.01f, col);
                break;
            case "maintenance_table":
                B(-hw, h - 0.05f, -hd, hw, h, hd, 0.02f, steel);
                C(0, 0.04f, 0, 0.09f, h - 0.09f, 0.01f, steel * 0.8f);
                B(-0.35f, 0, -0.3f, 0.35f, 0.04f, 0.3f, 0.015f, steel * 0.7f);
                break;
            default:
                if (item.Shape == "cyl") C(0, 0, 0, hw, h, MathF.Min(item.Edge / 1000f + 0.005f, hw * 0.5f), col);
                else B(-hw, 0, -hd, hw, h, hd, MathF.Max(item.Edge / 1000f, 0.004f), col);
                break;
        }

        // 90° 단위 회전과 자리: 회전한 바닥 모서리가 pos
        var yaw = ((int)MathF.Round(f.Yaw / 90) % 4 + 4) % 4;
        var (rw, rd) = yaw % 2 == 0 ? (w, d) : (d, w);
        var center = new Vector3(r.Pos.X + f.Pos.X + rw / 2, f.Ceiling ? FloorY : FloorY, r.Pos.Y + f.Pos.Y + rd / 2);
        Vector3 Rot(Vector3 v) => yaw switch { 1 => new(v.Z, v.Y, -v.X), 2 => new(-v.X, v.Y, -v.Z), 3 => new(-v.Z, v.Y, v.X), _ => v };
        var scene = new SdfScene { Blend = 0.008f };
        foreach (var p in parts)
        {
            p.Center = Rot(p.Center) + center;
            if (yaw % 2 == 1) p.Half = new(p.Half.Z, p.Half.Y, p.Half.X);
            scene.Add(p.Fit());
        }
        if (item.Id == "electric_lamp_home") m.Lamps.Add(center + new Vector3(0, m.WallHeight - FloorY - 0.45f, 0));
        return scene;
    }
}
