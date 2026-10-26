using Godot;
using DollyForge.Core;

namespace DollyForge.Game;

/// <summary>
/// 게임 시작점. 입력, 월드, 작업장 장치, 플레이어, 주문 흐름을 묶는다.
/// 개발 확인용 인자: -- --shot=경로.png --frames=N [--look=yaw,pitch] [--at=x,y,z]
/// </summary>
public partial class Main : Node3D
{
    const long StartBudgetKrw = 300_000;
    const double DeliverySeconds = 20; // 택배 도착까지 (게임 시간)

    World _world = null!;
    Devices _devices = null!;
    Player _player = null!;
    CatalogUi? _catalog;
    Label _slip = null!;
    double _slipTimer;
    readonly List<(double At, List<PartDef> Parts)> _deliveries = new();
    double _time;

    // 스크린샷 모드
    string? _shotPath;
    int _shotFrames;

    public override void _Ready()
    {
        SetupInput();
        _world = new World();
        AddChild(_world);
        _devices = new Devices { Shop = new ShopAccount(StartBudgetKrw) };
        AddChild(_devices);

        // 견본: 카탈로그 부품으로 실제 조립한 2륜 로봇 (작업대 위). 견본 비용은 예산에서 빠지지 않는다.
        var sample = Blueprints.TwoWheelRobot(new ShopAccount(1_000_000), (World.WorkbenchTop + new Vector3(0.3f, 0.01f, 0)).N());
        _devices.Adopt(sample);

        _player = new Player { Devices = _devices, Position = World.PlayerSpawn };
        _player.RotationDegrees = new Vector3(0, 25, 0);
        _player.OpenCatalog = OpenCatalog;
        _player.Notify = Notify;
        AddChild(_player);

        BuildOverlay();
        UpdateLedger();
        Notify("작업장입니다. 1–9 공구 · E 카탈로그 · Q 전원 · R 배선 반전 · F8 내보내기 · F9 물성 · Space 점프");
        Input.MouseMode = Input.MouseModeEnum.Captured;
        ParseArgs();
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
        _slip.AddThemeFontOverride("font", Look.Pen);
        _slip.AddThemeFontSizeOverride("font_size", 22);
        _slip.AddThemeColorOverride("font_color", new Color(0.1f, 0.12f, 0.22f));
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
        _world.LedgerTag.Text = $"장부\n잔액 ₩{shop.BalanceKrw:N0}\n\n소모품 재고\n" + (stock.Count > 0 ? string.Join("\n", stock) : "없음") +
                                (_deliveries.Count > 0 ? $"\n\n배송 중 {_deliveries.Count}건" : "");
    }

    void OpenCatalog()
    {
        if (_catalog is not null) return;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        _catalog = new CatalogUi { Shop = _devices.Shop };
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
        var layer = new CanvasLayer { Layer = 10 };
        layer.AddChild(_catalog);
        _catalog.TreeExiting += () => layer.QueueFree();
        AddChild(layer);
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

    void ParseArgs()
    {
        foreach (var arg in OS.GetCmdlineUserArgs())
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
            }
        }
        if (_shotPath is not null && _shotFrames <= 0) _shotFrames = 120;
    }
}
