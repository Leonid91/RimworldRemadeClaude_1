using System;
using Godot;
using Remade.Pawns;
using Remade.Things;

namespace Remade.Game.Render;

/// <summary>
/// A colonist as a small jointed puppet (hips, torso, head, two-segment arms and legs) built from procedural meshes,
/// coloured by skin, hair and the apparel actually worn on each layer, holding the equipped bow.
/// Poses are animated procedurally from the simulation state every frame.
/// </summary>
public partial class PawnModel : Node3D
{
    Node3D _yaw, _hips, _torso, _head, _shoulderL, _shoulderR, _elbowL, _elbowR, _thighL, _thighR, _kneeL, _kneeR, _bowSocket;
    MeshInstance3D _bow, _arrowNocked;
    float _phase, _breath;
    PawnAnim _lastAnim;
    float _animBlend;
    string _outfitKey = "";
    Pawn _pawn;

    static readonly StandardMaterial3D Vc = new() { VertexColorUseAsAlbedo = true, VertexColorIsSrgb = true, Roughness = 0.8f };

    public PawnModel(Pawn p)
    {
        _pawn = p;
        Rebuild(p);
    }

    static Color Hex(uint rgb) => Remade.Game.UI.UiKit.Rgb(rgb);

    static Color LayerColor(Pawn p, BodyRegion r, Color skin)
    {
        foreach (var layer in new[] { ApparelLayer.Outer, ApparelLayer.Middle, ApparelLayer.Skin })
        {
            var a = p.WornAt(layer, r);
            if (a != null) return Hex(a.Def.Color);
        }
        return skin;
    }

    static string OutfitKey(Pawn p)
    {
        var s = string.Join(",", p.Apparel.ConvertAll(a => a.Def.Id));
        return s + "|" + (p.Weapon?.Def.Id ?? "-");
    }

