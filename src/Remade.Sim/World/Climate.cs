using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Remade.Core;
using Remade.Diagnostics;

namespace Remade.World;

/// <summary>A travelling pressure system: lows bring cloud, wind and rain; highs bring clear, dry weather.</summary>
public struct WeatherSystem
{
    public Vector3 Center;    // unit vector
    public float Radius;      // radians
    public bool Low;
    public float Strength;    // peak intensity 0..1
    public float AgeDays, LifeDays;
    public float Drift;       // extra eastward speed factor

    public float Intensity => Strength * MathF.Sin(MathF.PI * Math.Clamp(AgeDays / LifeDays, 0f, 1f));
}

/// <summary>Natural conditions of a planet tile at the current moment. Local maps derive their outdoor state from this.</summary>
public readonly struct ClimateSample
{
    public readonly float Temperature;   // °C, daily mean natural outdoor temperature
    public readonly float Precipitation; // mm / day
    public readonly float SoilMoisture;  // 0..1
    public readonly float WindSpeed;     // m/s
    public readonly Vector2 WindDir;     // unit (east, north)
    public readonly float Cloud;         // 0..1
    public readonly float DiurnalRange;  // °C, day-night difference

    public ClimateSample(float t, float p, float s, float ws, Vector2 wd, float c, float dr)
    { Temperature = t; Precipitation = p; SoilMoisture = s; WindSpeed = ws; WindDir = wd; Cloud = c; DiurnalRange = dr; }

    /// <summary>Natural outdoor temperature at an hour of the day (warmest ~15:00, coldest ~03:00).</summary>
    public float TemperatureAtHour(float hour) => Temperature + DiurnalRange * 0.5f * MathF.Cos((hour - 15f) / 24f * MathF.Tau);
}

/// <summary>
/// Dynamic planetary climate. Updated once per game hour for every tile (in parallel, double-buffered):
/// seasonal cycle by latitude/elevation/continentality, travelling weather systems, slow natural anomalies and
/// soil moisture budget. The globe heatmaps display these arrays and local maps read them.
/// </summary>
public sealed class Climate
{
    readonly Planet _p;
    readonly HexSphere _g;

    // front buffers (read by renderer and maps)
    public float[] Temperature { get; private set; }
    public float[] Precipitation { get; private set; }
    public float[] Cloud { get; private set; }
    public float[] WindSpeed { get; private set; }
    public float[] Soil { get; private set; }
    Vector2[] _windDir;
    // back buffers
    float[] _bT, _bP, _bC, _bW, _bS;
    Vector2[] _bWd;

    public readonly List<WeatherSystem> Systems = new();
    Rng _rng;
    public long LastUpdateTick { get; private set; } = -1;
    /// <summary>Incremented on every update, so views know when to refresh.</summary>
    public int Version { get; private set; }

    const int SystemCount = 90;
    /// <summary>Scales weather-modulated precipitation so its long-run mean matches the annual normal (see tests).</summary>
    const float PrecipNormalisation = 1.7f;

    public Climate(Planet planet)
    {
        _p = planet;
        _g = planet.Grid;
        int n = _g.TileCount;
        Temperature = new float[n]; Precipitation = new float[n]; Cloud = new float[n]; WindSpeed = new float[n]; Soil = new float[n];
        _windDir = new Vector2[n];
        _bT = new float[n]; _bP = new float[n]; _bC = new float[n]; _bW = new float[n]; _bS = new float[n]; _bWd = new Vector2[n];
        _rng = Rng.FromParts(planet.Params.Seed, 777);
        for (int i = 0; i < n; i++) Soil[i] = Math.Clamp(planet.AnnualPrecip[i] / 1400f, 0.05f, 1f);
        for (int i = 0; i < SystemCount; i++) Systems.Add(NewSystem(randomAge: true));
        Update(0);
    }

    WeatherSystem NewSystem(bool randomAge)
    {
        // uniform direction on the sphere, biased away from the poles
        Vector3 c;
        do { c = new Vector3(_rng.Range(-1f, 1f), _rng.Range(-0.85f, 0.85f), _rng.Range(-1f, 1f)); }
        while (c.LengthSquared() < 0.01f || c.LengthSquared() > 1f);
        var s = new WeatherSystem
        {
            Center = Vector3.Normalize(c),
            Radius = _rng.Range(0.05f, 0.14f),
            Low = _rng.Chance(0.55f),
            Strength = _rng.Range(0.45f, 1f),
            LifeDays = _rng.Range(2.5f, 7f),
            Drift = _rng.Range(0.7f, 1.3f),
        };
        s.AgeDays = randomAge ? _rng.Range(0f, s.LifeDays) : 0f;
        return s;
    }

