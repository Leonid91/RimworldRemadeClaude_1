using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Remade.Core;
using Remade.Diagnostics;
using Remade.Pawns;
using Remade.Sim;
using Remade.Things;

namespace Remade.Game.UI;

/// <summary>
/// The selected colonist, RimWorld style: a compact info box at the bottom left (name, mode, activity, health and
/// needs at a glance, Draft and Direct control buttons) with a tab strip above it. Each tab opens a large panel:
/// Bio (name, sex, ages, traits, 12 skills), Equipment (layered body figure + grid inventory), Needs (food, thirst,
/// sleep) and Health (body figure with per-part condition, operations).
/// </summary>
public partial class InspectPanel : Control
{
    readonly GameSim _sim;
    Pawn _pawn;
    string _tab;
    readonly PanelContainer _info;
    readonly VBoxContainer _infoBox;
    readonly HBoxContainer _tabs;
    PanelContainer _big;
    Label _job, _mode, _healthL;
    ProgressBar _food, _thirst, _rest;
    Button _draftBtn, _controlBtn;
    ApparelLayer _layer = ApparelLayer.Skin;
    BodyRegion? _healthRegion;
    double _refresh;
    public Action<Pawn> OnModeChanged;

    public InspectPanel(GameSim sim)
    {
        _sim = sim;
        SetAnchorsPreset(LayoutPreset.BottomLeft);
        MouseFilter = MouseFilterEnum.Ignore;
        Position = new Vector2(12, -12);

        _tabs = UiKit.HBox(4);
        _tabs.Position = new Vector2(0, -310);
        AddChild(_tabs);
        foreach (var t in new[] { "Bio", "Equipment", "Needs", "Health" })
        {
            string tab = t;
            var b = UiKit.Button(t, () => ToggleTab(tab), 16, 108);
            b.Name = "Tab" + t;
            _tabs.AddChild(b);
        }

        _info = UiKit.Panel(null);
        _info.Position = new Vector2(0, -268);
        _info.CustomMinimumSize = new Vector2(452, 256);
        _infoBox = UiKit.VBox(5);
        _info.AddChild(_infoBox);
        AddChild(_info);
        Visible = false;
    }

    public Pawn Pawn => _pawn;
    public string OpenTab => _tab;

    public void Show(Pawn p)
    {
        if (p == _pawn && Visible) return;
        _pawn = p;
        Visible = p != null;
        if (p == null) { CloseTab(); return; }
        BuildInfo();
        if (_tab != null) BuildTab();
    }

    void ToggleTab(string tab)
    {
        if (_tab == tab) { CloseTab(); return; }
        _tab = tab;
        Log.Action($"open tab {tab} for {_pawn?.Name}");
        BuildTab();
    }

    public void OpenTabByName(string tab) { _tab = tab; BuildTab(); }

    void CloseTab()
    {
        _tab = null;
        _big?.QueueFree();
        _big = null;
        foreach (var c in _tabs.GetChildren()) ((Button)c).RemoveThemeColorOverride("font_color");
    }

    // ------------------------------------------------------------------ info box

    void BuildInfo()
    {
        foreach (var c in _infoBox.GetChildren()) c.QueueFree();
        var p = _pawn;
        var head = UiKit.HBox(8);
        head.AddChild(UiKit.Label(p.FullName, 21, UiKit.Text, bold: true));
        _infoBox.AddChild(head);
        string age = p.BioAge == p.ChronoAge ? $"age {p.BioAge}" : $"age {p.BioAge} ({p.ChronoAge})";
        _infoBox.AddChild(UiKit.Label($"{p.Sex}, {age}", 15, UiKit.Muted));
        _mode = UiKit.Label("", 15, UiKit.Accent);
        _infoBox.AddChild(_mode);
        _job = UiKit.Label("", 15);
        _infoBox.AddChild(_job);
        _healthL = UiKit.Label("", 15);
        _infoBox.AddChild(_healthL);
        _food = NeedRow("Food", UiKit.Warn);
        _thirst = NeedRow("Thirst", new Color(0.35f, 0.65f, 0.95f));
        _rest = NeedRow("Sleep", new Color(0.55f, 0.55f, 0.95f));
        _infoBox.AddChild(UiKit.Expander());
        var buttons = UiKit.HBox(8);
        _draftBtn = UiKit.Button("", ToggleDraft, 16, 150);
        _draftBtn.Name = "DraftButton";
        _controlBtn = UiKit.Button("", ToggleDirect, 16, 0);
        _controlBtn.Name = "DirectControlButton";
        _controlBtn.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        buttons.AddChild(_draftBtn);
        buttons.AddChild(_controlBtn);
        _infoBox.AddChild(buttons);
        RefreshInfo();
    }

