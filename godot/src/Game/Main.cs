using Godot;
using DollyForge.Core;
using DollyForge.Core.Net;

namespace DollyForge.Game;

/// <summary>
/// 게임 시작점. 월드, 작업장 장치, 플레이어, 통신망(Rust 월드 서버) 세션, 주문·창고 흐름을 묶는다.
/// 온라인이면 결제·소모품 재고·창고는 서버가 권위를 갖고, 오프라인이면 이 기기 안에서만 계산한다.
/// 개발 확인용 인자: -- --offline | --shot=경로.png --frames=N [--look=yaw,pitch] [--at=x,y,z] [--catalog] | --selftest
/// </summary>
public partial class Main : Node3D
{
    double DeliverySeconds = 20; // 택배 도착까지 (게임 시간)

    World _world = null!;
    Devices _devices = null!;
    Player _player = null!;
    Session _session = null!;
    RemotePlayers _remotes = null!;
    CatalogUi? _catalog;
    StorageUi? _storage;
    LoginUi? _login;
    Label _slip = null!;
    double _slipTimer, _time, _poseTimer;
    readonly List<(double At, List<PartDef> Parts)> _deliveries = new();
    (string Name, HashSet<int> Ids)? _pendingStow;
    IReadOnlyList<StoredDevice> _stored = [];

    string? _shotPath;
    int _shotFrames;

    bool Online => _session.Online;

    // 자동 시험(OnlineTest)용 접근
    public Session SessionNode => _session;
    public Devices DevicesNode => _devices;
    public RemotePlayers Remotes => _remotes;
    public Player PlayerNode => _player;
    public int PendingDeliveries => _deliveries.Count;
    public void StowDevice(PartBody b) => Stow(b);

    public override void _Ready()
    {
        SetupInput();
        _world = new World();
        AddChild(_world);
        _devices = new Devices { Shop = new ShopAccount(ShopAccount.StartBudgetKrw) };
        AddChild(_devices);
        _remotes = new RemotePlayers();
        AddChild(_remotes);
        _session = new Session();
        AddChild(_session);
        _session.Received += OnServer;
        _session.Disconnected += reason =>
        {
            _remotes.Clear();
            Notify($"{reason} — 오프라인으로 계속합니다. (결제·창고는 통신망이 필요)");
        };
        _devices.OnConsume = (sku, n) => { if (Online) _session.Send(Packets.Consume(sku, n)); };
        _devices.OnRefund = (sku, n) => { if (Online) _session.Send(Packets.Refund(sku, n)); };

        // 견본: 카탈로그 부품으로 실제 조립한 2륜 로봇 (작업대 위). 견본 비용은 예산에서 빠지지 않는다.
        var sample = Blueprints.TwoWheelRobot(new ShopAccount(1_000_000), (World.WorkbenchTop + new Vector3(0.3f, 0.01f, 0)).N());
        _devices.Adopt(sample);

        _player = new Player { Devices = _devices, Position = World.PlayerSpawn };
        _player.RotationDegrees = new Vector3(0, 25, 0);
        _player.OpenCatalog = OpenCatalog;
        _player.OpenStorage = OpenStorage;
        _player.Stow = Stow;
        _player.Notify = Notify;
        AddChild(_player);

        BuildOverlay();
        UpdateLedger();
        var args = OS.GetCmdlineUserArgs();
        foreach (var a in args)
        {
            if (a.StartsWith("--server=")) _session.ServerUrl = a["--server=".Length..];
            if (a.StartsWith("--delivery=")) DeliverySeconds = double.Parse(a["--delivery=".Length..]);
        }
        if (args.Any(a => a is "--offline" or "--selftest" or "--onlinetest" || a.StartsWith("--shot"))) StartOffline(quiet: true);
        else ShowLogin();
        ParseArgs(args);
    }

    static void SetupInput()
    {
        void Bind(string action, params Key[] keys)
        {
            if (!InputMap.HasAction(action)) InputMap.AddAction(action);
            foreach (var k in keys) InputMap.ActionAddEvent(action, new InputEventKey { PhysicalKeycode = k });
        }
        Bind("move_forward", Key.W);
        Bind("move_back", Key.S);
        Bind("move_left", Key.A);
        Bind("move_right", Key.D);
        Bind("run", Key.Shift);
        Bind("jump", Key.Space);
    }

