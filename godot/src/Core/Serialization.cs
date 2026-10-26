using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DollyForge.Core;

/// <summary>
/// JSON 직렬화: (1) Rust 서버가 읽는 카탈로그·가격 규칙, (2) 창고에 보관하는 기기.
/// </summary>
public static class Serialization
{
    static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ------------------------------------------------------------------ 카탈로그 (서버용)

    public sealed record CatalogDoc(int Version, long StartBudgetKrw, int ShippingKrw, int FreeShippingOverKrw,
        List<CatalogEntry> Items, List<PriceExample> Examples);
    public sealed record CatalogEntry(string Sku, string NameKo, string Category, int PriceKrw, int PackQty, bool Physical,
        LengthRange? CutToLength, SizeRange? CutToSize, PlateSpec? Plate, bool Fictional);
    public sealed record LengthRange(int Min, int Max, int CutFeeKrw);
    public sealed record SizeRange(int MinW, int MaxW, int MinD, int MaxD, int CutFeeKrw);
    public sealed record PlateSpec(int ThicknessMm, int DensityKgM3);
    public sealed record PriceExample(string Sku, int LengthMm, int WidthMm, int UnitKrw);

    /// <summary>server/catalog.json 내용. 서버는 이 파일만으로 결제 금액을 다시 계산한다.</summary>
    public static string CatalogJson()
    {
        var items = Catalog.All.Select(i => new CatalogEntry(
            i.Sku, i.NameKo, i.Category, i.PriceKrw, i.PackQty, !i.IsConsumable,
            i.CutToLength is { } c ? new LengthRange(c.Min, c.Max, c.CutFeeKrw) : null,
            i.CutToSize is { } s ? new SizeRange(s.MinW, s.MaxW, s.MinD, s.MaxD, s.CutFeeKrw) : null,
            i.Plate is { } p ? new PlateSpec(p.ThicknessMm, p.DensityKgM3) : null,
            i.CutToLength is null && i.CutToSize is null && i.Build() is { Fictional: true })).ToList();
        // 서버 구현이 같은 금액을 내는지 확인하는 예시들 (경계값·반올림 포함)
        var examples = new List<PriceExample>();
        foreach (var i in Catalog.All)
        {
            if (i.CutToLength is { } c)
                foreach (var l in new[] { c.Min, c.Max, 333, 750, 1001 }.Where(l => l >= c.Min && l <= c.Max))
                    examples.Add(new(i.Sku, l, 0, i.UnitPrice(l)));
            else if (i.CutToSize is { } s)
                foreach (var (w, d) in new[] { (s.MinW, s.MinD), (s.MaxW, s.MaxD), (220, 200), (333, 77), (501, 499) })
                    examples.Add(new(i.Sku, w, d, i.UnitPrice(w, d)));
            else examples.Add(new(i.Sku, 0, 0, i.UnitPrice()));
        }
        var doc = new CatalogDoc(1, ShopAccount.StartBudgetKrw, ShopAccount.ShippingKrw, ShopAccount.FreeShippingOverKrw, items, examples);
        return JsonSerializer.Serialize(doc, Opts);
    }

    // ------------------------------------------------------------------ 기기 (창고 보관)

    public sealed record DeviceDoc(int Version, string Name, List<PartDoc> Parts, List<JointDoc> Joints);
    public sealed record PartDoc(int Id, float[] P, float[] Q, string? Sku, int LengthMm, int WidthMm, DefDoc? Def);
    public sealed record DefDoc(string Id, string NameKo, string Shape, float[] Size, string Material, string DescriptionKo,
        float? MassOverride, float[]? Actuator, string Role, float ShaftMm, float BoreMm, float[]? Electrical, int PriceKrw,
        bool Fictional, string? RealEquivalentKo);
    public sealed record JointDoc(string Kind, int Parent, int Child, float[] Pivot, float[] Axis, float[] Secondary,
        float Lower, float Upper, float Effort, float Velocity, int? DrivenBy, Dictionary<string, int> Consumed);

