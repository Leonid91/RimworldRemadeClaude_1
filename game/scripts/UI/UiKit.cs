using System;
using Godot;

namespace Remade.Game.UI;

/// <summary>
/// The game's visual language: deep ink panels, ivory text, a teal survey accent, condensed technical type.
/// All controls are built in code through these helpers so the look stays consistent.
/// </summary>
public static class UiKit
{
    public static readonly Color Ink = new(0.035f, 0.055f, 0.075f, 0.88f);
    public static readonly Color InkSolid = new(0.05f, 0.07f, 0.09f, 0.97f);
    public static readonly Color InkLight = new(0.09f, 0.12f, 0.15f, 0.92f);
    public static readonly Color Line = new(0.45f, 0.78f, 0.74f, 0.28f);
    public static readonly Color Accent = new(0.37f, 0.82f, 0.75f);
    public static readonly Color AccentDim = new(0.18f, 0.43f, 0.40f);
    public static readonly Color Text = new(0.91f, 0.89f, 0.83f);
    public static readonly Color Muted = new(0.58f, 0.64f, 0.68f);
    public static readonly Color Good = new(0.55f, 0.80f, 0.36f);
    public static readonly Color Warn = new(0.91f, 0.77f, 0.42f);
    public static readonly Color Bad = new(0.88f, 0.48f, 0.37f);
    public static readonly Color Passion = new(0.98f, 0.62f, 0.25f);

    static Theme _theme;
    static Font _font, _fontBold;

    public static Font Font => _font ??= MakeFont(400);
    public static Font FontBold => _fontBold ??= MakeFont(700);

    static Font MakeFont(int weight)
    {
        var f = new SystemFont
        {
            FontNames = new[] { "Bahnschrift", "Segoe UI", "Arial" },
            FontWeight = weight,
            Antialiasing = TextServer.FontAntialiasing.Lcd,
            Hinting = TextServer.Hinting.Light,
            SubpixelPositioning = TextServer.SubpixelPositioning.Auto,
        };
        return f;
    }

    public static Theme Theme => _theme ??= BuildTheme();

    static StyleBoxFlat Box(Color bg, Color border, int borderW = 1, int radius = 3, int pad = 8)
    {
        var s = new StyleBoxFlat
        {
            BgColor = bg, BorderColor = border,
            BorderWidthLeft = borderW, BorderWidthRight = borderW, BorderWidthTop = borderW, BorderWidthBottom = borderW,
            CornerRadiusTopLeft = radius, CornerRadiusTopRight = radius, CornerRadiusBottomLeft = radius, CornerRadiusBottomRight = radius,
            ContentMarginLeft = pad, ContentMarginRight = pad, ContentMarginTop = pad * 0.6f, ContentMarginBottom = pad * 0.6f,
            AntiAliasing = true,
        };
        return s;
    }

    static Theme BuildTheme()
    {
        var t = new Theme { DefaultFont = Font, DefaultFontSize = 17 };
        t.SetColor("font_color", "Label", Text);
        t.SetColor("font_color", "Button", Text);
        t.SetColor("font_hover_color", "Button", new Color(1, 1, 1));
        t.SetColor("font_pressed_color", "Button", Accent);
        t.SetColor("font_focus_color", "Button", Text);
        t.SetColor("font_disabled_color", "Button", new Color(Muted, 0.5f));
        t.SetStylebox("normal", "Button", Box(new Color(0.08f, 0.11f, 0.14f, 0.85f), Line));
        t.SetStylebox("hover", "Button", Box(new Color(0.12f, 0.20f, 0.22f, 0.95f), Accent));
        t.SetStylebox("pressed", "Button", Box(new Color(0.06f, 0.15f, 0.15f, 0.95f), Accent, 2));
        t.SetStylebox("focus", "Button", new StyleBoxEmpty());
        t.SetStylebox("disabled", "Button", Box(new Color(0.06f, 0.07f, 0.08f, 0.6f), new Color(Line, 0.12f)));
        t.SetStylebox("panel", "PanelContainer", Box(Ink, Line, 1, 4, 12));
        t.SetStylebox("panel", "Panel", Box(Ink, Line, 1, 4, 12));
        t.SetStylebox("panel", "PopupMenu", Box(InkSolid, Accent, 1, 3, 6));
        t.SetColor("font_color", "PopupMenu", Text);
        t.SetColor("font_hover_color", "PopupMenu", Color.Color8(255, 255, 255));
        t.SetStylebox("hover", "PopupMenu", Box(new Color(AccentDim, 0.7f), new Color(0, 0, 0, 0), 0, 2, 4));
        t.SetStylebox("normal", "LineEdit", Box(new Color(0.02f, 0.03f, 0.04f, 0.9f), Line));
        t.SetStylebox("focus", "LineEdit", Box(new Color(0.02f, 0.03f, 0.04f, 0.9f), Accent));
        t.SetColor("font_color", "LineEdit", Text);
        t.SetStylebox("background", "ProgressBar", Box(new Color(0, 0, 0, 0.45f), new Color(0, 0, 0, 0), 0, 2, 0));
        t.SetStylebox("fill", "ProgressBar", Box(Accent, new Color(0, 0, 0, 0), 0, 2, 0));
        t.SetStylebox("panel", "TooltipPanel", Box(InkSolid, Accent, 1, 3, 8));
        t.SetColor("font_color", "TooltipLabel", Text);
        t.SetStylebox("slider", "HSlider", Box(new Color(0, 0, 0, 0.5f), Line, 1, 2, 2));
        t.SetStylebox("grabber_area", "HSlider", Box(AccentDim, new Color(0, 0, 0, 0), 0, 2, 2));
        t.SetStylebox("grabber_area_highlight", "HSlider", Box(Accent, new Color(0, 0, 0, 0), 0, 2, 2));
        t.SetStylebox("panel", "TabContainer", Box(Ink, Line, 1, 3, 10));
        t.SetStylebox("tab_selected", "TabContainer", Box(new Color(0.10f, 0.18f, 0.19f, 0.95f), Accent, 1, 2, 10));
        t.SetStylebox("tab_unselected", "TabContainer", Box(new Color(0.05f, 0.07f, 0.09f, 0.9f), Line, 1, 2, 10));
        t.SetStylebox("tab_hovered", "TabContainer", Box(new Color(0.09f, 0.14f, 0.16f, 0.95f), Accent, 1, 2, 10));
        t.SetColor("font_selected_color", "TabContainer", Accent);
        t.SetColor("font_unselected_color", "TabContainer", Muted);
        t.SetColor("font_hovered_color", "TabContainer", Text);
        t.SetStylebox("normal", "OptionButton", Box(new Color(0.08f, 0.11f, 0.14f, 0.85f), Line));
        t.SetStylebox("hover", "OptionButton", Box(new Color(0.12f, 0.20f, 0.22f, 0.95f), Accent));
        t.SetStylebox("pressed", "OptionButton", Box(new Color(0.06f, 0.15f, 0.15f, 0.95f), Accent));
        t.SetStylebox("separator", "HSeparator", new StyleBoxLine { Color = Line, Thickness = 1 });
        t.SetConstant("separation", "HSeparator", 10);
        t.SetStylebox("panel", "ScrollContainer", new StyleBoxEmpty());
        return t;
    }

