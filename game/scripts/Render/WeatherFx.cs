using Godot;
using Remade.Sim;

namespace Remade.Game.Render;

/// <summary>Rain streaks and snowflakes (GPU particles) in a box that follows the camera focus, slanted by the wind.</summary>
public partial class WeatherFx : Node3D
{
    readonly GameSim _sim;
    readonly GpuParticles3D _rain, _snow;
    readonly ParticleProcessMaterial _rainPm, _snowPm;

    public WeatherFx(GameSim sim)
    {
        _sim = sim;
        _rainPm = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box, EmissionBoxExtents = new Vector3(45, 1, 45),
            Direction = new Vector3(0, -1, 0), Spread = 3, InitialVelocityMin = 24, InitialVelocityMax = 30, Gravity = new Vector3(0, -9.8f, 0),
        };
        var rainMesh = new QuadMesh { Size = new Vector2(0.02f, 0.9f) };
        rainMesh.Material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color(0.75f, 0.8f, 0.9f, 0.32f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha, BillboardMode = BaseMaterial3D.BillboardModeEnum.FixedY,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        _rain = new GpuParticles3D
        {
            Amount = 9000, Lifetime = 1.6f, ProcessMaterial = _rainPm, DrawPass1 = rainMesh, Emitting = false,
            VisibilityAabb = new Aabb(new Vector3(-50, -40, -50), new Vector3(100, 60, 100)), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            FixedFps = 0, Interpolate = false,
        };
        AddChild(_rain);

        _snowPm = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box, EmissionBoxExtents = new Vector3(40, 1, 40),
            Direction = new Vector3(0, -1, 0), Spread = 20, InitialVelocityMin = 1.2f, InitialVelocityMax = 2.0f, Gravity = new Vector3(0, -0.6f, 0),
            TurbulenceEnabled = true, TurbulenceNoiseStrength = 1.5f, TurbulenceNoiseScale = 6f, TurbulenceInfluenceMin = 0.05f, TurbulenceInfluenceMax = 0.15f,
        };
        var flake = new QuadMesh { Size = new Vector2(0.07f, 0.07f) };
        flake.Material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color(1, 1, 1, 0.85f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha, BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
        };
        _snow = new GpuParticles3D
        {
            Amount = 7000, Lifetime = 14f, ProcessMaterial = _snowPm, DrawPass1 = flake, Emitting = false, Preprocess = 6,
            VisibilityAabb = new Aabb(new Vector3(-45, -40, -45), new Vector3(90, 60, 90)), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_snow);
    }

    public void Update(Vector3 focus)
    {
        var w = _sim.Weather;
        Position = new Vector3(focus.X, focus.Y + 22f, focus.Z);
        var wind = new Vector3(w.WindDir.X, 0, w.WindDir.Y) * w.WindSpeed;
        _rain.Emitting = w.Rain > 0.03f;
        _rain.AmountRatio = Mathf.Clamp(w.Rain * 1.2f, 0.05f, 1f);
        _rainPm.Gravity = new Vector3(wind.X * 0.6f, -9.8f, wind.Z * 0.6f);
        _snow.Emitting = w.Snowfall > 0.03f;
        _snow.AmountRatio = Mathf.Clamp(w.Snowfall * 1.2f, 0.05f, 1f);
        _snowPm.Gravity = new Vector3(wind.X * 0.15f, -0.6f, wind.Z * 0.15f);
    }
}
