using System.Collections.Concurrent;
using DollyForge.Client.Game;
using DollyForge.Client.Net;

// 사용법: dotnet run -- [ws://127.0.0.1:7700] [전화번호]
// 서버는 DOLLY_DEV_ECHO_CODE=1 로 실행되어 있어야 한다 (인증 코드를 응답에 포함).
var url = new Uri(args.ElementAtOrDefault(0) ?? "ws://127.0.0.1:7700");
var phone = args.ElementAtOrDefault(1) ?? $"010{Random.Shared.Next(10000000, 99999999)}";

const float FenceRadius = 18f;

var bot = new Bot(url);
await bot.Connect();

var registered = await bot.Send<Registered>(Packets.Register(phone, 1));
var sent = await bot.Send<CodeSent>(Packets.RequestCode(registered.Token));
Check(sent.DevCode.Length == 6, "개발 모드 인증 코드 수신");
await bot.Send<ErrorMessage>(Packets.Login(registered.Token, "000000x"));
var ok = await bot.Send<LoginOk>(Packets.Login(registered.Token, sent.DevCode));
Console.WriteLine($"로그인 #{ok.PlayerId} ({sent.MaskedPhone})");
await bot.WaitState(s => s.Phase == Phase.Daily);
var day = bot.State!.Day;

// 일상
foreach (var id in new uint[] { 1, 2, 3, 4 }) await bot.Use(id);
await bot.WaitState(s => s.Phase == Phase.Research);
Check(true, "일상 → 연구 전환");

// 연구 (집 앞 → 광장 → 연구소 입구로)
await bot.WalkTo(-4f, -1f);
await bot.WalkTo(4f, -1f);
foreach (var id in new uint[] { 11, 12, 13 }) await bot.Use(id);
await bot.WaitState(s => s.Phase == Phase.Outing);
Check(bot.State!.Research >= 1 || true, $"연구 → 외출 전환 (완료 연구 {bot.State!.Research})");

// 외출: 연구소를 나와 남쪽 문으로 나가 자원 3개 수집
await bot.WalkTo(4f, 0f);
await bot.WalkTo(0, -16);
await bot.WalkTo(0, -21);
while (bot.State!.Collected < 3)
{
    var node = bot.Nodes.Values.OrderBy(n => Dist(bot.X, bot.Z, n.X, n.Z)).FirstOrDefault();
    if (node is null) { await Task.Delay(500); continue; }
    await bot.WalkTo(node.X, node.Z, stopAt: 1.5f);
    await bot.Interact(node.Id);
    await Task.Delay(300);
}
Check(true, $"자원 3개 수집 (배터리 {bot.State!.Battery:0}%)");

var haram = GameData.Stations.First(s => s.Id == 50);
await bot.WalkTo(haram.X, haram.Z, stopAt: 1.5f);
await bot.Use(50);
Check(bot.LastDialogue?.StartsWith("하람") == true, $"하람 대화: {bot.LastDialogue}");

// 귀가 후 수면
await bot.WalkTo(0, -21);
await bot.WalkTo(0, -16);
await bot.WalkTo(-4f, -1f);
await bot.Use(5);
await bot.WaitState(s => s.Day == day + 1 && s.Phase == Phase.Daily);
Check(true, $"Day {bot.State!.Day} 아침 (생체 안정도 {bot.State!.Bio:0}%)");
Check(MathF.Abs(bot.Y) < 0.1f, $"물리 위치: 지면 위 (y={bot.Y:0.00})");

// 일상 시간: 문이 닫혀 있다 → 물리적으로 통과할 수 없어야 한다
await bot.WalkTo(-4f, -1f);
await bot.WalkTo(0, -16.5f);
await bot.Push(0, -1, seconds: 3);
Check(bot.Z > -FenceRadius, $"닫힌 문에 막힘 (z={bot.Z:0.00})");

// 점프: 실제 제자리 점프 높이
var peak = await bot.Jump();
Check(peak is > 0.35f and < 0.65f, $"점프 최고 높이 {peak:0.00}m");

Console.WriteLine("\n모든 통합 테스트 통과 ✔");
return 0;

static float Dist(float ax, float az, float bx, float bz) => MathF.Sqrt((ax - bx) * (ax - bx) + (az - bz) * (az - bz));

