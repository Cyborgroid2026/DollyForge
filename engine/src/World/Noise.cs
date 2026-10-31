namespace DollyForge;

/// <summary>
/// 2D 그래디언트 노이즈(Perlin 개선판)와 fBm. 씨앗이 같으면 어느 기계에서도 같은 값.
/// 기획서 0.9 의 FastNoise Lite 로 바꿔 언어 사이 재현성을 맞추기 전까지 쓰는 자체 구현이다.
/// </summary>
sealed class Noise2
{
    readonly int[] _p = new int[512];

    public Noise2(int seed)
    {
        var perm = Enumerable.Range(0, 256).ToArray();
        var rng = new Random(seed);
        for (var i = 255; i > 0; i--) { var j = rng.Next(i + 1); (perm[i], perm[j]) = (perm[j], perm[i]); }
        for (var i = 0; i < 512; i++) _p[i] = perm[i & 255];
    }

    static float Fade(float t) => t * t * t * (t * (t * 6 - 15) + 10);

    static float Grad(int h, float x, float y)
    {
        // 8방향 그래디언트
        return (h & 7) switch
        {
            0 => x + y, 1 => -x + y, 2 => x - y, 3 => -x - y,
            4 => x * 1.4142f, 5 => -x * 1.4142f, 6 => y * 1.4142f, _ => -y * 1.4142f,
        };
    }

    /// <summary>대략 −1 … 1</summary>
    public float Sample(float x, float y)
    {
        var xi = (int)MathF.Floor(x); var yi = (int)MathF.Floor(y);
        float xf = x - xi, yf = y - yi;
        xi &= 255; yi &= 255;
        float u = Fade(xf), v = Fade(yf);
        int aa = _p[_p[xi] + yi], ab = _p[_p[xi] + yi + 1], ba = _p[_p[xi + 1] + yi], bb = _p[_p[xi + 1] + yi + 1];
        var x1 = Lerp(Grad(aa, xf, yf), Grad(ba, xf - 1, yf), u);
        var x2 = Lerp(Grad(ab, xf, yf - 1), Grad(bb, xf - 1, yf - 1), u);
        return Lerp(x1, x2, v) * 0.7071f;
    }

    public float Fbm(float x, float y, int octaves, float lacunarity = 2.03f, float gain = 0.5f)
    {
        float sum = 0, amp = 1, norm = 0;
        for (var o = 0; o < octaves; o++)
        {
            sum += Sample(x, y) * amp; norm += amp;
            x *= lacunarity; y *= lacunarity; amp *= gain;
            (x, y) = (x * 0.8f - y * 0.6f + 17.3f, x * 0.6f + y * 0.8f - 9.1f);   // 옥타브마다 돌려 격자 무늬를 없앤다
        }
        return sum / norm;
    }

    static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
