using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using Remade.Diagnostics;

namespace Remade.Map;

/// <summary>
/// Movement costs and connectivity of a map. Cost 0 = impassable. Connected components let unreachable requests
/// fail in O(1) instead of flooding the whole map. A coarse layer of sector regions (the passable cells of a
/// Sector×Sector block that are connected inside it) forms a small graph that long searches are planned on first.
/// </summary>
public sealed class PathGrid
{
    public readonly LocalMap Map;
    public readonly byte[] Cost;

    int[] _component;
    bool _componentsDirty = true;
    public int ComponentCount { get; private set; }

    public const int Sector = 16;
    public readonly int SectorsX, SectorsY;
    readonly int[] _cellRegion;                 // region id per cell, -1 = impassable
    readonly List<int>[] _sectorRegions;        // live region ids per sector
    readonly bool[] _sectorDirty;
    readonly List<int> _dirtySectors = new();
    // region table (ids are recycled through _freeRegions)
    internal readonly List<Region> Regions = new();
    readonly Stack<int> _freeRegions = new();
    readonly int[] _floodStack = new int[Sector * Sector];

    internal sealed class Region
    {
        public bool Alive;
        public float X, Y;          // centroid (cell units)
        public float MeanCost;      // mean movement cost of its cells
        public readonly List<int> Neighbours = new(6);
    }

    public PathGrid(LocalMap map)
    {
        Map = map;
        Cost = new byte[map.CellCount];
        for (int i = 0; i < Cost.Length; i++) Cost[i] = ComputeCost(i);
        _component = new int[map.CellCount];
        SectorsX = (map.Width + Sector - 1) / Sector;
        SectorsY = (map.Height + Sector - 1) / Sector;
        _cellRegion = new int[map.CellCount];
        _sectorRegions = new List<int>[SectorsX * SectorsY];
        _sectorDirty = new bool[_sectorRegions.Length];
        using var _ = Log.Time("PathGrid.BuildRegions", 200);
        for (int sct = 0; sct < _sectorRegions.Length; sct++) { _sectorRegions[sct] = new List<int>(2); BuildSector(sct); }
        for (int sct = 0; sct < _sectorRegions.Length; sct++) { LinkEast(sct); LinkSouth(sct); }
    }

    public int RegionOf(int cell)
    {
        if (_dirtySectors.Count > 0) UpdateDirtySectors();
        return _cellRegion[cell];
    }

    internal int RegionCapacity => Regions.Count;

    /// <summary>Region ids per cell (valid after <see cref="RegionOf"/> brought dirty sectors up to date).</summary>
    internal int[] CellRegions => _cellRegion;

    void BuildSector(int sct)
    {
        int W = Map.Width, H = Map.Height;
        int x0 = sct % SectorsX * Sector, y0 = sct / SectorsX * Sector;
        int x1 = Math.Min(W, x0 + Sector), y1 = Math.Min(H, y0 + Sector);
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++) _cellRegion[y * W + x] = -1;
        var stack = _floodStack;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int start = y * W + x;
                if (Cost[start] == 0 || _cellRegion[start] >= 0) continue;
                int id = NewRegion();
                var reg = Regions[id];
                _sectorRegions[sct].Add(id);
                int sp = 0, count = 0;
                double sx = 0, sy = 0, sc = 0;
                _cellRegion[start] = id;
                stack[sp++] = start;
                while (sp > 0)
                {
                    int c = stack[--sp];
                    int cy = c / W, cx = c - cy * W;
                    count++; sx += cx + 0.5; sy += cy + 0.5; sc += Cost[c];
                    if (cx > x0) Visit(c - 1);
                    if (cx < x1 - 1) Visit(c + 1);
                    if (cy > y0) Visit(c - W);
                    if (cy < y1 - 1) Visit(c + W);
                }
                reg.X = (float)(sx / count); reg.Y = (float)(sy / count); reg.MeanCost = (float)(sc / count);

