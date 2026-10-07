using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Remade.Core;
using Remade.Diagnostics;

namespace Remade.World;

public enum Biome : byte
{
    Ocean, IceSheet, Tundra, BorealForest, TemperateForest, Grassland, AridShrubland, Desert, TropicalRainforest, Savanna, Lake,
}

/// <summary>What kind of water a tile is: open sea (large connected body) or an inland lake.</summary>
public enum WaterBody : byte { None, Ocean, Lake }

public enum Hilliness : byte { Flat, SmallHills, LargeHills, Mountainous, Impassable }

public sealed class WorldParams
{
    public int Seed = 1;
    /// <summary>Hex-sphere subdivision frequency; tiles = 10 f² + 2.</summary>
    public int Frequency = 64;
    /// <summary>Fraction of tiles under the sea.</summary>
    public float OceanCoverage = 0.62f;
    public float TemperatureOffset = 0f;
    public float RainfallScale = 1f;

    public void Validate()
    {
        if (Frequency < 8 || Frequency > 256) throw new ArgumentOutOfRangeException(nameof(Frequency), Frequency, "8..256");
        if (OceanCoverage < 0.05f || OceanCoverage > 0.95f) throw new ArgumentOutOfRangeException(nameof(OceanCoverage), OceanCoverage, "0.05..0.95");
        if (RainfallScale <= 0f || RainfallScale > 5f) throw new ArgumentOutOfRangeException(nameof(RainfallScale), RainfallScale, "(0..5]");
    }
}

public static class BiomeInfo
{
    public static string Label(Biome b) => b switch
    {
        Biome.Ocean => "Ocean",
        Biome.IceSheet => "Ice sheet",
        Biome.Tundra => "Tundra",
        Biome.BorealForest => "Boreal forest",
        Biome.TemperateForest => "Temperate forest",
        Biome.Grassland => "Grassland",
        Biome.AridShrubland => "Arid shrubland",
        Biome.Desert => "Desert",
        Biome.TropicalRainforest => "Tropical rainforest",
        Biome.Savanna => "Savanna",
        Biome.Lake => "Lake",
        _ => throw new ArgumentOutOfRangeException(nameof(b), b, null),
    };

    public static string Description(Biome b) => b switch
    {
        Biome.TemperateForest => "Mild seasons, fertile soil and dense oak woods. Deer roam the clearings.",
        Biome.Ocean => "Open sea.",
        Biome.IceSheet => "An endless plain of ice. Nothing grows here.",
        Biome.Tundra => "Frozen, windswept lowlands with mosses and lichens.",
        Biome.BorealForest => "Cold coniferous forest with long winters.",
        Biome.Grassland => "Open steppe of tall grasses.",
        Biome.AridShrubland => "Dry scrub and hardy bushes.",
        Biome.Desert => "Sand, stone and scorching days.",
        Biome.TropicalRainforest => "Hot, humid, teeming jungle.",
        Biome.Savanna => "Warm grassland with scattered trees.",
        Biome.Lake => "Fresh inland water.",
        _ => "",
    };

    /// <summary>Only the temperate forest is playable in this version; other biomes are shown on the planet only.</summary>
    public static bool Playable(Biome b) => b == Biome.TemperateForest;

    public static string HillLabel(Hilliness h) => h switch
    {
        Hilliness.Flat => "Flat",
        Hilliness.SmallHills => "Small hills",
        Hilliness.LargeHills => "Large hills",
        Hilliness.Mountainous => "Mountainous",
        Hilliness.Impassable => "Impassable mountains",
        _ => throw new ArgumentOutOfRangeException(nameof(h), h, null),
    };

    public static string RiverLabel(byte size) => size switch { 0 => "None", 1 => "Creek", 2 => "River", 3 => "Large river", _ => "Huge river" };
}

/// <summary>
/// A generated planet: static geography per tile (elevation, climate normals, biome, rivers) plus the dynamic
/// climate state (<see cref="Climate"/>) that every playable map reads its natural conditions from.
/// </summary>
public sealed class Planet
{
    public const float RadiusKm = 6371f;

    public readonly WorldParams Params;
    public readonly HexSphere Grid;
    public int TileCount => Grid.TileCount;