    ProgressBar NeedRow(string label, Color c)
    {
        var r = UiKit.HBox(8);
        var l = UiKit.Label(label, 14, UiKit.Muted);
        l.CustomMinimumSize = new Vector2(56, 0);
        r.AddChild(l);
        var bar = UiKit.Bar(1, c, 10);
        r.AddChild(bar);
        _infoBox.AddChild(r);
        return bar;
    }

    void RefreshInfo()
    {
        var p = _pawn;
        if (p == null || _mode == null) return;
        _mode.Text = p.Dead ? "Dead" : p.Mode switch { ControlMode.Direct => "Under your direct control", ControlMode.Drafted => "Drafted", _ => "Autonomous" };
        _job.Text = p.Dead ? p.Health.DeathCause : p.LastJobLabel;
        _healthL.Text = $"Health {p.Health.Summary * 100:F0} %{(p.Health.BleedRate > 0 ? "  ·  bleeding" : "")}";
        _food.Value = p.Needs.Food; _thirst.Value = p.Needs.Thirst; _rest.Value = p.Needs.Rest;
        _draftBtn.Text = p.Mode == ControlMode.Drafted ? $"Undraft [{Settings.KeyName("draft")}]" : $"Draft [{Settings.KeyName("draft")}]";
        _controlBtn.Text = p.Mode == ControlMode.Direct ? $"Release control [{Settings.KeyName("direct_control")}]" : $"Direct control [{Settings.KeyName("direct_control")}]";
        _draftBtn.Disabled = _controlBtn.Disabled = p.Dead;
    }

    void ToggleDraft()
    {
        if (_pawn == null || _pawn.Dead) return;
        _sim.SetMode(_pawn, _pawn.Mode == ControlMode.Drafted ? ControlMode.Autonomous : ControlMode.Drafted);
        OnModeChanged?.Invoke(_pawn);
        RefreshInfo();
    }

    public void ToggleDirect()
    {
        if (_pawn == null || _pawn.Dead) return;
        _sim.SetMode(_pawn, _pawn.Mode == ControlMode.Direct ? ControlMode.Autonomous : ControlMode.Direct);
        OnModeChanged?.Invoke(_pawn);
        RefreshInfo();
    }

    public void ToggleDraftExternal() => ToggleDraft();

    public override void _Process(double delta)
    {
        if (!Visible || _pawn == null) return;
        _refresh -= delta;
        if (_refresh > 0) return;
        _refresh = 0.25;
        RefreshInfo();
        if (_tab is "Needs" or "Health") BuildTab(); // live values
        for (int i = 0; i < _tabs.GetChildCount(); i++)
        {
            var b = (Button)_tabs.GetChild(i);
            if (b.Text == _tab) b.AddThemeColorOverride("font_color", UiKit.Accent);
            else b.RemoveThemeColorOverride("font_color");
        }
    }

    // ------------------------------------------------------------------ big tab panel

