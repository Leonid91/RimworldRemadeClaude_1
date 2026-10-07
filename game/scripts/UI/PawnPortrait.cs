using Godot;
using Remade.Game.Render;
using Remade.Pawns;

namespace Remade.Game.UI;

/// <summary>A small 3D stage showing a colonist slowly turning, lit with a key and a rim light.</summary>
public partial class PawnPortrait : SubViewportContainer
{
    readonly SubViewport _vp;
    readonly PawnModel _model;
    readonly Pawn _pawn;
    float _t;

    public PawnPortrait(Pawn p)
    {
        _pawn = p;
        Stretch = true;
        MouseFilter = MouseFilterEnum.Ignore;
        _vp = new SubViewport { OwnWorld3D = true, TransparentBg = true, Msaa3D = Viewport.Msaa.Msaa4X };
        AddChild(_vp);
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.ClearColor, BackgroundColor = new Color(0, 0, 0, 0),
            AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(0.35f, 0.42f, 0.5f), AmbientLightEnergy = 0.8f,
            TonemapMode = Godot.Environment.ToneMapper.Agx,
        };
        _vp.AddChild(new WorldEnvironment { Environment = env });
        var key = new DirectionalLight3D { LightEnergy = 1.6f, LightColor = new Color(1f, 0.95f, 0.88f), Rotation = new Vector3(-0.5f, 0.6f, 0) };
        var rim = new DirectionalLight3D { LightEnergy = 1.2f, LightColor = new Color(0.5f, 0.85f, 0.8f), Rotation = new Vector3(-0.2f, 2.8f, 0) };
        _vp.AddChild(key); _vp.AddChild(rim);
        var camPos = new Vector3(0, 1.05f, 3.9f);
        var cam = new Camera3D { Fov = 30, Transform = new Transform3D(Basis.LookingAt(new Vector3(0, 0.95f, 0) - camPos, Vector3.Up), camPos) };
        _vp.AddChild(cam);
        _model = new PawnModel(p);
        _vp.AddChild(_model);
        var floor = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.6f, BottomRadius = 0.6f, Height = 0.02f }, MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.1f, 0.14f, 0.16f), Roughness = 0.9f } };
        _vp.AddChild(floor);
    }

    public override void _Process(double delta)
    {
        _t += (float)delta;
        _model.Animate(_pawn, Vector3.Zero, (float)delta, 1f);
        _model.Rotation = new Vector3(0, Mathf.Sin(_t * 0.4f) * 0.7f + Mathf.Pi * 1.5f, 0); // facing the camera
    }
}
