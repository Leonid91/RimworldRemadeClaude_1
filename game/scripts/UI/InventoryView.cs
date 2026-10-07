using System;
using System.Collections.Generic;
using Godot;
using Remade.Diagnostics;
using Remade.Pawns;
using Remade.Sim;
using Remade.Things;

namespace Remade.Game.UI;

/// <summary>
/// Stalker / Project Zomboid style inventory: every worn container (trouser pockets, jacket pockets, satchel…) is a
/// grid of cells; stacks occupy their footprint. Drag items between cells and containers (right mouse while dragging
/// rotates), right-click for actions (equip, eat, drop). A load bar shows carried mass against the body-mass zones.
/// </summary>
public partial class InventoryView : VBoxContainer
{
    const float Cell = 34f;
    readonly GameSim _sim;
    readonly Pawn _pawn;
    readonly Action _changed;
    GridCanvas _dragFrom;
    Item _dragItem;
    bool _dragRot;

    public InventoryView(GameSim sim, Pawn pawn, Action changed)
    {
        _sim = sim; _pawn = pawn; _changed = changed;
        AddThemeConstantOverride("separation", 8);
        Rebuild();
    }

    public void Rebuild()
    {
        foreach (var c in GetChildren()) c.QueueFree();
        AddChild(LoadBar());
        foreach (var a in _pawn.Apparel)
        {
            if (a.Contents == null) continue;
            var g = a.Contents;
            AddChild(UiKit.Label($"{g.Label}  ·  {g.Mass:F2} kg", 14, UiKit.Muted));
            AddChild(new GridCanvas(this, g));
        }
    }

    Control LoadBar()
    {
        var v = UiKit.VBox(3);
        float mass = _pawn.CarriedMass, bm = _pawn.BodyMassKg;
        var lvl = _pawn.EncumbranceLevel;
        var col = lvl switch { Encumbrance.Unencumbered => UiKit.Good, Encumbrance.Encumbered => UiKit.Warn, Encumbrance.Heavy => UiKit.Passion, _ => UiKit.Bad };
        v.AddChild(UiKit.Label($"Carrying {mass:F1} kg  ·  {lvl}  ·  speed {_pawn.SpeedFactorFromLoad * 100:F0} %", 15, col));
        var bar = new LoadBarControl(mass / bm) { CustomMinimumSize = new Vector2(0, 16) };
        bar.TooltipText = $"Load zones as a share of body mass ({bm:F0} kg):\n" +
                          $"  up to {Pawn.LightLoad * 100:F0} % ({bm * Pawn.LightLoad:F1} kg): unencumbered\n" +
                          $"  up to {Pawn.MarchLoad * 100:F0} % ({bm * Pawn.MarchLoad:F1} kg): encumbered — slower (a soldier's approach-march load)\n" +
                          $"  up to {Pawn.HeavyLoad * 100:F0} % ({bm * Pawn.HeavyLoad:F1} kg): heavily encumbered\n" +
                          $"  above: overloaded, barely moving; nothing can be picked up beyond {bm * Pawn.MaxLoad:F0} kg";
        v.AddChild(bar);
        return v;
    }

    sealed partial class LoadBarControl : Control
    {
        readonly float _ratio;
        public LoadBarControl(float ratio) { _ratio = ratio; MouseFilter = MouseFilterEnum.Stop; }
        public override void _Draw()
        {
            float w = Size.X, h = Size.Y;
            float Scale(float r) => Mathf.Clamp(r / Pawn.MaxLoad, 0f, 1f) * w;
            DrawRect(new Rect2(0, 0, Scale(Pawn.LightLoad), h), new Color(UiKit.Good, 0.25f));
            DrawRect(new Rect2(Scale(Pawn.LightLoad), 0, Scale(Pawn.MarchLoad) - Scale(Pawn.LightLoad), h), new Color(UiKit.Warn, 0.25f));
            DrawRect(new Rect2(Scale(Pawn.MarchLoad), 0, Scale(Pawn.HeavyLoad) - Scale(Pawn.MarchLoad), h), new Color(UiKit.Passion, 0.25f));
            DrawRect(new Rect2(Scale(Pawn.HeavyLoad), 0, w - Scale(Pawn.HeavyLoad), h), new Color(UiKit.Bad, 0.25f));
            DrawRect(new Rect2(0, h * 0.25f, Scale(_ratio), h * 0.5f), UiKit.Text);
            DrawRect(new Rect2(0, 0, w, h), UiKit.Line, false, 1);
        }
    }