    void BuildTab()
    {
        if (_pawn == null || _tab == null) return;
        var old = _big;
        _big = UiKit.Panel(null, UiKit.InkSolid, 16);
        _big.Name = "TabPanel";
        var content = _tab switch
        {
            "Bio" => BioTab(),
            "Equipment" => EquipmentTab(),
            "Needs" => NeedsTab(),
            "Health" => HealthTab(),
            _ => throw new ArgumentOutOfRangeException(nameof(_tab), _tab, "unknown tab"),
        };
        _big.AddChild(content);
        AddChild(_big);
        // sits above the tab strip, growing upward
        _big.ResetSize();
        var min = _big.GetCombinedMinimumSize();
        _big.Position = new Vector2(0, -318 - min.Y);
        old?.QueueFree();
    }

    Control BioTab()
    {
        var p = _pawn;
        var row = UiKit.HBox(28);
        var c1 = UiKit.VBox(8);
        c1.CustomMinimumSize = new Vector2(300, 0);
        c1.AddChild(UiKit.Label(p.FullName, 22, UiKit.Text, bold: true));
        string age = p.BioAge == p.ChronoAge ? $"{p.BioAge}" : $"{p.BioAge} ({p.ChronoAge})";
        c1.AddChild(UiKit.Label($"{p.Sex}, age {age}", 17, UiKit.Muted));
        c1.AddChild(UiKit.Label($"{p.HeightM:F2} m · {p.BodyMassKg:F0} kg", 15, UiKit.Muted));
        c1.AddChild(UiKit.Spacer(0, 6));
        c1.AddChild(UiKit.Heading("Traits"));
        var flow = new HFlowContainer();
        flow.AddThemeConstantOverride("h_separation", 6);
        flow.AddThemeConstantOverride("v_separation", 6);
        foreach (var t in p.Traits)
        {
            var chip = UiKit.Panel(UiKit.Label(t.Label, 15), new Color(0.12f, 0.17f, 0.2f, 0.95f), 6);
            chip.TooltipText = t.EffectsText();
            chip.MouseFilter = MouseFilterEnum.Stop;
            flow.AddChild(chip);
        }
        c1.AddChild(flow);
        row.AddChild(c1);
        var c2 = UiKit.VBox(4);
        c2.CustomMinimumSize = new Vector2(380, 0);
        c2.AddChild(UiKit.Heading("Skills"));
        for (int s = 0; s < (int)SkillId.Count; s++)
        {
            var r = UiKit.HBox(6);
            var name = UiKit.Label(ColonistScreen.SkillLabel((SkillId)s), 15);
            name.CustomMinimumSize = new Vector2(115, 0);
            r.AddChild(name);
            r.AddChild(new PassionIcon(p.Passions[s]));
            var bar = UiKit.Bar(p.Skills[s] / 20f, p.Skills[s] >= 10 ? UiKit.Accent : UiKit.AccentDim, 14);
            bar.CustomMinimumSize = new Vector2(180, 14);
            r.AddChild(bar);
            r.AddChild(UiKit.Label(p.Skills[s].ToString(), 15, p.Skills[s] == 0 ? UiKit.Muted : UiKit.Text));
            c2.AddChild(r);
        }
        row.AddChild(c2);
        return row;
    }

