using System;
using System.Linq;
using Godot;
using Remade.Core;
using Remade.Game.Render;
using Remade.Pawns;
using Remade.Things;

namespace Remade.Game.UI;

/// <summary>"Create colonists": a wide, readable sheet per colonist with name, sex, ages, traits, skills, health and gear.</summary>
public partial class ColonistScreen : ScreenBase
{
    int _sel;
    readonly VBoxContainer _list;
    readonly MarginContainer _sheetHost;
    readonly HBoxContainer _team;
    PawnPortrait _portrait;

    public ColonistScreen() : base("Create colonists", "Review your colonists. Randomize anyone you are not happy with.")
    {
        var row = UiKit.HBox(16);
        row.SizeFlagsVertical = SizeFlags.ExpandFill;
        Body.AddChild(row);

        var listPanel = UiKit.Panel(null);
        listPanel.CustomMinimumSize = new Vector2(300, 0);
        _list = UiKit.VBox(6);
        listPanel.AddChild(_list);
        row.AddChild(listPanel);

        var sheetPanel = UiKit.Panel(null);
        sheetPanel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _sheetHost = new MarginContainer();
        sheetPanel.AddChild(_sheetHost);
        row.AddChild(sheetPanel);

        var teamPanel = UiKit.Panel(null);
        var teamCol = UiKit.VBox(6);
        teamPanel.AddChild(teamCol);
        teamCol.AddChild(UiKit.Heading("Team skills (best colonist)"));
        _team = UiKit.HBox(18);
        teamCol.AddChild(_team);
        Body.AddChild(teamPanel);

        Footer.AddChild(UiKit.Button("Back", () => Main.I.StartNewGameFlow(), 18, 160));
        FooterSpacer();
        Footer.AddChild(UiKit.Button("Next", () => Main.I.ShowWorldGen(), 18, 200));
        Rebuild();
    }

    Pawn Sel => Main.I.Setup.Colonists[_sel];

    void Rebuild()
    {
        foreach (var c in _list.GetChildren()) c.QueueFree();
        _list.AddChild(UiKit.Heading("Colonists"));
        var pawns = Main.I.Setup.Colonists;
        for (int i = 0; i < pawns.Count; i++)
        {
            int idx = i;
            var p = pawns[i];
            var b = UiKit.Button($"{p.Name}\n{p.Sex}, {p.AgeLabel}", () => { _sel = idx; Rebuild(); }, 16);
            b.Alignment = HorizontalAlignment.Left;
            b.CustomMinimumSize = new Vector2(0, 58);
            if (i == _sel) b.AddThemeStyleboxOverride("normal", UiKit.PanelStyle(new Color(0.10f, 0.20f, 0.20f, 0.95f), UiKit.Accent, 3, 8));
            _list.AddChild(b);
        }
        BuildSheet();
        BuildTeam();
    }