    // static geography (SoA)
    public readonly float[] Elevation;      // metres above sea level (negative = sea floor)
    public readonly float[] MeanTemp;       // annual mean °C at the tile's elevation
    public readonly float[] SeasonAmp;      // half of the summer-winter difference, °C
    public readonly float[] AnnualPrecip;   // mm / year
    public readonly Biome[] Biomes;
    public readonly Hilliness[] Hills;
    public readonly int[] Downstream;       // next tile toward the sea along the drainage, -1 for sea tiles
    public readonly float[] Flow;           // accumulated drainage (relative units)
    public readonly byte[] RiverSize;       // 0 none, 1 creek, 2 river, 3 large river
    public readonly bool[] Coast;           // land next to the ocean
    public readonly bool[] LakeShore;       // land next to a lake
    public readonly bool[] Estuary;         // a river mouth widening into the sea (land tile, playable)
    public readonly WaterBody[] Water;
    public readonly float[] Ruggedness;     // 0..1 mountain-ridge strength (used for local relief)

    internal readonly Noise ElevNoise, DetailNoise, RidgeNoise, ClimateNoise;
    internal float SeaLevelRaw;

    public readonly Climate Climate;

    public Planet(WorldParams p)
    {
        p.Validate();
        Params = p;
        using var _t = Log.Time($"Planet generation (seed {p.Seed}, f={p.Frequency})", 4000);
        Log.Info($"Generating planet: seed={p.Seed} frequency={p.Frequency} ocean={p.OceanCoverage:F2}");
        Log.WorldSeed = p.Seed;

        ElevNoise = new Noise(p.Seed);
        DetailNoise = new Noise(p.Seed + 101);
        RidgeNoise = new Noise(p.Seed + 202);
        ClimateNoise = new Noise(p.Seed + 303);

        Grid = new HexSphere(p.Frequency);
        int n = Grid.TileCount;
        Elevation = new float[n]; MeanTemp = new float[n]; SeasonAmp = new float[n]; AnnualPrecip = new float[n];
        Biomes = new Biome[n]; Hills = new Hilliness[n]; Downstream = new int[n]; Flow = new float[n];
        RiverSize = new byte[n]; Coast = new bool[n]; Ruggedness = new float[n];
        LakeShore = new bool[n]; Estuary = new bool[n]; Water = new WaterBody[n];

        GenerateElevation();
        ClassifyWater();
        GenerateClimateNormals();
        GenerateRivers();
        MakeEstuaries();
        UpdateShores();
        ClassifyBiomes();
        Climate = new Climate(this);
        Log.Info($"Planet ready: {n} tiles, land {LandFraction():P0}, rivers {CountRivers()} tiles, temperate {Count(Biome.TemperateForest)} tiles");
    }

    // ---------------------------------------------------------------- elevation

    /// <summary>Raw continuous elevation in noise units at a unit direction (sea level not yet subtracted).</summary>
    public float RawElevation(Vector3 d, out float ridge)
    {
        // domain warp for organic continents
        float wx = ElevNoise.Get(d.X * 1.3f + 11f, d.Y * 1.3f, d.Z * 1.3f) * 0.35f;
        float wy = ElevNoise.Get(d.X * 1.3f, d.Y * 1.3f + 23f, d.Z * 1.3f) * 0.35f;
        float wz = ElevNoise.Get(d.X * 1.3f, d.Y * 1.3f, d.Z * 1.3f + 37f) * 0.35f;
        Vector3 q = d + new Vector3(wx, wy, wz);
        float continents = ElevNoise.Fbm(q.X * 1.1f, q.Y * 1.1f, q.Z * 1.1f, 5, 2.1f, 0.5f);
        float detail = DetailNoise.Fbm(d.X * 6f, d.Y * 6f, d.Z * 6f, 4, 2.2f, 0.5f) * 0.12f;
        ridge = RidgeNoise.Ridged(q.X * 2.6f, q.Y * 2.6f, q.Z * 2.6f, 5, 2.05f, 0.55f);
        float landness = Math.Clamp((continents + 0.05f) * 4f, 0f, 1f);
        ridge = MathF.Pow(ridge, 2.2f) * landness;
        return continents + detail + ridge * 0.55f;
    }

    /// <summary>Elevation in metres at any unit direction (continuous; tiles sample it at their centres).</summary>
    public float ElevationAt(Vector3 d)
    {
        float raw = RawElevation(d, out _) - SeaLevelRaw;
        return raw >= 0 ? raw * 5200f : raw * 6500f;
    }