    Control EquipmentTab()
    {
        var p = _pawn;
        var row = UiKit.HBox(22);
        var left = UiKit.VBox(8);
        left.AddChild(UiKit.Heading("Worn — by layer"));
        var layers = UiKit.HBox(4);
        foreach (ApparelLayer l in Enum.GetValues<ApparelLayer>())
        {
            var layer = l;
            var b = UiKit.Button(l.ToString(), () => { _layer = layer; BuildTab(); }, 14);
            b.Name = "Layer" + l;
            if (l == _layer) b.AddThemeColorOverride("font_color", UiKit.Accent);
            layers.AddChild(b);
        }
        left.AddChild(layers);
        var fig = new BodyFigure
        {
            RegionColor = r =>
            {
                var a = p.WornAt(_layer, r);
                if (a != null) return Remade.Game.UI.UiKit.Rgb(a.Def.Color);
                return new Color(0.16f, 0.19f, 0.21f);
            },
            RegionTooltip = r =>
            {
                var a = p.WornAt(_layer, r);
                return $"{BodyFigure.RegionName(r)} — {_layer} layer: " + (a != null ? a.Def.Label : "free (can be equipped)");
            },
        };
        left.AddChild(fig);
        var worn = p.Apparel.Where(a => Array.IndexOf(a.Def.Layers, _layer) >= 0).ToList();
        left.AddChild(UiKit.Label(worn.Count == 0 ? $"Nothing worn on the {_layer.ToString().ToLowerInvariant()} layer." : string.Join(", ", worn.Select(a => a.Def.Label)), 14, UiKit.Muted));
        row.AddChild(left);

        var right = UiKit.VBox(8);
        right.CustomMinimumSize = new Vector2(330, 0);
        var handsRow = UiKit.HBox(8);
        if (p.Held != null)
        {
            var held = p.Held;
            handsRow.AddChild(UiKit.Button("Put away", () => { Log.Action($"put away {held}"); _sim.Unequip(p); BuildTab(); }, 14));
            handsRow.AddChild(UiKit.Button("Drop", () => { Log.Action($"drop {held}"); _sim.DropFromInventory(p, held); BuildTab(); }, 14));
        }
        // the whole tab is rebuilt after every inventory change so the hands, the figure and the load stay in sync
        right.AddChild(new InventoryView(_sim, p, () => CallDeferred(MethodName.BuildTab)));
        right.AddChild(handsRow);
        row.AddChild(right);
        return row;
    }

    Control NeedsTab()
    {
        var p = _pawn;
        var col = UiKit.VBox(10);
        col.CustomMinimumSize = new Vector2(520, 0);
        col.AddChild(UiKit.Heading("Needs"));
        NeedBlock(col, "Food", p.Needs.Food, Needs.FoodLabel(p.Needs.Food), UiKit.Warn, Needs.HungryThreshold,
            $"Empties in about {1f / Needs.FoodPerHour:F0} h while awake (slower asleep). Eats when below {Needs.HungryThreshold * 100:F0} %.");
        float heat = 1f + MathF.Max(0f, _sim.Weather.Temperature - 25f) * 0.06f;
        NeedBlock(col, "Thirst", p.Needs.Thirst, Needs.ThirstLabel(p.Needs.Thirst), new Color(0.35f, 0.65f, 0.95f), Needs.ThirstyThreshold,
            $"Empties in about {1f / (Needs.ThirstPerHour * heat):F0} h at {_sim.Weather.Temperature:F0} °C (heat and exertion make it faster).");
        NeedBlock(col, "Sleep", p.Needs.Rest, Needs.RestLabel(p.Needs.Rest), new Color(0.55f, 0.55f, 0.95f), Needs.TiredThreshold,
            $"About 16 h awake before exhaustion; a night of ~7.5 h restores it fully.");
        col.AddChild(UiKit.Label("Needs have no effects yet — they will in a later version.", 13, new Color(UiKit.Muted, 0.8f)));
        return col;
    }

    static void NeedBlock(VBoxContainer col, string name, float v, string state, Color c, float threshold, string note)
    {
        var top = UiKit.HBox(8);
        var l = UiKit.Label(name, 17, UiKit.Text, bold: true);
        l.CustomMinimumSize = new Vector2(80, 0);
        top.AddChild(l);
        top.AddChild(UiKit.Label($"{v * 100:F0} %  ·  {state}", 16, v < threshold ? UiKit.Bad : UiKit.Muted));
        col.AddChild(top);
        var bar = new NeedBar(v, c, threshold) { CustomMinimumSize = new Vector2(500, 16) };
        col.AddChild(bar);
        col.AddChild(UiKit.Label(note, 13, UiKit.Muted));
    }

    sealed partial class NeedBar : Control
    {
        readonly float _v, _t; readonly Color _c;
        public NeedBar(float v, Color c, float t) { _v = v; _c = c; _t = t; }
        public override void _Draw()
        {
            DrawRect(new Rect2(Vector2.Zero, Size), new Color(0, 0, 0, 0.45f));
            DrawRect(new Rect2(0, 0, Size.X * _v, Size.Y), _c);
            DrawLine(new Vector2(Size.X * _t, -2), new Vector2(Size.X * _t, Size.Y + 2), UiKit.Text, 2);
        }
    }

