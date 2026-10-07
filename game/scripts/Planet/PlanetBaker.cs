using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Remade.Core;
using Remade.Diagnostics;
using Remade.World;

namespace Remade.Game.Planet3D;

/// <summary>
/// Bakes the globe's cubemap faces on the CPU (parallel, engine-independent so it can run on a worker thread):
/// albedo with one flat colour per hexagonal tile (its biome; a map is always a single biome) and crisp,
/// anti-aliased tile borders, plus rivers; object-space normals (gentle relief) with elevation in alpha; and
/// low-resolution overlay faces from the live climate arrays.
/// Face order and orientation follow the cubemap convention (+X, −X, +Y, −Y, +Z, −Z).
/// </summary>
public sealed class PlanetBaker
{
    public readonly int Size;
    public readonly byte[][] Albedo = new byte[6][];   // RGBA8
    public readonly byte[][] Normal = new byte[6][];   // RGBA8: normal xyz, elevation
    readonly float[][] _elev = new float[6][];
    readonly Planet _p;

    /// <summary>True: the menu backdrop's natural look (smoothly blended biomes, sea depth, snow caps).
    /// False: the map look of the landing-site and in-game globes (one flat colour per tile, crisp borders).</summary>
    public readonly bool Natural;

    public PlanetBaker(Planet planet, int size, bool natural = false)
    {
        _p = planet;
        Size = size;
        Natural = natural;
    }

    /// <summary>Direction through the centre of texel (x, y) of a cube face.</summary>
    public static Vector3 FaceDir(int face, float u, float v)
    {
        // u, v in [-1, 1]; v grows downward in the image
        return face switch
        {
            0 => Vector3.Normalize(new Vector3(1, -v, -u)),
            1 => Vector3.Normalize(new Vector3(-1, -v, u)),
            2 => Vector3.Normalize(new Vector3(u, 1, v)),
            3 => Vector3.Normalize(new Vector3(u, -1, -v)),
            4 => Vector3.Normalize(new Vector3(u, -v, 1)),
            _ => Vector3.Normalize(new Vector3(-u, -v, -1)),
        };
    }

    /// <summary>Inverse of <see cref="FaceDir"/>: face and (u, v) in [-1, 1] for a direction.</summary>
    public static int DirFace(Vector3 d, out float u, out float v)
    {
        float ax = MathF.Abs(d.X), ay = MathF.Abs(d.Y), az = MathF.Abs(d.Z);
        if (ax >= ay && ax >= az)
        {
            if (d.X > 0) { u = -d.Z / ax; v = -d.Y / ax; return 0; }
            u = d.Z / ax; v = -d.Y / ax; return 1;
        }
        if (ay >= az)
        {
            if (d.Y > 0) { u = d.X / ay; v = d.Z / ay; return 2; }
            u = d.X / ay; v = -d.Z / ay; return 3;
        }
        if (d.Z > 0) { u = d.X / az; v = -d.Y / az; return 4; }
        u = -d.X / az; v = -d.Y / az; return 5;
    }

    static readonly Vector3[] BiomeColors = BuildBiomeColors();

    static Vector3 C(int r, int g, int b) => new(r / 255f, g / 255f, b / 255f);

    /// <summary>Flat, clearly distinct biome colours (sRGB), RimWorld world-map style. Also used by the legend.</summary>
    public static Vector3 BiomeColor(Biome b) => BiomeColors[(int)b];

    static Vector3[] BuildBiomeColors()
    {
        var c = new Vector3[Enum.GetValues<Biome>().Length];
        c[(int)Biome.Ocean] = C(32, 60, 104);
        c[(int)Biome.Lake] = C(70, 130, 186);
        c[(int)Biome.IceSheet] = C(238, 242, 247);
        c[(int)Biome.Tundra] = C(150, 152, 128);
        c[(int)Biome.BorealForest] = C(52, 96, 82);
        c[(int)Biome.TemperateForest] = C(78, 132, 50);
        c[(int)Biome.Grassland] = C(170, 184, 92);
        c[(int)Biome.AridShrubland] = C(186, 150, 96);
        c[(int)Biome.Desert] = C(232, 206, 146);
        c[(int)Biome.TropicalRainforest] = C(22, 92, 46);
        c[(int)Biome.Savanna] = C(206, 172, 70);
        return c;
    }