    void GenerateElevation()
    {
        int n = Grid.TileCount;
        var raw = new float[n];
        Parallel.For(0, n, i =>
        {
            raw[i] = RawElevation(Grid.Centers[i], out float ridge);
            Ruggedness[i] = ridge;
        });
        var sorted = (float[])raw.Clone();
        Array.Sort(sorted);
        SeaLevelRaw = sorted[Math.Clamp((int)(Params.OceanCoverage * n), 0, n - 1)];
        for (int i = 0; i < n; i++)
        {
            float e = raw[i] - SeaLevelRaw;
            Elevation[i] = e >= 0 ? e * 5200f : e * 6500f;
            if (Elevation[i] >= 0 && Elevation[i] < 2f) Elevation[i] = 2f; // land tiles are strictly above sea level
        }

    }

    // ---------------------------------------------------------------- water bodies

    /// <summary>Connected bodies of water: the large ones are ocean, small enclosed ones are lakes.</summary>
    void ClassifyWater()
    {
        int n = Grid.TileCount;
        var comp = new int[n];
        Array.Fill(comp, -1);
        var sizes = new List<int>();
        var q = new Queue<int>();
        for (int start = 0; start < n; start++)
        {
            if (Elevation[start] >= 0 || comp[start] >= 0) continue;
            int id = sizes.Count, size = 0;
            comp[start] = id; q.Enqueue(start);
            while (q.Count > 0)
            {
                int t = q.Dequeue(); size++;
                foreach (int nb in Grid.Neighbors(t))
                    if (Elevation[nb] < 0 && comp[nb] < 0) { comp[nb] = id; q.Enqueue(nb); }
            }
            sizes.Add(size);
        }
        // bodies covering less than 0.5 % of the planet are lakes
        int lakeMax = Math.Max(3, n / 200);
        int lakes = 0;
        for (int i = 0; i < n; i++)
        {
            if (Elevation[i] >= 0) { Water[i] = WaterBody.None; continue; }
            Water[i] = sizes[comp[i]] <= lakeMax ? WaterBody.Lake : WaterBody.Ocean;
            if (Water[i] == WaterBody.Lake) lakes++;
        }
        Log.Info($"Water bodies: {sizes.Count} ({sizes.Count(sz => sz <= lakeMax)} lakes covering {lakes} tiles)");
    }

    /// <summary>
    /// Narrow sea inlets that a river flows into become estuaries: land tiles carrying a large river into the sea
    /// (playable), instead of open ocean.
    /// </summary>
    void MakeEstuaries()
    {
        int made = 0;
        for (int pass = 0; pass < 4; pass++)
        {
            var convert = new List<int>();
            for (int t = 0; t < Grid.TileCount; t++)
            {
                if (Water[t] != WaterBody.Ocean) continue;
                int land = 0; bool fed = false;
                foreach (int nb in Grid.Neighbors(t))
                {
                    if (Water[nb] == WaterBody.None) land++;
                    if (Water[nb] == WaterBody.None && Downstream[nb] == t && RiverSize[nb] > 0) fed = true;
                }
                if (fed && land >= 4) convert.Add(t);
            }
            if (convert.Count == 0) break;
            foreach (int t in convert)
            {
                byte size = 2; float flow = 0; int sea = -1; float seaElev = float.MaxValue;
                foreach (int nb in Grid.Neighbors(t))
                {
                    if (Water[nb] == WaterBody.None && Downstream[nb] == t) { size = Math.Max(size, RiverSize[nb]); flow += Flow[nb]; }
                    if (Water[nb] == WaterBody.Ocean && !convert.Contains(nb) && Elevation[nb] < seaElev) { seaElev = Elevation[nb]; sea = nb; }
                }
                if (sea < 0) continue; // fully enclosed by other inlets: wait for the next pass
                Water[t] = WaterBody.None;
                Elevation[t] = 3f;
                Estuary[t] = true;
                RiverSize[t] = size;
                Flow[t] = flow;
                Downstream[t] = sea;
                made++;
            }
        }
        if (made > 0) Log.Info($"Estuaries: {made} narrow river mouths turned into river tiles");
    }

    void UpdateShores()
    {
        for (int i = 0; i < Grid.TileCount; i++)
        {
            Coast[i] = LakeShore[i] = false;
            if (Water[i] != WaterBody.None) continue;
            foreach (int nb in Grid.Neighbors(i))
            {
                if (Water[nb] == WaterBody.Ocean) Coast[i] = true;
                if (Water[nb] == WaterBody.Lake) LakeShore[i] = true;
            }
        }
    }

