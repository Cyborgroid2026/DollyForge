using DollyForge.Core;
using DollyForge.Core.Net;

// 월드 서버 통합 시험: QR 계정 → 결제(서버가 다시 계산한 금액 = C# 견적) → 소모품 설치·회수 → 창고 보관·꺼내기
// → 몸 구성 저장·복원 → 다른 플레이어 위치 중계
var url = new Uri(args.ElementAtOrDefault(0) ?? "ws://127.0.0.1:7700");
var failures = 0;
void Check(bool ok, string what)
{
    Console.WriteLine($"{(ok ? "✔" : "✘")} {what}");
    if (!ok) failures++;
}

var phoneA = $"010{Random.Shared.Next(10000000, 99999999)}";
var a = await Bot.Login(url, phoneA, gender: 0);
Check(a.Profile?.BalanceKrw == ShopAccount.StartBudgetKrw, $"로그인 · 시작 예산 ₩{a.Profile?.BalanceKrw:N0}");

// 결제: 재단품 포함. 서버 금액이 C# 견적과 원 단위까지 같아야 한다.
List<OrderLine> order =
[
    new(Catalog.Get("AL-2020"), 2, 750),
    new(Catalog.Get("PL-AL6061-5"), 1, 220, 200),
    new(Catalog.Get("MT-JGB37-100"), 2),
    new(Catalog.Get("FS-BOLT-M8"), 1),
];
var quote = ShopAccount.Quote(order);
var receipt = await a.Expect<PurchaseResult>(Packets.Purchase(order));
Check(receipt.Ok && receipt.TotalKrw == quote.Total, $"결제: 서버 ₩{receipt.TotalKrw:N0} = C# 견적 ₩{quote.Total:N0}");
Check(receipt.Lines.Zip(order).All(p => p.First.UnitKrw == p.Second.Item.UnitPrice(p.Second.LengthMm, p.Second.WidthMm)), "품목별 단가 일치 (재단 2020 750 mm, 판재 220×200 mm)");
var profile = await a.Next<Profile>();
Check(profile.BalanceKrw == ShopAccount.StartBudgetKrw - quote.Total && profile.Consumables.GetValueOrDefault("FS-BOLT-M8") == 10,
    $"잔액 ₩{profile.BalanceKrw:N0}, M8 볼트 재고 {profile.Consumables.GetValueOrDefault("FS-BOLT-M8")}");

var broke = await a.Expect<PurchaseResult>(Packets.Purchase([new(Catalog.Get("CY-F-THIGHL-HD"), 10)]));
Check(!broke.Ok && broke.Message.Contains("잔액"), $"잔액 초과 주문 거절: {broke.Message}");
var forged = await a.Expect<PurchaseResult>(Packets.Purchase([new(Catalog.Get("AL-2020"), 1, 9000)]));
Check(!forged.Ok, $"범위 밖 재단 거절: {forged.Message}");

// 소모품: 설치(consume) / 회수(refund) — 설치된 만큼만 회수된다
var used = await a.Expect<Profile>(Packets.Consume("FS-BOLT-M8", 4));
Check(used.Consumables["FS-BOLT-M8"] == 6, "볼트 4개 설치 → 재고 6");
var back = await a.Expect<Profile>(Packets.Refund("FS-BOLT-M8", 2));
Check(back.Consumables["FS-BOLT-M8"] == 8, "볼트 2개 회수 → 재고 8");
var cheat = await a.Expect<ErrorMessage>(Packets.Refund("FS-BOLT-M8", 3));
Check(cheat.Text.Contains("회수"), $"설치보다 많은 회수 거절: {cheat.Text}");

// 창고: 넣으면 목록에 있고, 꺼내면 같은 문서가 돌아오며 창고에서 사라진다 (복제 불가)
var robot = Blueprints.TwoWheelRobot(new ShopAccount(1_000_000), System.Numerics.Vector3.Zero);
var json = Serialization.DeviceJson(robot);
var list = await a.Expect<DeviceList>(Packets.StowDevice("2륜 로봇", json));
Check(list.Devices.Any(d => d.Name == "2륜 로봇" && d.Parts == robot.Parts.Count), $"창고 보관: {string.Join(", ", list.Devices.Select(d => $"{d.Name}({d.Parts}부품)"))}");
var data = await a.Expect<DeviceData>(Packets.RetrieveDevice("2륜 로봇"));
var restored = Serialization.ParseDevice(data.Json);
Check(data.Json == json && Export.Urdf(restored) == Export.Urdf(robot), "꺼낸 기기 = 넣은 기기 (URDF 동일)");
var empty = await a.Next<DeviceList>();
Check(empty.Devices.Count == 0, "꺼낸 뒤 창고 비어 있음");
var twice = await a.Expect<ErrorMessage>(Packets.RetrieveDevice("2륜 로봇"));
Check(twice.Text.Contains("없"), "같은 기기를 두 번 꺼낼 수 없음");

