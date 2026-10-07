using System;
using Godot;
using Remade.Core;
using Remade.Sim;

namespace Remade.Game.Render;

/// <summary>
/// Sun, moon, sky and atmosphere. The sun's position follows real solar geometry for the colony's latitude, the day
/// of the year and the hour (so days are long in summer and short in winter); its colour warms near the horizon.
/// Fog, volumetric haze, cloud cover, wind and wetness come from the local weather, which comes from the planet.
/// Also publishes the shader globals every frame.
/// </summary>
public partial class Lighting : Node3D
{
    readonly GameSim _sim;
    readonly DirectionalLight3D _sun, _moon;
    readonly WorldEnvironment _env;
    readonly Godot.Environment _e;
    readonly PhysicalSkyMaterial _sky;
    Vector2 _cloudOffset;
    public float Daylight { get; private set; }
    public Vector3 SunDir { get; private set; }

    public Lighting(GameSim sim)
    {
        _sim = sim;
        _sky = new PhysicalSkyMaterial
        {
            RayleighCoefficient = 2.2f, RayleighColor = new Color(0.30f, 0.45f, 0.85f), MieCoefficient = 0.006f, MieEccentricity = 0.82f,
            MieColor = new Color(0.85f, 0.78f, 0.65f), Turbidity = 9f, SunDiskScale = 1.4f, GroundColor = new Color(0.12f, 0.12f, 0.1f),
            EnergyMultiplier = 1f, NightSky = StarPanorama(),
        };
        _e = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = new Sky { SkyMaterial = _sky, RadianceSize = Sky.RadianceSizeEnum.Size128, ProcessMode = Sky.ProcessModeEnum.Incremental },
            AmbientLightSource = Godot.Environment.AmbientSource.Sky,
            AmbientLightSkyContribution = 1f,
            AmbientLightEnergy = 1f,
            ReflectedLightSource = Godot.Environment.ReflectionSource.Sky,
            TonemapMode = Godot.Environment.ToneMapper.Agx,
            TonemapExposure = 1.05f,
            SsaoEnabled = Settings.Ssao, SsaoRadius = 1.4f, SsaoIntensity = 2.2f, SsaoPower = 1.6f, SsaoDetail = 0.6f,
            GlowEnabled = true, GlowIntensity = 0.55f, GlowBloom = 0.04f, GlowHdrThreshold = 1.1f,
            FogEnabled = true, FogMode = Godot.Environment.FogModeEnum.Exponential, FogDensity = 0.0012f, FogSkyAffect = 0.2f,
            FogSunScatter = 0.25f, FogAerialPerspective = 0.35f, FogLightColor = new Color(0.62f, 0.68f, 0.78f),
            VolumetricFogEnabled = Settings.VolumetricFog, VolumetricFogDensity = 0.004f, VolumetricFogAnisotropy = 0.55f,
            VolumetricFogLength = 120f, VolumetricFogDetailSpread = 2f, VolumetricFogAmbientInject = 0.3f, VolumetricFogSkyAffect = 0.15f,
            AdjustmentEnabled = true, AdjustmentSaturation = 1.08f, AdjustmentContrast = 1.04f,
        };
        _env = new WorldEnvironment { Environment = _e };
        AddChild(_env);
        _sun = new DirectionalLight3D
        {
            ShadowEnabled = Settings.ShadowQuality > 0, DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel4Splits,
            ShadowBlur = 1.2f, LightAngularDistance = 0.6f, ShadowBias = 0.04f, ShadowNormalBias = 1.2f,
            DirectionalShadowBlendSplits = true, DirectionalShadowFadeStart = 0.85f, LightVolumetricFogEnergy = 1.4f,
        };
        AddChild(_sun);
        _moon = new DirectionalLight3D { ShadowEnabled = Settings.ShadowQuality >= 2, LightColor = new Color(0.55f, 0.65f, 0.95f), LightEnergy = 0.0f, DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel2Splits, LightVolumetricFogEnergy = 0.5f };
        AddChild(_moon);
        ApplyQuality();
    }

    /// <summary>Equirectangular star field for the night sky (generated once).</summary>
    static ImageTexture _stars;
    static ImageTexture StarPanorama()
    {
        if (_stars != null) return _stars;
        const int W = 2048, H = 1024;
        var img = Image.CreateEmpty(W, H, false, Image.Format.Rgb8);
        img.Fill(new Color(0.004f, 0.006f, 0.012f));
        var rng = new Rng(4040);
        for (int k = 0; k < 5200; k++)
        {
            int x = rng.Range(0, W), y = rng.Range(0, H);
            float b = MathF.Pow(rng.NextFloat(), 6f) * 1.5f + 0.08f;
            var tint = new Color(0.8f, 0.85f, 1f).Lerp(new Color(1f, 0.85f, 0.7f), rng.NextFloat());
            img.SetPixel(x, y, tint * b);
            if (b > 0.8f) for (int d = 1; d <= 1; d++) { img.SetPixel((x + d) % W, y, tint * b * 0.35f); img.SetPixel(x, Math.Min(H - 1, y + d), tint * b * 0.35f); }
        }
        _stars = ImageTexture.CreateFromImage(img);
        return _stars;
    }

    public void ApplyQuality()
    {
        int q = Settings.ShadowQuality;
        _sun.ShadowEnabled = q > 0;
        _moon.ShadowEnabled = q >= 2;
        _sun.DirectionalShadowMode = q >= 2 ? DirectionalLight3D.ShadowMode.Parallel4Splits : DirectionalLight3D.ShadowMode.Parallel2Splits;
        RenderingServer.DirectionalShadowAtlasSetSize(q >= 3 ? 8192 : q == 2 ? 4096 : 2048, true);
        RenderingServer.DirectionalSoftShadowFilterSetQuality(q >= 3 ? RenderingServer.ShadowQuality.SoftHigh : q == 2 ? RenderingServer.ShadowQuality.SoftMedium : RenderingServer.ShadowQuality.SoftLow);
        _e.SsaoEnabled = Settings.Ssao;
        _e.VolumetricFogEnabled = Settings.VolumetricFog;
    }

    /// <summary>Unit vector toward the sun (x east, y up, z south) from latitude, day of year and hour.</summary>
    public static Vector3 SolarDirection(float latDeg, long tick)
    {
        float yearFrac = GameTime.YearFraction(tick);
        // declination peaks at the northern summer solstice (0.375 of the year)
        float decl = 23.44f * MathF.Cos((yearFrac - 0.375f) * MathF.Tau) * MathF.PI / 180f;
        float lat = latDeg * MathF.PI / 180f;
        float hourAngle = (GameTime.HourOfDay(tick) - 12f) * 15f * MathF.PI / 180f;
        float east = -MathF.Cos(decl) * MathF.Sin(hourAngle);
        float north = MathF.Cos(lat) * MathF.Sin(decl) - MathF.Sin(lat) * MathF.Cos(decl) * MathF.Cos(hourAngle);
        float up = MathF.Sin(lat) * MathF.Sin(decl) + MathF.Cos(lat) * MathF.Cos(decl) * MathF.Cos(hourAngle);
        return new Vector3(east, up, -north).Normalized();
    }

    public void Update(float dt, Vector3 focus, float camDistance)
    {
        var w = _sim.Weather;
        float lat = _sim.Latitude;
        var sun = SolarDirection(lat, _sim.Tick);
        SunDir = sun;
        float elev = sun.Y;
        Daylight = Mathf.SmoothStep(-0.12f, 0.22f, elev);
        float low = 1f - Mathf.SmoothStep(0.0f, 0.45f, elev);
        var sunCol = new Color(1f, 0.97f, 0.92f).Lerp(new Color(1f, 0.55f, 0.28f), low * 0.85f);
        float overcast = Mathf.Clamp((w.Cloud - 0.35f) / 0.65f, 0f, 1f);
        _sun.LightColor = sunCol;
        _sun.LightEnergy = Mathf.SmoothStep(-0.04f, 0.12f, elev) * (1.9f - overcast * 1.25f - w.Fog * 0.4f);
        _sun.Visible = elev > -0.06f;
        if (_sun.Visible) _sun.LookAtFromPosition(focus + sun * 100f, focus, Mathf.Abs(sun.Y) > 0.99f ? Vector3.Forward : Vector3.Up);

        // the moon rides roughly opposite the sun, a little offset so it is not straight overhead at midnight
        var moon = new Vector3(-sun.X * 0.8f + 0.25f, Mathf.Max(0.15f, -sun.Y), -sun.Z * 0.8f).Normalized();
        float night = 1f - Daylight;
        _moon.LightEnergy = night * 0.22f * (1f - overcast * 0.75f);
        _moon.Visible = night > 0.05f;
        if (_moon.Visible) _moon.LookAtFromPosition(focus + moon * 100f, focus, Vector3.Up);

        // sky and ambient
        _sky.EnergyMultiplier = 0.35f + Daylight * 0.75f;
        _sky.Turbidity = 8f + overcast * 12f + w.Fog * 8f;
        _sky.MieCoefficient = 0.005f + overcast * 0.03f + w.Fog * 0.02f;
        _e.AmbientLightEnergy = 0.25f + Daylight * 0.75f - overcast * 0.15f;
        _e.AmbientLightSkyContribution = 0.85f;
        _e.AmbientLightColor = new Color(0.10f, 0.13f, 0.22f);
        _e.TonemapExposure = 1.05f + night * 0.55f;

        // fog: aerial haze, morning mist, rain murk
        float rainMurk = w.Rain * 0.6f + w.Snowfall * 0.8f;
        _e.FogDensity = 0.0008f + w.Fog * 0.012f + rainMurk * 0.004f;
        _e.FogLightColor = new Color(0.62f, 0.68f, 0.78f).Lerp(new Color(0.85f, 0.65f, 0.5f), low * Daylight).Lerp(new Color(0.06f, 0.08f, 0.13f), night);
        _e.FogSunScatter = 0.15f + w.Fog * 0.5f;
        _e.VolumetricFogDensity = 0.002f + w.Fog * 0.035f + rainMurk * 0.01f;
        _e.VolumetricFogAlbedo = new Color(0.9f, 0.92f, 0.95f);
        _e.FogDepthBegin = 0;

        // shadow distance follows zoom
        _sun.DirectionalShadowMaxDistance = Mathf.Clamp(camDistance * 2.6f, 40f, 260f);
        _moon.DirectionalShadowMaxDistance = Mathf.Clamp(camDistance * 1.8f, 30f, 160f);

        // shader globals
        float windK = Mathf.Clamp(w.WindSpeed / 12f, 0.05f, 1.5f);
        var wd = new Vector2(w.WindDir.X, w.WindDir.Y);
        _cloudOffset -= wd * (2f + w.WindSpeed * 0.8f) * dt * Math.Max(1, _sim.SpeedMultiplier) * 0.3f;
        RenderingServer.GlobalShaderParameterSet("daylight", Daylight);
        RenderingServer.GlobalShaderParameterSet("wind_dir", wd);
        RenderingServer.GlobalShaderParameterSet("wind_strength", windK);
        RenderingServer.GlobalShaderParameterSet("cloud_offset", _cloudOffset);
        RenderingServer.GlobalShaderParameterSet("cloud_cover", w.Cloud);
        RenderingServer.GlobalShaderParameterSet("wetness", w.Wetness);
        RenderingServer.GlobalShaderParameterSet("snow_cover", w.SnowCover);
        RenderingServer.GlobalShaderParameterSet("season", SeasonValue());
        RenderingServer.GlobalShaderParameterSet("focus_pos", focus);
    }

    /// <summary>Continuous season 0..4 for the colony's hemisphere (0 = start of spring).</summary>
    float SeasonValue()
    {
        float yf = GameTime.YearFraction(_sim.Tick);
        if (_sim.Latitude < 0) yf = (yf + 0.5f) % 1f;
        return yf * 4f;
    }
}