    /// <summary>Typical wind speed of a tile (m/s): the prevailing wind for its latitude, weaker on high ground, plus
    /// the average contribution of passing weather systems.</summary>
    public float TypicalWind(int tile)
    {
        float lat = Grid.Latitude(tile);
        var w = World.Climate.PrevailingWind(lat);
        return w.Length() * (0.6f + 0.4f * MathF.Max(0f, 1f - MathF.Max(0f, Elevation[tile]) / 4000f)) + 1.5f;
    }

    // ---------------------------------------------------------------- climate normals

    /// <summary>Annual mean sea-level temperature for a latitude (degrees).</summary>
    public static float LatitudeTemperature(float latDeg)
    {
        float c = MathF.Cos(MathF.Abs(latDeg) * MathF.PI / 180f);
        return -22f + 50f * MathF.Pow(MathF.Max(0f, c), 1.2f);
    }

    /// <summary>Zonal precipitation profile in mm/year: wet tropics, dry subtropics, wet mid-latitudes, dry poles.</summary>
    public static float LatitudePrecip(float latDeg)
    {
        float a = MathF.Abs(latDeg);
        float itcz = 2300f * MathF.Exp(-(latDeg / 13f) * (latDeg / 13f));
        float mid = 950f * MathF.Exp(-((a - 50f) / 14f) * ((a - 50f) / 14f));
        float sub = -250f * MathF.Exp(-((a - 27f) / 8f) * ((a - 27f) / 8f));
        return MathF.Max(60f, 180f + itcz + mid + sub);
    }

    void GenerateClimateNormals()
    {
        int n = Grid.TileCount;
        // distance to the sea (in tiles) drives continentality and inland drying
        var dist = SeaDistance();
        float tileKm = Grid.TileAngle * RadiusKm;
        Parallel.For(0, n, i =>
        {
            Vector3 d = Grid.Centers[i];
            float lat = Grid.Latitude(i);
            float elev = MathF.Max(0f, Elevation[i]);
            float noiseT = ClimateNoise.Fbm(d.X * 3f, d.Y * 3f, d.Z * 3f, 3) * 3.5f;
            MeanTemp[i] = LatitudeTemperature(lat) - elev * 0.0065f + noiseT + Params.TemperatureOffset;

            float inlandKm = dist[i] * tileKm;
            float continental = Math.Clamp(inlandKm / 1800f, 0f, 1f);
            float sinLat = MathF.Abs(MathF.Sin(lat * MathF.PI / 180f));
            SeasonAmp[i] = Elevation[i] < 0
                ? 1f + sinLat * 5f
                : 1.5f + sinLat * (7f + 15f * continental);

            float pNoise = 0.55f + 0.9f * (0.5f + 0.5f * ClimateNoise.Fbm(d.X * 2.2f + 40f, d.Y * 2.2f, d.Z * 2.2f, 4));
            float inlandDry = Elevation[i] < 0 ? 1f : MathF.Exp(-inlandKm / 2600f);
            float orographic = 1f + Math.Clamp(elev / 3000f, 0f, 0.6f);
            AnnualPrecip[i] = LatitudePrecip(lat) * pNoise * (0.35f + 0.65f * inlandDry) * orographic * Params.RainfallScale;
        });
    }

    int[] SeaDistance()
    {
        int n = Grid.TileCount;
        var dist = new int[n];
        var q = new Queue<int>();
        for (int i = 0; i < n; i++)
        {
            if (Water[i] == WaterBody.Ocean) { dist[i] = 0; q.Enqueue(i); }
            else dist[i] = int.MaxValue;
        }
        while (q.Count > 0)
        {
            int t = q.Dequeue();
            foreach (int nb in Grid.Neighbors(t))
                if (dist[nb] == int.MaxValue) { dist[nb] = dist[t] + 1; q.Enqueue(nb); }
        }
        for (int i = 0; i < n; i++) if (dist[i] == int.MaxValue) dist[i] = 64; // planet without sea
        return dist;
    }

    // ---------------------------------------------------------------- rivers