// 몸 구성: 정비 수술대에서 바꾼 모듈과 찬 기기를 계정에 저장한다
Check(a.Body is { Modules.Count: 0, Worn.Count: 0 }, "새 계정의 몸 = 표준 모듈, 찬 기기 없음");
var config = BodyConfig.Default(0) with
{
    Modules = new() { [BodySlot.ShinL] = ModuleVariant.HeavyDuty, [BodySlot.ShinR] = ModuleVariant.HeavyDuty },
    Worn = ["ATE-QW-1", "ATE-HS-G"],
};
var saved = await a.Expect<BodyData>(Packets.SaveBody(config.ModuleSkus(), config.Worn));
Check(saved.Modules.SequenceEqual(config.ModuleSkus()) && saved.Worn.SequenceEqual(config.Worn), $"몸 구성 저장: {string.Join(", ", saved.Modules.Concat(saved.Worn))}");
var wrongBody = await a.Expect<ErrorMessage>(Packets.SaveBody(["CY-M-SHINL-HD"], []));
Check(wrongBody.Text.Contains("맞지 않는"), $"다른 체형 모듈 거절: {wrongBody.Text}");
var notWearable = await a.Expect<ErrorMessage>(Packets.SaveBody([], ["MT-NEMA17"]));
Check(notWearable.Text.Contains("찰 수 없는"), $"몸에 찰 수 없는 품목 거절: {notWearable.Text}");

// 다른 플레이어: 입장 알림과 위치 중계
var b = await Bot.Login(url, $"010{Random.Shared.Next(10000000, 99999999)}", gender: 1);
var joined = await a.Next<PlayerJoined>();
Check(joined.Id == b.Id && joined.Gender == 1, $"다른 사이보그 입장 알림 #{joined.Id}");
await b.Client.SendAsync(Packets.Pose(3.5f, 0.2f, -1.25f, 0.5f));
var moved = await a.Next<PlayerMoved>();
Check(moved.Id == b.Id && Math.Abs(moved.X - 3.5f) < 1e-4 && Math.Abs(moved.Z + 1.25f) < 1e-4, $"위치 중계 ({moved.X}, {moved.Y}, {moved.Z})");

// 다시 접속하면 저장된 몸으로 깨어난다 (같은 계정의 이전 접속은 끊긴다)
var again = await Bot.Login(url, phoneA, gender: 0);
var restoredBody = BodyConfig.FromSkus(0, again.Body!.Modules, again.Body.Worn);
var caps = Cyborg.Capabilities(restoredBody);
Check(restoredBody.VariantAt(BodySlot.ShinR) == ModuleVariant.HeavyDuty && restoredBody.Worn.Count == 2,
    $"재접속: 몸 복원 — 점프 {caps.JumpHeight:0.00} m, 몸무게 {caps.MassKg:0.0} kg (찬 기기 포함)");

Console.WriteLine(failures == 0 ? "\n모든 서버 통합 시험 통과 ✔" : $"\n실패 {failures}건");
return failures == 0 ? 0 : 1;

sealed class Bot
{
    public WorldClient Client { get; } = new();
    public uint Id { get; private set; }
    public Profile? Profile { get; private set; }
    public BodyData? Body { get; private set; }

    public static async Task<Bot> Login(Uri url, string phone, byte gender)
    {
        var bot = new Bot();
        await bot.Client.ConnectAsync(url, TimeSpan.FromSeconds(5));
        var reg = await bot.Expect<Registered>(Packets.Register(phone, gender));
        var code = await bot.Expect<CodeSent>(Packets.RequestCode(reg.Token));
        var ok = await bot.Expect<LoginOk>(Packets.Login(reg.Token, code.DevCode));
        bot.Id = ok.PlayerId;
        bot.Profile = await bot.Next<Profile>();
        bot.Body = await bot.Next<BodyData>();
        return bot;
    }

    readonly Queue<ServerMessage> _backlog = new();

    public async Task<T> Expect<T>(byte[] packet) where T : ServerMessage
    {
        await Client.SendAsync(packet);
        return await Next<T>();
    }

    /// <summary>T 가 올 때까지 기다린다. 그 사이 다른 메시지는 나중을 위해 보관한다.</summary>
    public async Task<T> Next<T>() where T : ServerMessage
    {
        for (var n = _backlog.Count; n > 0; n--)
        {
            var m = _backlog.Dequeue();
            if (m is T hit) return hit;
            _backlog.Enqueue(m);
        }
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            foreach (var m in Client.Drain())
            {
                if (m is T t) return t;
                _backlog.Enqueue(m);
            }
            await Task.Delay(20);
        }
        throw new TimeoutException($"{typeof(T).Name} 응답 없음 (보관 중: {string.Join(", ", _backlog.Select(x => x.GetType().Name))})");
    }
}