    static readonly Vector3[] NaturalColors = BuildNaturalColors();

    static Vector3[] BuildNaturalColors()
    {
        var c = new Vector3[Enum.GetValues<Biome>().Length];
        c[(int)Biome.Ocean] = C(14, 40, 86);
        c[(int)Biome.Lake] = C(30, 70, 110);
        c[(int)Biome.IceSheet] = C(232, 238, 244);
        c[(int)Biome.Tundra] = C(138, 134, 108);
        c[(int)Biome.BorealForest] = C(44, 74, 52);
        c[(int)Biome.TemperateForest] = C(58, 104, 46);
        c[(int)Biome.Grassland] = C(128, 146, 72);
        c[(int)Biome.AridShrubland] = C(160, 138, 92);
        c[(int)Biome.Desert] = C(214, 184, 132);
        c[(int)Biome.TropicalRainforest] = C(28, 82, 38);
        c[(int)Biome.Savanna] = C(156, 150, 78);
        return c;
    }

    void BakeNatural()
    {
        var noise = new Noise(_p.Params.Seed + 909);
        float[] meanTemp = _p.MeanTemp, precip = _p.AnnualPrecip, elev = _p.Elevation;
        int n = Size;
        Parallel.For(0, 6 * n, row =>
        {
            int face = row / n, y = row % n;
            int hint = 0;
            var alb = Albedo[face];
            var el = _elev[face];
            for (int x = 0; x < n; x++)
            {
                float u = (x + 0.5f) / n * 2f - 1f, v = (y + 0.5f) / n * 2f - 1f;
                Vector3 d = FaceDir(face, u, v);
                float e = _p.ElevationAt(d);
                hint = _p.Interpolate(d, hint, meanTemp, precip, elev, out float t, out float pr, out float tileElev);
                float tLocal = t - 0.0065f * (MathF.Max(0, e) - MathF.Max(0, tileElev));
                Vector3 col = SurfaceColor(e, tLocal, pr, d, noise);
                int i = (y * n + x) * 4;
                alb[i] = ToByte(col.X); alb[i + 1] = ToByte(col.Y); alb[i + 2] = ToByte(col.Z); alb[i + 3] = 255;
                el[y * n + x] = e;
            }
        });
    }

    Vector3 SurfaceColor(float e, float t, float p, Vector3 d, Noise noise)
    {
        float grain = noise.Fbm(d.X * 40f, d.Y * 40f, d.Z * 40f, 3) * 0.5f + 0.5f;
        if (e < 0)
        {
            float depth = -e;
            Vector3 shallow = C(44, 128, 150), mid = C(20, 64, 120), deep = C(8, 24, 64);
            Vector3 c = depth < 250f ? Vector3.Lerp(shallow, mid, depth / 250f) : Vector3.Lerp(mid, deep, Math.Clamp((depth - 250f) / 3500f, 0f, 1f));
            c *= 0.92f + grain * 0.12f;
            // sea ice in polar waters
            float ice = Math.Clamp((-8f - t) / 6f, 0f, 1f);
            return Vector3.Lerp(c, C(220, 232, 240) * (0.92f + grain * 0.1f), ice * 0.9f);
        }
        // soft biome classification: average the colours of jittered (T, P) samples
        Vector3 sum = Vector3.Zero;
        for (int a = -1; a <= 1; a++)
            for (int b = -1; b <= 1; b++)
            {
                var biome = World.Planet.Classify(e, t + a * 2.2f, p * (1f + b * 0.14f));
                sum += NaturalColors[(int)biome];
            }
        Vector3 col = sum / 9f;
        col *= 0.88f + grain * 0.22f;
        // beaches
        if (e < 25f && t > 2f) col = Vector3.Lerp(C(196, 182, 140), col, Math.Clamp(e / 25f, 0f, 1f));
        // bare rock and snow with altitude
        float rock = Math.Clamp((e - 1400f) / 1800f, 0f, 1f);
        col = Vector3.Lerp(col, C(112, 102, 92) * (0.85f + grain * 0.3f), rock * 0.85f);
        float snow = Math.Clamp((-4f - t) / 5f, 0f, 1f) * Math.Clamp(e / 400f + 0.4f, 0f, 1f);
        col = Vector3.Lerp(col, C(240, 242, 248), snow);
        return col;
    }