                void Visit(int n)
                {
                    if (Cost[n] != 0 && _cellRegion[n] < 0) { _cellRegion[n] = id; stack[sp++] = n; }
                }
            }
    }

    int NewRegion()
    {
        if (_freeRegions.Count > 0)
        {
            int id = _freeRegions.Pop();
            Invariant.Check(!Regions[id].Alive && Regions[id].Neighbours.Count == 0, "recycled path region still in use");
            Regions[id].Alive = true;
            return id;
        }
        Regions.Add(new Region { Alive = true });
        return Regions.Count - 1;
    }

    // 4-connectivity across sector borders (diagonal moves need both orthogonal cells, so this is exact)
    void LinkEast(int sct)
    {
        int sx = sct % SectorsX, sy = sct / SectorsX;
        if (sx == SectorsX - 1) return;
        int W = Map.Width, x = sx * Sector + Sector - 1;
        int y1 = Math.Min(Map.Height, sy * Sector + Sector);
        for (int y = sy * Sector; y < y1; y++) Link(_cellRegion[y * W + x], _cellRegion[y * W + x + 1]);
    }

    void LinkSouth(int sct)
    {
        int sx = sct % SectorsX, sy = sct / SectorsX;
        if (sy == SectorsY - 1) return;
        int W = Map.Width, y = sy * Sector + Sector - 1;
        int x1 = Math.Min(W, sx * Sector + Sector);
        for (int x = sx * Sector; x < x1; x++) Link(_cellRegion[y * W + x], _cellRegion[(y + 1) * W + x]);
    }

    void Link(int a, int b)
    {
        if (a < 0 || b < 0 || Regions[a].Neighbours.Contains(b)) return;
        Regions[a].Neighbours.Add(b);
        Regions[b].Neighbours.Add(a);
    }

    void MarkDirty(int cell)
    {
        Map.XY(cell, out int x, out int y);
        int sct = y / Sector * SectorsX + x / Sector;
        if (_sectorDirty[sct]) return;
        _sectorDirty[sct] = true;
        _dirtySectors.Add(sct);
    }

    /// <summary>Rebuilds the regions of sectors whose passability changed and relinks them with their neighbours.</summary>
    void UpdateDirtySectors()
    {
        using var _ = Log.Time("PathGrid.UpdateRegions", 20);
        foreach (int sct in _dirtySectors)
        {
            foreach (int id in _sectorRegions[sct])
            {
                var reg = Regions[id];
                foreach (int n in reg.Neighbours) Regions[n].Neighbours.Remove(id);
                reg.Neighbours.Clear();
                reg.Alive = false;
                _freeRegions.Push(id);
            }
            _sectorRegions[sct].Clear();
            BuildSector(sct);
            int sx = sct % SectorsX, sy = sct / SectorsX;
            LinkEast(sct); LinkSouth(sct);
            if (sx > 0) LinkEast(sct - 1);
            if (sy > 0) LinkSouth(sct - SectorsX);
            _sectorDirty[sct] = false;
        }
        _dirtySectors.Clear();
    }

    byte ComputeCost(int cell)
    {
        if (!Map.Walkable(cell)) return 0;
        // tree trunks are solid in 3D, so tree cells are not part of paths (pawns walk around them)
        if (Map.Plants[cell] == Plant.Oak) return 0;
        int c = Map.TerrainAt(cell).Cost;
        if (Map.Plants[cell] == Plant.BerryBush) c += 4;
        if (Map.Buildings[cell] == Building.Door) c += 4;
        return (byte)Math.Min(255, c);
    }

    /// <summary>Applies map changes (call with the drained change list).</summary>
    public void Apply(List<CellChange> changes)
    {
        foreach (var ch in changes)
        {
            if ((ch.Layer & (MapLayer.Terrain | MapLayer.Building | MapLayer.Plant)) == 0) continue;
            byte before = Cost[ch.Cell];
            byte after = ComputeCost(ch.Cell);
            Cost[ch.Cell] = after;
            if ((before == 0) != (after == 0)) { _componentsDirty = true; MarkDirty(ch.Cell); }
        }
    }

    public int Component(int cell)
    {
        if (_componentsDirty) RebuildComponents();
        return _component[cell];
    }

    public bool Reachable(int from, int to)
    {
        if (Cost[from] == 0 || Cost[to] == 0) return false;
        if (_componentsDirty) RebuildComponents();
        return _component[from] == _component[to];
    }

    void RebuildComponents()
    {
        using var _ = Log.Time("PathGrid.RebuildComponents", 60);
        Array.Fill(_component, -1);
        int W = Map.Width, H = Map.Height;
        var stack = new Stack<int>();
        int next = 0;
        for (int start = 0; start < Cost.Length; start++)
        {
            if (Cost[start] == 0 || _component[start] >= 0) continue;
            int id = next++;
            _component[start] = id;
            stack.Push(start);
            while (stack.Count > 0)
            {
                int c = stack.Pop();
                int y = c / W, x = c - y * W;
                // 4-connectivity is exact: diagonal moves require both orthogonal neighbours to be passable
                if (x > 0) Visit(c - 1);
                if (x < W - 1) Visit(c + 1);
                if (y > 0) Visit(c - W);
                if (y < H - 1) Visit(c + W);
            }
            void Visit(int n)
            {
                if (Cost[n] != 0 && _component[n] < 0) { _component[n] = id; stack.Push(n); }
            }
        }
        ComponentCount = next;
        _componentsDirty = false;
    }

    /// <summary>Line between two cell centres crosses only passable cells (supercover walk).</summary>
    public bool ClearLine(int fromCell, int toCell)
    {
        Map.XY(fromCell, out int x0, out int y0);
        Map.XY(toCell, out int x1, out int y1);
        int dx = Math.Abs(x1 - x0), dy = Math.Abs(y1 - y0);
        int sx = x1 > x0 ? 1 : -1, sy = y1 > y0 ? 1 : -1;
        int x = x0, y = y0;
        int err = dx - dy;
        int W = Map.Width;
        int baseCost = Cost[fromCell];
        while (true)
        {
            int c = y * W + x;
            if (Cost[c] == 0 || Cost[c] > baseCost + 8) return false;
            if (x == x1 && y == y1) return true;
            int e2 = 2 * err;
            if (e2 > -dy && e2 < dx)
            {
                // diagonal step: both side cells must be passable (no corner cutting)
                if (Cost[y * W + x + sx] == 0 || Cost[(y + sy) * W + x] == 0) return false;
            }
            if (e2 > -dy) { err -= dy; x += sx; }
            if (e2 < dx) { err += dx; y += sy; }
        }
    }
}

