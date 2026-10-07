using System;
using System.Linq;
using System.Numerics;
using Remade.Core;
using Remade.World;
using Xunit.Abstractions;

namespace Remade.Tests;

public class HexSphereTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(32)]
    public void TileCountMatchesFormula(int f)
    {
        var g = new HexSphere(f);
        Assert.Equal(10 * f * f + 2, g.TileCount);
        Assert.Equal(20 * f * f * 3, g.Triangles.Length);
    }

    [Fact]
    public void ExactlyTwelvePentagonsRestHexagons()
    {
        var g = new HexSphere(16);
        int five = 0, six = 0;
        for (int i = 0; i < g.TileCount; i++)
        {
            int n = g.Neighbors(i).Length;
            if (n == 5) five++; else if (n == 6) six++;
        }
        Assert.Equal(12, five);
        Assert.Equal(g.TileCount - 12, six);
    }

    [Fact]
    public void AdjacencyIsSymmetricAndCentersAreUnit()
    {
        var g = new HexSphere(12);
        for (int i = 0; i < g.TileCount; i++)
        {
            Assert.InRange(g.Centers[i].Length(), 0.9999f, 1.0001f);
            foreach (int nb in g.Neighbors(i)) Assert.Contains(i, g.Neighbors(nb).ToArray());
        }
    }

    [Fact]
    public void NearestFindsTheTileItself()
    {
        var g = new HexSphere(24);
        for (int i = 0; i < g.TileCount; i += 37)
            Assert.Equal(i, g.Nearest(g.Centers[i], hint: (i * 7919) % g.TileCount));
    }

    [Fact]
    public void TrianglesAreCounterClockwiseFromOutside()
    {
        var g = new HexSphere(8);
        for (int k = 0; k < g.Triangles.Length; k += 3)
        {
            Vector3 a = g.Centers[g.Triangles[k]], b = g.Centers[g.Triangles[k + 1]], c = g.Centers[g.Triangles[k + 2]];
            Assert.True(Vector3.Dot(Vector3.Cross(b - a, c - a), a + b + c) > 0);
        }
    }
}

public class PlanetTests
{
    readonly ITestOutputHelper _out;
    public PlanetTests(ITestOutputHelper o) { _out = o; }

    static readonly Lazy<Planet> Shared = new(() => new Planet(new WorldParams { Seed = 42, Frequency = 48 }));

    [Fact]
    public void GenerationIsDeterministic()
    {
        var a = new Planet(new WorldParams { Seed = 7, Frequency = 20 });
        var b = new Planet(new WorldParams { Seed = 7, Frequency = 20 });
        Assert.Equal(a.Elevation, b.Elevation);
        Assert.Equal(a.Biomes, b.Biomes);
        Assert.Equal(a.RiverSize, b.RiverSize);
        Assert.Equal(a.Climate.Temperature, b.Climate.Temperature);
    }

    [Fact]
    public void OceanCoverageIsRespected()
    {
        var p = Shared.Value;
        Assert.InRange(1f - p.LandFraction(), 0.57f, 0.67f);
    }

    [Fact]
    public void EveryLandTileDrainsToTheSea()
    {
        var p = Shared.Value;
        for (int i = 0; i < p.TileCount; i++)
        {
            if (p.Elevation[i] < 0) continue;
            int t = i, steps = 0;
            while (p.Elevation[t] >= 0)
            {
                t = p.Downstream[t];
                Assert.True(t >= 0, $"tile {i} has no downstream");
                Assert.True(++steps < p.TileCount, "drainage loop");
            }
        }
    }

    [Fact]
    public void HasPlayableTemperateTilesAndRivers()
    {
        var p = Shared.Value;
        foreach (Biome b in Enum.GetValues<Biome>()) _out.WriteLine($"{b}: {p.Count(b)}");
        _out.WriteLine($"rivers: {p.CountRivers()} coast: {p.Coast.Count(c => c)}");
        Assert.True(p.Count(Biome.TemperateForest) > p.TileCount / 60, "too few temperate tiles");
        Assert.True(p.CountRivers() > p.TileCount / 200, "too few river tiles");
        int start = p.FindStartTile();
        Assert.True(start >= 0);
        Assert.Equal(Biome.TemperateForest, p.Biomes[start]);
    }