    void GenerateRivers()
    {
        int n = Grid.TileCount;
        Array.Fill(Downstream, -1);
        // Priority-flood from the sea: every land tile gets a downstream neighbour that leads to the sea,
        // depressions are filled so that drainage never dead-ends.
        var filled = new float[n];
        var visited = new bool[n];
        var pq = new PriorityQueue<int, (float, int)>();
        var order = new List<int>(n);
        for (int i = 0; i < n; i++)
        {
            if (Elevation[i] >= 0) continue;
            visited[i] = true;
            filled[i] = Elevation[i];
            if (IsShore(i)) pq.Enqueue(i, (0f, i));
        }
        bool anySea = pq.Count > 0;
        if (!anySea)
        {
            // degenerate world without an ocean: drain to the lowest tile
            int low = 0;
            for (int i = 1; i < n; i++) if (Elevation[i] < Elevation[low]) low = i;
            visited[low] = true; filled[low] = Elevation[low];
            pq.Enqueue(low, (Elevation[low], low));
        }
        while (pq.TryDequeue(out int t, out _))
        {
            if (Elevation[t] >= 0) order.Add(t);
            foreach (int nb in Grid.Neighbors(t))
            {
                if (visited[nb]) continue;
                visited[nb] = true;
                filled[nb] = MathF.Max(Elevation[nb], filled[t] + 0.01f);
                Downstream[nb] = t;
                pq.Enqueue(nb, (filled[nb], nb));
            }
        }

        // Accumulate runoff from the highest filled tiles down to the sea.
        float avg = 0; int land = 0;
        for (int i = 0; i < n; i++) if (Elevation[i] >= 0) { avg += AnnualPrecip[i]; land++; }
        avg = land > 0 ? avg / land : 1f;
        for (int k = order.Count - 1; k >= 0; k--)
        {
            int t = order[k];
            float runoff = MathF.Max(0f, AnnualPrecip[t] - 200f) / avg;
            Flow[t] += runoff;
            int d = Downstream[t];
            if (d >= 0 && Elevation[d] >= 0) Flow[d] += Flow[t];
        }
        // River classes scale with the grid resolution so rivers keep a similar look at any frequency.
        float scale = Params.Frequency / 64f;
        float t1 = 9f * scale, t2 = 26f * scale, t3 = 70f * scale;
        for (int i = 0; i < n; i++)
        {
            if (Elevation[i] < 0) continue;
            float f = Flow[i];
            RiverSize[i] = f >= t3 ? (byte)3 : f >= t2 ? (byte)2 : f >= t1 ? (byte)1 : (byte)0;
        }
    }

    bool IsShore(int seaTile)
    {
        foreach (int nb in Grid.Neighbors(seaTile)) if (Elevation[nb] >= 0) return true;
        return false;
    }

    // ---------------------------------------------------------------- biomes

    public static Biome Classify(float elevation, float meanTemp, float precip)
    {
        if (elevation < 0) return Biome.Ocean;
        if (meanTemp < -12f) return Biome.IceSheet;
        if (meanTemp < -3f) return Biome.Tundra;
        if (meanTemp < 5f) return precip > 350f ? Biome.BorealForest : Biome.Tundra;
        if (meanTemp < 19f)
        {
            if (precip > 650f) return Biome.TemperateForest;
            if (precip > 350f) return Biome.Grassland;
            if (precip > 180f) return Biome.AridShrubland;
            return Biome.Desert;
        }
        if (precip > 1700f) return Biome.TropicalRainforest;
        if (precip > 700f) return Biome.Savanna;
        if (precip > 250f) return Biome.AridShrubland;
        return Biome.Desert;
    }

    void ClassifyBiomes()
    {
        int n = Grid.TileCount;
        for (int i = 0; i < n; i++)
        {
            Biomes[i] = Water[i] == WaterBody.Lake ? Biome.Lake : Classify(Elevation[i], MeanTemp[i], AnnualPrecip[i]);
            if (Water[i] != WaterBody.None) { Hills[i] = Hilliness.Flat; continue; }
            float maxDiff = 0;
            foreach (int nb in Grid.Neighbors(i))
                if (Elevation[nb] >= 0) maxDiff = MathF.Max(maxDiff, MathF.Abs(Elevation[nb] - Elevation[i]));
            float score = maxDiff + Ruggedness[i] * 2600f + MathF.Max(0f, Elevation[i] - 1200f) * 0.35f;
            Hills[i] = score < 260f ? Hilliness.Flat
                : score < 650f ? Hilliness.SmallHills
                : score < 1300f ? Hilliness.LargeHills
                : score < 2600f ? Hilliness.Mountainous
                : Hilliness.Impassable;
        }
    }

