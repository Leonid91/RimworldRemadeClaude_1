using System;
using Godot;
using Remade.Core;
using Remade.Sim;

namespace Remade.Game.Render;

/// <summary>
/// Sun, moon, sky and atmosphere. The sun's position follows real solar geometry for the colony's latitude, the day
/// of the year and the hour (so days are long in summer and short in winter); its colour warms near the horizon.
/// Fog, volumetric haze, cloud cover, wind and wetness come from the local weather, which comes from the planet.
/// The look (procedural sky, coloured ambient that turns rosy at dawn and blue at night, ACES, saturation, moonlight)
/// follows the first prototype (RimworldTest). Also publishes the shader globals every frame.
/// </summary>
public partial class Lighting : Node3D
{
    readonly GameSim _sim;
    readonly DirectionalLight3D _sun, _moon;
    readonly WorldEnvironment _env;
    readonly Godot.Environment _e;
    readonly ProceduralSkyMaterial _sky;
    Vector2 _cloudOffset;
    public float Daylight { get; private set; }
    public Vector3 SunDir { get; private set; }

    public Lighting(GameSim sim)
    {
        _sim = sim;
        _sky = new ProceduralSkyMaterial { SunAngleMax = 20f, SunCurve = 0.1f, SkyCover = StarPanorama(), SkyCoverModulate = new Color(1, 1, 1, 0) };
        _e = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = new Sky { SkyMaterial = _sky, RadianceSize = Sky.RadianceSizeEnum.Size128, ProcessMode = Sky.ProcessModeEnum.Incremental },
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightEnergy = 0.65f,
            ReflectedLightSource = Godot.Environment.ReflectionSource.Sky,
            TonemapMode = Godot.Environment.ToneMapper.Aces,
            TonemapExposure = 0.95f,
            SsaoEnabled = Settings.Ssao, SsaoRadius = 1.4f, SsaoIntensity = 1.2f, SsaoPower = 1.2f, SsaoDetail = 0.6f, SsaoLightAffect = 0.15f,
            GlowEnabled = true, GlowIntensity = 0.55f, GlowStrength = 1.0f, GlowBloom = 0.04f, GlowHdrThreshold = 1.1f,
            GlowBlendMode = Godot.Environment.GlowBlendModeEnum.Softlight,
            FogEnabled = true, FogMode = Godot.Environment.FogModeEnum.Exponential, FogDensity = 0.0012f, FogSkyAffect = 0.2f,
            FogSunScatter = 0.25f, FogAerialPerspective = 0.35f, FogLightColor = new Color(0.62f, 0.68f, 0.78f),
            VolumetricFogEnabled = Settings.VolumetricFog, VolumetricFogDensity = 0.004f, VolumetricFogAnisotropy = 0.55f,
            VolumetricFogLength = 120f, VolumetricFogDetailSpread = 2f, VolumetricFogAmbientInject = 0.3f, VolumetricFogSkyAffect = 0.15f,
            AdjustmentEnabled = true, AdjustmentSaturation = 1.18f, AdjustmentContrast = 1.06f,
        };
        _e.SetGlowLevel(1, 0.6f); _e.SetGlowLevel(2, 0.8f); _e.SetGlowLevel(3, 0.6f); _e.SetGlowLevel(4, 0.3f);
        _env = new WorldEnvironment { Environment = _e };
        AddChild(_env);
        _sun = new DirectionalLight3D
        {
            ShadowEnabled = Settings.ShadowQuality > 0, DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel4Splits,
            ShadowBlur = 1.2f, LightAngularDistance = 1.2f, ShadowBias = 0.04f, ShadowNormalBias = 1.2f,
            DirectionalShadowBlendSplits = true, DirectionalShadowFadeStart = 0.85f, LightVolumetricFogEnergy = 0.8f,
        };
        AddChild(_sun);
        _moon = new DirectionalLight3D { ShadowEnabled = Settings.ShadowQuality >= 2, LightColor = new Color(0.55f, 0.66f, 1f), LightEnergy = 0.0f, ShadowBlur = 2f, DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel2Splits, LightVolumetricFogEnergy = 0.5f };
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

    public Godot.Environment Env => _e;
    public void SetShadows(bool on) { _sun.ShadowEnabled = on; _moon.ShadowEnabled = on; }

