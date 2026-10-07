using System;
using System.Collections.Generic;
using Godot;
using Remade.Pawns;
using Remade.Things;

namespace Remade.Game.UI;

/// <summary>
/// A front-view human figure split into body regions (head, eyes, neck, torso, shoulders, arms, hands, waist, legs,
/// feet), Project Zomboid style. The owner decides each region's fill colour and tooltip; hovering highlights a
/// region and clicking selects it. The figure faces the viewer, so the pawn's right side is on the left.
/// </summary>
public partial class BodyFigure : Control
{
    public Func<BodyRegion, Color> RegionColor = _ => new Color(0.3f, 0.35f, 0.38f);
    public Func<BodyRegion, string> RegionTooltip = r => r.ToString();
    public event Action<BodyRegion> RegionClicked;
    public BodyRegion? Hovered { get; private set; }
    public BodyRegion? SelectedRegion;

    static readonly Dictionary<BodyRegion, Vector2[]> Shapes = BuildShapes();
    const float W = 200, H = 430;

    public BodyFigure()
    {
        CustomMinimumSize = new Vector2(W, H);
        MouseFilter = MouseFilterEnum.Stop;
    }

    static Vector2[] Ellipse(float cx, float cy, float rx, float ry, int n = 20)
    {
        var p = new Vector2[n];
        for (int i = 0; i < n; i++)
        {
            float a = i / (float)n * Mathf.Tau;
            p[i] = new Vector2(cx + Mathf.Cos(a) * rx, cy + Mathf.Sin(a) * ry);
        }
        return p;
    }

    static Vector2[] Mirror(Vector2[] src)
    {
        var p = new Vector2[src.Length];
        for (int i = 0; i < src.Length; i++) p[src.Length - 1 - i] = new Vector2(W - src[i].X, src[i].Y);
        return p;
    }

    static Dictionary<BodyRegion, Vector2[]> BuildShapes()
    {
        var d = new Dictionary<BodyRegion, Vector2[]>();
        d[BodyRegion.Head] = Ellipse(100, 38, 26, 31);
        d[BodyRegion.Eyes] = new[] { new Vector2(84, 31), new Vector2(116, 31), new Vector2(116, 40), new Vector2(84, 40) };
        d[BodyRegion.Neck] = new[] { new Vector2(90, 66), new Vector2(110, 66), new Vector2(112, 82), new Vector2(88, 82) };
        d[BodyRegion.Torso] = new[] { new Vector2(74, 84), new Vector2(126, 84), new Vector2(132, 100), new Vector2(128, 168), new Vector2(122, 196), new Vector2(78, 196), new Vector2(72, 168), new Vector2(68, 100) };
        // the pawn's right shoulder/arm/hand/leg/foot is drawn on the viewer's left
        var shoulderR = new[] { new Vector2(52, 92), new Vector2(74, 84), new Vector2(68, 100), new Vector2(66, 116), new Vector2(48, 114) };
        var armR = new[] { new Vector2(48, 116), new Vector2(66, 118), new Vector2(64, 160), new Vector2(58, 206), new Vector2(42, 204), new Vector2(42, 160) };
        var handR = new[] { new Vector2(42, 208), new Vector2(58, 208), new Vector2(60, 230), new Vector2(52, 240), new Vector2(40, 232) };
        var legR = new[] { new Vector2(78, 222), new Vector2(99, 222), new Vector2(97, 300), new Vector2(95, 384), new Vector2(78, 384), new Vector2(74, 300) };
        var footR = new[] { new Vector2(76, 388), new Vector2(96, 388), new Vector2(98, 410), new Vector2(68, 412), new Vector2(70, 400) };
        d[BodyRegion.ShoulderR] = shoulderR; d[BodyRegion.ShoulderL] = Mirror(shoulderR);
        d[BodyRegion.ArmR] = armR; d[BodyRegion.ArmL] = Mirror(armR);
        d[BodyRegion.HandR] = handR; d[BodyRegion.HandL] = Mirror(handR);
        d[BodyRegion.Waist] = new[] { new Vector2(78, 198), new Vector2(122, 198), new Vector2(124, 220), new Vector2(76, 220) };
        d[BodyRegion.LegR] = legR; d[BodyRegion.LegL] = Mirror(legR);
        d[BodyRegion.FootR] = footR; d[BodyRegion.FootL] = Mirror(footR);
        return d;
    }

