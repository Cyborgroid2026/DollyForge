using Godot;
using DollyForge.Core;
using DollyForge.Core.Net;

namespace DollyForge.Game;

/// <summary>
/// 실제 게임 안에서 통신망 흐름을 확인한다 (월드 서버가 DOLLY_DEV_ECHO_CODE=1 로 떠 있어야 함):
/// QR 등록 → 인증 → 접속 → 서버 결제 = C# 견적 → 택배 실물 도착 → 기기 창고 보관(월드에서 사라짐) → 꺼내기(눈앞에 다시 놓임)
/// → 다른 사이보그 접속 시 몸이 보임.
/// 실행: godot --path godot -- --onlinetest --server=ws://127.0.0.1:7700 --delivery=1
/// </summary>
public partial class OnlineTest : Node
{
    public Main Main { get; set; } = null!;
    int _failures, _step;
    double _t, _deadline = 60;
    string? _token;
    int _partsBefore;
    PartBody? _robot;
    WorldClient? _other;
    List<OrderLine> _order = [];

    Session S => Main.SessionNode;
    Devices D => Main.DevicesNode;

    void Check(bool ok, string what)
    {
        GD.Print($"{(ok ? "✔" : "✘")} {what}");
        if (!ok) _failures++;
    }

    public override async void _Ready()
    {
        S.Received += OnMessage;
        if (!await S.ConnectAsync())
        {
            Check(false, $"서버 연결 ({S.ServerUrl})");
            Finish();
            return;
        }
        S.Send(Packets.Register($"010{GD.RandRange(10000000, 99999999)}", 1));
    }

    void OnMessage(ServerMessage m)
    {
        switch (m)
        {
            case Registered r:
                _token = r.Token;
                S.Send(Packets.RequestCode(r.Token));
                break;
            case CodeSent c:
                S.Send(Packets.Login(_token!, c.DevCode));
                break;
            case LoginOk ok:
                Check(true, $"QR 계정 접속: 사이보그 #{ok.PlayerId}");
                _step = 1;
                break;
            case PurchaseResult r when _step == 2:
                var quote = ShopAccount.Quote(_order).Total;
                Check(r.Ok && r.TotalKrw == quote, $"서버 결제 ₩{r.TotalKrw:N0} = C# 견적 ₩{quote:N0}");
                _step = 3;
                _t = 0;
                break;
            case ErrorMessage e:
                Check(false, $"서버 오류: {e.Text}");
                break;
        }
    }

    public override void _Process(double delta)
    {
        _t += delta;
        if ((_deadline -= delta) <= 0) { Check(false, $"시간 초과 (단계 {_step})"); Finish(); return; }
        switch (_step)
        {
            case 1 when _t > 0.5:
                Check(D.Shop.BalanceKrw == ShopAccount.StartBudgetKrw, $"장부 잔액 = 서버 ₩{D.Shop.BalanceKrw:N0}");
                _order = [new(Catalog.Get("AL-2020"), 2, 600), new(Catalog.Get("WH-65-D6"), 2), new(Catalog.Get("FS-BOLT-M8"), 1)];
                _partsBefore = D.Bodies.Count();
                S.Send(Packets.Purchase(_order));
                _step = 2;
                break;
            case 3 when Main.PendingDeliveries == 0 && _t > 0.2:
                var arrived = D.Bodies.Count() - _partsBefore;
                Check(arrived == 4, $"택배 실물 도착: 부품 {arrived}개 (프로파일 2 + 바퀴 2)");
                Check(D.Shop.Consumables.GetValueOrDefault("FS-BOLT-M8") == 10, "볼트 묶음은 장부(서버 재고)에");
                _robot = D.Bodies.First(b => b.Part.Def.Sku == "PL-AL6061-5");
                _partsBefore = D.Bodies.Count();
                Main.StowDevice(_robot);
                _step = 4;
                _t = 0;
                break;
            case 4 when _t > 1.0:
                Check(D.Bodies.Count() == _partsBefore - 7, $"견본 로봇 창고 보관 → 월드에서 부품 7개 사라짐 ({_partsBefore} → {D.Bodies.Count()})");
                S.Send(Packets.ListDevices());
                _step = 5;
                _t = 0;
                break;
            case 5 when _t > 0.5:
                S.Send(Packets.RetrieveDevice(_lastStored ?? ""));
                _step = 6;
                _t = 0;
                break;
            case 6 when _t > 1.0:
                var back = D.Bodies.Count() == _partsBefore;
                var player = Main.PlayerNode.GlobalPosition;
                var near = D.Bodies.Where(b => b.Part.Def.Sku is "PL-AL6061-5").Any(b => b.GlobalPosition.DistanceTo(player) < 3);
                Check(back && near, $"창고에서 꺼냄 → 부품 {D.Bodies.Count()}개, 플레이어 앞에 놓임");
                _ = ConnectOther();
                _step = 7;
                _t = 0;
                break;
            case 7 when Main.Remotes.Count > 0:
                Check(true, $"다른 사이보그 접속 → 몸 {Main.Remotes.Count}구가 월드에 보임");
                Finish();
                break;
        }
    }

    string? _lastStored;

    public override void _EnterTree()
    {
        // 창고 목록에서 이름을 기억해 둔다
        Main.SessionNode.Received += m => { if (m is DeviceList l && l.Devices.Count > 0) _lastStored = l.Devices[0].Name; };
    }

    async Task ConnectOther()
    {
        _other = new WorldClient();
        await _other.ConnectAsync(new Uri(S.ServerUrl), TimeSpan.FromSeconds(3));
        string? token = null;
        await _other.SendAsync(Packets.Register($"010{GD.RandRange(10000000, 99999999)}", 0));
        for (var i = 0; i < 100; i++)
        {
            await Task.Delay(30);
            foreach (var m in _other.Drain())
            {
                switch (m)
                {
                    case Registered r: token = r.Token; await _other.SendAsync(Packets.RequestCode(r.Token)); break;
                    case CodeSent c: await _other.SendAsync(Packets.Login(token!, c.DevCode)); break;
                    case LoginOk: await _other.SendAsync(Packets.Pose(-1, 0.1f, -4, 0)); return;
                }
            }
        }
    }

    void Finish()
    {
        GD.Print(_failures == 0 ? "ONLINETEST PASS" : $"ONLINETEST FAIL ({_failures})");
        _step = 99;
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }
}