public enum PathResult : byte { Found, Unreachable, StartBlocked, TooLong }

/// <summary>
/// 8-directional A* with an octile heuristic and no corner cutting. Long searches are planned on the sector-region
/// graph first and the cell search is confined to the regions along that plan and their neighbours, so a river with
/// one ford costs a corridor instead of a flood of half the map. Each thread uses its own workspace whose
/// node arrays are reused via a generation stamp (no clearing between searches). Paths are smoothed with
/// line-of-sight checks so pawns walk natural lines instead of grid zig-zags.
/// </summary>
public sealed class Pathfinder
{
    readonly PathGrid _grid;
    readonly ConcurrentBag<Workspace> _pool = new();
    public long Searches, Expanded;

    public Pathfinder(PathGrid grid) { _grid = grid; }

    sealed class Workspace
    {
        public readonly float[] G;
        public readonly int[] Parent;
        public readonly int[] Stamp;
        public int Gen;
        public readonly MinHeap Open = new(1024);
        public Workspace(int n) { G = new float[n]; Parent = new int[n]; Stamp = new int[n]; }
        // region search (sized on demand: the region table can grow)
        public float[] RG = Array.Empty<float>();
        public int[] RParent = Array.Empty<int>(), RStamp = Array.Empty<int>(), Corridor = Array.Empty<int>();
        public int RGen, CorridorGen;
        public readonly MinHeap ROpen = new(256);
    }

    public PathResult FindPath(int start, int goal, List<Vector2> outPath, int maxExpand = 0, bool goalMayBeBlocked = false, bool doorsBlock = false)
    {
        outPath.Clear();
        var grid = _grid;
        var map = grid.Map;
        if (grid.Cost[start] == 0) return PathResult.StartBlocked;
        if (grid.Cost[goal] == 0 && !goalMayBeBlocked) return PathResult.Unreachable;
        if (!goalMayBeBlocked && !grid.Reachable(start, goal)) return PathResult.Unreachable;
        if (start == goal) { outPath.Add(map.CellCenter(goal)); return PathResult.Found; }

        if (!_pool.TryTake(out var ws)) ws = new Workspace(map.CellCount);
        try
        {
            Interlocked.Increment(ref Searches);
            int limit = maxExpand <= 0 ? map.CellCount : maxExpand;
            map.XY(start, out int sx, out int sy);
            map.XY(goal, out int gx, out int gy);
            bool far = Math.Max(Math.Abs(sx - gx), Math.Abs(sy - gy)) > 2 * PathGrid.Sector;
            if (!far || grid.Cost[goal] == 0) return Search(ws, start, goal, outPath, limit, goalMayBeBlocked, doorsBlock, false);
            PlanCorridor(ws, grid.RegionOf(start), grid.RegionOf(goal), gx + 0.5f, gy + 0.5f);
            var res = Search(ws, start, goal, outPath, limit, goalMayBeBlocked, doorsBlock, true);
            // the regions ignore closed doors: an animal's corridor may run through one, then the whole map decides
            if (res == PathResult.Unreachable && doorsBlock) res = Search(ws, start, goal, outPath, limit, goalMayBeBlocked, doorsBlock, false);
            return res;
        }
        finally { _pool.Add(ws); }
    }