    /// <summary>Prevailing wind (east, north) in m/s by latitude: trade winds, westerlies, polar easterlies.</summary>
    public static Vector2 PrevailingWind(float latDeg)
    {
        float a = MathF.Abs(latDeg);
        float east = a < 30f ? -5f * MathF.Sin(a / 30f * MathF.PI)    // easterly trades (blowing west)
                   : a < 60f ? 7f * MathF.Sin((a - 30f) / 30f * MathF.PI) // westerlies
                   : -3f * MathF.Sin((a - 60f) / 30f * MathF.PI);
        float north = a < 30f ? -1.5f * MathF.Sign(latDeg) : 0.8f * MathF.Sign(latDeg);
        return new Vector2(east, north);
    }

    /// <summary>Seasonal mean temperature (no weather) of a tile at a year fraction.</summary>
    public float SeasonalTemperature(int tile, float yearFrac)
    {
        float lat = _g.Latitude(tile);
        // northern summer peaks at 0.375 of the year, winter at 0.875; the south is mirrored
        float phase = MathF.Cos((yearFrac - 0.375f) * MathF.Tau);
        return _p.MeanTemp[tile] + _p.SeasonAmp[tile] * (lat >= 0 ? phase : -phase);
    }

    /// <summary>Seasonal precipitation factor: tropics get wet and dry seasons, mid-latitudes stay fairly even.</summary>
    static float SeasonalPrecipFactor(float lat, float yearFrac)
    {
        float phase = MathF.Cos((yearFrac - 0.375f) * MathF.Tau) * (lat >= 0 ? 1f : -1f); // +1 local summer
        float a = MathF.Abs(lat);
        float monsoon = Math.Clamp(1f - MathF.Abs(a - 15f) / 20f, 0f, 1f);
        return 1f + phase * (0.6f * monsoon + 0.15f * (1f - monsoon));
    }