    /// <summary>One container grid.</summary>
    sealed partial class GridCanvas : Control
    {
        readonly InventoryView _owner;
        public readonly InventoryGrid Grid;
        Vector2I _hoverCell = new(-1, -1);

        public GridCanvas(InventoryView owner, InventoryGrid g)
        {
            _owner = owner; Grid = g;
            CustomMinimumSize = new Vector2(g.W * Cell + 1, g.H * Cell + 1);
            MouseFilter = MouseFilterEnum.Stop;
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        }

        public override void _Draw()
        {
            for (int y = 0; y < Grid.H; y++)
                for (int x = 0; x < Grid.W; x++)
                {
                    var r = new Rect2(x * Cell, y * Cell, Cell - 2, Cell - 2);
                    DrawRect(r, new Color(0.02f, 0.03f, 0.04f, 0.85f));
                    DrawRect(r, new Color(UiKit.Line, 0.5f), false, 1);
                }
            for (int i = 0; i < Grid.Entries.Count; i++)
            {
                var e = Grid.Entries[i];
                if (e.Item == _owner._dragItem) continue;
                DrawItem(e.Item, e.X, e.Y, e.W, e.H, 1f);
            }
            // drop preview
            if (_owner._dragItem != null && _hoverCell.X >= 0)
            {
                var def = _owner._dragItem.Def;
                int w = _owner._dragRot ? def.GridH : def.GridW, h = _owner._dragRot ? def.GridW : def.GridH;
                bool ok = CanDropAt(_hoverCell.X, _hoverCell.Y, w, h);
                DrawItem(_owner._dragItem, _hoverCell.X, _hoverCell.Y, w, h, 0.6f);
                DrawRect(new Rect2(_hoverCell.X * Cell, _hoverCell.Y * Cell, w * Cell - 2, h * Cell - 2), ok ? UiKit.Good : UiKit.Bad, false, 2);
            }
        }

        bool CanDropAt(int x, int y, int w, int h)
        {
            int self = Grid.Entries.FindIndex(e => e.Item == _owner._dragItem);
            return Grid.Fits(x, y, w, h, self);
        }

        void DrawItem(Item it, int x, int y, int w, int h, float alpha)
        {
            var r = new Rect2(x * Cell + 2, y * Cell + 2, w * Cell - 6, h * Cell - 6);
            var c = Remade.Game.UI.UiKit.Rgb(it.Def.Color);
            DrawRect(r, new Color(c.Darkened(0.35f), 0.9f * alpha));
            DrawRect(r, new Color(c.Lightened(0.2f), alpha), false, 1.5f);
            var f = UiKit.Font;
            string label = it.Def.Label;
            int fs = 12;
            DrawString(f, r.Position + new Vector2(4, 14), label.Length > w * 5 ? label[..Math.Max(3, w * 5)] : label, HorizontalAlignment.Left, r.Size.X - 6, fs, new Color(UiKit.Text, alpha));
            if (it.Count > 1) DrawString(f, r.End - new Vector2(r.Size.X - 4, 4), $"x{it.Count}", HorizontalAlignment.Right, r.Size.X - 8, 13, new Color(UiKit.Accent, alpha));
        }

        Vector2I CellAt(Vector2 p) => new((int)(p.X / Cell), (int)(p.Y / Cell));

