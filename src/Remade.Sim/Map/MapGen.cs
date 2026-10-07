using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Remade.Core;
using Remade.Diagnostics;
using Remade.World;

namespace Remade.Map;

/// <summary>Everything the local generator needs to know about the planet tile, extracted once.</summary>
public sealed class TileFeatures
{
    public Biome Biome;
    public Hilliness Hills;
    public float Ruggedness;
    public byte RiverSize;
    /// <summary>Map-space unit directions (x = east, y = south) toward upstream river inflows.</summary>
    public List<Vector2> RiverIn = new();
    /// <summary>Map-space direction toward the downstream tile (river outflow), if any.</summary>
    public Vector2? RiverOut;
    /// <summary>Map-space directions toward neighbouring sea tiles.</summary>
    public List<Vector2> SeaDirs = new();
    /// <summary>Map-space directions toward neighbouring lake tiles (fresh water shores).</summary>
    public List<Vector2> LakeDirs = new();
    public bool Estuary;
    public float AnnualPrecip, MeanTemp, Soil;
    public float Latitude, Longitude;

    public static TileFeatures From(Planet p, int tile)
    {
        var f = new TileFeatures
        {
            Biome = p.Biomes[tile], Hills = p.Hills[tile], Ruggedness = p.Ruggedness[tile], RiverSize = p.RiverSize[tile],
            AnnualPrecip = p.AnnualPrecip[tile], MeanTemp = p.MeanTemp[tile], Soil = p.Climate.Soil[tile],
            Latitude = p.Grid.Latitude(tile), Longitude = p.Grid.Longitude(tile), Estuary = p.Estuary[tile],
        };
        p.Grid.Frame(tile, out var east, out var north);
        Vector3 c = p.Grid.Centers[tile];
        Vector2 Dir(int nb)
        {
            Vector3 d = p.Grid.Centers[nb] - c;
            return Vector2.Normalize(new Vector2(Vector3.Dot(d, east), -Vector3.Dot(d, north)));
        }
        foreach (int nb in p.Grid.Neighbors(tile))
        {
            if (p.Water[nb] == WaterBody.Ocean) f.SeaDirs.Add(Dir(nb));
            if (p.Water[nb] == WaterBody.Lake) f.LakeDirs.Add(Dir(nb));
            if (p.Downstream[nb] == tile && p.RiverSize[nb] > 0) f.RiverIn.Add(Dir(nb));
        }
        if (f.RiverSize > 0 && p.Downstream[tile] >= 0) f.RiverOut = Dir(p.Downstream[tile]);
        return f;
    }
}

/// <summary>
/// Generates a local map from its planet tile: relief from hilliness, granite massifs, river course from the
/// planetary drainage (entering from upstream neighbours, leaving toward the downstream one), sea along coasts,
/// soils, oak woods, berry bushes, grass, and an abandoned cabin near the landing site. Deterministic per seed.
/// </summary>
public static class MapGen
{
    public sealed class Result
    {
        public LocalMap Map;
        public Vector2 LandingSpot;
        /// <summary>Cells inside the cabin, where the starting loot goes.</summary>
        public List<int> CabinInterior = new();
        public int CabinDoor = -1;
        /// <summary>Cabin rectangle (outer walls), x0, y0, width, height.</summary>
        public int CabinX0, CabinY0, CabinW, CabinH;
        public bool InCabinZone(int x, int y, int margin)
            => CabinW > 0 && x >= CabinX0 - margin && x < CabinX0 + CabinW + margin && y >= CabinY0 - margin && y < CabinY0 + CabinH + margin;
        public double Millis;
    }

    public static Result Generate(Planet planet, int tile, int size, int seed)
    {
        if ((uint)tile >= (uint)planet.TileCount) throw new ArgumentOutOfRangeException(nameof(tile), tile, "not a planet tile");
        var feat = TileFeatures.From(planet, tile);
        return Generate(feat, tile, size, seed);
    }

