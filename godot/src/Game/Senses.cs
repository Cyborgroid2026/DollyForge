using Godot;

namespace DollyForge.Game;

/// <summary>
/// 사이보그의 다섯 감각. 화면과 소리만이 아니라, 물리 세계에서 실제로 닿고 맡는 것을 읽는다.
///   촉각: 발밑 충돌체의 표면(Surface) — 마찰이 걸음의 가속을 바꾸고, 결·온도감·미끄러움을 느낀다
///   청각: 발이 닿을 때마다 그 표면의 발소리를 합성한다 (재생 파일 없이 잡음을 걸러 만든다)
///   후각: 길가 냄새원(가게·쓰레기·차·풀)과 바람으로 퍼진 농도
///   미각: 손에 쥔 재료의 접촉 화학 (Surface.TasteOf)
///   시각: 거리뷰 기준 화면 그 자체
/// 기록은 계기판 숫자가 아니라 말로 적은 감각 기록지로 보여 준다 (Instrument 원칙).
/// </summary>
public partial class Senses : Node
{
    /// <summary>바깥 기온 (°C). 손끝 접촉 온도를 정한다.</summary>
    public const float AmbientC = 18f;
    const float MixRate = 22050f;

    public static readonly List<(Vector3 At, string Smell, float Strength)> Odors = [];

    CharacterBody3D _body = null!;
    AudioStreamGeneratorPlayback? _playback;
    readonly RandomNumberGenerator _rng = new();
    public Surface? Underfoot { get; private set; }
    public (string Smell, float Level)? Smell { get; private set; }
    public string? Taste { get; set; }

    public override void _Ready()
    {
        _body = GetParent<CharacterBody3D>();
        var player = new AudioStreamPlayer
        {
            Stream = new AudioStreamGenerator { MixRate = MixRate, BufferLength = 0.5f },
            VolumeDb = -4,
        };
        AddChild(player);
        player.Play();
        _playback = player.GetStreamPlayback() as AudioStreamGeneratorPlayback;
    }

    public override void _PhysicsProcess(double delta)
    {
        // 발밑: 발 위 0.8 m 에서 아래로 1.6 m 광선, 맞은 충돌체의 표면
        var from = _body.GlobalPosition + Vector3.Up * 0.8f;
        var query = PhysicsRayQueryParameters3D.Create(from, from + Vector3.Down * 1.6f, PartBody.LayerWorld | PartBody.LayerParts, [_body.GetRid()]);
        var hit = _body.GetWorld3D().DirectSpaceState.IntersectRay(query);
        Underfoot = hit.Count > 0 ? Surface.Of(hit["collider"].AsGodotObject()) : null;
        Smell = SmellAt(_body.GlobalPosition + Vector3.Up * 1.6f);
    }

    /// <summary>걸음 가속에 곱하는 접지력 (아스팔트 0.75 를 1 로)</summary>
    public float Grip => Mathf.Clamp((Underfoot?.Friction ?? 0.75f) / 0.75f, 0.45f, 1.1f);

    /// <summary>
    /// 냄새 농도: 냄새원마다 거리에 따라 줄고(반경 4 m 에서 절반), 바람이 불어오는 쪽에서 오는 냄새는 짙어진다.
    /// 가장 짙은 냄새 하나와 그 세기 (0–1+).
    /// </summary>
    public static (string Smell, float Level)? SmellAt(Vector3 at)
    {
        var wind = Nature.WindDirection;
        (string, float)? best = null;
        foreach (var (source, smell, strength) in Odors)
        {
            var offset = new Vector2(at.X - source.X, at.Z - source.Z);
            var d = offset.Length();
            var level = strength / (1f + d * d / 16f);
            if (d > 0.1f)
                level *= 1f + Nature.WindStrength * (offset / d).Dot(wind) * 0.8f; // 바람 아래쪽(+)이면 짙게
            if (level > (best?.Item2 ?? 0.08f)) best = (smell, level);
        }
        return best;
    }