    public static StyleBoxFlat PanelStyle(Color? bg = null, Color? border = null, int radius = 4, int pad = 12)
        => Box(bg ?? Ink, border ?? Line, 1, radius, pad);

    // ------------------------------------------------------------------ builders

    public static Label Label(string text, int size = 17, Color? color = null, bool bold = false, HorizontalAlignment align = HorizontalAlignment.Left)
    {
        var l = new Label { Text = text, HorizontalAlignment = align };
        l.AddThemeFontSizeOverride("font_size", size);
        if (color.HasValue) l.AddThemeColorOverride("font_color", color.Value);
        if (bold) l.AddThemeFontOverride("font", FontBold);
        return l;
    }

    public static Button Button(string text, Action onClick, int size = 17, int minWidth = 0)
    {
        var b = new Button { Text = text, FocusMode = Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(minWidth, 0) };
        b.AddThemeFontSizeOverride("font_size", size);
        if (onClick != null) b.Pressed += () => { Remade.Diagnostics.Log.Action($"button '{text}'"); onClick(); };
        b.MouseDefaultCursorShape = Control.CursorShape.PointingHand;
        return b;
    }

    public static VBoxContainer VBox(int sep = 8)
    {
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", sep);
        return v;
    }

    public static HBoxContainer HBox(int sep = 8)
    {
        var h = new HBoxContainer();
        h.AddThemeConstantOverride("separation", sep);
        return h;
    }

    public static PanelContainer Panel(Control content = null, Color? bg = null, int pad = 14)
    {
        var p = new PanelContainer();
        p.AddThemeStyleboxOverride("panel", PanelStyle(bg, null, 4, pad));
        if (content != null) p.AddChild(content);
        return p;
    }

    /// <summary>Section heading: small caps-like label with an accent rule.</summary>
    public static Control Heading(string text, int size = 15)
    {
        var v = VBox(3);
        var l = Label(text.ToUpperInvariant(), size, Accent, bold: true);
        l.AddThemeConstantOverride("outline_size", 0);
        v.AddChild(l);
        var line = new ColorRect { Color = new Color(Accent, 0.35f), CustomMinimumSize = new Vector2(0, 1) };
        v.AddChild(line);
        return v;
    }

    public static Control Spacer(float w = 0, float h = 0) => new Control { CustomMinimumSize = new Vector2(w, h) };

    public static Control Expander()
    {
        var c = new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        return c;
    }

    public static ProgressBar Bar(float value, Color fill, float height = 12)
    {
        var b = new ProgressBar { MinValue = 0, MaxValue = 1, Value = value, ShowPercentage = false, CustomMinimumSize = new Vector2(0, height) };
        b.AddThemeStyleboxOverride("fill", Box(fill, new Color(0, 0, 0, 0), 0, 2, 0));
        b.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        return b;
    }

    /// <summary>Makes a control fill its parent (anchors full rect).</summary>
    public static T Fill<T>(T c) where T : Control
    {
        c.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        c.OffsetLeft = c.OffsetTop = c.OffsetRight = c.OffsetBottom = 0;
        return c;
    }

    public static string Temp(float c) => $"{c:F0}°C";

    /// <summary>Colour from a 0xRRGGBB value (sRGB).</summary>
    public static Color Rgb(uint rgb) => new((rgb << 8) | 0xFFu);
}