    [Fact]
    public void LakesAreInlandWaterNotOcean()
    {
        var p = Shared.Value;
        int lakes = 0, ocean = 0;
        for (int i = 0; i < p.TileCount; i++)
        {
            Assert.Equal(p.Elevation[i] < 0, p.Water[i] != WaterBody.None);
            if (p.Water[i] == WaterBody.Lake) { lakes++; Assert.Equal(Biome.Lake, p.Biomes[i]); }
            if (p.Water[i] == WaterBody.Ocean) { ocean++; Assert.Equal(Biome.Ocean, p.Biomes[i]); }
            if (p.LakeShore[i]) Assert.Contains(p.Grid.Neighbors(i).ToArray(), nb => p.Water[nb] == WaterBody.Lake);
        }
        _out.WriteLine($"lake tiles {lakes}, ocean tiles {ocean}");
        Assert.True(lakes > 0, "no lakes");
        Assert.True(ocean > lakes * 20);
    }

    [Fact]
    public void EstuariesAreRiverMouthsOnLand()
    {
        var p = Shared.Value;
        int n = 0;
        for (int i = 0; i < p.TileCount; i++)
        {
            if (!p.Estuary[i]) continue;
            n++;
            Assert.Equal(WaterBody.None, p.Water[i]);
            Assert.True(p.RiverSize[i] >= 2);
            // the mouth flows to the sea, possibly through further estuary tiles
            int t = p.Downstream[i], steps = 0;
            while (p.Estuary[t]) { t = p.Downstream[t]; Assert.True(++steps < 8); }
            Assert.Equal(WaterBody.Ocean, p.Water[t]);
        }
        _out.WriteLine($"estuaries {n}");
    }

    [Fact]
    public void MostLandIsFlatPlainsAndPlateaus()
    {
        var p = Shared.Value;
        int land = 0, flat = 0, high = 0, highFlat = 0;
        for (int i = 0; i < p.TileCount; i++)
        {
            if (p.Water[i] != WaterBody.None) continue;
            land++;
            if (p.Hills[i] == Hilliness.Flat) flat++;
            if (p.Elevation[i] > 1000f) { high++; if (p.Hills[i] == Hilliness.Flat) highFlat++; }
        }
        _out.WriteLine($"flat {flat}/{land}, plateaus {highFlat}/{high} high tiles");
        Assert.InRange(flat / (float)land, 0.65f, 0.75f);
        Assert.True(highFlat > 0, "no plateaus: high ground is always classed as hills");
    }

    [Fact]
    public void RiversGrowDownstream()
    {
        var p = Shared.Value;
        for (int i = 0; i < p.TileCount; i++)
        {
            int d = p.Downstream[i];
            if (p.Elevation[i] < 0 || d < 0 || p.Elevation[d] < 0) continue;
            Assert.True(p.Flow[d] >= p.Flow[i]);
            Assert.True(p.RiverSize[d] >= p.RiverSize[i]);
        }
    }

    [Fact]
    public void EquatorIsWarmerThanPoles()
    {
        Assert.True(Planet.LatitudeTemperature(0) > 25f);
        Assert.True(Planet.LatitudeTemperature(45) is > 5f and < 18f);
        Assert.True(Planet.LatitudeTemperature(85) < -15f);
    }

    [Theory]
    [InlineData(-100f, 10f, 800f, Biome.Ocean)]
    [InlineData(100f, 11f, 900f, Biome.TemperateForest)]
    [InlineData(100f, 25f, 100f, Biome.Desert)]
    [InlineData(100f, -20f, 300f, Biome.IceSheet)]
    [InlineData(100f, 2f, 500f, Biome.BorealForest)]
    public void BiomeClassification(float elev, float temp, float precip, Biome expected)
        => Assert.Equal(expected, Planet.Classify(elev, temp, precip));
}

public class ClimateTests
{
    static readonly Lazy<Planet> Shared = new(() => new Planet(new WorldParams { Seed = 3, Frequency = 32 }));