    public void Rebuild(Pawn p)
    {
        foreach (var c in GetChildren()) { RemoveChild(c); c.QueueFree(); }
        _outfitKey = OutfitKey(p);
        var skin = Hex(p.SkinColor);
        var hair = Hex(p.HairColor);
        var shirt = LayerColor(p, BodyRegion.Torso, skin);
        var sleeve = LayerColor(p, BodyRegion.ArmL, skin);
        var pants = LayerColor(p, BodyRegion.LegL, skin);
        var boots = LayerColor(p, BodyRegion.FootL, skin * 0.9f);
        bool female = p.Sex == Sex.Female;
        float scale = p.HeightM / 1.75f;
        float bulk = Math.Clamp(p.BodyMassKg / (p.HeightM * p.HeightM) / 23f, 0.85f, 1.25f);

        _yaw = new Node3D();
        AddChild(_yaw);
        _yaw.Scale = new Vector3(scale * bulk, scale, scale * bulk);
        _hips = Joint(_yaw, new Vector3(0, 0.95f, 0));
        Part(_hips, b => { b.Color = pants; b.Ellipsoid(new Vector3(0, 0.02f, 0), new Vector3(female ? 0.17f : 0.16f, 0.11f, 0.11f), 10, 6); });
        _torso = Joint(_hips, new Vector3(0, 0.06f, 0));
        Part(_torso, b =>
        {
            b.Color = shirt;
            b.Ellipsoid(new Vector3(0, 0.27f, 0), new Vector3(female ? 0.15f : 0.18f, 0.27f, 0.11f), 12, 8);
            b.Ellipsoid(new Vector3(0, 0.44f, 0), new Vector3(female ? 0.18f : 0.21f, 0.09f, 0.12f), 10, 6);
            b.Color = skin;
            b.Tube(new Vector3(0, 0.5f, 0), new Vector3(0, 0.6f, 0), 0.05f, 0.045f, 8);
            if (p.Apparel.Exists(a => a.Def.Layers[0] == ApparelLayer.Belt))
            {
                b.Color = new Color(0.30f, 0.20f, 0.12f);
                b.Tube(new Vector3(0, 0.02f, 0) + new Vector3(-0.17f, 0, 0), new Vector3(0.17f, 0.02f, 0), 0.025f, 0.025f, 6);
            }
        });
        if (p.Apparel.Exists(a => a.Def == Defs.Satchel))
            Part(_hips, b => { b.Color = Hex(Defs.Satchel.Color); b.Box(new Vector3(0.19f, -0.04f, 0.04f), new Vector3(0.04f, 0.09f, 0.11f)); });

        _head = Joint(_torso, new Vector3(0, 0.62f, 0));
        Part(_head, b =>
        {
            b.Color = skin;
            b.Ellipsoid(new Vector3(0, 0.1f, 0), new Vector3(0.095f, 0.12f, 0.105f), 12, 9);
            b.Ellipsoid(new Vector3(0, 0.07f, -0.1f), new Vector3(0.02f, 0.025f, 0.02f), 5, 3); // nose
            b.Color = new Color(0.08f, 0.07f, 0.06f);
            b.Ellipsoid(new Vector3(-0.035f, 0.115f, -0.093f), new Vector3(0.012f, 0.012f, 0.008f), 5, 3);
            b.Ellipsoid(new Vector3(0.035f, 0.115f, -0.093f), new Vector3(0.012f, 0.012f, 0.008f), 5, 3);
            b.Color = hair;
            // hair styles: cropped, long, bun, bald-ish
            switch (p.HairStyle % 4)
            {
                case 0: b.Ellipsoid(new Vector3(0, 0.15f, 0.01f), new Vector3(0.102f, 0.09f, 0.11f), 12, 6); break;
                case 1: b.Ellipsoid(new Vector3(0, 0.13f, 0.02f), new Vector3(0.108f, 0.11f, 0.115f), 12, 7); b.Ellipsoid(new Vector3(0, 0.02f, 0.07f), new Vector3(0.09f, 0.14f, 0.05f), 8, 5); break;
                case 2: b.Ellipsoid(new Vector3(0, 0.15f, 0.01f), new Vector3(0.102f, 0.09f, 0.11f), 12, 6); b.Ellipsoid(new Vector3(0, 0.2f, 0.09f), new Vector3(0.05f, 0.05f, 0.05f), 8, 5); break;
                default: b.Ellipsoid(new Vector3(0, 0.17f, 0.02f), new Vector3(0.098f, 0.07f, 0.105f), 12, 5); break;
            }
            var cap = p.WornAt(ApparelLayer.Headgear, BodyRegion.Head);
            if (cap != null)
            {
                b.Color = Hex(cap.Def.Color);
                b.Ellipsoid(new Vector3(0, 0.17f, 0.005f), new Vector3(0.11f, 0.085f, 0.115f), 12, 6);
            }
            if (p.WornAt(ApparelLayer.Eyes, BodyRegion.Eyes) != null)
            {
                b.Color = new Color(0.1f, 0.1f, 0.1f);
                b.Tube(new Vector3(-0.07f, 0.115f, -0.1f), new Vector3(0.07f, 0.115f, -0.1f), 0.006f, 0.006f, 4);
            }
        });

        (_shoulderL, _elbowL) = Arm(-1, sleeve, skin, female);
        (_shoulderR, _elbowR) = Arm(1, sleeve, skin, female);
        (_thighL, _kneeL) = Leg(-1, pants, boots);
        (_thighR, _kneeR) = Leg(1, pants, boots);

        _bowSocket = Joint(_elbowL, new Vector3(0, -0.27f, 0));
        if (p.Weapon != null && p.Weapon.Def.Weapon == WeaponKind.Bow)
        {
            _bow = new MeshInstance3D { Mesh = Models.HeldBow };
            _bowSocket.AddChild(_bow);
            _arrowNocked = new MeshInstance3D { Mesh = Models.FlyingArrow, Visible = false };
            _bowSocket.AddChild(_arrowNocked);
        }
        else { _bow = null; _arrowNocked = null; }
    }

    (Node3D shoulder, Node3D elbow) Arm(int side, Color sleeve, Color skin, bool female)
    {
        var sh = Joint(_torso, new Vector3(side * (female ? 0.19f : 0.22f), 0.47f, 0));
        Part(sh, b => { b.Color = sleeve; b.Tube(Vector3.Zero, new Vector3(0, -0.27f, 0), 0.055f, 0.045f, 8); b.Ellipsoid(Vector3.Zero, new Vector3(0.06f, 0.06f, 0.06f), 8, 5); });
        var el = Joint(sh, new Vector3(0, -0.27f, 0));
        Part(el, b =>
        {
            b.Color = sleeve == skin ? skin : sleeve * 0.95f;
            b.Tube(Vector3.Zero, new Vector3(0, -0.24f, 0), 0.044f, 0.036f, 8);
            b.Color = skin;
            b.Ellipsoid(new Vector3(0, -0.27f, 0), new Vector3(0.04f, 0.05f, 0.035f), 8, 5);
        });
        return (sh, el);
    }

