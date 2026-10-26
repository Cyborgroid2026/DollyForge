using Godot;

namespace DollyForge.Game;

/// <summary>
/// 터의 배치: 작업장, 모노레일 선로와 정류장, 정류장에서 작업장까지의 오솔길.
/// 지형은 이 배치에 맞춰 깎이고(선로 비탈 깎기, 정류장 터, 오솔길), 풀·나무는 비워 둔 자리를 피한다.
/// 좌표: +X 동쪽, -Z 북쪽(먼 도시와 공명탑), 작업장 문은 남쪽(z = 0)을 본다.
/// </summary>
public static class Site
{
    /// <summary>모노레일 선로 중심선 (동서 방향)</summary>
    public const float RailZ = 58f;
    /// <summary>정류장 승강장: x 범위, 선로 북쪽의 z 범위, 승강장 높이(터 위)</summary>
    public const float StationX0 = -22f, StationX1 = 4f, PlatformZ0 = 53.4f, PlatformZ1 = 56.6f, PlatformHeight = 0.9f;
    public const float StationX = -8f;

    /// <summary>오솔길: 정류장 경사로 발치에서 연못 서쪽을 돌아 작업장 앞마당까지</summary>
    public static readonly Vector2[] Path =
    [
        new(-8f, 47.5f), new(-10.5f, 38f), new(-9f, 28f), new(-5f, 18f), new(-1.5f, 11f), new(0f, 8.5f),
    ];
    public const float PathHalfWidth = 0.9f;

    /// <summary>작업장 앞 콘크리트 마당 (택배 하차장 포함)</summary>
    public static readonly Rect2 Apron = new(-6.2f, 0.2f, 12.4f, 8.6f);
    /// <summary>작업장 동쪽의 평평한 콘크리트 시험장: 만든 기기를 굴려 보는 곳</summary>
    public static readonly Rect2 TestPad = new(8.5f, -11f, 12f, 12f);
    /// <summary>작업장 건물 바닥 (벽 포함)</summary>
    public static readonly Rect2 Building = new(-6.3f, -10.3f, 12.6f, 10.6f);

    /// <summary>선로 높이 단면 (지형을 깎기 전에 계산). x = -80..80 를 1 m 간격으로.</summary>
    public static float[] RailProfile { get; private set; } = new float[161];

    public static float RailGroundAt(float x)
    {
        var fx = Mathf.Clamp(x + 80f, 0, 159.999f);
        var i = (int)fx;
        return Mathf.Lerp(RailProfile[i], RailProfile[i + 1], fx - i);
    }

    /// <summary>승강장 윗면 높이</summary>
    public static float PlatformTop => RailGroundAt(StationX) + PlatformHeight;

    /// <summary>시작 지점: 승강장 위, 작업장 쪽(북쪽)을 보며 연못이 오른쪽 앞에 보이게</summary>
    public static Vector3 Spawn => new(StationX + 1.5f, PlatformTop + 0.05f, 55.2f);
    public const float SpawnYawDegrees = -12f;

    /// <summary>오솔길 중심선까지의 거리와, 길을 따라 간 비율 (0 = 정류장, 1 = 작업장)</summary>
    public static (float Distance, float Along) PathDistance(Vector2 p)
    {
        var best = float.MaxValue;
        var along = 0f;
        var total = 0f;
        for (var i = 0; i < Path.Length - 1; i++) total += Path[i].DistanceTo(Path[i + 1]);
        var walked = 0f;
        for (var i = 0; i < Path.Length - 1; i++)
        {
            var a = Path[i];
            var b = Path[i + 1];
            var seg = b - a;
            var t = Mathf.Clamp((p - a).Dot(seg) / seg.LengthSquared(), 0, 1);
            var d = p.DistanceTo(a + seg * t);
            if (d < best) { best = d; along = (walked + seg.Length() * t) / total; }
            walked += seg.Length();
        }
        return (best, along);
    }

