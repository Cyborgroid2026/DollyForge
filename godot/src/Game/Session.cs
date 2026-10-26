using Godot;
using DollyForge.Core.Net;

namespace DollyForge.Game;

/// <summary>
/// 월드 서버 연결. 받은 메시지는 메인 스레드(_Process)에서 Received 로 전달한다.
/// 서버에 닿지 않으면 오프라인(혼자 작업)으로 계속할 수 있다.
/// </summary>
public partial class Session : Node
{
    const string AccountFile = "user://account.cfg";

    public WorldClient Client { get; } = new();
    public bool Online { get; private set; }
    public uint MyId { get; private set; }
    public byte Gender { get; private set; }
    public string ServerUrl { get; set; } = System.Environment.GetEnvironmentVariable("DOLLY_SERVER") ?? "ws://127.0.0.1:7700";

    public event Action<ServerMessage>? Received;
    public event Action<string>? Disconnected;

    public override void _Process(double delta)
    {
        foreach (var m in Client.Drain())
        {
            if (m is LoginOk ok)
            {
                MyId = ok.PlayerId;
                Gender = ok.Gender;
                Online = true;
            }
            Received?.Invoke(m);
        }
        if (Online && !Client.IsOpen)
        {
            Online = false;
            Disconnected?.Invoke(Client.CloseReason ?? "서버 연결이 끊어졌습니다.");
        }
    }

    public async Task<bool> ConnectAsync()
    {
        try
        {
            await Client.ConnectAsync(new Uri(ServerUrl), TimeSpan.FromSeconds(3));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Send(byte[] packet)
    {
        if (Client.IsOpen) Client.Send(packet);
    }

    // QR 계정 토큰은 이 기기에 저장해 둔다 (다음 접속 때 QR 카드를 다시 쓰기 위해)
    public static string? SavedToken()
    {
        var cfg = new ConfigFile();
        return cfg.Load(AccountFile) == Error.Ok ? cfg.GetValue("account", "token", "").AsString() is { Length: 32 } t ? t : null : null;
    }

    public static void SaveToken(string? token)
    {
        var cfg = new ConfigFile();
        cfg.Load(AccountFile);
        cfg.SetValue("account", "token", token ?? "");
        cfg.Save(AccountFile);
    }

    public override void _ExitTree() => _ = Client.DisposeAsync();
}
