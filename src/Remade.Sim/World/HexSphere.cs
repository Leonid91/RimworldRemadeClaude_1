using System;
using System.Collections.Generic;
using System.Numerics;
using Remade.Diagnostics;

namespace Remade.World;

/// <summary>
/// Geodesic hex grid on the unit sphere: the vertices of a subdivided icosahedron are the tile centres, so every
/// tile is a hexagon except 12 pentagons. Tile count = 10·f² + 2 for subdivision frequency f.
/// The triangle list doubles as a render mesh whose vertex i is tile i (smooth per-tile data interpolation).
/// </summary>
public sealed class HexSphere
{
    public readonly int Frequency;
    public readonly int TileCount;
    /// <summary>Unit-length tile centres.</summary>
    public readonly Vector3[] Centers;
    /// <summary>CSR adjacency: neighbours of tile t are NeighborList[NeighborStart[t] .. NeighborStart[t+1]), ordered counter-clockwise seen from outside.</summary>
    public readonly int[] NeighborStart;
    public readonly int[] NeighborList;
    /// <summary>Triangle indices (3 per triangle), wound counter-clockwise seen from outside.</summary>
    public readonly int[] Triangles;
    /// <summary>Polygon corners of each tile (CSR, same layout as neighbours): corner k lies between neighbour k and k+1.</summary>
    public readonly Vector3[] Corners;