    public void Bake()
    {
        using var _ = Log.Time($"PlanetBaker.Bake {Size}x{Size}x6", 5000);
        for (int f = 0; f < 6; f++)
        {
            Albedo[f] = new byte[Size * Size * 4];
            Normal[f] = new byte[Size * Size * 4];
            _elev[f] = new float[Size * Size];
        }
        if (Natural)
        {
            BakeNatural();
            BakeNormals();
            DrawRivers();
            return;
        }
        int n = Size;
        var g = _p.Grid;
        // half a texel, in radians (faces span 90°; texels are smallest at face corners, largest at the centre)
        float halfTexel = MathF.PI / 2f / n * 0.5f;
        Parallel.For(0, 6 * n, row =>
        {
            int face = row / n, y = row % n;
            int hint = 0;
            var alb = Albedo[face];
            var el = _elev[face];
            for (int x = 0; x < n; x++)
            {
                float u = (x + 0.5f) / n * 2f - 1f, v = (y + 0.5f) / n * 2f - 1f;
                Vector3 d = FaceDir(face, u, v);
                int t = g.Nearest(d, hint);
                hint = t;
                Vector3 col = TileColor(t);
                // the nearest neighbour tile: blend across the shared edge over one texel (anti-aliasing),
                // and darken the edge a little where two different biomes meet so borders read clearly
                Vector3 ct = g.Centers[t];
                int best = -1; float bestDot = -2f;
                foreach (int nb in g.Neighbors(t))
                {
                    float dt = Vector3.Dot(g.Centers[nb], d);
                    if (dt > bestDot) { bestDot = dt; best = nb; }
                }
                Vector3 diff = ct - g.Centers[best];
                float edgeDist = Vector3.Dot(d, diff) / diff.Length(); // ≈ radians from the bisector (≥ 0 on our side)
                if (KindOf(best) != KindOf(t))
                {
                    float k = Math.Clamp(edgeDist / halfTexel, 0f, 1f);       // 0 on the edge, 1 a half texel inside
                    Vector3 other = TileColor(best);
                    Vector3 mixed = Vector3.Lerp((col + other) * 0.5f, col, k);
                    float line = Math.Clamp(1f - edgeDist / (halfTexel * 2.2f), 0f, 1f);
                    col = mixed * (1f - 0.22f * line);
                }
                float e = _p.ElevationAt(d);
                // keep the texel's land/water sign identical to its tile (lakes, estuaries, coasts follow the hexes)
                bool water = _p.Water[t] != WaterBody.None;
                if (water && e >= 0) e = -1f;
                if (!water && e < 0) e = 1f;
                int i = (y * n + x) * 4;
                alb[i] = ToByte(col.X); alb[i + 1] = ToByte(col.Y); alb[i + 2] = ToByte(col.Z); alb[i + 3] = 255;
                el[y * n + x] = e;
            }
        });
        BakeNormals();
        DrawRivers();
    }

    static byte ToByte(float v) => (byte)Math.Clamp((int)(v * 255f + 0.5f), 0, 255);

    Vector3 TileColor(int t) => BiomeColors[(int)_p.Biomes[t]];

    /// <summary>What a tile shows: its biome (ocean and lakes are biomes too).</summary>
    int KindOf(int t) => (int)_p.Biomes[t];