    /// <summary>
    /// A* over the region graph (edges weighted by centroid distance × mean cost), then marks the regions of the plan
    /// and their neighbours as the corridor the cell search may use. Regions on the plan are connected cell by cell,
    /// so the corridor always holds a path.
    /// </summary>
    void PlanCorridor(Workspace ws, int from, int to, float gx, float gy)
    {
        var regs = _grid.Regions;
        int n = _grid.RegionCapacity;
        if (ws.RG.Length < n)
        {
            int cap = Math.Max(n, ws.RG.Length * 2);
            ws.RG = new float[cap]; ws.RParent = new int[cap]; ws.RStamp = new int[cap]; ws.Corridor = new int[cap];
            ws.RGen = 0; ws.CorridorGen = 0;
        }
        int gen = ++ws.RGen, closed = -gen;
        var G = ws.RG; var P = ws.RParent; var S = ws.RStamp;
        var open = ws.ROpen;
        open.Clear();
        G[from] = 0; P[from] = -1; S[from] = gen;
        open.Push(from, H(from));
        while (open.Count > 0)
        {
            int r = open.Pop();
            if (S[r] == closed) continue;
            S[r] = closed;
            if (r == to) break;
            var reg = regs[r];
            foreach (int m in reg.Neighbours)
            {
                if (S[m] == closed) continue;
                var rm = regs[m];
                float dx = Math.Abs(rm.X - reg.X), dy = Math.Abs(rm.Y - reg.Y);
                float ng = G[r] + (Math.Max(dx, dy) + 0.41421356f * Math.Min(dx, dy)) * (reg.MeanCost + rm.MeanCost) * 0.5f;
                if (S[m] == gen && ng >= G[m]) continue;
                G[m] = ng; P[m] = r; S[m] = gen;
                open.Push(m, ng + H(m));
            }
        }
        Invariant.Check(S[to] == closed, $"region plan found no route between connected regions {from} and {to}");
        int cg = ++ws.CorridorGen;
        var corr = ws.Corridor;
        for (int r = to; r != -1; r = P[r])
        {
            corr[r] = cg;
            foreach (int m in regs[r].Neighbours) corr[m] = cg;
        }

        float H(int r)
        {
            float dx = Math.Abs(regs[r].X - gx), dy = Math.Abs(regs[r].Y - gy);
            return (Math.Max(dx, dy) + 0.41421356f * Math.Min(dx, dy)) * 10f;
        }
    }

