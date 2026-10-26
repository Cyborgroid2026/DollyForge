using Godot;
using DollyForge.Core.Net;
using QRCoder;

namespace DollyForge.Game;

/// <summary>종이 양식 공통: 손글씨 잉크, 연한 괘선, 종이 패널.</summary>
public static class Paper
{
    public static readonly Color Sheet = new(0.96f, 0.94f, 0.88f), Ink = new(0.1f, 0.12f, 0.22f), Rule = new(0.55f, 0.62f, 0.75f);

    public static Theme Theme()
    {
        var theme = new Theme { DefaultFont = Look.PenUi, DefaultFontSize = 22 };
        foreach (var t in new[] { "Label", "Button", "ItemList", "OptionButton", "LineEdit", "CheckBox" })
            theme.SetColor("font_color", t, Ink);
        theme.SetColor("font_selected_color", "ItemList", new Color(0.6f, 0.1f, 0.1f));
        theme.SetColor("font_placeholder_color", "LineEdit", new Color(0.4f, 0.45f, 0.55f));
        var flat = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0), BorderColor = Rule, BorderWidthBottom = 1 };
        theme.SetStylebox("panel", "ItemList", flat);
        theme.SetStylebox("normal", "LineEdit", flat);
        theme.SetStylebox("focus", "LineEdit", flat);
        theme.SetStylebox("normal", "OptionButton", flat);
        var button = new StyleBoxFlat
        {
            BgColor = new Color(0.9f, 0.86f, 0.74f), BorderColor = Ink, BorderWidthBottom = 2, BorderWidthTop = 1, BorderWidthLeft = 1, BorderWidthRight = 1,
            ContentMarginLeft = 12, ContentMarginRight = 12, ContentMarginTop = 4, ContentMarginBottom = 4,
        };
        theme.SetStylebox("normal", "Button", button);
        var hover = (StyleBoxFlat)button.Duplicate();
        hover.BgColor = new Color(0.85f, 0.8f, 0.66f);
        theme.SetStylebox("hover", "Button", hover);
        theme.SetStylebox("pressed", "Button", hover);
        return theme;
    }

    public static VBoxContainer Sheet_(Control parent, float left, float right, float top, float bottom)
    {
        var panel = new PanelContainer { AnchorLeft = left, AnchorRight = right, AnchorTop = top, AnchorBottom = bottom };
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = Sheet, ShadowColor = new Color(0, 0, 0, 0.4f), ShadowSize = 8,
            ContentMarginLeft = 30, ContentMarginRight = 30, ContentMarginTop = 24, ContentMarginBottom = 24,
        });
        parent.AddChild(panel);
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 10);
        panel.AddChild(v);
        return v;
    }

    public static Label Heading(string text, int size = 34)
    {
        var l = new Label { Text = text };
        l.AddThemeFontSizeOverride("font_size", size);
        return l;
    }

    public static Label Note(string text) => new() { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
}

/// <summary>
/// 전신 사이보그 신분 등록소: 전화번호로 QR 신분 계정을 발급받고, 그 번호로 온 인증 코드로 접속한다.
/// 서버에 닿지 않으면 오프라인으로 작업할 수 있다.
/// </summary>
public partial class LoginUi : Control
{
    public Session Session { get; set; } = null!;
    public Action? Offline;

    VBoxContainer _form = null!;
    Label _status = null!;
    string? _token;
    byte _gender;

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        Theme = Paper.Theme();
        AddChild(new ColorRect { Color = new Color(0, 0, 0, 0.45f), AnchorRight = 1, AnchorBottom = 1 });
        var sheet = Paper.Sheet_(this, 0.3f, 0.7f, 0.08f, 0.92f);
        sheet.AddChild(Paper.Heading("전신 사이보그 신분 등록"));
        sheet.AddChild(Paper.Note("양자 아날로그 통신망 접속 신청서"));
        sheet.AddChild(new HSeparator());
        _form = new VBoxContainer();
        _form.AddThemeConstantOverride("separation", 10);
        _form.SizeFlagsVertical = SizeFlags.ExpandFill;
        sheet.AddChild(_form);
        _status = Paper.Note("");
        _status.AddThemeColorOverride("font_color", new Color(0.55f, 0.12f, 0.1f));
        sheet.AddChild(_status);
        var offline = new Button { Text = "통신망 없이 혼자 작업하기" };
        offline.Pressed += () => Offline?.Invoke();
        sheet.AddChild(offline);

