using Godot;

namespace DollyForge.Game;

/// <summary>
/// 바깥의 소리 바탕: 풀을 흔드는 것과 같은 바람(Nature.WindStrength)의 쉬익 소리와, 지평선 도시의 낮은 웅웅거림.
/// 실제 거리는 조용한 적이 없다. 재생 파일 없이 잡음을 걸러 만든다.
/// </summary>
public partial class Ambience : Node
{
    const float MixRate = 22050f;
    AudioStreamGeneratorPlayback? _playback;
    readonly RandomNumberGenerator _rng = new();
    float _wind, _windSlow, _brown, _gust;

    public override void _Ready()
    {
        var player = new AudioStreamPlayer
        {
            Stream = new AudioStreamGenerator { MixRate = MixRate, BufferLength = 0.4f },
            VolumeDb = -16,
        };
        AddChild(player);
        player.Play();
        _playback = player.GetStreamPlayback() as AudioStreamGeneratorPlayback;
    }

    public override void _Process(double delta)
    {
        if (_playback is null) return;
        var frames = _playback.GetFramesAvailable();
        if (frames <= 0) return;
        var buffer = new Vector2[frames];
        for (var i = 0; i < frames; i++)
        {
            // 돌풍 세기를 부드럽게 따라간다: 바람이 세지면 쉬익 소리가 커지고 밝아진다
            _gust += (Nature.WindStrength - _gust) * 0.00005f;
            var white = _rng.Randf() * 2f - 1f;
            var cutoff = 0.02f + _gust * 0.08f;
            _wind += (white - _wind) * cutoff;
            _windSlow += (_wind - _windSlow) * 0.01f;
            var wind = (_wind - _windSlow) * (0.15f + _gust * _gust * 0.9f);
            // 먼 도시: 갈색 잡음 (낮은 소리만)
            _brown = Mathf.Clamp(_brown * 0.998f + white * 0.02f, -1f, 1f);
            var city = _brown * 0.12f;
            var sample = Mathf.Clamp(wind * 2.2f + city, -1f, 1f);
            buffer[i] = new Vector2(sample, sample);
        }
        _playback.PushBuffer(buffer);
    }
}