    /// <summary>Advances the climate to <paramref name="tick"/>. Call regularly (the simulation calls it every game hour).</summary>
    public void Update(long tick)
    {
        if (tick < LastUpdateTick) throw new ArgumentException($"Climate time cannot go backwards ({tick} < {LastUpdateTick})");
        float dtDays = LastUpdateTick < 0 ? 0f : (tick - LastUpdateTick) / (float)GameTime.TicksPerDay;
        LastUpdateTick = tick;
        float year = GameTime.YearFraction(tick);
        float days = GameTime.Days(tick);

        // move and age systems
        for (int i = 0; i < Systems.Count; i++)
        {
            var s = Systems[i];
            if (dtDays > 0)
            {
                s.AgeDays += dtDays;
                if (s.AgeDays >= s.LifeDays) { Systems[i] = NewSystem(false); continue; }
                float lat = MathF.Asin(Math.Clamp(s.Center.Y, -1f, 1f)) * 180f / MathF.PI;
                Vector2 w = PrevailingWind(lat) * s.Drift;
                Vector3 east = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, s.Center) + new Vector3(1e-6f, 0, 0));
                Vector3 north = Vector3.Cross(s.Center, east);
                // ~ wind (m/s) * 86 400 s/day → angular speed on the planet, damped (systems move slower than winds)
                float scale = 0.35f * 86.4f / Planet.RadiusKm * dtDays;
                s.Center = Vector3.Normalize(s.Center + (east * w.X + north * w.Y) * scale);
                if (MathF.Abs(s.Center.Y) > 0.93f) { Systems[i] = NewSystem(false); continue; }
            }
            Systems[i] = s;
        }

        var systems = Systems.ToArray();
        var intensities = new float[systems.Length];
        var cosCut = new float[systems.Length];
        for (int k = 0; k < systems.Length; k++)
        {
            intensities[k] = systems[k].Intensity;
            cosCut[k] = MathF.Cos(MathF.Min(MathF.PI, systems[k].Radius * 2.6f));
        }
        float tAnimZ = days * 0.035f;

        var soil = Soil;
        Parallel.For(0, _g.TileCount, i =>
        {
            Vector3 d = _g.Centers[i];
            float lat = _g.Latitude(i);
            float tSeason = SeasonalTemperature(i, year);
            float natural = _p.ClimateNoise.Get(d.X * 1.7f, d.Y * 1.7f, d.Z * 1.7f + tAnimZ) * 2.4f;
            float tempAnom = natural, precipMul = 0.42f, wind = 0f, cloud = 0.25f;
            Vector2 swirl = Vector2.Zero;
            _g.Frame(i, out var e, out var nn);
            for (int k = 0; k < systems.Length; k++)
            {
                float dot = Vector3.Dot(d, systems[k].Center);
                if (dot < cosCut[k]) continue;
                float ang = MathF.Acos(Math.Min(1f, dot)) / systems[k].Radius;
                float w = MathF.Exp(-ang * ang) * intensities[k];
                if (systems[k].Low)
                {
                    precipMul += w * 3.2f; tempAnom -= w * 1.8f; wind += w * 11f; cloud += w * 0.85f;
                    // cyclonic swirl: counter-clockwise in the north, clockwise in the south
                    Vector3 r = Vector3.Cross(systems[k].Center, d) * (lat >= 0 ? 1f : -1f);
                    swirl += new Vector2(Vector3.Dot(r, e), Vector3.Dot(r, nn)) * (w * 9f / MathF.Max(0.02f, systems[k].Radius) * 0.02f);
                }
                else
                {
                    precipMul -= w * 0.45f; tempAnom += w * 1.3f; wind += w * 2f; cloud -= w * 0.4f;
                }
            }
            precipMul = MathF.Max(0.02f, precipMul);
            float baseDaily = _p.AnnualPrecip[i] / 365f * SeasonalPrecipFactor(lat, year);
            float precip = baseDaily * precipMul * PrecipNormalisation;
            float temp = tSeason + tempAnom;
            float humidity = Math.Clamp(_p.AnnualPrecip[i] / 1600f, 0f, 1f);
            cloud = Math.Clamp(cloud + humidity * 0.25f + MathF.Min(0.4f, precip / 25f), 0f, 1f);
            Vector2 prevailing = PrevailingWind(lat);
            Vector2 wv = prevailing * (0.6f + 0.4f * MathF.Max(0f, 1f - MathF.Max(0f, _p.Elevation[i]) / 4000f)) + swirl;
            float ws = wv.Length() + wind;

            float s0 = soil[i];
            float evap = MathF.Max(0.004f, 0.012f + 0.0022f * temp) * (1f - 0.5f * cloud);
            float s1 = Math.Clamp(s0 + (precip / 22f - evap) * dtDays, 0f, 1f);
            if (_p.Elevation[i] < 0) s1 = 1f;

            _bT[i] = temp;
            _bP[i] = precip;
            _bC[i] = cloud;
            _bW[i] = ws;
            _bWd[i] = wv.LengthSquared() > 1e-6f ? Vector2.Normalize(wv) : new Vector2(1, 0);
            _bS[i] = s1;
        });

        (Temperature, _bT) = (_bT, Temperature);
        (Precipitation, _bP) = (_bP, Precipitation);
        (Cloud, _bC) = (_bC, Cloud);
        (WindSpeed, _bW) = (_bW, WindSpeed);
        (_windDir, _bWd) = (_bWd, _windDir);
        (Soil, _bS) = (_bS, Soil);
        Version++;
    }

    public ClimateSample Sample(int tile)
    {
        if ((uint)tile >= (uint)_g.TileCount) throw new ArgumentOutOfRangeException(nameof(tile), tile, $"0..{_g.TileCount - 1}");
        float humidity = Math.Clamp(Soil[tile] * 0.6f + Cloud[tile] * 0.4f, 0f, 1f);
        float diurnal = 4f + 10f * (1f - humidity);
        return new ClimateSample(Temperature[tile], Precipitation[tile], Soil[tile], WindSpeed[tile], _windDir[tile], Cloud[tile], diurnal);
    }

    // ---------------------------------------------------------------- persistence

    public void Write(System.IO.BinaryWriter w)
    {
        w.Write(LastUpdateTick);
        _rng.GetState(out ulong a, out ulong b, out ulong c, out ulong d);
        w.Write(a); w.Write(b); w.Write(c); w.Write(d);
        w.Write(Systems.Count);
        foreach (var s in Systems)
        {
            w.Write(s.Center.X); w.Write(s.Center.Y); w.Write(s.Center.Z);
            w.Write(s.Radius); w.Write(s.Low); w.Write(s.Strength); w.Write(s.AgeDays); w.Write(s.LifeDays); w.Write(s.Drift);
        }
        w.Write(Soil.Length);
        foreach (float v in Soil) w.Write(v);
    }

    public void Read(System.IO.BinaryReader r)
    {
        long tick = r.ReadInt64();
        _rng = Rng.FromState(r.ReadUInt64(), r.ReadUInt64(), r.ReadUInt64(), r.ReadUInt64());
        int count = r.ReadInt32();
        if (count < 0 || count > 10000) throw new System.IO.InvalidDataException($"Climate: bad weather system count {count}");
        Systems.Clear();
        for (int i = 0; i < count; i++)
        {
            Systems.Add(new WeatherSystem
            {
                Center = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
                Radius = r.ReadSingle(), Low = r.ReadBoolean(), Strength = r.ReadSingle(),
                AgeDays = r.ReadSingle(), LifeDays = r.ReadSingle(), Drift = r.ReadSingle(),
            });
        }
        int n = r.ReadInt32();
        if (n != _g.TileCount) throw new System.IO.InvalidDataException($"Climate: soil array has {n} tiles, planet has {_g.TileCount}");
        for (int i = 0; i < n; i++) Soil[i] = r.ReadSingle();
        LastUpdateTick = tick;
        // recompute the derived arrays at the saved moment without advancing time
        LastUpdateTick = -1;
        Update(tick);
    }
}
