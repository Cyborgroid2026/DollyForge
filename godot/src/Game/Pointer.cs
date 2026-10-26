using Godot;

namespace DollyForge.Game;

/// <summary>
/// 마우스 잡기 상태. 보통은 Godot 창의 마우스를 그대로 잡지만, 통합 브라우저로 화면을 보낼 때(--stream)는
/// 숨은 창이 실제 마우스를 가두면 안 되므로 상태만 기억하고, 브라우저가 포인터 잠금으로 대신 잡는다.
/// </summary>
public static class Pointer
{
    public static bool Streaming { get; set; }
    static Input.MouseModeEnum _virtual = Input.MouseModeEnum.Visible;

    public static Input.MouseModeEnum Mode
    {
        get => Streaming ? _virtual : Input.MouseMode;
        set
        {
            if (Streaming) _virtual = value;
            else Input.MouseMode = value;
        }
    }

    public static bool Captured => Mode == Input.MouseModeEnum.Captured;
}
