using DollyForge.Client.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using QRCoder;

namespace DollyForge.Client.Game;

public enum LoginStep { Connecting, Phone, QrIssued, CodeSent, InGame, Disconnected }

public sealed record FeedLine(MessageKind Kind, string Text, bool IsChat = false);

/// <summary>클라이언트 쪽 게임 세션: 서버 메시지를 상태로 바꾸고 렌더러와 UI 에 반영한다.</summary>
public sealed class GameSession(Engine engine, IJSRuntime js, NavigationManager nav) : IAsyncDisposable
{
    private const string TokenKey = "dollyforge.qrToken";
    private const int MaxFeed = 8;

    private readonly WorldClient _net = new();
    private DotNetObjectReference<GameSession>? _self;
    private int _toastSeq;

    public LoginStep Step { get; private set; } = LoginStep.Connecting;
    public string? Token { get; private set; }
    public string? QrDataUrl { get; private set; }
    public string? MaskedPhone { get; private set; }
    public string? DevCode { get; private set; }
    public string? Error { get; private set; }
    public byte Gender { get; private set; }
    public uint MyId { get; private set; }
    public PlayerState? State { get; private set; }
    public string? HoverLabel { get; private set; }
    public string? Toast { get; private set; }
    public string? Dialogue { get; private set; }
    public EndingMessage? Ending { get; private set; }
    public List<FeedLine> Feed { get; } = [];
    public int OnlineOthers { get; private set; }

    private readonly HashSet<uint> _others = [];

    public event Action? Changed;
    public event Action? ChatRequested;

    public string ServerUrl
    {
        get
        {
            var host = new Uri(nav.BaseUri).Host;
            return $"ws://{host}:7700";
        }
    }

    public async Task StartAsync(string viewportId)
    {
        _self = DotNetObjectReference.Create(this);
        try { await engine.InitAsync(viewportId, _self, Gender); }
        catch (JSException e)
        {
            Error = $"3D 엔진을 불러오지 못했습니다. 새로고침해 주세요. ({e.Message})";
            Step = LoginStep.Disconnected;
            Changed?.Invoke();
            return;
        }
        Token = await LoadTokenAsync();
        if (Token is not null) QrDataUrl = MakeQr(Token);
        _net.OnMessage += Handle;
        _net.OnClosed += reason =>
        {
            Error = reason;
            Step = LoginStep.Disconnected;
            Changed?.Invoke();
        };
        await ConnectAsync();
    }

    public async Task ConnectAsync()
    {
        Step = LoginStep.Connecting;
        Error = null;
        Changed?.Invoke();
        try
        {
            await _net.ConnectAsync(new Uri(ServerUrl));
            Step = Token is null ? LoginStep.Phone : LoginStep.QrIssued;
        }
        catch (Exception e)
        {
            Error = $"월드 서버({ServerUrl})에 연결할 수 없습니다. 서버가 실행 중인지 확인하세요. ({e.Message})";
            Step = LoginStep.Disconnected;
        }
        Changed?.Invoke();
    }

    // ---------- 로그인 흐름 ----------

    public async Task SetGender(byte gender)
    {
        Gender = gender;
        await engine.ShowPreview(gender);
    }

    public Task Register(string phone) { Error = null; return _net.SendAsync(Packets.Register(phone, Gender)); }

    public Task RequestCode()
    {
        Error = null;
        return Token is null ? Task.CompletedTask : _net.SendAsync(Packets.RequestCode(Token));
    }

    public Task Login(string code)
    {
        Error = null;
        return Token is null ? Task.CompletedTask : _net.SendAsync(Packets.Login(Token, code.Trim()));
    }

    public async Task ForgetAccount()
    {
        Token = null;
        QrDataUrl = null;
        DevCode = null;
        Step = LoginStep.Phone;
        try { await js.InvokeVoidAsync("localStorage.removeItem", TokenKey); } catch (JSException) { }
        Changed?.Invoke();
    }

    public Task SendChat(string text) =>
        string.IsNullOrWhiteSpace(text) ? Task.CompletedTask : _net.SendAsync(Packets.Chat(text));

    public Task Restart()
    {
        Ending = null;
        Changed?.Invoke();
        return _net.SendAsync(Packets.Restart());
    }

    public void CloseDialogue()
    {
        Dialogue = null;
        Changed?.Invoke();
    }

    // ---------- 서버 메시지 ----------

