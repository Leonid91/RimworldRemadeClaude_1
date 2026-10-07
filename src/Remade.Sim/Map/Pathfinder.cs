using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using Remade.Diagnostics;

namespace Remade.Map;

/// <summary>
/// Movement costs and connectivity of a map. Cost 0 = impassable. Connected components ("regions") let
/// unreachable requests fail in O(1) instead of flooding the whole map.
/// </summary>
public sealed class PathGrid
{
    public readonly LocalMap Map;
    public readonly byte[] Cost;

    int[] _component;
    bool _componentsDirty = true;
    public int ComponentCount { get; private set; }

    public PathGrid(LocalMap map)
    {
        Map = map;
        Cost = new byte[map.CellCount];
        for (int i = 0; i < Cost.Length; i++) Cost[i] = ComputeCost(i);
        _component = new int[map.CellCount];
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
            if ((before == 0) != (after == 0)) _componentsDirty = true;
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
/// 8-directional A* with an octile heuristic and no corner cutting. Each thread uses its own workspace whose
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
            return Search(ws, start, goal, outPath, maxExpand <= 0 ? map.CellCount : maxExpand, goalMayBeBlocked, doorsBlock);
        }
        finally { _pool.Add(ws); }
    }

    PathResult Search(Workspace ws, int start, int goal, List<Vector2> outPath, int maxExpand, bool goalMayBeBlocked, bool doorsBlock)
    {
        var cost = _grid.Cost;
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
