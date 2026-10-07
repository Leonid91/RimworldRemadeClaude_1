using System;
using System.Collections.Generic;
using Godot;
using Remade.Diagnostics;
using Remade.Pawns;
using Remade.Sim;
using Remade.Things;

namespace Remade.Game.UI;

/// <summary>
/// Stalker / Project Zomboid style inventory: what is worn, the hands (one item: a weapon, or anything carried by
/// hand) and one grid whose size follows the colonist's carrying capacity. Drag items between worn clothes, the grid
/// and the hands (dragging clothes into the grid takes them off, dragging clothes onto "Worn" puts them on; right mouse
/// while dragging rotates), right-click for actions (wear, take off, take in hands, put away, eat, drop). A load bar
/// shows carried mass against the load zones.
/// </summary>
public partial class InventoryView : VBoxContainer
{
    const float Cell = 34f;
    readonly GameSim _sim;
    readonly Pawn _pawn;
    readonly Action _changed;
    Item _dragItem;
    bool _dragFromHands, _dragFromWorn;
    bool _dragRot;
    GridCanvas _grid;
    HandsSlot _hands;
    WornStrip _worn;

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
        AddChild(UiKit.Heading("Worn"));
        _worn = new WornStrip(this) { Name = "WornStrip" };
        AddChild(_worn);
        AddChild(UiKit.Heading("Hands"));
        _hands = new HandsSlot(this) { Name = "HandsSlot" };
        AddChild(_hands);
        var held = _pawn.Held;
        AddChild(UiKit.Label(held == null ? "Empty (fists)"
            : held.Def.Kind == ThingKind.Weapon ? $"{held.Def.Label} · {held.Def.Range:F0} m range · {_pawn.Arrows} arrows"
            : $"{held.Label} · {held.Mass:F2} kg", 14, UiKit.Muted));
        var g = _pawn.Inventory;
        AddChild(UiKit.Heading("Inventory"));
        AddChild(UiKit.Label($"{g.W}×{g.H} = {g.W * g.H} slots  ·  {g.Mass:F2} kg", 14, UiKit.Muted));
        _grid = new GridCanvas(this, g) { Name = "InventoryGrid" };
        AddChild(_grid);
    }

    /// <summary>Puts the held item away, exactly like the "Put away" menu entry (also used by the autopilot).</summary>
    public void PutAway()
    {
        Log.Action($"inventory: Put away {_pawn.Held}");
        _sim.Unequip(_pawn);
        Changed();
    }

    /// <summary>Takes an inventory item in hand, exactly like the "Equip" menu entry (also used by the autopilot).</summary>
    public void Equip(Item it)
    {
        Log.Action($"inventory: Equip {it}");
        _sim.EquipFromInventory(_pawn, it);
        Changed();
    }

    /// <summary>Takes a worn garment off into the inventory, like the "Take off" menu entry (also used by the autopilot).</summary>
    public void TakeOff(Item worn)
    {
        Log.Action($"inventory: Take off {worn}");
        _sim.TakeOff(_pawn, worn);
        Changed();
    }

    /// <summary>Puts on a garment from the inventory, like the "Wear" menu entry (also used by the autopilot).</summary>
    public void Wear(Item it)
    {
        Log.Action($"inventory: Wear {it}");
        _sim.WearFromInventory(_pawn, it);
        Changed();
    }

    void Changed()
    {
        _dragItem = null; _dragFromHands = false; _dragFromWorn = false;
        Rebuild();
        _changed?.Invoke();
    }

    Control LoadBar()
    {
        var v = UiKit.VBox(3);
        float mass = _pawn.CarriedMass, basis = _pawn.CarryBasisKg;
        var lvl = _pawn.EncumbranceLevel;
        var col = lvl switch { Encumbrance.Unencumbered => UiKit.Good, Encumbrance.Encumbered => UiKit.Warn, Encumbrance.Heavy => UiKit.Passion, _ => UiKit.Bad };
        v.AddChild(UiKit.Label($"Carrying {mass:F1} kg  ·  {lvl}  ·  speed {_pawn.SpeedFactorFromLoad * 100:F0} %", 15, col));
        var bar = new LoadBarControl(mass / basis) { CustomMinimumSize = new Vector2(0, 16), Name = "LoadBar" };
        string basisNote = Math.Abs(basis - _pawn.BodyMassKg) > 0.01f ? $"{basis:F0} kg (body mass {_pawn.BodyMassKg:F0} kg × traits)" : $"body mass {basis:F0} kg";
        bar.TooltipText = $"Load zones as a share of {basisNote}:\n" +
                          $"  up to {Pawn.LightLoad * 100:F0} % ({basis * Pawn.LightLoad:F1} kg): unencumbered (comfortable)\n" +
                          $"  up to {Pawn.MarchLoad * 100:F0} % ({basis * Pawn.MarchLoad:F1} kg): encumbered, slower (a soldier's approach-march load)\n" +
                          $"  up to {Pawn.HeavyLoad * 100:F0} % ({basis * Pawn.HeavyLoad:F1} kg): heavily encumbered\n" +
                          $"  above: overloaded, barely moving; nothing can be picked up beyond {basis * Pawn.MaxLoad:F0} kg";
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

    static void DrawItem(CanvasItem c, Item it, Rect2 r, float alpha)
    {
        var col = UiKit.Rgb(it.Def.Color);
        c.DrawRect(r, new Color(col.Darkened(0.35f), 0.9f * alpha));
        c.DrawRect(r, new Color(col.Lightened(0.2f), alpha), false, 1.5f);
        var f = UiKit.Font;
        string label = it.Def.Label;
        int maxChars = Math.Max(3, (int)(r.Size.X / 7f));
        c.DrawString(f, r.Position + new Vector2(4, 14), label.Length > maxChars ? label[..maxChars] : label, HorizontalAlignment.Left, r.Size.X - 6, 12, new Color(UiKit.Text, alpha));
        // stack count at the bottom-left, never clipped by narrow items
        if (it.Count > 1) c.DrawString(f, new Vector2(r.Position.X + 3, r.End.Y - 4), $"x{it.Count}", HorizontalAlignment.Left, -1, 12, new Color(UiKit.Accent, alpha));
    }

    /// <summary>The hands: holds one item of any size.</summary>
    sealed partial class HandsSlot : Control
    {
        readonly InventoryView _owner;
        bool _hover;
        public HandsSlot(InventoryView owner)
        {
            _owner = owner;
            CustomMinimumSize = new Vector2(4 * Cell, 2 * Cell);
            MouseFilter = MouseFilterEnum.Stop;
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        }

        public override void _Draw()
        {
            var r = new Rect2(Vector2.Zero, Size - new Vector2(2, 2));
            DrawRect(r, new Color(0.02f, 0.03f, 0.04f, 0.85f));
            var held = _owner._pawn.Held;
            if (held != null && !(_owner._dragFromHands && _owner._dragItem == held))
                DrawItem(this, held, r.Grow(-3), 1f);
            else if (held == null)
                DrawString(UiKit.Font, new Vector2(8, Size.Y * 0.5f + 5), "hands", HorizontalAlignment.Left, -1, 13, new Color(UiKit.Muted, 0.6f));
            bool dropping = _owner._dragItem != null && !_owner._dragFromHands && _hover;
            DrawRect(r, dropping ? UiKit.Good : new Color(UiKit.Line, 0.8f), false, dropping ? 2 : 1);
        }

        public override void _GuiInput(InputEvent e)
        {
            var held = _owner._pawn.Held;
            if (e is InputEventMouseMotion)
            {
                TooltipText = held != null ? ItemTooltip(held) : "Empty hands. Drag an item here to hold it.";
            }
            else if (e is InputEventMouseButton mb && mb.Pressed)
            {
                if (mb.ButtonIndex == MouseButton.Left)
                {
                    if (_owner._dragItem == null && held != null)
                    {
                        _owner._dragItem = held; _owner._dragFromHands = true; _owner._dragFromWorn = false; _owner._dragRot = false;
                        QueueRedraw(); _owner._grid.QueueRedraw();
                    }
                    else if (_owner._dragItem != null && _owner._dragFromWorn)
                    {
                        _owner._sim.Message("Take it off into the inventory first.", _owner._pawn.Position);
                        _owner.Changed();
                    }
                    else if (_owner._dragItem != null && !_owner._dragFromHands)
                    {
                        Log.Action($"inventory: drag {_owner._dragItem} into the hands");
                        _owner._sim.EquipFromInventory(_owner._pawn, _owner._dragItem);
                        _owner.Changed();
                    }
                    else if (_owner._dragFromHands) { _owner._dragItem = null; _owner._dragFromHands = false; QueueRedraw(); }
                    AcceptEvent();
                }
                else if (mb.ButtonIndex == MouseButton.Right)
                {
                    if (_owner._dragItem != null) { _owner._dragRot = !_owner._dragRot; _owner._grid.QueueRedraw(); }
                    else if (held != null) _owner.ItemMenu(held, inHands: true, mb.GlobalPosition);
                    AcceptEvent();
                }
            }
        }

        public override void _Notification(int what)
        {
            if (what == NotificationMouseEnter) { _hover = true; QueueRedraw(); }
            if (what == NotificationMouseExit) { _hover = false; QueueRedraw(); }
        }
    }

    /// <summary>The inventory grid.</summary>
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

        static Rect2 CellRect(int x, int y, int w, int h) => new(x * Cell + 2, y * Cell + 2, w * Cell - 6, h * Cell - 6);

        public override void _Draw()
        {
            for (int y = 0; y < Grid.H; y++)
                for (int x = 0; x < Grid.W; x++)
                {
                    var r = new Rect2(x * Cell, y * Cell, Cell - 2, Cell - 2);
                    DrawRect(r, new Color(0.02f, 0.03f, 0.04f, 0.85f));
                    DrawRect(r, new Color(UiKit.Line, 0.5f), false, 1);
                }
            foreach (var e in Grid.Entries)
            {
                if (e.Item == _owner._dragItem) continue;
                DrawItem(this, e.Item, CellRect(e.X, e.Y, e.W, e.H), 1f);
            }
            // drop preview
            if (_owner._dragItem != null && _hoverCell.X >= 0)
            {
                var def = _owner._dragItem.Def;
                int w = _owner._dragRot ? def.GridH : def.GridW, h = _owner._dragRot ? def.GridW : def.GridH;
                bool ok = CanDropAt(_hoverCell.X, _hoverCell.Y, w, h);
                DrawItem(this, _owner._dragItem, CellRect(_hoverCell.X, _hoverCell.Y, w, h), 0.6f);
                DrawRect(new Rect2(_hoverCell.X * Cell, _hoverCell.Y * Cell, w * Cell - 2, h * Cell - 2), ok ? UiKit.Good : UiKit.Bad, false, 2);
            }
        }

        bool CanDropAt(int x, int y, int w, int h)
        {
            int self = Grid.Entries.FindIndex(e => e.Item == _owner._dragItem);
            return Grid.Fits(x, y, w, h, self);
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
                        _owner._dragFromHands = false; _owner._dragFromWorn = false;
                        _owner._dragRot = Grid.Entries[idx].Rotated;
                        QueueRedraw(); _owner._hands.QueueRedraw();
                    }
                    else if (_owner._dragItem != null) _owner.DropOnGrid(cell);
                    AcceptEvent();
                }
                else if (mb.ButtonIndex == MouseButton.Right)
                {
                    if (_owner._dragItem != null) { _owner._dragRot = !_owner._dragRot; QueueRedraw(); }
                    else if (idx >= 0) _owner.ItemMenu(Grid.Entries[idx].Item, inHands: false, mb.GlobalPosition);
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

    void DropOnGrid(Vector2I cell)
    {
        var it = _dragItem;
        bool ok;
        if (_dragFromWorn)
        {
            ok = _sim.TakeOff(_pawn, it, cell.X, cell.Y, _dragRot);
            if (ok) Log.Action($"inventory: took off {it} at {cell.X},{cell.Y}");
        }
        else if (_dragFromHands)
        {
            ok = _sim.MoveHeldToGrid(_pawn, cell.X, cell.Y, _dragRot);
            if (ok) Log.Action($"inventory: put {it} away at {cell.X},{cell.Y}");
        }
        else
        {
            ok = _pawn.Inventory.Move(it, cell.X, cell.Y, _dragRot);
            if (ok) Log.Action($"inventory: moved {it} to {cell.X},{cell.Y}");
        }
        if (!ok) _sim.Message("It does not fit there.", _pawn.Position);
        Changed();
    }

    void WornMenu(Item worn, Vector2 at)
    {
        var menu = new PopupMenu();
        var actions = new List<Action>
        {
            () => _sim.TakeOff(_pawn, worn),
            () => _sim.TakeOffAndDrop(_pawn, worn),
        };
        menu.AddItem("Take off (into the inventory)");
        menu.AddItem("Take off and drop");
        menu.IdPressed += id =>
        {
            Log.Action($"worn: {menu.GetItemText((int)id)} {worn}");
            actions[(int)id]();
            Changed();
        };
        AddChild(menu);
        menu.Position = (Vector2I)at;
        menu.Popup();
    }

    /// <summary>The clothes being worn, one 2×2 tile each: drag one into the grid to take it off, drop clothes here to wear them.</summary>
    sealed partial class WornStrip : Control
    {
        readonly InventoryView _owner;
        bool _hover;
        const float Tile = 2 * Cell;

        public WornStrip(InventoryView owner)
        {
            _owner = owner;
            CustomMinimumSize = new Vector2(Math.Max(1, owner._pawn.Apparel.Count + 1) * (Tile + 6), Tile + 2);
            MouseFilter = MouseFilterEnum.Stop;
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        }

        Rect2 TileRect(int i) => new(i * (Tile + 6), 0, Tile, Tile);

        int TileAt(Vector2 p)
        {
            for (int i = 0; i < _owner._pawn.Apparel.Count; i++) if (TileRect(i).HasPoint(p)) return i;
            return -1;
        }

        public override void _Draw()
        {
            var worn = _owner._pawn.Apparel;
            for (int i = 0; i < worn.Count; i++)
            {
                var r = TileRect(i);
                DrawRect(r, new Color(0.02f, 0.03f, 0.04f, 0.85f));
                if (!(_owner._dragFromWorn && _owner._dragItem == worn[i])) DrawItem(this, worn[i], r.Grow(-3), 1f);
                DrawRect(r, new Color(UiKit.Line, 0.8f), false, 1);
            }
            // a free tile: where clothes are dropped to be worn
            var free = TileRect(worn.Count);
            bool dropping = _hover && _owner._dragItem != null && !_owner._dragFromWorn && _owner._dragItem.Def.Kind == ThingKind.Apparel;
            DrawRect(free, new Color(0.02f, 0.03f, 0.04f, 0.6f));
            DrawString(UiKit.Font, free.Position + new Vector2(6, Tile * 0.5f + 5), "wear", HorizontalAlignment.Left, -1, 13, new Color(UiKit.Muted, 0.6f));
            DrawRect(free, dropping ? UiKit.Good : new Color(UiKit.Line, 0.5f), false, dropping ? 2 : 1);
        }

        public override void _GuiInput(InputEvent e)
        {
            var worn = _owner._pawn.Apparel;
            if (e is InputEventMouseMotion mm)
            {
                int i = TileAt(mm.Position);
                TooltipText = i >= 0 ? ItemTooltip(worn[i]) + "\nDrag into the inventory or right-click to take it off." : "Drop clothes here to put them on.";
            }
            else if (e is InputEventMouseButton mb && mb.Pressed)
            {
                int i = TileAt(mb.Position);
                if (mb.ButtonIndex == MouseButton.Left)
                {
                    if (_owner._dragItem == null && i >= 0)
                    {
                        _owner._dragItem = worn[i]; _owner._dragFromWorn = true; _owner._dragFromHands = false; _owner._dragRot = false;
                        QueueRedraw(); _owner._grid.QueueRedraw();
                    }
                    else if (_owner._dragItem != null && !_owner._dragFromWorn)
                    {
                        var it = _owner._dragItem;
                        if (it.Def.Kind == ThingKind.Apparel) { Log.Action($"inventory: drag {it} onto Worn"); _owner._sim.WearFromInventory(_owner._pawn, it); }
                        else _owner._sim.Message($"The {it.Def.Label} cannot be worn.", _owner._pawn.Position);
                        _owner.Changed();
                    }
                    else if (_owner._dragFromWorn) { _owner._dragItem = null; _owner._dragFromWorn = false; QueueRedraw(); }
                    AcceptEvent();
                }
                else if (mb.ButtonIndex == MouseButton.Right)
                {
                    if (_owner._dragItem != null) { _owner._dragRot = !_owner._dragRot; _owner._grid.QueueRedraw(); }
                    else if (i >= 0) _owner.WornMenu(worn[i], mb.GlobalPosition);
                    AcceptEvent();
                }
            }
        }

        public override void _Notification(int what)
        {
            if (what == NotificationMouseEnter) { _hover = true; QueueRedraw(); }
            if (what == NotificationMouseExit) { _hover = false; QueueRedraw(); }
        }
    }

    void ItemMenu(Item it, bool inHands, Vector2 at)
    {
        var menu = new PopupMenu();
        var actions = new List<Action>();
        if (inHands)
        {
            menu.AddItem("Put away"); actions.Add(() => _sim.Unequip(_pawn));
        }
        else
        {
            menu.AddItem(it.Def.Kind == ThingKind.Weapon ? "Equip" : "Take in hands"); actions.Add(() => _sim.EquipFromInventory(_pawn, it));
        }
        if (it.Def.Kind == ThingKind.Apparel) { menu.AddItem("Wear"); actions.Add(() => _sim.WearFromInventory(_pawn, it)); }
        if (it.Def.Kind == ThingKind.Food) { menu.AddItem("Eat"); actions.Add(() => _sim.EatFromInventory(_pawn, it)); }
        menu.AddItem("Drop"); actions.Add(() => _sim.DropFromInventory(_pawn, it));
        menu.IdPressed += id =>
        {
            Log.Action($"inventory: {menu.GetItemText((int)id)} {it}");
            actions[(int)id]();
            Changed();
        };
        AddChild(menu);
        menu.Position = (Vector2I)at;
        menu.Popup();
    }
}