    public HexSphere(int frequency)
    {
        if (frequency < 1 || frequency > 400) throw new ArgumentOutOfRangeException(nameof(frequency), frequency, "1..400");
        Frequency = frequency;
        TileCount = 10 * frequency * frequency + 2;

        float t = (1f + MathF.Sqrt(5f)) / 2f;
        var ico = new Vector3[]
        {
            new(-1, t, 0), new(1, t, 0), new(-1, -t, 0), new(1, -t, 0),
            new(0, -1, t), new(0, 1, t), new(0, -1, -t), new(0, 1, -t),
            new(t, 0, -1), new(t, 0, 1), new(-t, 0, -1), new(-t, 0, 1),
        };
        for (int i = 0; i < ico.Length; i++) ico[i] = Vector3.Normalize(ico[i]);
        int[] faces =
        {
            0,11,5, 0,5,1, 0,1,7, 0,7,10, 0,10,11,
            1,5,9, 5,11,4, 11,10,2, 10,7,6, 7,1,8,
            3,9,4, 3,4,2, 3,2,6, 3,6,8, 3,8,9,
            4,9,5, 2,4,11, 6,2,10, 8,6,7, 9,8,1,
        };

        var centers = new List<Vector3>(TileCount);
        var tris = new List<int>(20 * frequency * frequency * 3);
        // Shared vertices: icosahedron corners and edge points are keyed so each appears once.
        var cornerIndex = new int[12];
        for (int i = 0; i < 12; i++) { cornerIndex[i] = centers.Count; centers.Add(ico[i]); }
        var edgePoints = new Dictionary<long, int[]>();

        int f = frequency;
        var grid = new int[(f + 1) * (f + 2) / 2];
        for (int face = 0; face < 20; face++)
        {
            int a = faces[face * 3], b = faces[face * 3 + 1], c = faces[face * 3 + 2];
            // grid point (i, j): i steps from a toward b, j from a toward c, i + j <= f
            int Idx(int i, int j) => (i + j) * (i + j + 1) / 2 + j;
            for (int s = 0; s <= f; s++)
                for (int j = 0; j <= s; j++)
                {
                    int i = s - j;
                    int id;
                    if (i == 0 && j == 0) id = cornerIndex[a];
                    else if (i == f && j == 0) id = cornerIndex[b];
                    else if (i == 0 && j == f) id = cornerIndex[c];
                    else if (j == 0) id = EdgePoint(a, b, i);
                    else if (i == 0) id = EdgePoint(a, c, j);
                    else if (i + j == f) id = EdgePoint(b, c, j);
                    else
                    {
                        id = centers.Count;
                        centers.Add(Point(ico[a], ico[b], ico[c], i, j, f));
                    }
                    grid[Idx(i, j)] = id;
                }
            for (int s = 0; s < f; s++)
                for (int j = 0; j <= s; j++)
                {
                    int i = s - j;
                    int p0 = grid[Idx(i, j)], p1 = grid[Idx(i + 1, j)], p2 = grid[Idx(i, j + 1)];
                    tris.Add(p0); tris.Add(p1); tris.Add(p2);
                    if (i + j + 2 <= f)
                    {
                        int p3 = grid[Idx(i + 1, j + 1)];
                        tris.Add(p1); tris.Add(p3); tris.Add(p2);
                    }
                }

            int EdgePoint(int u, int v, int k)
            {
                // k-th point (1..f-1) from u to v; stored canonically from min(u,v)
                int lo = Math.Min(u, v), hi = Math.Max(u, v);
                long key = lo * 16 + hi;
                if (!edgePoints.TryGetValue(key, out var arr))
                {
                    arr = new int[f + 1];
                    for (int q = 1; q < f; q++)
                    {
                        arr[q] = centers.Count;
                        centers.Add(Point(ico[lo], ico[hi], ico[hi], q, 0, f));
                    }
                    edgePoints[key] = arr;
                }
                return u == lo ? arr[k] : arr[f - k];
            }
        }

        Invariant.Check(centers.Count == TileCount, $"HexSphere: built {centers.Count} tiles, expected {TileCount}");
        Centers = centers.ToArray();
        Triangles = tris.ToArray();

        // Make all triangles counter-clockwise seen from outside.
        for (int k = 0; k < Triangles.Length; k += 3)
        {
            Vector3 p0 = Centers[Triangles[k]], p1 = Centers[Triangles[k + 1]], p2 = Centers[Triangles[k + 2]];
            if (Vector3.Dot(Vector3.Cross(p1 - p0, p2 - p0), p0 + p1 + p2) < 0)
                (Triangles[k + 1], Triangles[k + 2]) = (Triangles[k + 2], Triangles[k + 1]);
        }

        // Adjacency from triangles, then order neighbours around each tile.
        var adj = new List<int>[TileCount];
        for (int i = 0; i < TileCount; i++) adj[i] = new List<int>(6);
        for (int k = 0; k < Triangles.Length; k += 3)
            for (int e = 0; e < 3; e++)
            {
                int u = Triangles[k + e], v = Triangles[k + (e + 1) % 3];
                if (!adj[u].Contains(v)) adj[u].Add(v);
                if (!adj[v].Contains(u)) adj[v].Add(u);
            }

        NeighborStart = new int[TileCount + 1];
        for (int i = 0; i < TileCount; i++)
        {
            int n = adj[i].Count;
            Invariant.Check(n == 5 || n == 6, $"HexSphere: tile {i} has {n} neighbours");
            NeighborStart[i + 1] = NeighborStart[i] + n;
        }
        NeighborList = new int[NeighborStart[TileCount]];
        Corners = new Vector3[NeighborStart[TileCount]];
        for (int i = 0; i < TileCount; i++)
        {
            Vector3 c = Centers[i];
            Vector3 east = Vector3.Normalize(Vector3.Cross(MathF.Abs(c.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX, c));
            Vector3 north = Vector3.Cross(c, east);
            var list = adj[i];
            list.Sort((p, q) =>
            {
                Vector3 dp = Centers[p] - c, dq = Centers[q] - c;
                float ap = MathF.Atan2(Vector3.Dot(dp, north), Vector3.Dot(dp, east));
                float aq = MathF.Atan2(Vector3.Dot(dq, north), Vector3.Dot(dq, east));
                return ap.CompareTo(aq);
            });
            int s0 = NeighborStart[i];
            for (int k = 0; k < list.Count; k++) NeighborList[s0 + k] = list[k];
            for (int k = 0; k < list.Count; k++)
            {
                Vector3 n0 = Centers[list[k]], n1 = Centers[list[(k + 1) % list.Count]];
                Corners[s0 + k] = Vector3.Normalize(c + n0 + n1);
            }
        }
    }

    static Vector3 Point(Vector3 a, Vector3 b, Vector3 c, int i, int j, int f)
    {
        // Barycentric point on the flat face, projected onto the sphere.
        float u = i / (float)f, v = j / (float)f;
        return Vector3.Normalize(a + (b - a) * u + (c - a) * v);
    }

    public ReadOnlySpan<int> Neighbors(int tile) => new(NeighborList, NeighborStart[tile], NeighborStart[tile + 1] - NeighborStart[tile]);
    public ReadOnlySpan<Vector3> TileCorners(int tile) => new(Corners, NeighborStart[tile], NeighborStart[tile + 1] - NeighborStart[tile]);

    /// <summary>Latitude in degrees (+90 north pole = +Y).</summary>
    public float Latitude(int tile) => MathF.Asin(Math.Clamp(Centers[tile].Y, -1f, 1f)) * (180f / MathF.PI);
    public float Longitude(int tile) => MathF.Atan2(Centers[tile].X, Centers[tile].Z) * (180f / MathF.PI);

    /// <summary>Approximate angular distance between neighbouring tile centres (radians).</summary>
    public float TileAngle => 1.1f / Frequency;

    /// <summary>
    /// Nearest tile to a unit direction. Greedy walk on the adjacency graph (O(sqrt N)), starting from a hint tile.
    /// </summary>
    public int Nearest(Vector3 dir, int hint = 0)
    {
        dir = Vector3.Normalize(dir);
        int cur = (uint)hint < (uint)TileCount ? hint : 0;
        float best = Vector3.Dot(Centers[cur], dir);
        while (true)
        {
            int next = cur;
            foreach (int n in Neighbors(cur))
            {
                float d = Vector3.Dot(Centers[n], dir);
                if (d > best) { best = d; next = n; }
            }
            if (next == cur) return cur;
            cur = next;
        }
    }

    /// <summary>Local tangent frame at a tile: east and north unit vectors (north toward +Y pole).</summary>
    public void Frame(int tile, out Vector3 east, out Vector3 north)
    {
        Vector3 c = Centers[tile];
        Vector3 up = MathF.Abs(c.Y) > 0.9999f ? Vector3.UnitZ : Vector3.UnitY;
        east = Vector3.Normalize(Vector3.Cross(up, c));
        north = Vector3.Cross(c, east);
    }
}
