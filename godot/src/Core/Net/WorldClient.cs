using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace DollyForge.Core.Net;

/// <summary>
/// Rust 월드 서버와의 WebSocket 연결. 받은 메시지는 큐에 쌓고, 게임 루프(메인 스레드)가 Drain 으로 꺼내 처리한다.
/// </summary>
public sealed class WorldClient : IAsyncDisposable
{
    ClientWebSocket? _ws;
    CancellationTokenSource? _cts;
    readonly ConcurrentQueue<ServerMessage> _inbox = new();
    readonly SemaphoreSlim _sendLock = new(1, 1);

    public bool IsOpen => _ws?.State == WebSocketState.Open;
    public string? CloseReason { get; private set; }

    public async Task ConnectAsync(Uri uri, TimeSpan timeout)
    {
        await DisposeAsync();
        _ws = new ClientWebSocket();
        _cts = new CancellationTokenSource();
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        connectCts.CancelAfter(timeout);
        await _ws.ConnectAsync(uri, connectCts.Token);
        CloseReason = null;
        _ = ReceiveLoop(_ws, _cts.Token);
    }

    public async Task SendAsync(byte[] payload)
    {
        if (_ws is not { State: WebSocketState.Open }) return;
        await _sendLock.WaitAsync();
        try { await _ws.SendAsync(payload, WebSocketMessageType.Binary, true, _cts?.Token ?? default); }
        catch (WebSocketException e) { CloseReason = e.Message; }
        finally { _sendLock.Release(); }
    }

    public void Send(byte[] payload) => _ = SendAsync(payload);

    /// <summary>쌓인 서버 메시지를 모두 꺼낸다 (호출한 스레드에서 처리).</summary>
    public IEnumerable<ServerMessage> Drain()
    {
        while (_inbox.TryDequeue(out var m)) yield return m;
    }

    async Task ReceiveLoop(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close) { CloseReason = "서버가 연결을 닫았습니다."; return; }
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);
                if (result.MessageType != WebSocketMessageType.Binary) continue;
                try
                {
                    if (Packets.Parse(ms.ToArray()) is { } msg) _inbox.Enqueue(msg);
                }
                catch (InvalidDataException) { }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException e) { CloseReason = $"서버 연결 오류: {e.Message}"; }
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
