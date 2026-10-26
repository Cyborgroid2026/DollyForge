using System.Net.WebSockets;

namespace DollyForge.Client.Net;

/// <summary>Rust 월드 서버와의 WebSocket 연결. 받은 메시지는 OnMessage 로 전달한다.</summary>
public sealed class WorldClient : IAsyncDisposable
{
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;

    public event Action<ServerMessage>? OnMessage;
    public event Action<string>? OnClosed;

    public bool IsOpen => _ws?.State == WebSocketState.Open;

    public async Task ConnectAsync(Uri uri)
    {
        await DisposeAsync();
        _ws = new ClientWebSocket();
        _cts = new CancellationTokenSource();
        await _ws.ConnectAsync(uri, _cts.Token);
        _ = ReceiveLoop(_ws, _cts.Token);
    }

    public async Task SendAsync(byte[] payload)
    {
        if (_ws is not { State: WebSocketState.Open }) return;
        await _ws.SendAsync(payload, WebSocketMessageType.Binary, true, _cts?.Token ?? default);
    }

    private async Task ReceiveLoop(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var reason = "서버 연결이 끊어졌습니다.";
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close) goto closed;
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Binary) continue;
                ServerMessage? msg;
                try { msg = Packets.Parse(ms.ToArray()); }
                catch (InvalidDataException) { continue; }
                if (msg is not null) OnMessage?.Invoke(msg);
            }
        }
        catch (OperationCanceledException) { return; }
        catch (WebSocketException e) { reason = $"서버 연결 오류: {e.Message}"; }
        closed:
        OnClosed?.Invoke(reason);
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_ws is { State: WebSocketState.Open })
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); }
            catch (WebSocketException) { }
        }
        _ws?.Dispose();
        _ws = null;
    }
}