    (Node3D thigh, Node3D knee) Leg(int side, Color pants, Color boots)
    {
        var th = Joint(_hips, new Vector3(side * 0.09f, 0, 0));
        Part(th, b => { b.Color = pants; b.Tube(Vector3.Zero, new Vector3(0, -0.44f, 0), 0.08f, 0.06f, 8); });
        var kn = Joint(th, new Vector3(0, -0.44f, 0));
        Part(kn, b =>
        {
            b.Color = pants;
            b.Tube(Vector3.Zero, new Vector3(0, -0.36f, 0), 0.058f, 0.045f, 8);
            b.Color = boots;
            b.Tube(new Vector3(0, -0.3f, 0), new Vector3(0, -0.44f, 0), 0.055f, 0.05f, 8);
            b.Box(new Vector3(0, -0.46f, -0.04f), new Vector3(0.05f, 0.035f, 0.1f));
        });
        return (th, kn);
    }

    static Node3D Joint(Node3D parent, Vector3 pos)
    {
        var n = new Node3D { Position = pos };
        parent.AddChild(n);
        return n;
    }

    static void Part(Node3D parent, Action<MeshBuilder> build)
    {
        var b = new MeshBuilder();
        build(b);
        parent.AddChild(new MeshInstance3D { Mesh = b.Commit(Vc) });
    }

    // ------------------------------------------------------------------ animation

    /// <summary>Updates position, facing and pose from the simulation (called every frame).</summary>
    public void Animate(Pawn p, Vector3 worldPos, float dt, float simSpeed)
    {
        if (OutfitKey(p) != _outfitKey) Rebuild(p);
        Position = worldPos;
        // facing: sim angle 0 = +x (east); model forward is -Z
        float targetYaw = -p.Facing - Mathf.Pi * 0.5f;
        float cur = _yaw.Rotation.Y;
        float diff = Mathf.Wrap(targetYaw - cur, -Mathf.Pi, Mathf.Pi);
        _yaw.Rotation = new Vector3(0, cur + diff * Mathf.Min(1f, dt * 12f), 0);

        var anim = p.Anim;
        if (anim != _lastAnim) { _lastAnim = anim; _animBlend = 0; }
        _animBlend = Mathf.Min(1f, _animBlend + dt * 6f);
        float speed = p.Velocity.Length() * 60f; // cells per second at 1x
        _phase += speed * dt * simSpeed * 1.25f;
        _breath += dt;

        Pose pose = default;
        pose.Root = new Vector3(0, 0, 0);
        float sw = Mathf.Sin(_phase * Mathf.Pi);
        float sw2 = Mathf.Sin(_phase * Mathf.Pi * 2f);
        switch (anim)
        {
            case PawnAnim.Walk:
                pose.ThighL = sw * 0.55f; pose.ThighR = -sw * 0.55f;
                pose.KneeL = Mathf.Max(0, -sw) * 0.8f; pose.KneeR = Mathf.Max(0, sw) * 0.8f;
                pose.ShoulderL = -sw * 0.45f; pose.ShoulderR = sw * 0.45f;
                pose.ElbowL = pose.ElbowR = -0.25f;
                pose.Bob = Mathf.Abs(sw2) * 0.03f;
                break;
            case PawnAnim.Run:
                pose.ThighL = sw * 0.9f; pose.ThighR = -sw * 0.9f;
                pose.KneeL = Mathf.Max(0, -sw) * 1.4f + 0.2f; pose.KneeR = Mathf.Max(0, sw) * 1.4f + 0.2f;
                pose.ShoulderL = -sw * 0.8f; pose.ShoulderR = sw * 0.8f;
                pose.ElbowL = pose.ElbowR = -1.2f;
                pose.Lean = 0.22f;
                pose.Bob = Mathf.Abs(sw2) * 0.06f;
                break;
            case PawnAnim.Aim:
            case PawnAnim.Shoot:
            {
                bool walking = speed > 0.3f;
                if (walking)
                {
                    pose.ThighL = sw * 0.35f; pose.ThighR = -sw * 0.35f;
                    pose.KneeL = Mathf.Max(0, -sw) * 0.5f; pose.KneeR = Mathf.Max(0, sw) * 0.5f;
                }
                // bow arm straight forward, draw hand at the cheek
                pose.ShoulderL = -1.5f; pose.ShoulderLz = -0.15f;
                pose.ShoulderR = -1.45f; pose.ShoulderRz = 0.55f;
                pose.ElbowR = anim == PawnAnim.Shoot && p.AnimTime < 10 ? -0.3f : -2.1f;
                pose.TorsoYaw = 0.35f;
                pose.Arrow = anim == PawnAnim.Aim && p.Arrows > 0;
                break;
            }
            case PawnAnim.Swing:
            {
                float t = Mathf.Clamp(p.AnimTime / 20f, 0f, 1f);
                pose.ShoulderR = Mathf.Lerp(-2.4f, 0.4f, t); pose.ElbowR = -0.4f;
                pose.TorsoYaw = Mathf.Lerp(0.5f, -0.4f, t);
                pose.ThighL = -0.3f; pose.ThighR = 0.25f;
                break;
            }
            case PawnAnim.Work:
            case PawnAnim.PickUp:
            {
                float w = Mathf.Sin(_breath * 6f) * 0.25f;
                pose.Lean = 0.55f;
                pose.ThighL = pose.ThighR = -0.5f; pose.KneeL = pose.KneeR = 0.9f;
                pose.Root.Y = -0.15f;
                pose.ShoulderL = -1.0f + w; pose.ShoulderR = -1.1f - w;
                pose.ElbowL = pose.ElbowR = -0.4f;
                break;
            }
            case PawnAnim.Drink:
                pose.Lean = 0.9f;
                pose.ThighL = pose.ThighR = -1.2f; pose.KneeL = pose.KneeR = 2.0f;
                pose.Root.Y = -0.42f;
                pose.ShoulderL = pose.ShoulderR = -1.3f; pose.ElbowL = pose.ElbowR = -0.9f;
                break;
            case PawnAnim.Eat:
                pose.ShoulderR = -1.2f + Mathf.Sin(_breath * 5f) * 0.15f; pose.ElbowR = -2.0f;
                break;
            case PawnAnim.Sleep:
            case PawnAnim.Dead:
                pose.LieDown = true;
                pose.ShoulderL = -0.2f; pose.ShoulderR = 0.1f; pose.ThighL = 0.1f; pose.KneeL = 0.25f;
                break;
            default: // idle: breathing and a slight sway
                pose.ShoulderL = 0.05f + Mathf.Sin(_breath * 1.3f) * 0.03f;
                pose.ShoulderR = 0.05f - Mathf.Sin(_breath * 1.3f) * 0.03f;
                pose.ElbowL = pose.ElbowR = -0.15f;
                pose.Bob = Mathf.Sin(_breath * 1.6f) * 0.006f;
                break;
        }
        Apply(pose, dt);
    }