    [Fact]
    public void NorthernTileIsColderInWinterThanSummer()
    {
        var p = Shared.Value;
        int tile = Enumerable.Range(0, p.TileCount).First(t => p.Grid.Latitude(t) is > 40f and < 55f && p.Elevation[t] > 0);
        float summer = p.Climate.SeasonalTemperature(tile, 0.375f);
        float winter = p.Climate.SeasonalTemperature(tile, 0.875f);
        Assert.True(summer > winter + 5f, $"summer {summer} winter {winter}");
        int south = Enumerable.Range(0, p.TileCount).First(t => p.Grid.Latitude(t) is < -40f and > -55f);
        Assert.True(p.Climate.SeasonalTemperature(south, 0.875f) > p.Climate.SeasonalTemperature(south, 0.375f));
    }

    [Fact]
    public void ClimateEvolvesOverTimeAndStaysFinite()
    {
        var p = new Planet(new WorldParams { Seed = 11, Frequency = 24 });
        var before = (float[])p.Climate.Temperature.Clone();
        int v0 = p.Climate.Version;
        for (int h = 1; h <= 24 * 20; h += 6) p.Climate.Update(h * (long)GameTime.TicksPerHour);
        Assert.True(p.Climate.Version > v0);
        Assert.NotEqual(before, p.Climate.Temperature);
        for (int i = 0; i < p.TileCount; i++)
        {
            Assert.True(float.IsFinite(p.Climate.Temperature[i]));
            Assert.InRange(p.Climate.Precipitation[i], 0f, 500f);
            Assert.InRange(p.Climate.Soil[i], 0f, 1f);
            Assert.InRange(p.Climate.Cloud[i], 0f, 1f);
        }
    }

    [Fact]
    public void PlanetTemperatureIsTheLocalBaseline()
    {
        var p = Shared.Value;
        int tile = p.FindStartTile();
        var s = p.Climate.Sample(tile);
        Assert.Equal(p.Climate.Temperature[tile], s.Temperature);
        // the daily mean of the diurnal curve equals the planetary value
        float sum = 0;
        for (int h = 0; h < 24; h++) sum += s.TemperatureAtHour(h + 0.5f);
        Assert.InRange(sum / 24f, s.Temperature - 0.05f, s.Temperature + 0.05f);
    }

    [Fact]
    public void LongRunPrecipitationIsNearTheAnnualNormal()
    {
        var p = new Planet(new WorldParams { Seed = 5, Frequency = 24 });
        var sum = new double[p.TileCount];
        int samples = 0;
        for (long h = 0; h < 24 * GameTime.DaysPerYear; h += 12)
        {
            p.Climate.Update(h * GameTime.TicksPerHour);
            for (int i = 0; i < p.TileCount; i++) sum[i] += p.Climate.Precipitation[i];
            samples++;
        }
        double ratio = 0; int land = 0;
        for (int i = 0; i < p.TileCount; i++)
        {
            if (p.Elevation[i] < 0) continue;
            ratio += sum[i] / samples * 365.0 / p.AnnualPrecip[i];
            land++;
        }
        ratio /= land;
        Assert.InRange(ratio, 0.6, 1.6);
    }

    [Fact]
    public void ClimateRoundTripsThroughSerialization()
    {
        var p = new Planet(new WorldParams { Seed = 9, Frequency = 16 });
        p.Climate.Update(GameTime.TicksPerDay * 3);
        var ms = new System.IO.MemoryStream();
        using (var w = new System.IO.BinaryWriter(ms, System.Text.Encoding.UTF8, true)) p.Climate.Write(w);
        var q = new Planet(new WorldParams { Seed = 9, Frequency = 16 });
        ms.Position = 0;
        using (var r = new System.IO.BinaryReader(ms)) q.Climate.Read(r);
        Assert.Equal(p.Climate.Temperature, q.Climate.Temperature);
        Assert.Equal(p.Climate.Soil, q.Climate.Soil);
        p.Climate.Update(GameTime.TicksPerDay * 4);
        q.Climate.Update(GameTime.TicksPerDay * 4);
        Assert.Equal(p.Climate.Precipitation, q.Climate.Precipitation);
    }
}