    PathResult Search(Workspace ws, int start, int goal, List<Vector2> outPath, int maxExpand, bool goalMayBeBlocked, bool doorsBlock, bool corridor)
    {
        var cost = _grid.Cost;
        var region = corridor ? _grid.CellRegions : null;
        var corr = ws.Corridor;
        int cg = ws.CorridorGen;
        var map = _grid.Map;
        int W = map.Width, H = map.Height;
        if (++ws.Gen == int.MaxValue) { Array.Clear(ws.Stamp); ws.Gen = 1; }
        int gen = ws.Gen;
        int closedGen = -gen; // stamp == -gen means closed
        var G = ws.G; var P = ws.Parent; var S = ws.Stamp;
        var open = ws.Open;
        open.Clear();
        map.XY(goal, out int gx, out int gy);

        G[start] = 0; P[start] = -1; S[start] = gen;
        open.Push(start, Heuristic(start));
        int expanded = 0;
        while (open.Count > 0)
        {
            int c = open.Pop();
            if (S[c] == closedGen) continue;
            S[c] = closedGen;
            if (c == goal) break;
            if (++expanded > maxExpand)
            {
                Interlocked.Add(ref Expanded, expanded);
                Log.Warn($"A* gave up after {expanded} nodes ({start}->{goal})");
                return PathResult.TooLong;
            }
            int y = c / W, x = c - y * W;
            float gc = G[c];
            for (int d = 0; d < 8; d++)
            {
                int nx = x + DX[d], ny = y + DY[d];
                if ((uint)nx >= (uint)W || (uint)ny >= (uint)H) continue;
                int n = ny * W + nx;
                int cn = cost[n];
                if (cn == 0)
                {
                    if (!(goalMayBeBlocked && n == goal)) continue;
                    cn = 10;
                }
                if (d >= 4 && (cost[y * W + nx] == 0 || cost[ny * W + x] == 0)) continue; // no corner cutting
                if (region != null && corr[region[n]] != cg) continue;
                if (doorsBlock && map.Buildings[n] == Building.Door && !map.DoorOpen[n]) continue;
                float ng = gc + cn * (d >= 4 ? 1.41421356f : 1f);
                int sn = S[n];
                if (sn == closedGen) continue;
                if (sn == gen && ng >= G[n]) continue;
                G[n] = ng; P[n] = c; S[n] = gen;
                open.Push(n, ng + Heuristic(n));
            }
        }
        Interlocked.Add(ref Expanded, expanded);
        if (S[goal] != closedGen) return PathResult.Unreachable;

        // reconstruct (goal → start), then smooth
        var cells = new List<int>(64);
        for (int c = goal; c != -1; c = P[c]) cells.Add(c);
        cells.Reverse();
        Smooth(cells, outPath, goalMayBeBlocked);
        return PathResult.Found;

        float Heuristic(int cell)
        {
            int cy = cell / W, cx = cell - cy * W;
            int dx = Math.Abs(cx - gx), dy = Math.Abs(cy - gy);
            int mn = Math.Min(dx, dy), mx = Math.Max(dx, dy);
            return 10f * (mx - mn) + 14.1421356f * mn;
        }
    }

    void Smooth(List<int> cells, List<Vector2> outPath, bool lastBlocked)
    {
        var map = _grid.Map;
        int last = lastBlocked ? cells.Count - 2 : cells.Count - 1;
        int anchor = 0;
        for (int i = 1; i <= last; i++)
        {
            if (i == last || !_grid.ClearLine(cells[anchor], cells[i + 1]))
            {
                outPath.Add(map.CellCenter(cells[i]));
                anchor = i;
            }
        }
        if (lastBlocked && cells.Count >= 2 && outPath.Count == 0) outPath.Add(map.CellCenter(cells[^2]));
    }

    static readonly int[] DX = { 1, -1, 0, 0, 1, 1, -1, -1 };
    static readonly int[] DY = { 0, 0, 1, -1, 1, -1, 1, -1 };

    /// <summary>Binary min-heap of (cell, priority) without allocations after warm-up.</summary>
    sealed class MinHeap
    {
        int[] _items; float[] _prio;
        public int Count;
        public MinHeap(int cap) { _items = new int[cap]; _prio = new float[cap]; }
        public void Clear() => Count = 0;
        public void Push(int item, float p)
        {
            if (Count == _items.Length) { Array.Resize(ref _items, Count * 2); Array.Resize(ref _prio, Count * 2); }
            int i = Count++;
            while (i > 0)
            {
                int parent = (i - 1) >> 1;
                if (_prio[parent] <= p) break;
                _items[i] = _items[parent]; _prio[i] = _prio[parent];
                i = parent;
            }
            _items[i] = item; _prio[i] = p;
        }
        public int Pop()
        {
            int top = _items[0];
            int last = --Count;
            if (last > 0)
            {
                int item = _items[last]; float p = _prio[last];
                int i = 0;
                while (true)
                {
                    int l = i * 2 + 1;
                    if (l >= last) break;
                    int r = l + 1;
                    int m = r < last && _prio[r] < _prio[l] ? r : l;
                    if (_prio[m] >= p) break;
                    _items[i] = _items[m]; _prio[i] = _prio[m];
                    i = m;
                }
                _items[i] = item; _prio[i] = p;
            }
            return top;
        }
    }
}