    Vector2 Origin => (Size - new Vector2(W, H)) * 0.5f;

    public override void _Draw()
    {
        var o = Origin;
        foreach (var kv in Shapes)
        {
            if (kv.Key == BodyRegion.Eyes) continue;
            DrawRegion(kv.Key, kv.Value, o);
        }
        DrawRegion(BodyRegion.Eyes, Shapes[BodyRegion.Eyes], o);
        var f = UiKit.Font;
        DrawString(f, o + new Vector2(6, 20), "R", HorizontalAlignment.Left, -1, 14, UiKit.Muted);
        DrawString(f, o + new Vector2(W - 16, 20), "L", HorizontalAlignment.Left, -1, 14, UiKit.Muted);
    }

    void DrawRegion(BodyRegion r, Vector2[] pts, Vector2 o)
    {
        var poly = new Vector2[pts.Length];
        for (int i = 0; i < pts.Length; i++) poly[i] = pts[i] + o;
        var fill = RegionColor(r);
        bool hl = Hovered == r || SelectedRegion == r;
        if (hl) fill = fill.Lightened(0.25f);
        DrawColoredPolygon(poly, fill);
        var outline = new Vector2[poly.Length + 1];
        Array.Copy(poly, outline, poly.Length);
        outline[^1] = poly[0];
        DrawPolyline(outline, hl ? UiKit.Accent : new Color(0.85f, 0.88f, 0.9f, 0.55f), hl ? 2.5f : 1.3f, true);
    }

    BodyRegion? RegionAt(Vector2 local)
    {
        var p = local - Origin;
        // eyes first (they lie inside the head)
        if (Geometry2D.IsPointInPolygon(p, Shapes[BodyRegion.Eyes])) return BodyRegion.Eyes;
        foreach (var kv in Shapes)
            if (kv.Key != BodyRegion.Eyes && Geometry2D.IsPointInPolygon(p, kv.Value)) return kv.Key;
        return null;
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseMotion mm)
        {
            var r = RegionAt(mm.Position);
            if (r != Hovered) { Hovered = r; TooltipText = r.HasValue ? RegionTooltip(r.Value) : ""; QueueRedraw(); }
        }
        else if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mb)
        {
            var r = RegionAt(mb.Position);
            if (r.HasValue) { SelectedRegion = r; RegionClicked?.Invoke(r.Value); QueueRedraw(); }
        }
    }

    public override void _Notification(int what)
    {
        if (what == NotificationMouseExit && Hovered != null) { Hovered = null; QueueRedraw(); }
    }

    public static string RegionName(BodyRegion r) => r switch
    {
        BodyRegion.Head => "Head", BodyRegion.Eyes => "Eyes", BodyRegion.Neck => "Neck", BodyRegion.Torso => "Torso",
        BodyRegion.ShoulderL => "Left shoulder", BodyRegion.ShoulderR => "Right shoulder", BodyRegion.ArmL => "Left arm", BodyRegion.ArmR => "Right arm",
        BodyRegion.HandL => "Left hand", BodyRegion.HandR => "Right hand", BodyRegion.Waist => "Waist", BodyRegion.LegL => "Left leg",
        BodyRegion.LegR => "Right leg", BodyRegion.FootL => "Left foot", BodyRegion.FootR => "Right foot",
        _ => throw new ArgumentOutOfRangeException(nameof(r), r, null),
    };
}
