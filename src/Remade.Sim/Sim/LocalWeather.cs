using System;
using System.Numerics;
using Remade.Core;
using Remade.World;

namespace Remade.Sim;

/// <summary>
/// The natural outdoor conditions of the current map, derived from its planet tile every few game minutes:
/// temperature (planet daily mean + diurnal curve), rain/snow from precipitation, cloud, wind, fog, and the
/// ground's wetness and snow cover which integrate precipitation over time. Values ease toward their targets so
/// weather changes are gradual.
/// </summary>
public sealed class LocalWeather
{
    public float Temperature;    // °C outdoors, now
    public float Precipitation;  // mm/day from the planet tile
    public float Rain;           // 0..1 rain intensity (liquid)
    public float Snowfall;       // 0..1 snowfall intensity
    public float Cloud;          // 0..1
    public float WindSpeed;      // m/s
    public Vector2 WindDir = new(1, 0); // map space unit vector (x east, y south)
    public float Fog;            // 0..1
    public float Wetness;        // 0..1 ground wetness / puddles
    public float SnowCover;      // 0..1
    public float SoilMoisture;   // planet soil moisture
    public float PlanetTemperature; // daily mean at the planet tile

    public const int UpdateInterval = 250; // ticks (6 game minutes)

    /// <summary>Debug/testing only (command line): pins the local weather instead of following the planet.</summary>
    public string DebugOverride;

    public void Update(Planet planet, int tile, long tick, bool snap)
    {
        var s = planet.Climate.Sample(tile);
        if (DebugOverride != null) { ApplyOverride(s, tick); return; }
        float hour = GameTime.HourOfDay(tick);
        float dtHours = UpdateInterval / (float)GameTime.TicksPerHour;
        float k = snap ? 1f : 0.08f;

        PlanetTemperature = s.Temperature;
        SoilMoisture = s.SoilMoisture;
        Precipitation = s.Precipitation;
        float targetTemp = s.TemperatureAtHour(hour);
        // precipitation threshold: below ~1.5 mm/day it is dry; heavy rain at ~30 mm/day
        float intensity = Math.Clamp((s.Precipitation - 1.5f) / 28f, 0f, 1f);
        if (intensity > 0) intensity = MathF.Max(intensity, 0.12f);
        float snowShare = Math.Clamp((1.5f - targetTemp) / 3f, 0f, 1f);
        float targetCloud = Math.Clamp(s.Cloud + intensity * 0.4f, 0f, 1f);
        // radiation fog: calm, humid, around dawn
        float dawn = MathF.Max(0f, 1f - MathF.Abs(hour - 6f) / 3.5f);
        float targetFog = Math.Clamp(dawn * (s.SoilMoisture - 0.45f) * 2.2f * Math.Clamp(1f - s.WindSpeed / 6f, 0f, 1f), 0f, 0.85f);
        targetFog = MathF.Max(targetFog, intensity * 0.25f);

        Temperature += (targetTemp - Temperature) * (snap ? 1f : 0.35f);
        Rain += (intensity * (1f - snowShare) - Rain) * k * 2f;
        Snowfall += (intensity * snowShare - Snowfall) * k * 2f;
        Cloud += (targetCloud - Cloud) * k;
        WindSpeed += (s.WindSpeed - WindSpeed) * k;
        var wd = new Vector2(s.WindDir.X, -s.WindDir.Y); // planet (east, north) → map (east, south)
        WindDir = Vector2.Normalize(Vector2.Lerp(WindDir, wd, k) + new Vector2(1e-4f, 0));
        Fog += (targetFog - Fog) * k;

        // ground state integrates over time
        float evaporation = MathF.Max(0.02f, 0.03f + Temperature * 0.004f) * (1f - Cloud * 0.6f) * (1f + WindSpeed * 0.05f);
        if (snap) Wetness = Math.Clamp(s.SoilMoisture * 0.4f + Rain, 0f, 1f);
        else Wetness = Math.Clamp(Wetness + (Rain * 0.9f - evaporation) * dtHours, 0f, 1f);
        if (snap) SnowCover = Temperature < -1f && s.SoilMoisture > 0.3f ? 0.6f : 0f;
        else
        {
            float melt = Temperature > 0.5f ? (Temperature * 0.03f + Rain * 0.2f) : 0f;
            SnowCover = Math.Clamp(SnowCover + (Snowfall * 0.25f - melt) * dtHours, 0f, 1f);
        }
    }

    void ApplyOverride(ClimateSample s, long tick)
    {
        Temperature = s.TemperatureAtHour(GameTime.HourOfDay(tick));
        PlanetTemperature = s.Temperature;
        (Rain, Snowfall, Cloud, Fog, Wetness, SnowCover, WindSpeed) = DebugOverride switch
        {
            "clear" => (0f, 0f, 0.15f, 0f, 0f, 0f, 3f),
            "cloudy" => (0f, 0f, 0.75f, 0.05f, 0.1f, 0f, 5f),
            "rain" => (0.6f, 0f, 0.9f, 0.15f, 0.8f, 0f, 8f),
            "storm" => (1f, 0f, 1f, 0.2f, 1f, 0f, 16f),
            "fog" => (0f, 0f, 0.5f, 0.75f, 0.3f, 0f, 1f),
            "snow" => (0f, 0.6f, 0.85f, 0.15f, 0f, 0.85f, 5f),
            _ => throw new ArgumentException($"unknown weather override '{DebugOverride}' (clear|cloudy|rain|storm|fog|snow)"),
        };
    }

    public string Describe()
    {
        if (Snowfall > 0.5f) return "Heavy snow";
        if (Snowfall > 0.05f) return "Snow";
        if (Rain > 0.6f) return "Downpour";
        if (Rain > 0.25f) return "Rain";
        if (Rain > 0.03f) return "Drizzle";
        if (Fog > 0.35f) return "Fog";
        if (Cloud > 0.75f) return "Overcast";
        if (Cloud > 0.45f) return "Cloudy";
        return "Clear";
    }

    public void Write(System.IO.BinaryWriter w)
    {
        w.Write(Temperature); w.Write(Rain); w.Write(Snowfall); w.Write(Cloud); w.Write(WindSpeed);
        w.Write(WindDir.X); w.Write(WindDir.Y); w.Write(Fog); w.Write(Wetness); w.Write(SnowCover);
    }

    public void Read(System.IO.BinaryReader r)
    {
        Temperature = r.ReadSingle(); Rain = r.ReadSingle(); Snowfall = r.ReadSingle(); Cloud = r.ReadSingle(); WindSpeed = r.ReadSingle();
        WindDir = new Vector2(r.ReadSingle(), r.ReadSingle()); Fog = r.ReadSingle(); Wetness = r.ReadSingle(); SnowCover = r.ReadSingle();
    }
}