    void BakeNormals()
    {
        int n = Size;
        float texelAngle = 2f / n; // approx. radians per texel near the face centre
        Parallel.For(0, 6 * n, row =>
        {
            int face = row / n, y = row % n;
            var el = _elev[face];
            var nm = Normal[face];
            for (int x = 0; x < n; x++)
            {
                float u = (x + 0.5f) / n * 2f - 1f, v = (y + 0.5f) / n * 2f - 1f;
                Vector3 d = FaceDir(face, u, v);
                float e = el[y * n + x];
                // central differences over a 2-texel baseline (smoother than adjacent texels)
                float ex0 = Land(el, x - 2, y, n), ex1 = Land(el, x + 2, y, n);
                float ey0 = Land(el, x, y - 2, n), ey1 = Land(el, x, y + 2, n);
                Vector3 du = FaceDir(face, u + 1f / n, v) - FaceDir(face, u - 1f / n, v);
                Vector3 dv = FaceDir(face, u, v + 1f / n) - FaceDir(face, u, v - 1f / n);
                du = Vector3.Normalize(du); dv = Vector3.Normalize(dv);
                // exaggerated relief so ranges read clearly from orbit
                // natural look: ≈15× real relief so ranges read from orbit; map look: ≈2×, biome colours stay flat
                float k = Natural ? 1f / 420000f : 1f / 3000000f;
                float gx = (ex1 - ex0) * k / (texelAngle * 2f), gy = (ey1 - ey0) * k / (texelAngle * 2f);
                Vector3 normal = Vector3.Normalize(d - du * gx - dv * gy);
                int i = (y * n + x) * 4;
                nm[i] = ToByte(normal.X * 0.5f + 0.5f);
                nm[i + 1] = ToByte(normal.Y * 0.5f + 0.5f);
                nm[i + 2] = ToByte(normal.Z * 0.5f + 0.5f);
                nm[i + 3] = ToByte(Math.Clamp(0.5f + e / 13000f, 0f, 1f));
            }
        });
    }

    static float Land(float[] el, int x, int y, int n) => MathF.Max(0, el[Math.Clamp(y, 0, n - 1) * n + Math.Clamp(x, 0, n - 1)]);

    float[][] _riverCov;

    /// <summary>
    /// Rasterises every river (source → sea or lake) as a smoothed, anti-aliased line whose width grows downstream:
    /// strokes accumulate coverage per texel, then one pass blends the river colour in. Water tiles are left alone.
    /// </summary>
    void DrawRivers()
    {
        var g = _p.Grid;
        _riverCov = new float[6][];
        for (int f = 0; f < 6; f++) _riverCov[f] = new float[Size * Size];
        var hasInflow = new bool[_p.TileCount];
        for (int t = 0; t < _p.TileCount; t++)
        {
            int d = _p.Downstream[t];
            if (_p.RiverSize[t] > 0 && d >= 0) hasInflow[d] = true;
        }
        var drawn = new bool[_p.TileCount];
        int chains = 0;
        for (int start = 0; start < _p.TileCount; start++)
        {
            if (_p.RiverSize[start] == 0 || hasInflow[start] || _p.Water[start] != WaterBody.None) continue;
            // walk downstream until water (or a tile already drawn: join it and stop)
            var pts = new List<Vector3>();
            var widths = new List<float>();
            int t = start;
            while (t >= 0)
            {
                pts.Add(g.Centers[t]);
                widths.Add(_p.Water[t] != WaterBody.None ? widths[^1] : RiverWidth(_p.RiverSize[t], _p.Estuary[t]));
                if (_p.Water[t] != WaterBody.None || drawn[t]) break;
                drawn[t] = true;
                t = _p.Downstream[t];
            }
            if (pts.Count < 2) continue;
            for (int it = 0; it < 4; it++) Chaikin(pts, widths);
            for (int k = 0; k + 1 < pts.Count; k++) StrokeSegment(pts[k], pts[k + 1], widths[k], widths[k + 1]);
            chains++;
        }
        Vector3 riverCol = Natural ? C(46, 104, 160) : BiomeColors[(int)Biome.Lake];
        int n = Size;
        Parallel.For(0, 6 * n, row =>
        {
            int face = row / n, y = row % n;
            var alb = Albedo[face];
            var cov = _riverCov[face];
            var el = _elev[face];
            for (int x = 0; x < n; x++)
            {
                float a = MathF.Min(1f, cov[y * n + x]);
                if (a <= 0 || el[y * n + x] < 0) continue;
                int i = (y * n + x) * 4;
                alb[i] = ToByte(alb[i] / 255f + (riverCol.X - alb[i] / 255f) * a);
                alb[i + 1] = ToByte(alb[i + 1] / 255f + (riverCol.Y - alb[i + 1] / 255f) * a);
                alb[i + 2] = ToByte(alb[i + 2] / 255f + (riverCol.Z - alb[i + 2] / 255f) * a);
            }
        });
        _riverCov = null;
        Log.Info($"Globe rivers drawn: {chains} river chains");
    }

