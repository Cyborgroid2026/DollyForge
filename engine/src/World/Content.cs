using System.Numerics;
using System.Text.Json;

namespace DollyForge;

/// <summary>콘텐츠 팩 재료 (렌더에 필요한 것만: 색·밀도)</summary>
sealed record ContentMaterial(string Id, string Name, float Density, Vector3 Color);

/// <summary>콘텐츠 팩 아이템: 형상과 실제 치수(mm)</summary>
sealed record ContentItem(string Id, string Name, string Category, string Shape, float[] Mm, float Edge, string Mat);

sealed record FurnitureDef(string Item, Vector2 Pos, float Yaw, bool SpawnPoint, bool Wall, bool Ceiling);
sealed record RoomDef(string Id, string Name, Vector2 Pos, Vector2 Size, bool Spawn, FurnitureDef[] Furniture);
sealed record HouseDef(string Id, string Name, Vector2 Footprint, float WallHeight, Dictionary<string, string> Shell, RoomDef[] Rooms);

/// <summary>
/// 콘텐츠 팩 content/dolly 를 읽는다 (실행 폴더에 복사된 것, tools/content/validate.py 로 검증된 데이터).
/// 엔진은 집 설계도로 시작 방을 짓고, 아이템 치수로 가구를, 재료 색으로 표면을 칠한다.
/// </summary>
sealed class ContentPack
{
    public readonly Dictionary<string, ContentMaterial> Materials = [];
    public readonly Dictionary<string, ContentItem> Items = [];
    public readonly Dictionary<string, HouseDef> Houses = [];

    public static string DefaultDir => Path.Combine(AppContext.BaseDirectory, "content", "dolly");

    public static ContentPack Load(string? dir = null)
    {
        dir ??= DefaultDir;
        var pack = new ContentPack();
        using var packDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "pack.json")));
        var files = packDoc.RootElement.GetProperty("files");

        using (var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, files.GetProperty("materials").GetString()!))))
            foreach (var m in doc.RootElement.GetProperty("materials").EnumerateArray())
            {
                var c = m.GetProperty("color");
                pack.Materials[m.GetProperty("id").GetString()!] = new(m.GetProperty("id").GetString()!, m.GetProperty("name").GetString()!,
                    m.GetProperty("density").GetSingle(), new Vector3(c[0].GetSingle(), c[1].GetSingle(), c[2].GetSingle()));
            }

        foreach (var rel in files.GetProperty("items").EnumerateArray())
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, rel.GetString()!)));
            var cat = doc.RootElement.GetProperty("category").GetString()!;
            foreach (var it in doc.RootElement.GetProperty("items").EnumerateArray())
            {
                var mm = it.TryGetProperty("mm", out var a) ? a.EnumerateArray().Select(x => x.GetSingle()).ToArray() : [];
                pack.Items[it.GetProperty("id").GetString()!] = new(it.GetProperty("id").GetString()!, it.GetProperty("name").GetString()!, cat,
                    it.GetProperty("shape").GetString()!, mm, it.TryGetProperty("edge", out var e) ? e.GetSingle() : 0,
                    it.TryGetProperty("mat", out var mt) ? mt.GetString()! : "");
            }
        }

        using (var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, files.GetProperty("houses").GetString()!))))
            foreach (var h in doc.RootElement.GetProperty("houses").EnumerateArray())
            {
                static Vector2 V2(JsonElement e) => new(e[0].GetSingle(), e[1].GetSingle());
                var rooms = h.GetProperty("rooms").EnumerateArray().Select(r => new RoomDef(
                    r.GetProperty("id").GetString()!, r.GetProperty("name").GetString()!, V2(r.GetProperty("pos_m")), V2(r.GetProperty("size_m")),
                    r.TryGetProperty("spawn", out var sp) && sp.GetBoolean(),
                    r.TryGetProperty("furniture", out var fs)
                        ? fs.EnumerateArray().Select(f => new FurnitureDef(f.GetProperty("item").GetString()!, V2(f.GetProperty("pos_m")),
                            f.TryGetProperty("yaw_deg", out var y) ? y.GetSingle() : 0,
                            f.TryGetProperty("spawn_point", out var s) && s.GetBoolean(),
                            f.TryGetProperty("wall", out var w) && w.GetBoolean(),
                            f.TryGetProperty("ceiling", out var cl) && cl.GetBoolean())).ToArray()
                        : [])).ToArray();
                var shell = h.GetProperty("shell").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
                pack.Houses[h.GetProperty("id").GetString()!] = new(h.GetProperty("id").GetString()!, h.GetProperty("name").GetString()!,
                    V2(h.GetProperty("footprint_m")), h.GetProperty("wall_height_m").GetSingle(), shell, rooms);
            }
        Console.WriteLine($"[content] 재료 {pack.Materials.Count} · 아이템 {pack.Items.Count} · 집 설계도 {pack.Houses.Count}");
        return pack;
    }

    public Vector3 Color(string mat, Vector3 fallback) => Materials.TryGetValue(mat, out var m) ? m.Color : fallback;
}