    static float[] A(Vector3 v) => [v.X, v.Y, v.Z];
    static Vector3 V(float[] a) => new(a[0], a[1], a[2]);

    public static string DeviceJson(Assembly a)
    {
        var parts = a.Parts.Select(p =>
        {
            var d = p.Def;
            var fromCatalog = d.Sku is not null && Catalog.All.Any(i => i.Sku == d.Sku);
            DefDoc? def = fromCatalog ? null : new DefDoc(d.Id, d.NameKo, d.Shape.Kind.ToString(), A(d.Shape.Size), d.Material.Id,
                d.DescriptionKo, d.MassOverride,
                d.Actuator is { } act ? [act.MaxTorque, act.MaxSpeed, act.EnergyJ] : null,
                d.Role.ToString(), d.ShaftMm, d.BoreMm,
                d.Electrical is { } e ? [e.Volts, e.CapacityWh, e.RatedCurrentA] : null, d.PriceKrw, d.Fictional, d.RealEquivalentKo);
            var q = p.Pose.Rotation;
            return new PartDoc(p.Id, A(p.Pose.Position), [q.X, q.Y, q.Z, q.W], fromCatalog ? d.Sku : null, d.CutLengthMm, d.CutWidthMm, def);
        }).ToList();
        var joints = a.Joints.Select(j => new JointDoc(j.Kind.ToString(), j.Parent, j.Child, A(j.Pivot), A(j.Axis), A(j.Secondary),
            j.Lower, j.Upper, j.Effort, j.Velocity, j.DrivenBy, new(j.Consumed))).ToList();
        return JsonSerializer.Serialize(new DeviceDoc(1, a.Name, parts, joints), Opts);
    }

    public static Assembly ParseDevice(string json)
    {
        var doc = JsonSerializer.Deserialize<DeviceDoc>(json, Opts) ?? throw new InvalidDataException("빈 기기 문서");
        var a = new Assembly(doc.Name);
        var map = new Dictionary<int, PartInstance>();
        foreach (var p in doc.Parts)
        {
            PartDef def;
            if (p.Sku is not null) def = Catalog.Get(p.Sku).Build(p.LengthMm, p.WidthMm)!;
            else
            {
                var d = p.Def ?? throw new InvalidDataException("부품 정의 없음");
                var size = V(d.Size);
                var shape = Enum.Parse<ShapeKind>(d.Shape) switch
                {
                    ShapeKind.Box => Shape.Box(size.X, size.Y, size.Z),
                    ShapeKind.Cylinder => Shape.Cylinder(size.X, size.Y),
                    _ => Shape.Sphere(size.X),
                };
                def = new PartDef(d.Id, d.NameKo, shape, Materials.All[d.Material], d.DescriptionKo, d.MassOverride,
                    d.Actuator is { } act ? new ActuatorSpec(act[0], act[1], act[2]) : null)
                {
                    Role = Enum.Parse<PartRole>(d.Role), ShaftMm = d.ShaftMm, BoreMm = d.BoreMm, PriceKrw = d.PriceKrw,
                    Electrical = d.Electrical is { } e ? new ElectricalSpec(e[0], e[1], e[2]) : null,
                    Fictional = d.Fictional, RealEquivalentKo = d.RealEquivalentKo,
                };
            }
            map[p.Id] = a.AddPart(def, new Pose(V(p.P), new Quaternion(p.Q[0], p.Q[1], p.Q[2], p.Q[3])));
        }
        foreach (var j in doc.Joints)
        {
            var nj = a.AddJoint(Enum.Parse<JointKind>(j.Kind), map[j.Parent], map[j.Child], V(j.Pivot), V(j.Axis), j.Lower, j.Upper, V(j.Secondary));
            nj.Effort = j.Effort;
            nj.Velocity = j.Velocity;
            if (j.DrivenBy is { } m) nj.DrivenBy = map[m].Id;
            foreach (var (k, v) in j.Consumed) nj.Consumed[k] = v;
        }
        return a;
    }
}