    float RiverWidth(byte size, bool estuary) => (estuary ? 4.2f : size switch { 1 => 1.1f, 2 => 1.8f, _ => 2.6f }) * Size / 1024f + 0.5f;

    static void Chaikin(List<Vector3> pts, List<float> w)
    {
        var np = new List<Vector3> { pts[0] };
        var nw = new List<float> { w[0] };
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            np.Add(Vector3.Normalize(Vector3.Lerp(pts[i], pts[i + 1], 0.25f)));
            np.Add(Vector3.Normalize(Vector3.Lerp(pts[i], pts[i + 1], 0.75f)));
            nw.Add(w[i] * 0.75f + w[i + 1] * 0.25f);
            nw.Add(w[i] * 0.25f + w[i + 1] * 0.75f);
        }
        np.Add(pts[^1]); nw.Add(w[^1]);
        pts.Clear(); pts.AddRange(np);
        w.Clear(); w.AddRange(nw);
    }

    void StrokeSegment(Vector3 a, Vector3 b, float wa, float wb)
    {
        float texelAngle = 2f / Size * 0.85f;
        float ang = MathF.Acos(Math.Clamp(Vector3.Dot(a, b), -1f, 1f));
        int steps = Math.Max(1, (int)(ang / (texelAngle * 0.3f)));
        for (int s = 0; s <= steps; s++)
        {
            float t = s / (float)steps;
            Vector3 d = Vector3.Normalize(Vector3.Lerp(a, b, t));
            float w = wa + (wb - wa) * t;
            Splat(d, w * 0.5f);
        }
    }

    void Splat(Vector3 d, float radiusPx)
    {
        int face = DirFace(d, out float u, out float v);
        float px = (u + 1f) * 0.5f * Size - 0.5f, py = (v + 1f) * 0.5f * Size - 0.5f;
        int r = (int)MathF.Ceiling(radiusPx + 1);
        var cov = _riverCov[face];
        for (int y = (int)py - r; y <= (int)py + r + 1; y++)
            for (int x = (int)px - r; x <= (int)px + r + 1; x++)
            {
                if ((uint)x >= (uint)Size || (uint)y >= (uint)Size) continue;
                float dist = MathF.Sqrt((x - px) * (x - px) + (y - py) * (y - py));
                float c = Math.Clamp(radiusPx + 0.5f - dist, 0f, 1f);
                int i = y * Size + x;
                if (c > cov[i]) cov[i] = c;
            }
    }

    // ------------------------------------------------------------------ overlays

    public enum OverlayKind { None, Temperature, Elevation, Precipitation }

    /// <summary>Bakes low-resolution overlay faces (R8) from the climate simulation's current per-tile values.</summary>
    public static byte[][] BakeOverlay(Planet p, OverlayKind kind, int size)
    {
        if (kind == OverlayKind.None || kind == OverlayKind.Elevation) throw new ArgumentException("overlay kind has no climate data", nameof(kind));
        float[] src = kind == OverlayKind.Temperature ? (float[])p.Climate.Temperature.Clone() : (float[])p.Climate.Precipitation.Clone();
        var faces = new byte[6][];
        for (int f = 0; f < 6; f++) faces[f] = new byte[size * size];
        Parallel.For(0, 6 * size, row =>
        {
            int face = row / size, y = row % size;
            int hint = 0;
            for (int x = 0; x < size; x++)
            {
                float u = (x + 0.5f) / size * 2f - 1f, v = (y + 0.5f) / size * 2f - 1f;
                hint = p.Interpolate(FaceDir(face, u, v), hint, src, out float val);
                float norm = kind == OverlayKind.Temperature ? (val + 45f) / 90f : MathF.Sqrt(MathF.Max(0f, val) / 30f);
                faces[face][y * size + x] = ToByte(Math.Clamp(norm, 0f, 1f));
            }
        });
        return faces;
    }
}