    /// <summary>가게 이름(여섯 나라 말)으로 그 가게가 길에 내놓는 냄새</summary>
    public static string? SmellOfShop(string name)
    {
        var n = name.ToUpperInvariant();
        bool Has(params string[] keys) => keys.Any(k => n.Contains(k));
        if (Has("분식")) return "떡볶이·튀김 기름 냄새";
        if (Has("BOULANGERIE", "BAKERY", "PADARIA", "빵")) return "갓 구운 빵 냄새";
        if (Has("CAFÉ", "喫茶", "DINER", "LANCHONETE")) return "커피와 기름 냄새";
        if (Has("세탁", "クリーニング", "LAUNDROMAT")) return "세제와 다림질 증기 냄새";
        if (Has("꽃", "FLEURISTE", "花")) return "꽃 냄새";
        if (Has("약국", "PHARMACIE", "PHARMACY", "FARMÁCIA", "CHEMIST", "薬局")) return "소독약 냄새";
        if (Has("BOUCHERIE", "AÇOUGUE")) return "날고기 냄새";
        if (Has("FISH & CHIPS")) return "튀긴 생선과 식초 냄새";
        if (Has("酒店", "OFF LICENCE", "BAR")) return "술 냄새";
        if (Has("TABAC")) return "담배 냄새";
        if (Has("철물", "HARDWARE")) return "기름 먹은 쇠 냄새";
        if (Has("미용", "理容", "BARBER", "COIFFURE")) return "샴푸 냄새";
        if (Has("米店", "MERCADO", "슈퍼")) return "곡물·과일 냄새";
        return null;
    }

    public static string LevelWord(float level) => level switch
    {
        > 0.6f => "진함",
        > 0.25f => "뚜렷함",
        _ => "희미함",
    };

    /// <summary>한 발짝: 발밑 표면의 소리를 합성해 넣는다. speed 는 m/s, landing 은 내려앉은 속도</summary>
    public void Step(float speed, float landing = 0)
    {
        if (_playback is null) return;
        var step = (Underfoot ?? Surface.Grass).Step;
        // 티타늄 발: 단단해서 딱딱한 표면에서는 또렷하게 울린다. 빠를수록·높이서 내려앉을수록 세다.
        var gain = step.Gain * Mathf.Clamp(0.45f + speed / 4f + landing * 0.25f, 0.3f, 1.4f);
        var frames = (int)(MixRate * (step.DecayMs * 4f / 1000f));
        if (_playback.GetFramesAvailable() < frames) return;
        // 2차 공진 필터(대역 통과)로 잡음을 그 표면의 색으로 거르고, 금속은 고유 진동으로 울린다
        var w = Mathf.Tau * step.CenterHz / MixRate;
        var alpha = Mathf.Sin(w) / (2f * step.Q);
        float b0 = alpha, b2 = -alpha, a0 = 1 + alpha, a1 = -2 * Mathf.Cos(w), a2 = 1 - alpha;
        float x1 = 0, x2 = 0, y1 = 0, y2 = 0;
        var decay = step.DecayMs / 1000f;
        var buffer = new Vector2[frames];
        for (var i = 0; i < frames; i++)
        {
            var t = i / MixRate;
            var envelope = Mathf.Exp(-t / decay) * Mathf.Min(1f, t * 2000f);
            var x = _rng.Randf() * 2f - 1f;
            var y = (b0 * x + b2 * x2 - a1 * y1 - a2 * y2) / a0;
            x2 = x1; x1 = x; y2 = y1; y1 = y;
            var sample = y * 3f * envelope;
            if (step.RingHz > 0) sample += Mathf.Sin(Mathf.Tau * step.RingHz * t) * Mathf.Exp(-t / (decay * 1.6f)) * 0.35f;
            sample = Mathf.Clamp(sample * gain, -1f, 1f);
            buffer[i] = new Vector2(sample, sample);
        }
        _playback.PushBuffer(buffer);
        LastStep = Underfoot;
    }

    /// <summary>마지막으로 소리를 낸 발밑 (시험용)</summary>
    public Surface? LastStep { get; private set; }

    /// <summary>감각 기록지 한 장</summary>
    public string Record()
    {
        var lines = new List<string>();
        if (Underfoot is { } s)
            lines.Add($"발밑  {s.Name} · {s.TextureWord} · {s.TemperatureWord(AmbientC)} {s.ContactTemperature(AmbientC):0}°C · {s.GripWord}");
        var smell = Smell is { } sm ? $"{sm.Smell} ({LevelWord(sm.Level)})" : Underfoot?.Smell is { } own ? $"{own} (희미함)" : "맡히는 것 없음";
        lines.Add($"냄새  {smell}");
        if (Taste is { } taste) lines.Add($"맛    {taste}");
        return string.Join('\n', lines);
    }
}