        Session.Received += OnMessage;
        _token = Session.SavedToken();
        Connect();
    }

    public override void _ExitTree() => Session.Received -= OnMessage;

    void Clear()
    {
        foreach (var c in _form.GetChildren()) c.QueueFree();
        _status.Text = "";
    }

    async void Connect()
    {
        Clear();
        _form.AddChild(Paper.Note($"통신망({Session.ServerUrl})에 연결하는 중…"));
        if (await Session.ConnectAsync())
        {
            if (_token is not null) ShowCard(); else ShowPhoneForm();
        }
        else
        {
            Clear();
            _form.AddChild(Paper.Note("통신망에 연결할 수 없습니다. 월드 서버가 켜져 있는지 확인하세요."));
            var retry = new Button { Text = "다시 연결" };
            retry.Pressed += Connect;
            _form.AddChild(retry);
        }
    }

    void ShowPhoneForm()
    {
        Clear();
        _form.AddChild(new Label { Text = "전화번호" });
        var phone = new LineEdit { PlaceholderText = "010-1234-5678" };
        _form.AddChild(phone);
        _form.AddChild(new Label { Text = "몸의 성별" });
        var gender = new OptionButton();
        gender.AddItem("여성형");
        gender.AddItem("남성형");
        gender.ItemSelected += i => _gender = (byte)i;
        _form.AddChild(gender);
        var issue = new Button { Text = "QR 신분 계정 발급" };
        issue.Pressed += () => Session.Send(Packets.Register(phone.Text, _gender));
        phone.TextSubmitted += _ => Session.Send(Packets.Register(phone.Text, _gender));
        _form.AddChild(issue);
        _form.AddChild(Paper.Note("QR 계정은 전화번호에 묶입니다. 접속할 때마다 이 번호로 인증 코드가 갑니다."));
    }

    void ShowCard()
    {
        Clear();
        _form.AddChild(QrCard(_token!));
        var send = new Button { Text = "이 계정으로 인증 코드 받기" };
        send.Pressed += () => Session.Send(Packets.RequestCode(_token!));
        _form.AddChild(send);
        var other = new Button { Text = "다른 번호로 새로 등록" };
        other.Pressed += () => { _token = null; Session.SaveToken(null); ShowPhoneForm(); };
        _form.AddChild(other);
    }

    void ShowCodeForm(CodeSent sent)
    {
        Clear();
        _form.AddChild(QrCard(_token!));
        _form.AddChild(Paper.Note($"{sent.MaskedPhone} 로 인증 코드를 보냈습니다."));
        if (!string.IsNullOrEmpty(sent.DevCode))
            _form.AddChild(Paper.Note($"(개발 모드) 수신 문자: [Dolly Forge] 인증코드 {sent.DevCode}"));
        var code = new LineEdit { PlaceholderText = "인증 코드 6자리", MaxLength = 6, Text = sent.DevCode };
        _form.AddChild(code);
        var go = new Button { Text = "접속" };
        go.Pressed += () => Session.Send(Packets.Login(_token!, code.Text.Trim()));
        code.TextSubmitted += _ => Session.Send(Packets.Login(_token!, code.Text.Trim()));
        _form.AddChild(go);
    }

    void OnMessage(ServerMessage m)
    {
        switch (m)
        {
            case Registered r:
                _token = r.Token;
                Session.SaveToken(r.Token);
                ShowCard();
                break;
            case CodeSent c:
                ShowCodeForm(c);
                break;
            case ErrorMessage e:
                _status.Text = e.Text;
                break;
        }
    }

    /// <summary>QR 신분 카드: 계정 토큰을 담은 실제 QR 코드</summary>
    static Control QrCard(string token)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode($"dollyforge://account/{token}", QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(6, [26, 30, 56], [245, 240, 225]);
        var img = new Image();
        img.LoadPngFromBuffer(png);
        var row = new HBoxContainer();
        row.AddChild(new TextureRect
        {
            Texture = ImageTexture.CreateFromImage(img), CustomMinimumSize = new Vector2(180, 180),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
        });
        row.AddChild(Paper.Note($"QR 신분 계정\n{token[..8]}…{token[^4..]}\n이 기기에 저장됨"));
        return row;
    }
}