    struct Pose
    {
        public Vector3 Root;
        public float Bob, Lean, TorsoYaw;
        public float ThighL, ThighR, KneeL, KneeR;
        public float ShoulderL, ShoulderR, ShoulderLz, ShoulderRz, ElbowL, ElbowR;
        public bool LieDown, Arrow;
    }

    void Apply(Pose p, float dt)
    {
        float k = Mathf.Min(1f, dt * 14f);
        static Vector3 L(Vector3 a, Vector3 b, float t) => a.Lerp(b, t);
        _hips.Position = L(_hips.Position, new Vector3(0, 0.95f + p.Bob, 0) + p.Root, k);
        _hips.Rotation = L(_hips.Rotation, new Vector3(-p.Lean * 0.4f, 0, 0), k);
        _torso.Rotation = L(_torso.Rotation, new Vector3(-p.Lean * 0.6f, p.TorsoYaw, 0), k);
        _thighL.Rotation = L(_thighL.Rotation, new Vector3(p.ThighL, 0, 0), k);
        _thighR.Rotation = L(_thighR.Rotation, new Vector3(p.ThighR, 0, 0), k);
        _kneeL.Rotation = L(_kneeL.Rotation, new Vector3(p.KneeL, 0, 0), k);
        _kneeR.Rotation = L(_kneeR.Rotation, new Vector3(p.KneeR, 0, 0), k);
        _shoulderL.Rotation = L(_shoulderL.Rotation, new Vector3(p.ShoulderL, 0, -0.08f + p.ShoulderLz), k);
        _shoulderR.Rotation = L(_shoulderR.Rotation, new Vector3(p.ShoulderR, 0, 0.08f + p.ShoulderRz), k);
        _elbowL.Rotation = L(_elbowL.Rotation, new Vector3(p.ElbowL, 0, 0), k);
        _elbowR.Rotation = L(_elbowR.Rotation, new Vector3(p.ElbowR, 0, 0), k);
        // lying down: the whole body tips over onto its back
        if (p.LieDown)
        {
            _hips.Rotation = L(_hips.Rotation, new Vector3(Mathf.Pi * 0.5f, 0, 0), k);
            _hips.Position = L(_hips.Position, new Vector3(0, 0.14f, 0), k);
        }
        if (_arrowNocked != null)
        {
            _arrowNocked.Visible = p.Arrow;
            _arrowNocked.Position = new Vector3(0.0f, 0.0f, -0.05f);
            _arrowNocked.Rotation = new Vector3(-Mathf.Pi * 0.5f, 0, 0);
        }
        if (_bow != null)
        {
            // hold the bow upright when aiming, slung low otherwise
            bool aiming = p.ShoulderL < -1.2f;
            _bowSocket.Rotation = aiming ? new Vector3(Mathf.Pi * 0.5f, 0, 0) : new Vector3(0.3f, 0, 0);
        }
    }
}