    // ---------------------------------------------------------------- queries

    public float LandFraction()
    {
        int land = 0;
        for (int i = 0; i < Elevation.Length; i++) if (Elevation[i] >= 0) land++;
        return land / (float)Elevation.Length;
    }

    public int Count(Biome b)
    {
        int c = 0;
        for (int i = 0; i < Biomes.Length; i++) if (Biomes[i] == b) c++;
        return c;
    }

    public int CountRivers()
    {
        int c = 0;
        for (int i = 0; i < RiverSize.Length; i++) if (RiverSize[i] > 0) c++;
        return c;
    }

    /// <summary>Upstream tiles whose river flows into this tile.</summary>
    public List<int> RiverInflows(int tile)
    {
        var list = new List<int>();
        foreach (int nb in Grid.Neighbors(tile))
            if (Downstream[nb] == tile && RiverSize[nb] > 0) list.Add(nb);
        return list;
    }

    /// <summary>Picks a playable starting tile (temperate, not impassable), preferring rivers or coasts. Deterministic.</summary>
    public int FindStartTile(bool preferRiver = true)
    {
        int best = -1; float bestScore = float.MinValue;
        for (int i = 0; i < TileCount; i++)
        {
            if (!BiomeInfo.Playable(Biomes[i]) || Hills[i] == Hilliness.Impassable) continue;
            float s = (preferRiver && RiverSize[i] > 0 ? 3f : 0f) + (Coast[i] || LakeShore[i] ? 1f : 0f) + (Hills[i] == Hilliness.SmallHills ? 1f : 0f)
                      - MathF.Abs(MeanTemp[i] - 11f) * 0.15f + Hash.Cell01(i, 7, Params.Seed) * 0.5f;
            if (s > bestScore) { bestScore = s; best = i; }
        }
        return best;
    }

    /// <summary>
    /// Smoothly interpolated tile data at any direction (inverse-distance over the nearest tile and its ring).
    /// Used to bake smooth globe textures from the same values the simulation uses.
    /// </summary>
    public int Interpolate(Vector3 dir, int hint, float[] data, out float value)
    {
        int t = InterpolationWeights(dir, hint, out var tiles, out var weights, out int n);
        float v = 0;
        for (int k = 0; k < n; k++) v += weights[k] * data[tiles[k]];
        value = v;
        return t;
    }

    /// <summary>Interpolates three per-tile arrays in one pass (one nearest-tile search).</summary>
    public int Interpolate(Vector3 dir, int hint, float[] a, float[] b, float[] c, out float va, out float vb, out float vc)
    {
        int t = InterpolationWeights(dir, hint, out var tiles, out var weights, out int n);
        va = vb = vc = 0;
        for (int k = 0; k < n; k++)
        {
            float w = weights[k]; int i = tiles[k];
            va += w * a[i]; vb += w * b[i]; vc += w * c[i];
        }
        return t;
    }

    [ThreadStatic] static int[] _wTiles;
    [ThreadStatic] static float[] _wWeights;

    /// <summary>
    /// Normalised smooth weights of the nearest tile and its ring for a direction (cheap: dot products only,
    /// weight = ((dot - cos R) / (1 - cos R))², R = 1.6 tile spacings).
    /// </summary>
    int InterpolationWeights(Vector3 dir, int hint, out int[] tiles, out float[] weights, out int n)
    {
        tiles = _wTiles ??= new int[8];
        weights = _wWeights ??= new float[8];
        int t = Grid.Nearest(dir, hint);
        float cosR = MathF.Cos(Grid.TileAngle * 1.6f);
        float inv = 1f / (1f - cosR);
        float wsum = 0;
        n = 0;
        int s0 = Grid.NeighborStart[t], s1 = Grid.NeighborStart[t + 1];
        for (int k = s0 - 1; k < s1; k++)
        {
            int tile = k < s0 ? t : Grid.NeighborList[k];
            float w = MathF.Max(0f, (Vector3.Dot(Grid.Centers[tile], dir) - cosR) * inv);
            w *= w;
            tiles[n] = tile; weights[n] = w; n++;
            wsum += w;
        }
        if (wsum <= 0) { tiles[0] = t; weights[0] = 1; n = 1; return t; }
        for (int k = 0; k < n; k++) weights[k] /= wsum;
        return t;
    }
}
