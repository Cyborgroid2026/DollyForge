using Godot;
using DollyForge.Core;

namespace DollyForge.Game;

/// <summary>
/// 책상 위 종이 카탈로그를 펼쳐 본 모습. 품목을 고르고 재단 치수와 수량을 적어 주문서를 쓴다.
/// 결제하면 잔액에서 빠지고, 물리 부품은 잠시 뒤 택배 하차장에 도착한다.
/// </summary>
public partial class CatalogUi : Control
{
    public ShopAccount Shop { get; set; } = null!;
    public Action<List<PartDef>>? Ordered;
    /// <summary>온라인이면 결제는 서버가 한다: 주문서를 넘기고 ShowResult 로 영수증을 받는다.</summary>
    public Action<List<OrderLine>>? SubmitOnline;
    public Action? Closed;

    readonly List<OrderLine> _cart = new();
    ItemList _items = null!;
    Label _detail = null!, _summary = null!, _cartText = null!;
    SpinBox _qty = null!, _len = null!, _wid = null!;
    Label _lenLabel = null!, _widLabel = null!;
    OptionButton _category = null!;
    List<CatalogItem> _shown = new();

    static readonly Color Paper = new(0.96f, 0.94f, 0.88f), Ink = new(0.1f, 0.12f, 0.22f), Rule = new(0.55f, 0.62f, 0.75f);

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        var theme = new Theme { DefaultFont = Look.PenUi, DefaultFontSize = 22 };
        theme.SetColor("font_color", "Label", Ink);
        theme.SetColor("font_color", "Button", Ink);
        theme.SetColor("font_color", "ItemList", Ink);
        theme.SetColor("font_selected_color", "ItemList", new Color(0.6f, 0.1f, 0.1f));
        theme.SetColor("font_color", "OptionButton", Ink);
        theme.SetColor("font_color", "LineEdit", Ink);
        var flat = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0), BorderColor = Rule, BorderWidthBottom = 1 };
        foreach (var t in new[] { "ItemList", "LineEdit", "OptionButton" })
            theme.SetStylebox(t == "ItemList" ? "panel" : "normal", t, flat);
        var button = new StyleBoxFlat { BgColor = new Color(0.9f, 0.86f, 0.74f), BorderColor = Ink, BorderWidthBottom = 2, BorderWidthTop = 1, BorderWidthLeft = 1, BorderWidthRight = 1, ContentMarginLeft = 12, ContentMarginRight = 12, ContentMarginTop = 4, ContentMarginBottom = 4 };
        theme.SetStylebox("normal", "Button", button);
        var hover = (StyleBoxFlat)button.Duplicate();
        hover.BgColor = new Color(0.85f, 0.8f, 0.66f);
        theme.SetStylebox("hover", "Button", hover);
        Theme = theme;

        // 어두운 배경(책상 위로 시선을 내린 상태) 위의 펼친 종이 두 장
        AddChild(new ColorRect { Color = new Color(0, 0, 0, 0.55f), AnchorRight = 1, AnchorBottom = 1 });
        var book = new HBoxContainer { AnchorLeft = 0.06f, AnchorRight = 0.94f, AnchorTop = 0.06f, AnchorBottom = 0.94f };
        book.AddThemeConstantOverride("separation", 6);
        AddChild(book);

        var left = Page(book);
        left.AddChild(Heading("부품 카탈로그"));
        _category = new OptionButton();
        _category.AddItem("전체");
        foreach (var c in Catalog.Items.Select(i => i.Category).Distinct()) _category.AddItem(c);
        _category.ItemSelected += _ => Refresh();
        left.AddChild(_category);
        _items = new ItemList { SizeFlagsVertical = SizeFlags.ExpandFill };
        _items.ItemSelected += _ => ShowDetail();
        left.AddChild(_items);

        var right = Page(book);
        right.AddChild(Heading("주문서"));
        _detail = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(0, 150) };
        right.AddChild(_detail);
        var form = new GridContainer { Columns = 2 };
        _lenLabel = new Label { Text = "길이 (mm)" };
        _len = new SpinBox { MinValue = 0, MaxValue = 3600, Step = 1, Value = 500 };
        _widLabel = new Label { Text = "폭 (mm)" };
        _wid = new SpinBox { MinValue = 0, MaxValue = 1200, Step = 1, Value = 300 };
        _qty = new SpinBox { MinValue = 1, MaxValue = 50, Value = 1 };
        foreach (var c in new Control[] { _lenLabel, _len, _widLabel, _wid, new Label { Text = "수량" }, _qty }) form.AddChild(c);
        _len.ValueChanged += _ => ShowDetail();
        _wid.ValueChanged += _ => ShowDetail();
        _qty.ValueChanged += _ => ShowDetail();
        right.AddChild(form);
        var add = new Button { Text = "주문서에 적기" };
        add.Pressed += AddToCart;
        right.AddChild(add);
        right.AddChild(new HSeparator());
        _cartText = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsVertical = SizeFlags.ExpandFill };
        right.AddChild(_cartText);
        _summary = new Label();
        right.AddChild(_summary);
        var row = new HBoxContainer();
        var pay = new Button { Text = "결제하고 주문" };
        pay.Pressed += Checkout;
        var clear = new Button { Text = "주문서 지우기" };
        clear.Pressed += () => { _cart.Clear(); RefreshCart(); };
        var close = new Button { Text = "카탈로그 덮기 (Esc)" };
        close.Pressed += () => Closed?.Invoke();
        row.AddChild(pay);
        row.AddChild(clear);
        row.AddChild(close);
        right.AddChild(row);

        Refresh();
        RefreshCart();
    }

    VBoxContainer Page(Container parent)
    {
        var panel = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = Paper, ShadowColor = new Color(0, 0, 0, 0.4f), ShadowSize = 8,
            ContentMarginLeft = 28, ContentMarginRight = 28, ContentMarginTop = 22, ContentMarginBottom = 22,
        });
        parent.AddChild(panel);
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 10);
        panel.AddChild(v);
        return v;
    }

    static Label Heading(string text)
    {
        var l = new Label { Text = text };
        l.AddThemeFontSizeOverride("font_size", 34);
        return l;
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, PhysicalKeycode: Key.Escape })
        {
            Closed?.Invoke();
            GetViewport().SetInputAsHandled();
        }
    }

    void Refresh()
    {
        var cat = _category.Selected <= 0 ? null : _category.GetItemText(_category.Selected);
        _shown = Catalog.Items.Where(i => cat is null || i.Category == cat).ToList();
        _items.Clear();
        foreach (var i in _shown)
            _items.AddItem($"{i.NameKo}   ₩{i.PriceKrw:N0}/{i.UnitKo}");
        if (_shown.Count > 0) { _items.Select(0); ShowDetail(); }
    }

    CatalogItem? Selected => _items.GetSelectedItems() is { Length: > 0 } s ? _shown[s[0]] : null;

    void ShowDetail()
    {
        if (Selected is not { } item) return;
        var cutLen = item.CutToLength is not null || item.CutToSize is not null;
        _lenLabel.Text = item.CutToSize is not null ? "가로 (mm)" : "길이 (mm)";
        _lenLabel.Visible = _len.Visible = cutLen;
        _widLabel.Visible = _wid.Visible = item.CutToSize is not null;
        string price;
        try
        {
            var unit = item.UnitPrice((int)_len.Value, (int)_wid.Value);
            var part = item.Build((int)_len.Value, (int)_wid.Value);
            price = $"단가 ₩{unit:N0} × {_qty.Value} = ₩{unit * (int)_qty.Value:N0}" + (part is not null ? $"\n개당 {part.Mass:0.###} kg" : "");
        }
        catch (ArgumentOutOfRangeException ex) { price = ex.Message.Split(" (")[0]; }
        var cut = item.CutToLength is { } c ? $"\n재단: {c.Min:0}–{c.Max:0} mm, 재단비 ₩{c.CutFeeKrw:N0}" :
                  item.CutToSize is { } s ? $"\n재단: {s.MinW:0}–{s.MaxW:0} × {s.MinD:0}–{s.MaxD:0} mm, 재단비 ₩{s.CutFeeKrw:N0}" : "";
        var pack = item.IsConsumable ? $"\n{item.PackQty}개 단위" : "";
        _detail.Text = $"{item.NameKo}  ({item.Sku})\n{item.SpecKo}{cut}{pack}\n{price}";
    }

    void AddToCart()
    {
        if (Selected is not { } item) return;
        var line = new OrderLine(item, (int)_qty.Value, (int)_len.Value, (int)_wid.Value);
        try { _ = line.Price; item.Build(line.LengthMm, line.WidthMm); }
        catch (ArgumentOutOfRangeException) { return; }
        _cart.Add(line);
        RefreshCart();
    }

    void RefreshCart()
    {
        _cartText.Text = _cart.Count == 0 ? "(주문서가 비어 있습니다)" :
            string.Join("\n", _cart.Select(l =>
                $"· {l.Item.NameKo}" + (l.Item.CutToLength is not null ? $" {l.LengthMm:0} mm" : l.Item.CutToSize is not null ? $" {l.LengthMm:0}×{l.WidthMm:0} mm" : "") +
                $" × {l.Qty} = ₩{l.Price:N0}"));
        var (sub, ship, total) = ShopAccount.Quote(_cart);
        _summary.Text = $"상품 ₩{sub:N0} + 배송 ₩{ship:N0} = 합계 ₩{total:N0}   (잔액 ₩{Shop.BalanceKrw:N0}, ₩{ShopAccount.FreeShippingOverKrw:N0} 이상 무료 배송)";
    }

    void Checkout()
    {
        if (SubmitOnline is not null)
        {
            if (_cart.Count == 0) return;
            _summary.Text = "통신망으로 결제 요청 중…";
            SubmitOnline([.. _cart]);
            return;
        }
        var (ok, msg, delivered) = Shop.Purchase(_cart);
        _summary.Text = msg;
        if (!ok) return;
        _cart.Clear();
        _cartText.Text = "(주문 완료)";
        Ordered?.Invoke(delivered);
    }

    /// <summary>서버 영수증 반영</summary>
    public void ShowResult(bool ok, string message)
    {
        _summary.Text = message;
        if (!ok) return;
        _cart.Clear();
        _cartText.Text = "(주문 완료)";
    }
}