/// <summary>창고 목록 (종이 장부). 꺼내면 창고에서 사라지고 눈앞에 놓인다.</summary>
public partial class StorageUi : Control
{
    public Action<string>? Retrieve;
    public Action? Closed;
    IReadOnlyList<StoredDevice> _devices = [];
    VBoxContainer _list = null!;

    public void SetDevices(IReadOnlyList<StoredDevice> devices)
    {
        _devices = devices;
        if (_list is not null) Refresh();
    }

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        Theme = Paper.Theme();
        AddChild(new ColorRect { Color = new Color(0, 0, 0, 0.45f), AnchorRight = 1, AnchorBottom = 1 });
        var sheet = Paper.Sheet_(this, 0.3f, 0.7f, 0.12f, 0.88f);
        sheet.AddChild(Paper.Heading("창고 보관 장부"));
        _list = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        sheet.AddChild(_list);
        var close = new Button { Text = "장부 덮기 (Esc)" };
        close.Pressed += () => Closed?.Invoke();
        sheet.AddChild(close);
        Refresh();
    }

    void Refresh()
    {
        foreach (var c in _list.GetChildren()) c.QueueFree();
        if (_devices.Count == 0) _list.AddChild(Paper.Note("(보관 중인 기기가 없습니다. 기기를 보고 F5 로 창고에 넣으세요)"));
        foreach (var d in _devices)
        {
            var b = new Button { Text = $"{d.Name} — 부품 {d.Parts}개  · 꺼내기", Alignment = HorizontalAlignment.Left };
            b.Pressed += () => Retrieve?.Invoke(d.Name);
            _list.AddChild(b);
        }
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, PhysicalKeycode: Key.Escape or Key.F6 })
        {
            Closed?.Invoke();
            GetViewport().SetInputAsHandled();
        }
    }
}

/// <summary>같은 통신망의 다른 사이보그들</summary>
public partial class RemotePlayers : Node3D
{
    sealed class Remote
    {
        public CyborgBody Body = null!;
        public Vector3 Target;
        public float Yaw;
        public float Speed;
    }

    readonly Dictionary<uint, Remote> _players = new();

    public void Upsert(uint id, byte gender, Vector3 at, float yaw)
    {
        if (!_players.TryGetValue(id, out var r))
        {
            r = new Remote { Body = CyborgBody.Build(gender), Target = at, Yaw = yaw };
            r.Body.Position = at;
            var tag = Look.Tag($"사이보그 #{id}", 0.0012f);
            tag.Billboard = BaseMaterial3D.BillboardModeEnum.Enabled;
            tag.Modulate = new Color(0.95f, 0.93f, 0.86f);
            tag.OutlineSize = 8;
            tag.OutlineModulate = new Color(0, 0, 0, 0.8f);
            tag.Position = new Vector3(0, r.Body.Height + 0.25f, 0);
            r.Body.AddChild(tag);
            AddChild(r.Body);
            _players[id] = r;
        }
        r.Target = at;
        r.Yaw = yaw;
    }

    public void Move(uint id, Vector3 at, float yaw)
    {
        if (_players.TryGetValue(id, out var r)) { r.Target = at; r.Yaw = yaw; }
    }

    public void Remove(uint id)
    {
        if (_players.Remove(id, out var r)) r.Body.QueueFree();
    }

    public int Count => _players.Count;
    public CyborgBody? BodyOf(uint id) => _players.TryGetValue(id, out var r) ? r.Body : null;

    public void Clear()
    {
        foreach (var id in _players.Keys.ToList()) Remove(id);
    }

    public override void _Process(double delta)
    {
        var dt = (float)delta;
        var k = 1 - Mathf.Exp(-10 * dt);
        foreach (var r in _players.Values)
        {
            var before = r.Body.Position;
            r.Body.Position = before.Lerp(r.Target, k);
            var v = (r.Body.Position - before) with { Y = 0 };
            r.Speed += (v.Length() / Mathf.Max(dt, 1e-3f) - r.Speed) * Mathf.Min(1, dt * 10);
            // 플레이어 정면은 -Z, 몸의 정면은 +Z
            var want = r.Yaw + Mathf.Pi;
            var d = Mathf.Wrap(want - r.Body.Rotation.Y, -Mathf.Pi, Mathf.Pi);
            r.Body.Rotation = new Vector3(0, r.Body.Rotation.Y + d * k, 0);
            r.Body.Animate(dt, r.Speed);
        }
    }
}