    static float RectDistance(Rect2 r, Vector2 p)
    {
        var dx = Mathf.Max(Mathf.Max(r.Position.X - p.X, p.X - r.End.X), 0);
        var dz = Mathf.Max(Mathf.Max(r.Position.Y - p.Y, p.Y - r.End.Y), 0);
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    static readonly Rect2 StationPad = new(StationX0 - 2f, PlatformZ0 - 1.5f, StationX1 - StationX0 + 4f, RailZ + 3f - PlatformZ0 + 1.5f);

    /// <summary>
    /// 사람이 만든 자리에서 떨어진 정도 (m). 풀은 margin 0, 나무는 margin 수 m 로 묻는다.
    /// 작업장·마당·정류장 터·선로·오솔길·연못 안은 비어 있어야 한다.
    /// </summary>
    public static bool Clear(float x, float z, float margin = 0)
    {
        var p = new Vector2(x, z);
        if (RectDistance(Building, p) < 0.6f + margin) return false;
        if (RectDistance(Apron, p) < 0.3f + margin) return false;
        if (RectDistance(TestPad, p) < 0.3f + margin) return false;
        if (RectDistance(StationPad, p) < margin) return false;
        if (Mathf.Abs(z - RailZ) < 3.2f + margin) return false;
        if (PathDistance(p).Distance < PathHalfWidth + 0.25f + margin) return false;
        if (p.DistanceTo(World.PondCenter) < World.PondRadius + 0.8f + margin) return false;
        return true;
    }

    /// <summary>
    /// 지형 높이맵을 배치에 맞춰 다듬는다 (n×n, 1 m 격자, 중심이 원점).
    /// 선로는 원래 지형을 길게 고른 완만한 단면을 따라가고(비탈 깎기·북돋기), 정류장 터는 평평하게,
    /// 오솔길은 정류장에서 마당까지 고르게 내려간다. 경계는 부드럽게 섞는다.
    /// </summary>
    public static void ShapeTerrain(float[] h, int n)
    {
        var half = (n - 1) / 2f;
        float Raw(float x, float z)
        {
            int j = Mathf.Clamp((int)Mathf.Round(x + half), 0, n - 1), i = Mathf.Clamp((int)Mathf.Round(z + half), 0, n - 1);
            return h[i * n + j];
        }

        // 선로 단면: 선로 줄의 지형을 31 m 상자 필터로 두 번 고른다 (철도 종단 경사처럼 완만하게)
        var raw = new float[161];
        for (var k = 0; k < 161; k++) raw[k] = (Raw(k - 80, RailZ - 2) + Raw(k - 80, RailZ) + Raw(k - 80, RailZ + 2)) / 3f;
        float[] Smooth(float[] a)
        {
            var o = new float[a.Length];
            for (var k = 0; k < a.Length; k++)
            {
                float s = 0; var c = 0;
                for (var m = -15; m <= 15; m++) { var q = Math.Clamp(k + m, 0, a.Length - 1); s += a[q]; c++; }
                o[k] = s / c;
            }
            return o;
        }
        RailProfile = Smooth(Smooth(raw));
        // 정류장 구간은 수평
        var stationGround = RailGroundAt(StationX);
        for (var k = 0; k < 161; k++)
        {
            var x = k - 80f;
            var w = Mathf.SmoothStep(StationX1 + 14f, StationX1 + 2f, x) * Mathf.SmoothStep(StationX0 - 14f, StationX0 - 2f, x);
            RailProfile[k] = Mathf.Lerp(RailProfile[k], stationGround, w);
        }

        var yardGround = Raw(0, 6);
        for (var i = 0; i < n; i++)
        for (var j = 0; j < n; j++)
        {
            var x = j - half;
            var z = i - half;
            var p = new Vector2(x, z);
            var y = h[i * n + j];
            // 선로: 폭 9 m 를 단면 높이로, 바깥 8 m 에 걸쳐 원래 지형과 섞는다
            var rail = Mathf.SmoothStep(12.5f, 4.5f, Mathf.Abs(z - RailZ));
            y = Mathf.Lerp(y, RailGroundAt(x), rail);
            // 정류장 터
            var pad = Mathf.SmoothStep(7f, 0.5f, RectDistance(StationPad, p));
            y = Mathf.Lerp(y, stationGround, pad);
            // 시험장 (마당 높이로 평평하게)
            y = Mathf.Lerp(y, yardGround, Mathf.SmoothStep(6f, 0.3f, RectDistance(TestPad, p)));
            // 오솔길: 정류장 높이 → 마당 높이로 고르게
            var (d, along) = PathDistance(p);
            // 길 자체는 정확히, 그 둘레 16 m 는 언덕을 대부분 낮춰 정류장에서 작업장까지 트인 풀밭 골짜기로
            var path = Mathf.Max(Mathf.SmoothStep(PathHalfWidth + 5f, PathHalfWidth + 0.6f, d),
                                 0.8f * Mathf.SmoothStep(PathHalfWidth + 18f, PathHalfWidth + 5f, d) * Mathf.SmoothStep(0.02f, 0.15f, along));
            y = Mathf.Lerp(y, Mathf.Lerp(stationGround, yardGround, Mathf.SmoothStep(0, 1, along)), path);
            h[i * n + j] = y;
        }
    }
}