    // ------------------------------------------------------------------ 접속

    void ShowLogin()
    {
        _player.SetPhysicsProcess(false); // 양식에 글자를 쓰는 동안 몸이 움직이지 않게
        Input.MouseMode = Input.MouseModeEnum.Visible;
        _login = new LoginUi { Session = _session, Offline = () => StartOffline(quiet: false) };
        Overlay(_login);
    }

    void CloseLogin()
    {
        _login?.QueueFree();
        _login = null;
        _player.SetPhysicsProcess(true);
        Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    void StartOffline(bool quiet)
    {
        CloseLogin();
        if (!quiet) Notify("오프라인 작업 — 예산·재고는 이 기기 안에서만 계산됩니다.");
        else Notify("작업장입니다. 1–9 공구 · E 카탈로그 · Q 전원 · R 배선 반전 · F5 창고에 넣기 · F6 창고 · F8 내보내기 · F9 물성");
    }

    void OnServer(ServerMessage m)
    {
        switch (m)
        {
            case LoginOk ok:
                _player.EyeHeight = ok.Gender == 1 ? 1.67f : 1.55f;
                CloseLogin();
                Notify($"통신망 접속 — 전신 사이보그 #{ok.PlayerId}. E 카탈로그 · F5 창고에 넣기 · F6 창고 · F8 내보내기");
                break;
            case Profile p:
                _devices.Shop.Sync(p.BalanceKrw, p.Consumables);
                UpdateLedger();
                break;
            case PurchaseResult r:
                _catalog?.ShowResult(r.Ok, r.Message);
                if (!r.Ok) { Notify(r.Message); break; }
                var parts = r.Lines
                    .Where(l => !Catalog.Get(l.Sku).IsConsumable)
                    .SelectMany(l => Enumerable.Range(0, l.Qty).Select(_ => Catalog.Get(l.Sku).Build(l.LengthMm, l.WidthMm)!))
                    .ToList();
                if (parts.Count > 0) _deliveries.Add((_time + DeliverySeconds, parts));
                Notify(parts.Count > 0 ? $"{r.Message} — 택배가 약 {DeliverySeconds:0}초 뒤 하차장에 도착합니다." : $"{r.Message} — 소모품을 장부에 적었습니다.");
                break;
            case PlayerJoined j:
                if (j.Id != _session.MyId) _remotes.Upsert(j.Id, j.Gender, new Vector3(j.X, j.Y, j.Z), j.Yaw);
                break;
            case PlayerMoved mv:
                _remotes.Move(mv.Id, new Vector3(mv.X, mv.Y, mv.Z), mv.Yaw);
                break;
            case PlayerLeft left:
                _remotes.Remove(left.Id);
                break;
            case DeviceList list:
                _stored = list.Devices;
                _storage?.SetDevices(list.Devices);
                if (_pendingStow is { } ps && list.Devices.Any(d => d.Name == ps.Name))
                {
                    _devices.RemoveDevice(ps.Ids);
                    _pendingStow = null;
                }
                break;
            case DeviceData data:
                PlaceRetrieved(data);
                break;
            case ErrorMessage e:
                _pendingStow = null;
                if (_login is null) Notify(e.Text);
                break;
            case TextMessage t:
                Notify(t.Text);
                break;
            case ChatBroadcast c:
                Notify($"사이보그 #{c.Id}: {c.Text}");
                break;
        }
    }

    // ------------------------------------------------------------------ 창고

    void Stow(PartBody any)
    {
        if (!Online) { Notify("창고는 통신망 접속이 필요합니다."); return; }
        var ids = _devices.World.ConnectedTo(any.Part.Id);
        var name = $"기기-{DateTime.Now:MMdd-HHmmss}";
        var json = Serialization.DeviceJson(_devices.DeviceOf(any, name));
        _pendingStow = (name, ids);
        _session.Send(Packets.StowDevice(name, json));
    }

    void OpenStorage()
    {
        if (!Online) { Notify("창고는 통신망 접속이 필요합니다."); return; }
        if (_storage is not null) return;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        _storage = new StorageUi
        {
            Retrieve = name => _session.Send(Packets.RetrieveDevice(name)),
            Closed = () =>
            {
                _storage?.QueueFree();
                _storage = null;
                Input.MouseMode = Input.MouseModeEnum.Captured;
            },
        };
        _storage.SetDevices(_stored);
        Overlay(_storage);
        _session.Send(Packets.ListDevices());
    }

    /// <summary>창고에서 꺼낸 기기를 눈앞 1.5 m 바닥에 내려놓는다.</summary>
    void PlaceRetrieved(DeviceData data)
    {
        var device = Serialization.ParseDevice(data.Json);
        var center = device.Parts.Aggregate(System.Numerics.Vector3.Zero, (acc, p) => acc + p.Pose.Position) / device.Parts.Count;
        var front = _player.GlobalPosition - _player.GlobalBasis.Z * 1.5f;
        var offset = new System.Numerics.Vector3(front.X - center.X, 0, front.Z - center.Z);
        offset.Y = _player.GlobalPosition.Y + 0.03f - device.LowestPoint();
        device.Translate(offset);
        _devices.Adopt(device);
        Notify($"창고에서 꺼냈습니다: {data.Name} (부품 {device.Parts.Count}개)");
    }

    // ------------------------------------------------------------------ 화면

    void Overlay(Control c)
    {
        var layer = new CanvasLayer { Layer = 10 };
        layer.AddChild(c);
        c.TreeExiting += () => layer.QueueFree();
        AddChild(layer);
    }

    /// <summary>화면 아래 종이 쪽지 한 장 (안내·영수증). 몇 초 뒤 사라진다.</summary>
    void BuildOverlay()
    {
        var layer = new CanvasLayer();
        AddChild(layer);
        // 조준점: 아주 작은 점 하나
        layer.AddChild(new ColorRect
        {
            Color = new Color(1, 1, 1, 0.55f), Size = new Vector2(4, 4),
            AnchorLeft = 0.5f, AnchorTop = 0.5f, AnchorRight = 0.5f, AnchorBottom = 0.5f,
            OffsetLeft = -2, OffsetTop = -2, OffsetRight = 2, OffsetBottom = 2,
        });
        var slipBox = new PanelContainer
        {
            AnchorLeft = 0.5f, AnchorRight = 0.5f, AnchorTop = 1, AnchorBottom = 1,
            OffsetLeft = -460, OffsetRight = 460, OffsetTop = -120, OffsetBottom = -24,
        };
        slipBox.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.96f, 0.94f, 0.88f, 0.94f), ShadowColor = new Color(0, 0, 0, 0.35f), ShadowSize = 6,
            ContentMarginLeft = 18, ContentMarginRight = 18, ContentMarginTop = 10, ContentMarginBottom = 10,
        });
        _slip = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, HorizontalAlignment = HorizontalAlignment.Center };
        _slip.AddThemeFontOverride("font", Look.PenUi);
        _slip.AddThemeFontSizeOverride("font_size", 22);
        _slip.AddThemeColorOverride("font_color", Paper.Ink);
        slipBox.AddChild(_slip);
        layer.AddChild(slipBox);
        _slip.SetMeta("box", slipBox);
    }

    void Notify(string text)
    {
        _slip.Text = text;
        ((Control)_slip.GetMeta("box").AsGodotObject()).Visible = true;
        _slipTimer = 6;
        UpdateLedger();
    }

    void UpdateLedger()
    {
        var shop = _devices.Shop;
        var stock = shop.Consumables.Where(kv => kv.Value > 0).Select(kv => $"{Catalog.Get(kv.Key).NameKo} {kv.Value}").ToList();
        _world.LedgerTag.Text = $"장부 ({(Online ? "통신망" : "오프라인")})\n잔액 ₩{shop.BalanceKrw:N0}\n\n소모품 재고\n" +
                                (stock.Count > 0 ? string.Join("\n", stock) : "없음") +
                                (_deliveries.Count > 0 ? $"\n\n배송 중 {_deliveries.Count}건" : "");
    }

    void OpenCatalog()
    {
        if (_catalog is not null) return;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        _catalog = new CatalogUi { Shop = _devices.Shop };
        if (Online) _catalog.SubmitOnline = lines => _session.Send(Packets.Purchase(lines));
        _catalog.Ordered = parts =>
        {
            if (parts.Count > 0) _deliveries.Add((_time + DeliverySeconds, parts));
            Notify(parts.Count > 0 ? $"주문 완료 — 택배가 약 {DeliverySeconds:0}초 뒤 하차장에 도착합니다." : "주문 완료 — 소모품을 장부에 적었습니다.");
        };
        _catalog.Closed = () =>
        {
            _catalog?.QueueFree();
            _catalog = null;
            Input.MouseMode = Input.MouseModeEnum.Captured;
        };
        Overlay(_catalog);
    }

    public override void _Process(double delta)
    {
        _time += delta;
        if (_slipTimer > 0 && (_slipTimer -= delta) <= 0)
            ((Control)_slip.GetMeta("box").AsGodotObject()).Visible = false;

        for (var i = _deliveries.Count - 1; i >= 0; i--)
        {
            if (_deliveries[i].At > _time) continue;
            Deliver(_deliveries[i].Parts);
            _deliveries.RemoveAt(i);
        }

        // 내 위치를 통신망에 알린다 (10 Hz)
        if (Online && (_poseTimer -= delta) <= 0)
        {
            _poseTimer = 0.1;
            var p = _player.GlobalPosition;
            _session.Send(Packets.Pose(p.X, p.Y, p.Z, _player.Rotation.Y));
        }

        if (_shotPath is not null && --_shotFrames <= 0)
        {
            var img = GetViewport().GetTexture().GetImage();
            img.SavePng(_shotPath);
            GD.Print($"[shot] {_shotPath}");
            _shotPath = null;
            GetTree().Quit();
        }
    }

    /// <summary>택배 도착: 팔레트 위에 부품을 차곡차곡 내려놓는다.</summary>
    void Deliver(List<PartDef> parts)
    {
        var x = -0.45f;
        var z = -0.35f;
        var layer = 0f;
        foreach (var def in parts)
        {
            var size = def.Shape.Kind switch
            {
                ShapeKind.Box => def.Shape.Size.G(),
                ShapeKind.Cylinder => new Vector3(def.Shape.Radius * 2, def.Shape.Radius * 2, def.Shape.Length),
                _ => Vector3.One * def.Shape.Radius * 2,
            };
            var pos = World.DeliveryPoint + new Vector3(x + size.X / 2, 0.08f + layer + size.Y / 2, z);
            _devices.Spawn(def, new Transform3D(Basis.Identity, pos));
            x += size.X + 0.05f;
            if (x > 0.45f) { x = -0.45f; z += 0.25f; }
            if (z > 0.4f) { z = -0.35f; layer += 0.15f; }
        }
        Notify($"택배 도착 — 부품 {parts.Count}개가 하차장 팔레트에 놓였습니다.");
    }

    void ParseArgs(string[] args)
    {
        foreach (var arg in args)
        {
            var (k, v) = arg.Split('=', 2) is [var a, var b] ? (a, b) : (arg, "");
            switch (k)
            {
                case "--shot": _shotPath = v; break;
                case "--frames": _shotFrames = int.Parse(v); break;
                case "--at":
                    var p = v.Split(',').Select(float.Parse).ToArray();
                    _player.Position = new Vector3(p[0], p[1], p[2]);
                    break;
                case "--look":
                    var l = v.Split(',').Select(float.Parse).ToArray();
                    _player.RotationDegrees = new Vector3(0, l[0], 0);
                    _player.Camera.RotationDegrees = new Vector3(l[1], 0, 0);
                    break;
                case "--catalog": CallDeferred(nameof(OpenCatalog)); break;
                case "--login": CallDeferred(nameof(ShowLogin)); break;
                case "--selftest": AddChild(new SelfTest { Devices = _devices }); break;
                case "--onlinetest": AddChild(new OnlineTest { Main = this }); break;
            }
        }
        if (_shotPath is not null && _shotFrames <= 0) _shotFrames = 120;
    }
}
