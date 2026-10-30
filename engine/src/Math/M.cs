using System.Numerics;

namespace DollyForge;

/// <summary>Godot의 Mathf를 대체하는 정적 수학 유틸리티</summary>
static class M
{
    public const float Pi  = MathF.PI;
    public const float Tau = MathF.PI * 2f;

    public static float Sin(float x)  => MathF.Sin(x);
    public static float Cos(float x)  => MathF.Cos(x);
    public static float Exp(float x)  => MathF.Exp(x);
    public static float Sqrt(float x) => MathF.Sqrt(x);
    public static float Abs(float x)  => MathF.Abs(x);
    public static float Sign(float x) => MathF.Sign(x);
    public static float Atan2(float y, float x) => MathF.Atan2(y, x);
    public static float Pow(float x, float y)   => MathF.Pow(x, y);
    public static float Max(float a, float b)   => a > b ? a : b;
    public static float Min(float a, float b)   => a < b ? a : b;
    public static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
    public static float Lerp(float a, float b, float t)    => a + (b - a) * t;
    public static float PosMod(float a, float b)           => ((a % b) + b) % b;
    public static float MoveToward(float from, float to, float delta)
    {
        var d = to - from;
        return MathF.Abs(d) <= delta ? to : from + MathF.Sign(d) * delta;
    }

    public static Vector3 Up    => Vector3.UnitY;
    public static Vector3 Back  => -Vector3.UnitZ;
    public static Vector3 Right => Vector3.UnitX;
}
