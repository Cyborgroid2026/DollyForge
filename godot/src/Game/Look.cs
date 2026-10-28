using Godot;
using DollyForge.Core;

namespace DollyForge.Game;

/// <summary>
/// 겉모습: 재료별 실사 PBR 재질(Poly Haven CC0 스캔 텍스처)과 사물에 새겨진 글씨용 글꼴.
/// 화면에 떠 있는 UI 대신 사물 표면의 꼬리표·도면·종이에 글을 쓴다.
/// </summary>
public static class Look
{
    static readonly Dictionary<string, Material> Cache = new();

    internal static Texture2D? Tex(string name, string map)
    {
        var path = $"res://assets/tex/{name}/{map}.jpg";
        return ResourceLoader.Exists(path) ? GD.Load<Texture2D>(path) : null;
    }

    /// <summary>스캔 텍스처를 월드 기준 삼면 투영으로 입힌 재질. tile 은 텍스처 한 장의 실제 크기(m).</summary>
    public static StandardMaterial3D Scanned(string name, float tile, Color tint, float metallic = 0)
    {
        var m = new StandardMaterial3D
        {
            AlbedoColor = tint,
            AlbedoTexture = Tex(name, "diff"),
            Metallic = metallic,
            RoughnessTexture = Tex(name, "rough"),
            RoughnessTextureChannel = BaseMaterial3D.TextureChannel.Red,
            Uv1Triplanar = true,
            Uv1WorldTriplanar = true,
            Uv1Scale = Vector3.One / tile,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
        };
        if (Tex(name, "nor_gl") is { } n)
        {
            m.NormalEnabled = true;
            m.NormalTexture = n;
        }
        return m;
    }

    /// <summary>
    /// 도장면: 색은 고른 도료색이고, 바탕 재질의 요철(노멀)과 거칠기만 스캔 텍스처에서 빌린다.
    /// 흰 에나멜 외벽처럼 깨끗하지만 손으로 칠한 미세한 결이 남는다.
    /// </summary>
    public static StandardMaterial3D Painted(string name, float tile, Color paint)
    {
        var m = Scanned(name, tile, paint);
        m.AlbedoTexture = null;
        return m;
    }

    /// <summary>재료 → 표면. 금속은 가공된 금속 광택, 나무·돌은 스캔 텍스처.</summary>
    public static Material For(MaterialDef mat)
    {
        if (Cache.TryGetValue(mat.Id, out var cached)) return cached;
        var c = new Color(mat.Color.X, mat.Color.Y, mat.Color.Z, mat.Color.W);
        Material m = mat.Id switch
        {
            "pine" => Scanned("plywood", 0.8f, new Color(1, 0.95f, 0.85f)),
            "oak" => Scanned("weathered_planks", 1.2f, new Color(0.8f, 0.7f, 0.6f)),
            "concrete_block" => Scanned("rock_boulder_dry", 0.6f, new Color(0.85f, 0.85f, 0.82f)),
            "granite" => Scanned("rock_face_03", 0.8f, Colors.White),
            "clay_brick" => Scanned("rock_boulder_dry", 0.5f, new Color(0.85f, 0.45f, 0.35f)),
            "iron_ore" => Scanned("rock_face_03", 0.5f, new Color(0.75f, 0.45f, 0.38f)),
            "copper_ore" => Scanned("rock_face_03", 0.5f, new Color(0.7f, 0.65f, 0.4f)),
            "natural_rubber" => new StandardMaterial3D { AlbedoColor = c, Roughness = 0.85f },
            "soda_lime_glass" => new StandardMaterial3D
            {
                AlbedoColor = c, Roughness = 0.05f, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                RefractionEnabled = true, RefractionScale = 0.02f,
            },
            _ => Metal(mat, c),
        };
        Cache[mat.Id] = m;
        return m;
    }

    static StandardMaterial3D Metal(MaterialDef mat, Color c)
    {
        // 가공된 금속: 금속성 1, 미세 가공 흔적은 금속판 스캔의 노멀·거칠기를 아주 작게 반복해 표현
        var m = Scanned("metal_plate", 0.25f, c, metallic: 1);
        m.AlbedoTexture = null;
        m.Roughness = mat.Id == "brass_c260" || mat.Id == "copper" ? 0.28f : 0.33f;
        m.NormalScale = 0.15f;
        return m;
    }

    static readonly string[] PenNames = ["NanumBarunpen", "Nanum Pen Script", "NanumGothic"];
    static readonly string[] StampNames = ["NanumBarunGothic", "NanumGothic", "Noto Sans CJK KR"];
    static FontVariation? _pen, _penUi, _stamp;

    /// <summary>손글씨 — 월드 안 종이(3D, 거리장 글꼴)</summary>
    public static Font Pen => _pen ??= Variation(PenNames, 400, msdf: true);

    /// <summary>손글씨 — 눈앞에 펼친 종이(2D). 거리장 글꼴은 일부 숫자 획이 깨져 일반 래스터를 쓴다.</summary>
    public static Font PenUi => _penUi ??= Variation(PenNames, 400, msdf: false);

    /// <summary>찍어 새긴 글씨 (금속 꼬리표·기계 명판)</summary>
    public static Font Stamp => _stamp ??= Variation(StampNames, 600, msdf: true);

    static FontVariation Variation(string[] names, int weight, bool msdf) =>
        new() { BaseFont = new SystemFont { FontNames = names, FontWeight = weight, MultichannelSignedDistanceField = msdf } };

    /// <summary>사물 표면에 붙는 꼬리표 글씨</summary>
    public static Label3D Tag(string text, float size = 0.004f, Font? font = null)
    {
        return new Label3D
        {
            Text = text,
            Font = font ?? Stamp,
            FontSize = 48,
            PixelSize = size,
            Modulate = new Color(0.12f, 0.12f, 0.12f),
            OutlineSize = 0,
            Shaded = true,
            DoubleSided = false,
            NoDepthTest = false,
        };
    }
}