    Control HealthTab()
    {
        var p = _pawn;
        var row = UiKit.HBox(22);
        var left = UiKit.VBox(8);
        left.AddChild(UiKit.Heading("Body"));
        var fig = new BodyFigure
        {
            RegionColor = r => ConditionColor(RegionCondition(p, r)),
            RegionTooltip = r => $"{BodyFigure.RegionName(r)}: {RegionCondition(p, r) * 100:F0} %",
            SelectedRegion = _healthRegion,
        };
        fig.RegionClicked += r => { _healthRegion = r; BuildTab(); };
        left.AddChild(fig);
        left.AddChild(UiKit.Label("Click a body part to see its organs and bones.", 13, UiKit.Muted));
        row.AddChild(left);

        var right = UiKit.VBox(6);
        right.CustomMinimumSize = new Vector2(360, 0);
        var sub = UiKit.HBox(4);
        var overview = UiKit.Button("Overview", () => { _healthOps = false; BuildTab(); }, 14);
        var ops = UiKit.Button("Operations", () => { _healthOps = true; BuildTab(); }, 14);
        (_healthOps ? ops : overview).AddThemeColorOverride("font_color", UiKit.Accent);
        sub.AddChild(overview); sub.AddChild(ops);
        right.AddChild(sub);
        if (_healthOps)
        {
            right.AddChild(UiKit.Heading("Operations"));
            right.AddChild(UiKit.Label("No bills.", 15, UiKit.Muted));
            var add = new MenuButton { Text = "Add bill", Flat = false, Name = "AddBill" };
            add.AddThemeFontSizeOverride("font_size", 15);
            add.GetPopup().AddItem("(no operations available yet)");
            add.GetPopup().SetItemDisabled(0, true);
            right.AddChild(add);
        }
        else
        {
            right.AddChild(UiKit.Label($"Overall health {p.Health.Summary * 100:F0} %" + (p.Health.BloodLoss > 0.001f ? $"  ·  blood loss {p.Health.BloodLoss * 100:F0} %" : ""), 16));
            var region = _healthRegion ?? BodyRegion.Head;
            right.AddChild(UiKit.Heading(BodyFigure.RegionName(region)));
            foreach (var part in p.Health.Body.InRegion(region))
            {
                var r = UiKit.HBox(8);
                var name = UiKit.Label(part.Name + (part.Vital ? "  ♥" : ""), 15, part.Vital ? UiKit.Text : UiKit.Muted);
                name.CustomMinimumSize = new Vector2(190, 0);
                name.TooltipText = part.Vital ? "Vital: destroying it is fatal." : part.Group != null ? "Paired organ: losing both is fatal." : "";
                name.MouseFilter = MouseFilterEnum.Stop;
                r.AddChild(name);
                float cond = p.Health.Condition(part.Index);
                var bar = UiKit.Bar(cond, ConditionColor(cond), 10);
                bar.CustomMinimumSize = new Vector2(100, 10);
                r.AddChild(bar);
                r.AddChild(UiKit.Label($"{cond * 100:F0} %", 15, ConditionColor(cond)));
                right.AddChild(r);
            }
        }
        row.AddChild(right);
        return row;
    }

    bool _healthOps;

    static float RegionCondition(Pawn p, BodyRegion r)
    {
        float cur = 0, max = 0;
        foreach (var part in p.Health.Body.InRegion(r)) { cur += p.Health.Hp[part.Index]; max += part.MaxHp; }
        return max > 0 ? cur / max : 1f;
    }

    static Color ConditionColor(float c) => c > 0.95f ? new Color(0.30f, 0.62f, 0.42f) : c > 0.6f ? UiKit.Warn : UiKit.Bad;
}