    void BuildSheet()
    {
        foreach (var c in _sheetHost.GetChildren()) c.QueueFree();
        var p = Sel;
        var col = UiKit.VBox(10);
        _sheetHost.AddChild(col);

        // names
        var names = UiKit.HBox(8);
        col.AddChild(names);
        names.AddChild(NameEdit(p.FirstName, v => p.FirstName = v, 190));
        names.AddChild(NameEdit(p.NickName, v => p.NickName = v, 160));
        names.AddChild(NameEdit(p.LastName, v => p.LastName = v, 190));
        names.AddChild(UiKit.Expander());
        names.AddChild(UiKit.Button("Randomize", Randomize, 17, 160));

        string age = p.BioAge == p.ChronoAge ? $"age {p.BioAge}" : $"age {p.BioAge} ({p.ChronoAge})";
        col.AddChild(UiKit.Label($"{p.Sex}, {age}   ·   {p.HeightM:F2} m, {p.BodyMassKg:F0} kg", 18, UiKit.Muted));

        var cols = UiKit.HBox(22);
        cols.SizeFlagsVertical = SizeFlags.ExpandFill;
        col.AddChild(cols);

        // column 1: portrait + traits
        var c1 = UiKit.VBox(10);
        c1.CustomMinimumSize = new Vector2(280, 0);
        _portrait = new PawnPortrait(p) { CustomMinimumSize = new Vector2(280, 330) };
        c1.AddChild(_portrait);
        c1.AddChild(UiKit.Heading("Traits"));
        var flow = new HFlowContainer();
        flow.AddThemeConstantOverride("h_separation", 6);
        flow.AddThemeConstantOverride("v_separation", 6);
        foreach (var t in p.Traits)
        {
            var chip = UiKit.Panel(UiKit.Label(t.Label, 15), new Color(0.12f, 0.17f, 0.2f, 0.95f), 6);
            var trait = t;
            HoverCard.Attach(chip, () => HoverCard.TraitLines(trait));
            flow.AddChild(chip);
        }
        c1.AddChild(flow);
        cols.AddChild(c1);

        // column 2: skills
        var c2 = UiKit.VBox(5);
        c2.CustomMinimumSize = new Vector2(420, 0);
        c2.AddChild(UiKit.Heading("Skills"));
        for (int s = 0; s < (int)SkillId.Count; s++)
        {
            var r = UiKit.HBox(6);
            var name = UiKit.Label(SkillLabel((SkillId)s), 16);
            name.CustomMinimumSize = new Vector2(120, 0);
            r.AddChild(name);
            var bar = UiKit.Bar(p.Skills[s] / 20f, p.Skills[s] >= 10 ? UiKit.Accent : UiKit.AccentDim, 16);
            bar.CustomMinimumSize = new Vector2(200, 16);
            r.AddChild(bar);
            r.AddChild(UiKit.Label(p.Skills[s].ToString(), 16, p.Skills[s] == 0 ? UiKit.Muted : UiKit.Text));
            c2.AddChild(r);
        }
        cols.AddChild(c2);

        // column 3: health + gear
        var c3 = UiKit.VBox(6);
        c3.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        c3.AddChild(UiKit.Heading("Health"));
        c3.AddChild(UiKit.Label("No health conditions", 16, UiKit.Good));
        c3.AddChild(UiKit.Spacer(0, 8));
        c3.AddChild(UiKit.Heading("Worn"));
        foreach (var a in p.Apparel)
            c3.AddChild(UiKit.Label($"{a.Def.Label}  ·  {string.Join("/", a.Def.Layers.Select(l => l.ToString().ToLowerInvariant()))}", 15, UiKit.Muted));
        c3.AddChild(UiKit.Spacer(0, 8));
        c3.AddChild(UiKit.Heading("Carrying capacity"));
        var cap = UiKit.Label($"Comfortable up to {p.CarryBasisKg * Pawn.LightLoad:F0} kg, max {p.CarryBasisKg * Pawn.MaxLoad:F0} kg  ·  inventory {p.Inventory.W}×{p.Inventory.H} ({p.Inventory.W * p.Inventory.H} slots)", 15, UiKit.Muted);
        cap.TooltipText = $"Measured against {(p.CarryBasisKg != p.BodyMassKg ? $"{p.CarryBasisKg:F0} kg (body mass {p.BodyMassKg:F0} kg × traits)" : $"the body mass ({p.BodyMassKg:F0} kg)")}:\n" +
                          $"{Pawn.LightLoad * 100:F0} % comfortable, {Pawn.MarchLoad * 100:F0} % march load (slower), {Pawn.HeavyLoad * 100:F0} % heavy, {Pawn.MaxLoad * 100:F0} % maximum.\n" +
                          "Inventory slots: about one per kilogram of the march load.";
        cap.MouseFilter = MouseFilterEnum.Stop;
        c3.AddChild(cap);
        cols.AddChild(c3);
    }

    LineEdit NameEdit(string value, Action<string> set, int w)
    {
        var e = new LineEdit { Text = value, CustomMinimumSize = new Vector2(w, 38), MaxLength = 18 };
        e.AddThemeFontSizeOverride("font_size", 18);
        e.TextChanged += t => { if (!string.IsNullOrWhiteSpace(t)) set(t.Trim()); };
        e.TextSubmitted += _ => Rebuild();
        return e;
    }

    void Randomize()
    {
        var setup = Main.I.Setup;
        var rng = Rng.FromParts(setup.Seed, (long)(Time.GetTicksUsec() & 0xffffff), _sel);
        var p = PawnGenerator.Generate(ref rng, () => setup.NextPawnId++);
        setup.Colonists[_sel] = p;
        Remade.Diagnostics.Log.Info($"Colonist {_sel} randomized: {p.FullName}");
        Rebuild();
    }

    void BuildTeam()
    {
        foreach (var c in _team.GetChildren()) c.QueueFree();
        var pawns = Main.I.Setup.Colonists;
        for (int s = 0; s < (int)SkillId.Count; s++)
        {
            int best = pawns.Max(p => p.Skills[s]);
            var v = UiKit.VBox(0);
            v.AddChild(UiKit.Label(SkillLabel((SkillId)s), 13, UiKit.Muted));
            v.AddChild(UiKit.Label(best.ToString(), 18, best >= 10 ? UiKit.Accent : UiKit.Text, bold: true));
            _team.AddChild(v);
        }
    }

    public static string SkillLabel(SkillId s) => s switch
    {
        SkillId.Shooting => "Shooting", SkillId.Melee => "Melee", SkillId.Construction => "Construction", SkillId.Mining => "Mining",
        SkillId.Cooking => "Cooking", SkillId.Plants => "Plants", SkillId.Animals => "Animals", SkillId.Crafting => "Crafting",
        SkillId.Artistic => "Artistic", SkillId.Medical => "Medical", SkillId.Social => "Social", SkillId.Intellectual => "Intellectual",
        _ => throw new ArgumentOutOfRangeException(nameof(s), s, null),
    };
}