    public static Result Generate(TileFeatures feat, int tile, int size, int seed)
    {
        using var corr = Log.Correlate($"MapGen tile {tile} size {size} seed {seed}");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Log.Info($"Generating map {size}x{size} for tile {tile}: biome={feat.Biome} hills={feat.Hills} river={feat.RiverSize} " +
                 $"in={feat.RiverIn.Count} out={(feat.RiverOut.HasValue ? "yes" : "no")} coastDirs={feat.SeaDirs.Count} seed={seed}");
        var map = new LocalMap(size, size, tile, seed);
        var res = new Result { Map = map };
        int W = size, H = size;
        var noise = new Noise(seed);
        var noise2 = new Noise(seed ^ 0x5bd1e995);
        var rng = Rng.FromParts(seed, tile, 1);

        Vector2 center = new(W * 0.5f, H * 0.5f);
        res.LandingSpot = center;

        // ---- water: sea and river distance fields (in cells)
        var seaDepth = new float[W * H];   // >0 inside the sea (distance into the sea), very negative far inland
        Array.Fill(seaDepth, -1e6f);
        var riverDist = new float[W * H];
        Array.Fill(riverDist, float.MaxValue);
        float riverHalfWidth = feat.RiverSize switch { 0 => 0, 1 => 2.6f, 2 => 5f, _ => 8.5f };

        // shores: the sea on ocean sides, a lake on lake sides (fresh water); the larger one wins where both exist
        var shoreDirs = feat.SeaDirs.Count >= feat.LakeDirs.Count ? feat.SeaDirs : feat.LakeDirs;
        bool freshShore = shoreDirs == feat.LakeDirs && feat.LakeDirs.Count > 0;
        if (shoreDirs.Count > 0)
        {
            Vector2 seaDir = Vector2.Zero;
            foreach (var d in shoreDirs) seaDir += d;
            seaDir = seaDir.LengthSquared() < 1e-4f ? shoreDirs[0] : Vector2.Normalize(seaDir);
            float spread = 0.22f + 0.08f * shoreDirs.Count; // more water neighbours → more water on the map
            if (feat.Estuary) spread *= 0.6f;               // a river mouth: the sea only at the map's edge
            Parallel.For(0, H, y =>
            {
                for (int x = 0; x < W; x++)
                {
                    Vector2 p = new(x + 0.5f, y + 0.5f);
                    float proj = Vector2.Dot(p - center, seaDir) / (size * 0.5f); // -1..1 toward the water
                    float wobble = noise.Fbm(x * 0.012f, y * 0.012f, 4) * 0.22f + noise2.Get(x * 0.05f, y * 0.05f) * 0.04f;
                    float edge = 1f - spread * 2f + wobble;
                    seaDepth[y * W + x] = (proj - edge) * size * 0.5f;
                }
            });
        }

        // river course: polyline from each inflow edge point through the map centre region to the outflow edge
        var riverPolys = new List<List<Vector2>>();
        if (feat.RiverSize > 0)
        {
            Vector2 outDir = feat.RiverOut ?? (feat.SeaDirs.Count > 0 ? feat.SeaDirs[0] : feat.LakeDirs.Count > 0 ? feat.LakeDirs[0] : new Vector2(0, 1));
            Vector2 exit = EdgePoint(center, outDir, W, H);
            Vector2 meet = center + outDir * (size * 0.12f) + new Vector2(rng.Range(-0.08f, 0.08f), rng.Range(-0.08f, 0.08f)) * size;
            var inflows = feat.RiverIn.Count > 0 ? feat.RiverIn : new List<Vector2> { -outDir };
            foreach (var inDir in inflows)
            {
                Vector2 entry = EdgePoint(center, inDir, W, H);
                var pts = Meander(new[] { entry, meet, exit }, noise2, size, rng.NextFloat() * 100f);
                riverPolys.Add(pts);
            }
            // distance to the nearest polyline (only near the course: band-limited brute force per segment)
            float band = riverHalfWidth + 40f;
            foreach (var poly in riverPolys)
                for (int s = 0; s + 1 < poly.Count; s++)
                    StampSegment(poly[s], poly[s + 1], band, riverDist, W, H);
        }

        float hillAmp = feat.Hills switch
        {
            Hilliness.Flat => 2.2f, Hilliness.Hills => 9f, Hilliness.Mountainous => 13f, _ => 16f,
        };
        // the rolling relief before water shapes it (also used to find natural basins for ponds). On hills, about half
        // of the map rises into hills and the rest stays as gentle lowland between them.
        bool hilly = feat.Hills == Hilliness.Hills;
        float Rolling(float x, float y)
        {
            float v = noise.Fbm(x * 0.008f, y * 0.008f, 5) * 0.5f + 0.5f;
            return hilly ? (0.15f * v + 0.85f * SmoothStep(0.42f, 0.7f, v)) * hillAmp : v * hillAmp;
        }

        // ponds and small lakes: fresh still water, more of them on wet tiles; one is guaranteed near the landing
        // site when no river or sea is close, so colonists always have something to drink. Ponds lie in the low
        // ground of the rolling relief (natural basins), so their shores stay gentle instead of being dug as craters.
        var pond = new float[W * H];
        Array.Fill(pond, 9f);
        {
            int count = (int)(W * H / 45000f * (0.4f + feat.Soil)) + 1;
            bool waterNear = feat.RiverSize > 0 || feat.LakeDirs.Count > 0;
            for (int k = 0; k < count + (waterNear ? 0 : 1); k++)
            {
                bool guaranteed = !waterNear && k == count;
                float r = guaranteed ? rng.Range(5f, 8f) : rng.Range(3.5f, 11f + size * 0.01f);
                // a few candidate spots; keep the lowest one
                Vector2 pc = default; float best = float.MaxValue;
                for (int c = 0; c < 8; c++)
                {
                    Vector2 cand;
                    if (guaranteed)
                    {
                        float ang = rng.Range(0f, MathF.Tau);
                        cand = center + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * rng.Range(24f, 34f);
                    }
                    else cand = new Vector2(rng.Range(r + 4, W - r - 4), rng.Range(r + 4, H - r - 4));
                    float lowness = Rolling(cand.X, cand.Y);
                    if (lowness < best) { best = lowness; pc = cand; }
                }
                // ponds need low ground; on a ridge there is no pond (the guaranteed one always stays)
                if (!guaranteed && best > MathF.Max(1.2f, hillAmp * 0.3f)) continue;
                if (!guaranteed && Vector2.Distance(pc, center) < r + 22f) continue;
                int x0 = Math.Max(0, (int)(pc.X - r * 1.6f)), x1 = Math.Min(W - 1, (int)(pc.X + r * 1.6f));
                int y0 = Math.Max(0, (int)(pc.Y - r * 1.6f)), y1 = Math.Min(H - 1, (int)(pc.Y + r * 1.6f));
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), pc) / r;
                        d += noise.Get(x * 0.13f + k * 31f, y * 0.13f) * 0.22f;
                        int i = y * W + x;
                        if (d < pond[i]) pond[i] = d;
                    }
            }
        }

        // ---- relief
        // share of the map covered by granite massifs
        float rockCoverage = feat.Hills switch
        {
            Hilliness.Flat => 0.03f, Hilliness.Hills => 0.18f, Hilliness.Mountainous => 0.75f, _ => 0.88f,
        };
        float landingClear = Math.Clamp(size * 0.06f, 14f, 28f);

        var rockField = new float[W * H];
        Parallel.For(0, H, y =>
        {
            for (int x = 0; x < W; x++)
            {
                int i = y * W + x;
                float m = noise.Fbm(x * 0.011f + 100f, y * 0.011f, 5, 2f, 0.5f) * 0.5f + 0.5f;
                float r = noise2.Ridged(x * 0.017f, y * 0.017f, 4) * 0.35f;
                float v = m * 0.8f + r;
                // keep the landing area, water and its banks free of rock
                float dc = Vector2.Distance(new Vector2(x, y), center);
                v -= MathF.Max(0f, 1f - dc / (landingClear * 1.6f)) * 0.6f;
                float waterDist = MathF.Min(MathF.Min(riverDist[i] - riverHalfWidth, -seaDepth[i]), (pond[i] - 1f) * 8f);
                v -= MathF.Max(0f, 1f - waterDist / 12f) * 0.5f;
                rockField[i] = v;
            }
        });
        float rockThreshold = Quantile(rockField, 1f - rockCoverage);

        int W1 = W + 1;
        Parallel.For(0, H + 1, y =>
        {
            for (int x = 0; x <= W; x++)
            {
                int cx = Math.Min(x, W - 1), cy = Math.Min(y, H - 1);
                int i = cy * W + cx;
                float hills = Rolling(x, y) + noise2.Fbm(x * 0.05f, y * 0.05f, 3) * 0.35f;
                // valleys toward rivers and the coast: rise smoothly away from them
                float wd = MathF.Min(riverDist[i] - riverHalfWidth, -seaDepth[i]);
                float valley = SmoothStep(0f, 60f, wd);
                float h = 0.45f + hills * valley + MathF.Max(0f, rockField[i] - rockThreshold) * 6f;
                // ponds: a gentle beach blending into the surrounding ground within about one radius of the shore
                if (pond[i] < 2.2f)
                {
                    float beach = 0.05f + MathF.Max(0f, pond[i] - 1f) * 0.6f;
                    h = beach + (h - beach) * SmoothStep(1f, 2.2f, pond[i]);
                }
                // river bed and sea floor below the water surface
                if (riverDist[i] < riverHalfWidth)
                {
                    // channel cross-section, continuous with the bank at the edge (h = 0.05 m)
                    float t = riverDist[i] / MathF.Max(0.5f, riverHalfWidth);
                    h = 0.05f - (1f - t * t) * (0.75f + riverHalfWidth * 0.12f) - (1f - t) * 0.15f;
                }
                else if (riverDist[i] < riverHalfWidth + 3f) h = MathF.Min(h, 0.05f + (riverDist[i] - riverHalfWidth) * 0.15f);
                if (pond[i] < 1f) h = MathF.Min(h, 0.05f - (1f - pond[i]) * 1.8f);
                if (seaDepth[i] > -6f)
                {
                    float sd = seaDepth[i];
                    float shore = sd < 0 ? 0.1f + (-sd) * 0.08f : -0.15f - sd * 0.06f;
                    h = sd < 0 ? MathF.Min(h, shore) : MathF.Max(-6f, shore);
                }
                map.Ground[y * W1 + x] = h;
            }
        });

        // ---- terrain, rock, soils
        var soilNoise = new Noise(seed + 7);
        Parallel.For(0, H, y =>
        {
            for (int x = 0; x < W; x++)
            {
                int i = y * W + x;
                Terrain t = Terrain.Soil;
                float sd = seaDepth[i];
                float rd = riverDist[i];
                if (sd > 0) t = freshShore ? (sd > 9f ? Terrain.LakeDeep : Terrain.LakeShallow) : (sd > 9f ? Terrain.OceanDeep : Terrain.OceanShallow);
                else if (rd < riverHalfWidth) t = rd < riverHalfWidth * 0.55f && riverHalfWidth > 3f ? Terrain.RiverDeep : Terrain.RiverShallow;
                else if (pond[i] < 1f) t = pond[i] < 0.55f ? Terrain.LakeDeep : Terrain.LakeShallow;
                else
                {
                    float sn = soilNoise.Fbm(x * 0.03f, y * 0.03f, 3);
                    float wet = feat.Soil + soilNoise.Get(x * 0.02f + 50f, y * 0.02f) * 0.3f;
                    if (sd > -3.5f + sn * 2f) t = Terrain.Sand;
                    else if (rd < riverHalfWidth + 1.5f + sn * 1.5f) t = sn > 0.1f ? Terrain.Gravel : Terrain.Mud;
                    else if (pond[i] < 1.05f + sn * 0.05f) t = Terrain.Mud;
                    else if (pond[i] < 1.5f && wet > 0.6f && sn < 0f) t = Terrain.Marsh;
                    else if (rd < riverHalfWidth + 9f && wet > 0.75f && sn < -0.25f) t = Terrain.Marsh;
                    else if (sn > 0.38f) t = Terrain.Gravel;
                    else if (sn < -0.2f || rd < riverHalfWidth + 14f) t = Terrain.RichSoil;
                }
                map.Terrain[i] = t;
                if (!TerrainDef.Of(t).Water && t != Terrain.Sand && rockField[i] > rockThreshold)
                {
                    map.Buildings[i] = Building.Granite;
                    map.BuildingHp[i] = BuildingInfo.MaxHp(Building.Granite);
                    map.Terrain[i] = Terrain.RoughGranite;
                }
            }
        });
        RemoveTinyRocks(map);
        ComputeRockHeights(map, noise2);

        // ---- cabin (walls + door) near the landing spot, on dry level ground
        PlaceCabin(map, res, rng);

        // ---- vegetation
        PlaceVegetation(map, feat, seed, center, landingClear, res);

        map.ClearChanges();
        res.Millis = sw.Elapsed.TotalMilliseconds;
        Log.Info($"Map generated in {res.Millis:F0} ms: rock {Count(map, Building.Granite)} cells, trees {CountPlants(map, Plant.Oak)}, bushes {CountPlants(map, Plant.BerryBush)}");
        return res;
    }

    /// <summary>Approximate q-quantile of an array (sampled), used to hit a target coverage exactly.</summary>
    static float Quantile(float[] values, float q)
    {
        int step = Math.Max(1, values.Length / 65536);
        var sample = new float[(values.Length + step - 1) / step];
        for (int i = 0, k = 0; i < values.Length; i += step, k++) sample[k] = values[i];
        Array.Sort(sample);
        return sample[Math.Clamp((int)(q * sample.Length), 0, sample.Length - 1)];
    }

    static float SmoothStep(float a, float b, float x)
    {
        float t = Math.Clamp((x - a) / (b - a), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>Point where a ray from the centre leaves the map, pulled 2 cells outside so rivers reach the edge.</summary>
    static Vector2 EdgePoint(Vector2 c, Vector2 dir, int w, int h)
    {
        float tx = dir.X > 1e-4f ? (w - c.X) / dir.X : dir.X < -1e-4f ? -c.X / dir.X : float.MaxValue;
        float ty = dir.Y > 1e-4f ? (h - c.Y) / dir.Y : dir.Y < -1e-4f ? -c.Y / dir.Y : float.MaxValue;
        float t = MathF.Min(tx, ty);
        return c + dir * (t + 2f);
    }

    /// <summary>Subdivides a control polyline and displaces it sideways with smooth noise (meanders).</summary>
    static List<Vector2> Meander(Vector2[] ctrl, Noise n, int size, float offset)
    {
        var pts = new List<Vector2>();
        float amp = size * 0.06f;
        for (int k = 0; k + 1 < ctrl.Length; k++)
        {
            Vector2 a = ctrl[k], b = ctrl[k + 1];
            float len = Vector2.Distance(a, b);
            int steps = Math.Max(2, (int)(len / 3f));
            Vector2 dir = (b - a) / len, perp = new(-dir.Y, dir.X);
            for (int s = 0; s < steps; s++)
            {
                float t = s / (float)steps;
                float along = (k + t) * 3.1f;
                // taper the displacement at segment ends so segments join smoothly
                float taper = MathF.Sin(t * MathF.PI);
                float d = (n.Fbm(along + offset, 0.5f, 3) * amp + n.Get(along * 4f + offset, 7.7f) * amp * 0.15f) * taper;
                pts.Add(a + dir * (len * t) + perp * d);
            }
        }
        pts.Add(ctrl[^1]);
        return pts;
    }

    static void StampSegment(Vector2 a, Vector2 b, float band, float[] dist, int w, int h)
    {
        int x0 = Math.Max(0, (int)(MathF.Min(a.X, b.X) - band)), x1 = Math.Min(w - 1, (int)(MathF.Max(a.X, b.X) + band));
        int y0 = Math.Max(0, (int)(MathF.Min(a.Y, b.Y) - band)), y1 = Math.Min(h - 1, (int)(MathF.Max(a.Y, b.Y) + band));
        Vector2 ab = b - a;
        float len2 = MathF.Max(1e-6f, ab.LengthSquared());
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                Vector2 p = new(x + 0.5f, y + 0.5f);
                float t = Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f);
                float d = Vector2.Distance(p, a + ab * t);
                int i = y * w + x;
                if (d < dist[i]) dist[i] = d;
            }
    }

    /// <summary>Single isolated rock cells look like noise; remove those with fewer than 3 rock neighbours.</summary>
    static void RemoveTinyRocks(LocalMap map)
    {
        int W = map.Width, H = map.Height;
        var kill = new List<int>();
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = y * W + x;
                if (map.Buildings[i] != Building.Granite) continue;
                int n = 0;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if ((dx | dy) == 0) continue;
                        int nx = x + dx, ny = y + dy;
                        if (map.InBounds(nx, ny) && map.Buildings[ny * W + nx] == Building.Granite) n++;
                    }
                if (n < 3) kill.Add(i);
            }
        foreach (int i in kill)
        {
            map.Buildings[i] = Building.None;
            map.BuildingHp[i] = 0;
            map.Terrain[i] = Terrain.Gravel;
        }
    }

    /// <summary>
    /// Rock massif heights grow with the distance from the massif edge, so mountains rise toward their cores
    /// (two-pass chamfer distance transform, O(cells)).
    /// </summary>
    public static void ComputeRockHeights(LocalMap map, Noise n)
    {
        int W = map.Width, H = map.Height;
        var d = new float[W * H];
        const float Inf = 1e9f, D1 = 1f, D2 = 1.4142f;
        for (int i = 0; i < d.Length; i++) d[i] = map.Buildings[i] == Building.Granite ? Inf : 0f;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = y * W + x;
                if (d[i] == 0) continue;
                float v = d[i];
                if (x > 0) v = MathF.Min(v, d[i - 1] + D1); else v = MathF.Min(v, D1);
                if (y > 0) v = MathF.Min(v, d[i - W] + D1); else v = MathF.Min(v, D1);
                if (x > 0 && y > 0) v = MathF.Min(v, d[i - W - 1] + D2);
                if (x < W - 1 && y > 0) v = MathF.Min(v, d[i - W + 1] + D2);
                d[i] = v;
            }
        for (int y = H - 1; y >= 0; y--)
            for (int x = W - 1; x >= 0; x--)
            {
                int i = y * W + x;
                if (d[i] == 0) continue;
                float v = d[i];
                if (x < W - 1) v = MathF.Min(v, d[i + 1] + D1); else v = MathF.Min(v, D1);
                if (y < H - 1) v = MathF.Min(v, d[i + W] + D1); else v = MathF.Min(v, D1);
                if (x < W - 1 && y < H - 1) v = MathF.Min(v, d[i + W + 1] + D2);
                if (x > 0 && y < H - 1) v = MathF.Min(v, d[i + W - 1] + D2);
                d[i] = v;
            }
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = y * W + x;
                if (d[i] == 0) { map.RockHeight[i] = 0; continue; }
                float core = MathF.Min(d[i], 14f);
                map.RockHeight[i] = 2.4f + core * 0.75f + n.Fbm(x * 0.09f, y * 0.09f, 3) * 0.6f + (core > 3 ? n.Ridged(x * 0.04f, y * 0.04f, 3) * 2.5f : 0f);
            }
    }

    static void PlaceCabin(LocalMap map, Result res, Rng rng)
    {
        int W = map.Width;
        const int cw = 6, ch = 5; // outer size incl. walls
        int rejBounds = 0, rejBlocked = 0, rejLow = 0;
        Vector2 c = res.LandingSpot;
        for (int attempt = 0; attempt < 400; attempt++)
        {
            float ang = rng.Range(0f, MathF.Tau);
            float r = rng.Range(7f, 14f + attempt * 0.1f);
            int x0 = (int)(c.X + MathF.Cos(ang) * r) - cw / 2, y0 = (int)(c.Y + MathF.Sin(ang) * r) - ch / 2;
            if (!Fits(x0, y0)) continue;
            // walls
            for (int y = y0; y < y0 + ch; y++)
                for (int x = x0; x < x0 + cw; x++)
                {
                    int i = y * W + x;
                    map.Plants[i] = Plant.None;
                    bool wall = x == x0 || y == y0 || x == x0 + cw - 1 || y == y0 + ch - 1;
                    if (map.Terrain[i] != Terrain.RichSoil) map.Terrain[i] = Terrain.Soil;
                    if (wall)
                    {
                        map.Buildings[i] = Building.WoodWall;
                        map.BuildingHp[i] = BuildingInfo.MaxHp(Building.WoodWall);
                    }
                    else res.CabinInterior.Add(i);
                }
            // door on the wall facing the landing spot
            int dx = x0 + cw / 2, dy = y0 + ch / 2;
            Vector2 toLanding = c - new Vector2(dx, dy);
            int door;
            if (MathF.Abs(toLanding.X) > MathF.Abs(toLanding.Y))
                door = toLanding.X > 0 ? dy * W + x0 + cw - 1 : dy * W + x0;
            else
                door = toLanding.Y > 0 ? (y0 + ch - 1) * W + dx : y0 * W + dx;
            map.Buildings[door] = Building.Door;
            map.BuildingHp[door] = BuildingInfo.MaxHp(Building.Door);
            map.DoorOpen[door] = false;
            res.CabinDoor = door;
            res.CabinX0 = x0; res.CabinY0 = y0; res.CabinW = cw; res.CabinH = ch;
            Log.Info($"Cabin placed at ({x0},{y0}) door cell {door}");
            return;
        }
        throw new InvalidOperationException($"MapGen: could not place the starting cabin near the landing spot {c} " +
            $"(rejections: bounds {rejBounds}, blocked {rejBlocked}, low ground {rejLow})");

        bool Fits(int x0, int y0)
        {
            for (int y = y0 - 2; y < y0 + ch + 2; y++)
                for (int x = x0 - 2; x < x0 + cw + 2; x++)
                {
                    if (!map.InBounds(x, y)) { rejBounds++; return false; }
                    int i = y * W + x;
                    if (map.Buildings[i] != Building.None || TerrainDef.Of(map.Terrain[i]).Water || map.Terrain[i] == Terrain.Marsh) { rejBlocked++; return false; }
                    if (map.Ground[y * (W + 1) + x] < 0.2f) { rejLow++; return false; }
                }
            return true;
        }
    }

    static void PlaceVegetation(LocalMap map, TileFeatures feat, int seed, Vector2 center, float landingClear, Result res)
    {
        int W = map.Width, H = map.Height;
        var forest = new Noise(seed + 31);
        float moisture = Math.Clamp(feat.Soil * 0.6f + feat.AnnualPrecip / 2000f, 0.2f, 1.2f);
        float treeBase = feat.Biome switch
        {
            Biome.TemperateForest => 0.105f,
            Biome.BorealForest or Biome.TropicalRainforest => 0.12f,
            Biome.Savanna or Biome.Grassland => 0.02f,
            Biome.AridShrubland => 0.012f,
            _ => 0.004f,
        } * moisture;

        // pass 1: trees and bushes, row-parallel with per-cell hashing (deterministic regardless of threading);
        // spacing is enforced by only allowing a tree where the cell hash is the local maximum of its 3x3 block.
        Parallel.For(0, H, y =>
        {
            for (int x = 0; x < W; x++)
            {
                int i = y * W + x;
                var t = map.Terrain[i];
                if (map.Buildings[i] != Building.None || TerrainDef.Of(t).Water || t == Terrain.Sand || t == Terrain.RoughGranite) continue;
                if (res.InCabinZone(x, y, 5)) continue; // a clearing around the cabin so it can be seen from above
                float f = forest.Fbm(x * 0.012f, y * 0.012f, 4) * 0.5f + 0.5f;  // groves vs clearings
                float density = treeBase * SmoothStep(0.32f, 0.62f, f) * 2.2f * TerrainDef.Of(t).Fertility;
                float dc = Vector2.Distance(new Vector2(x, y), center);
                density *= SmoothStep(landingClear * 0.5f, landingClear * 1.3f, dc);
                float h = Hash.Cell01(x, y, seed);
                if (h < density * 9f && IsLocalMax(x, y, h))
                {
                    if (Hash.Cell01(x, y, seed + 1) < density * 9f)
                    {
                        byte variant = (byte)(Hash.Cell(x, y, seed + 2) % PlantInfo.OakVariants);
                        float g = Hash.Cell01(x, y, seed + 3);
                        byte growth = (byte)(g < 0.12f ? 40 + g * 900 : 150 + (g - 0.12f) * 120);
                        map.Plants[i] = Plant.Oak; map.PlantVariant[i] = variant; map.PlantGrowth[i] = growth;
                        continue;
                    }
                }
                // berry bushes at forest edges
                float edge = 1f - MathF.Abs(f - 0.45f) * 5f;
                if (edge > 0 && Hash.Cell01(x, y, seed + 9) < 0.006f * edge * moisture && dc > 6f)
                {
                    map.Plants[i] = Plant.BerryBush;
                    map.PlantVariant[i] = (byte)(Hash.Cell(x, y, seed + 4) % PlantInfo.BushVariants);
                    map.PlantGrowth[i] = (byte)(170 + Hash.Cell(x, y, seed + 5) % 80);
                    map.Berries[i] = (byte)(4 + Hash.Cell(x, y, seed + 6) % (PlantInfo.MaxBerries - 3));
                }
            }
        });

        // a guaranteed bush near the landing site so gathering is discoverable
        PlaceBushNear(map, center, seed, res);

        // pass 2: grass density
        Parallel.For(0, H, y =>
        {
            for (int x = 0; x < W; x++)
            {
                int i = y * W + x;
                var t = map.Terrain[i];
                if (map.Buildings[i] != Building.None || res.InCabinZone(x, y, 0)) { map.Grass[i] = 0; continue; }
                float fert = TerrainDef.Of(t).Fertility;
                float n = forest.Fbm(x * 0.05f + 77f, y * 0.05f, 3) * 0.5f + 0.5f;
                float shade = map.Plants[i] == Plant.Oak ? 0.5f : 1f;
                float g = fert * (0.35f + 0.9f * n) * moisture * shade;
                map.Grass[i] = (byte)Math.Clamp(g * 200f, 0f, 255f);
            }
        });

        bool IsLocalMax(int x, int y, float h)
        {
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if ((dx | dy) == 0) continue;
                    if (Hash.Cell01(x + dx, y + dy, seed) > h) return false;
                }
            return true;
        }
    }

    static void PlaceBushNear(LocalMap map, Vector2 c, int seed, Result res)
    {
        for (int r = 4; r < 30; r++)
            for (int a = 0; a < 16; a++)
            {
                float ang = a / 16f * MathF.Tau + seed;
                int x = (int)(c.X + MathF.Cos(ang) * r), y = (int)(c.Y + MathF.Sin(ang) * r);
                if (!map.InBounds(x, y) || res.InCabinZone(x, y, 1)) continue;
                int i = map.Index(x, y);
                if (map.Buildings[i] != Building.None || map.Plants[i] != Plant.None || TerrainDef.Of(map.Terrain[i]).Water) continue;
                map.Plants[i] = Plant.BerryBush; map.PlantVariant[i] = 0; map.PlantGrowth[i] = 220; map.Berries[i] = PlantInfo.MaxBerries;
                return;
            }
    }

    static int Count(LocalMap m, Building b) { int c = 0; foreach (var v in m.Buildings) if (v == b) c++; return c; }
    static int CountPlants(LocalMap m, Plant p) { int c = 0; foreach (var v in m.Plants) if (v == p) c++; return c; }
}