        public override void _GuiInput(InputEvent e)
        {
            if (e is InputEventMouseMotion mm)
            {
                _hoverCell = CellAt(mm.Position);
                int idx = Grid.EntryAt(_hoverCell.X, _hoverCell.Y);
                TooltipText = idx >= 0 ? ItemTooltip(Grid.Entries[idx].Item) : "";
                if (_owner._dragItem != null) QueueRedraw();
            }
            else if (e is InputEventMouseButton mb && mb.Pressed)
            {
                var cell = CellAt(mb.Position);
                int idx = Grid.EntryAt(cell.X, cell.Y);
                if (mb.ButtonIndex == MouseButton.Left)
                {
                    if (_owner._dragItem == null && idx >= 0)
                    {
                        _owner._dragItem = Grid.Entries[idx].Item;
                        _owner._dragFrom = this;
                        _owner._dragRot = Grid.Entries[idx].Rotated;
                        QueueRedraw();
                    }
                    else if (_owner._dragItem != null) _owner.Drop(this, cell);
                    AcceptEvent();
                }
                else if (mb.ButtonIndex == MouseButton.Right)
                {
                    if (_owner._dragItem != null) { _owner._dragRot = !_owner._dragRot; QueueRedraw(); }
                    else if (idx >= 0) _owner.ItemMenu(Grid.Entries[idx].Item, this, mb.GlobalPosition);
                    AcceptEvent();
                }
            }
        }

        public override void _Notification(int what)
        {
            if (what == NotificationMouseExit) { _hoverCell = new Vector2I(-1, -1); QueueRedraw(); }
        }
    }

    static string ItemTooltip(Item it) =>
        $"{it.Def.Label}{(it.Count > 1 ? $" x{it.Count}" : "")}\n{it.Mass:F2} kg" +
        (it.Def.Kind == ThingKind.Food ? $"\nNutrition {it.Def.Nutrition * 100:F0} % each" : "") +
        (it.Def.Kind == ThingKind.Weapon ? $"\nRange {it.Def.Range:F0} m, damage {it.Def.Damage:F0}" : "") +
        (string.IsNullOrEmpty(it.Def.Description) ? "" : "\n" + it.Def.Description);

    void Drop(GridCanvas target, Vector2I cell)
    {
        var it = _dragItem;
        var def = it.Def;
        int w = _dragRot ? def.GridH : def.GridW, h = _dragRot ? def.GridW : def.GridH;
        if (target == _dragFrom)
        {
            if (!target.Grid.Move(it, cell.X, cell.Y, _dragRot)) { _sim.Message("It does not fit there.", _pawn.Position); }
        }
        else if (target.Grid.Fits(cell.X, cell.Y, w, h))
        {
            bool removed = _dragFrom.Grid.Remove(it);
            Invariant.Check(removed, $"{it} vanished from {_dragFrom.Grid.Label} while dragging");
            target.Grid.Place(it, cell.X, cell.Y, _dragRot);
            Log.Action($"moved {it} from {_dragFrom.Grid.Label} to {target.Grid.Label}");
        }
        else _sim.Message("It does not fit there.", _pawn.Position);
        _dragItem = null; _dragFrom = null;
        Rebuild();
        _changed?.Invoke();
    }

    void ItemMenu(Item it, GridCanvas from, Vector2 at)
    {
        var menu = new PopupMenu();
        var actions = new List<Action>();
        if (it.Def.Kind == ThingKind.Weapon) { menu.AddItem("Equip"); actions.Add(() => _sim.EquipFromInventory(_pawn, it)); }
        if (it.Def.Kind == ThingKind.Food) { menu.AddItem("Eat"); actions.Add(() => _sim.EatFromInventory(_pawn, it)); }
        menu.AddItem("Drop"); actions.Add(() => _sim.DropFromInventory(_pawn, it));
        menu.IdPressed += id =>
        {
            Log.Action($"inventory: {menu.GetItemText((int)id)} {it}");
            actions[(int)id]();
            Rebuild();
            _changed?.Invoke();
        };
        AddChild(menu);
        menu.Position = (Vector2I)at;
        menu.Popup();
    }
}