    public void ApplyQuality()
    {
        int q = Settings.ShadowQuality;
        _sun.ShadowEnabled = q > 0;
        _moon.ShadowEnabled = q >= 2;
        _sun.DirectionalShadowMode = q >= 3 ? DirectionalLight3D.ShadowMode.Parallel4Splits : DirectionalLight3D.ShadowMode.Parallel2Splits;
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
        float elevDeg = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(elev, -1f, 1f)));
        float dayK = Mathf.SmoothStep(-5f, 10f, elevDeg);
        float warm = 1f - Mathf.SmoothStep(3f, 28f, elevDeg);
        Daylight = dayK;
        float overcast = Mathf.Clamp(w.Cloud * 0.75f + w.Rain * 0.5f, 0f, 1f);
        var sunCol = new Color(1.0f, 0.93f, 0.84f).Lerp(new Color(1.0f, 0.52f, 0.26f), warm);
        // mist and overcast scatter the light: the low sun loses its orange in fog
        sunCol = sunCol.Lerp(new Color(0.85f, 0.88f, 0.95f), Mathf.Clamp(overcast * 0.6f + w.Fog * 0.5f, 0f, 0.85f));
        _sun.LightColor = sunCol;
        _sun.LightEnergy = dayK * (2.3f - 0.8f * warm) * (1f - overcast * 0.7f) * (1f - w.Fog * 0.3f);
        _sun.Visible = _sun.LightEnergy > 0.01f;
        if (_sun.Visible) _sun.LookAtFromPosition(focus + sun * 100f, focus, Mathf.Abs(sun.Y) > 0.99f ? Vector3.Forward : Vector3.Up);

        // the moon rides roughly opposite the sun, a little offset so it is not straight overhead at midnight
        var moon = new Vector3(-sun.X * 0.8f + 0.25f, Mathf.Max(0.12f, -sun.Y * 0.85f + 0.14f), -sun.Z * 0.8f + 0.2f).Normalized();
        float nightK = 1f - Mathf.SmoothStep(-6f, 4f, elevDeg);
        _moon.LightEnergy = nightK * 1.0f * (1f - overcast * 0.55f);
        _moon.Visible = _moon.LightEnergy > 0.01f;
        if (_moon.Visible) _moon.LookAtFromPosition(focus + moon * 100f, focus, Vector3.Up);
        float night = nightK;

        // ambient: blue at night, rosy at dawn and dusk, neutral by day; overcast greys it
        var amb = Lerp3(new Color(0.22f, 0.3f, 0.52f), new Color(0.62f, 0.45f, 0.48f), new Color(0.62f, 0.67f, 0.74f), dayK, warm);
        amb = amb.Lerp(new Color(0.55f, 0.58f, 0.62f), overcast * dayK * 0.5f);
        _e.AmbientLightColor = amb;
        _e.AmbientLightEnergy = Mathf.Lerp(0.68f, 0.62f + overcast * 0.35f, dayK);

        // sky
        _sky.SkyTopColor = Lerp3(new Color(0.02f, 0.03f, 0.08f), new Color(0.55f, 0.38f, 0.45f), new Color(0.32f, 0.52f, 0.85f), dayK, warm)
            .Lerp(new Color(0.5f, 0.52f, 0.56f), overcast * dayK);
        _sky.SkyHorizonColor = Lerp3(new Color(0.05f, 0.06f, 0.12f), new Color(1f, 0.6f, 0.4f), new Color(0.7f, 0.8f, 0.92f), dayK, warm);
        _sky.GroundHorizonColor = _sky.SkyHorizonColor;
        _sky.GroundBottomColor = _sky.SkyHorizonColor.Lerp(_sky.SkyTopColor, 0.5f);
        _sky.SkyEnergyMultiplier = 0.4f + dayK * 0.8f;
        _sky.SkyCoverModulate = new Color(1, 1, 1, nightK * (1f - overcast));
        _e.AdjustmentSaturation = Mathf.Lerp(0.9f, 1.2f, dayK) - overcast * 0.12f;
        _e.TonemapExposure = Mathf.Lerp(1.3f, 0.95f, dayK);
        float low = warm;

        // fog: aerial haze, morning mist, rain murk
        float rainMurk = w.Rain * 0.6f + w.Snowfall * 0.8f;
        _e.FogDensity = 0.0012f + w.Fog * 0.0055f + rainMurk * 0.0018f;
        // fog is pale; a hint of warmth when the sun is low, dark blue at night
        _e.FogLightColor = new Color(0.70f, 0.74f, 0.80f).Lerp(new Color(0.86f, 0.78f, 0.70f), low * Daylight * 0.5f).Lerp(new Color(0.10f, 0.12f, 0.18f), night);
        _e.FogSunScatter = 0.08f + w.Fog * 0.2f;
        _e.VolumetricFogDensity = 0.0015f + w.Fog * 0.012f + rainMurk * 0.004f;
        _e.VolumetricFogAlbedo = new Color(0.9f, 0.92f, 0.95f);
        _e.FogDepthBegin = 0;

        // shadow distance follows zoom
        // zoomed out, one shadow split covers the view at plenty of resolution and halves the shadow work
        if (Settings.ShadowQuality < 3)
            _sun.DirectionalShadowMode = camDistance > 24f ? DirectionalLight3D.ShadowMode.Orthogonal : DirectionalLight3D.ShadowMode.Parallel2Splits;
        // shadows cover what the camera shows (the visible ground reaches about 1.3× the camera distance)
        _sun.DirectionalShadowMaxDistance = Mathf.Clamp(camDistance * 1.35f + 12f, 35f, 160f);
        _moon.DirectionalShadowMaxDistance = Mathf.Clamp(camDistance * 1.3f + 10f, 30f, 120f);

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

    static Color Lerp3(Color night, Color dawn, Color day, float k, float warm) => night.Lerp(day, k).Lerp(dawn, warm * k);

    /// <summary>Continuous season 0..4 for the colony's hemisphere (0 = start of spring).</summary>
    float SeasonValue()
    {
        float yf = GameTime.YearFraction(_sim.Tick);
        if (_sim.Latitude < 0) yf = (yf + 0.5f) % 1f;
        return yf * 4f;
    }
}