    private void Handle(ServerMessage msg)
    {
        switch (msg)
        {
            case Registered r:
                Token = r.Token;
                QrDataUrl = MakeQr(r.Token);
                Step = LoginStep.QrIssued;
                _ = SaveTokenAsync(r.Token);
                break;
            case CodeSent c:
                MaskedPhone = c.MaskedPhone;
                DevCode = string.IsNullOrEmpty(c.DevCode) ? null : c.DevCode;
                Step = LoginStep.CodeSent;
                break;
            case LoginOk ok:
                MyId = ok.PlayerId;
                Gender = ok.Gender;
                Step = LoginStep.InGame;
                _ = engine.EnterWorld(ok.Gender);
                break;
            case StateUpdate s:
                State = s.State;
                _ = engine.SetWorld(s.State);
                _ = engine.SetVrHud(VrHudLines());
                break;
            case TextMessage t when t.Kind == MessageKind.Dialogue:
                Dialogue = t.Text;
                AddFeed(new FeedLine(t.Kind, t.Text));
                break;
            case TextMessage t:
                ShowToast(t.Text);
                AddFeed(new FeedLine(t.Kind, t.Text));
                break;
            case EndingMessage e:
                Ending = e;
                _ = engine.ExitVr();
                break;
            case Teleport t:
                _ = engine.Teleport(t.X, t.Y, t.Z, t.Yaw);
                break;
            case ErrorMessage e:
                Error = e.Text;
                if (Step == LoginStep.InGame) ShowToast(e.Text);
                break;
            case PlayerJoined p:
                _others.Add(p.Id);
                _ = engine.UpsertPlayer(p.Id, p.Gender, p.X, p.Y, p.Z, p.Yaw);
                break;
            case PlayerMoved p:
                _ = engine.MovePlayer(p.Id, p.X, p.Y, p.Z, p.Yaw);
                return; // 화면 갱신 불필요
            case PlayerLeft p:
                _others.Remove(p.Id);
                _ = engine.RemovePlayer(p.Id);
                break;
            case NodeSpawn n:
                _ = engine.AddNode(n.Id, n.Kind, n.X, n.Z);
                return;
            case NodeRemove n:
                _ = engine.RemoveNode(n.Id);
                return;
            case ChatBroadcast c:
                var who = c.Id == MyId ? "나" : $"돌리 #{c.Id}";
                AddFeed(new FeedLine(MessageKind.System, $"{who}: {c.Text}", IsChat: true));
                break;
        }
        OnlineOthers = _others.Count;
        Changed?.Invoke();
    }

    private void AddFeed(FeedLine line)
    {
        Feed.Add(line);
        if (Feed.Count > MaxFeed) Feed.RemoveAt(0);
    }

    private void ShowToast(string text)
    {
        Toast = text;
        var seq = ++_toastSeq;
        _ = engine.SetVrHud(VrHudLines());
        _ = Task.Delay(4500).ContinueWith(_ =>
        {
            if (seq != _toastSeq) return;
            Toast = null;
            Changed?.Invoke();
        });
    }

    private IEnumerable<string> VrHudLines()
    {
        if (State is not { } s) yield break;
        yield return $"Day {s.Day} · {GameData.PhaseName(s.Phase)} · 동면 {s.Years}년";
        yield return $"배터리 {s.Battery:0}%   생체 {s.Bio:0}%   기억 {s.Memory}";
        var names = GameData.TaskNames(s);
        for (var i = 0; i < names.Count; i++)
            yield return $"{(s.HasTask(i) ? "✔" : "·")} {names[i]}";
        yield return string.Join("  ", GameData.ResourceNames.Select((n, i) => $"{n} {s.Inv[i]}"));
        if (Toast is not null) yield return "» " + Toast;
    }

    // ---------- 렌더러 콜백 ----------

    [JSInvokable]
    public Task OnLocalMove(float x, float y, float z, float yaw) => _net.SendAsync(Packets.Move(x, y, z, yaw));

    [JSInvokable]
    public Task OnInteract(uint target)
    {
        Dialogue = null;
        return _net.SendAsync(Packets.Interact(target));
    }

    [JSInvokable]
    public void OnHover(uint target)
    {
        var label = target == 0 ? null : GameData.StationLabel(target);
        if (label == HoverLabel) return;
        HoverLabel = label;
        _ = engine.SetVrHud(VrHudLines().Append(label is null ? "" : $"[트리거] {label}"));
        Changed?.Invoke();
    }

    [JSInvokable]
    public void OnOpenChat() => ChatRequested?.Invoke();

    // ---------- QR 계정 ----------

    private static string MakeQr(string token)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode($"dollyforge://account/{token}", QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(6, [16, 20, 28], [230, 240, 250]);
        return "data:image/png;base64," + Convert.ToBase64String(png);
    }

    private async Task<string?> LoadTokenAsync()
    {
        try { return await js.InvokeAsync<string?>("localStorage.getItem", TokenKey); }
        catch (JSException) { return null; }
    }

    private async Task SaveTokenAsync(string token)
    {
        try { await js.InvokeVoidAsync("localStorage.setItem", TokenKey, token); }
        catch (JSException) { }
    }

    public async ValueTask DisposeAsync()
    {
        await _net.DisposeAsync();
        _self?.Dispose();
    }
}