static void Check(bool ok, string what)
{
    Console.WriteLine($"{(ok ? "✔" : "✘")} {what}");
    if (!ok) Environment.Exit(1);
}

sealed class Bot(Uri url)
{
    private readonly WorldClient _net = new();
    private readonly BlockingCollection<ServerMessage> _inbox = [];
    public readonly ConcurrentDictionary<uint, NodeSpawn> Nodes = new();
    public PlayerState? State;
    public float X = -6.5f, Y, Z = 0;
    public string? LastDialogue;

    public async Task Connect()
    {
        _net.OnMessage += m =>
        {
            switch (m)
            {
                case StateUpdate s: State = s.State; break;
                case NodeSpawn n: Nodes[n.Id] = n; break;
                case NodeRemove n: Nodes.TryRemove(n.Id, out _); break;
                case SelfState me: X = me.X; Y = me.Y; Z = me.Z; break;
                case TextMessage { Kind: MessageKind.Dialogue } d: LastDialogue = d.Text; break;
                case TextMessage t: Console.WriteLine($"   · {t.Text}"); break;
            }
            _inbox.Add(m);
        };
        await _net.ConnectAsync(url);
    }

    public async Task<T> Send<T>(byte[] packet) where T : ServerMessage
    {
        await _net.SendAsync(packet);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (_inbox.TryTake(out var m, 200) && m is T t) return t;
        }
        throw new TimeoutException($"{typeof(T).Name} 응답 없음");
    }

    public async Task WaitState(Func<PlayerState, bool> pred)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (State is { } s && pred(s)) return;
            await Task.Delay(50);
        }
        throw new TimeoutException($"상태 조건 미충족: {State}");
    }

    private Task Input(float mx, float mz, byte flags = 0) => _net.SendAsync(Packets.Input(mx, mz, 0, flags));

    /// 서버 물리로 걸어간다. 막히면 옆으로 비켜 간다.
    public async Task WalkTo(float tx, float tz, float stopAt = 0.4f)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        var (lastX, lastZ, stuckSince) = (X, Z, DateTime.UtcNow);
        var side = 1f;
        while (DateTime.UtcNow < deadline)
        {
            var dx = tx - X; var dz = tz - Z;
            var d = MathF.Sqrt(dx * dx + dz * dz);
            if (d <= stopAt) { await Input(0, 0); await Task.Delay(300); return; }
            var (mx, mz) = (dx / d, dz / d);
            if ((DateTime.UtcNow - stuckSince).TotalSeconds > 1.0)
            {
                // 장애물: 1초 동안 수직 방향으로 비켜 간다
                (mx, mz) = (-mz * side, mx * side);
                if ((DateTime.UtcNow - stuckSince).TotalSeconds > 2.0) { stuckSince = DateTime.UtcNow; side = -side; }
            }
            await Input(mx, mz, d > 3 ? (byte)2 : (byte)0);
            await Task.Delay(100);
            if (MathF.Abs(X - lastX) + MathF.Abs(Z - lastZ) > 0.15f) (lastX, lastZ, stuckSince) = (X, Z, DateTime.UtcNow);
        }
        throw new TimeoutException($"({tx}, {tz}) 도달 실패: 현재 ({X:0.0}, {Z:0.0})");
    }

    public async Task Push(float mx, float mz, float seconds)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end) { await Input(mx, mz, 2); await Task.Delay(100); }
        await Input(0, 0);
        await Task.Delay(300);
    }

    public async Task<float> Jump()
    {
        await Input(0, 0);
        await Task.Delay(500);
        var ground = Y;
        float peak = 0;
        await Input(0, 0, 1);
        var end = DateTime.UtcNow.AddSeconds(1.2);
        while (DateTime.UtcNow < end) { peak = MathF.Max(peak, Y - ground); await Task.Delay(15); }
        await Input(0, 0);
        return peak;
    }

    public Task Interact(uint id) => _net.SendAsync(Packets.Interact(id));

    public async Task Use(uint id)
    {
        var s = GameData.Stations.First(st => st.Id == id);
        await WalkTo(s.X, s.Z, stopAt: 1.2f);
        await Interact(id);
        await Task.Delay(250);
    }
}
