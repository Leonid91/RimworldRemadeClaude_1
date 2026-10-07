using System;
using Godot;
using Remade.Pawns;
using Remade.Things;

namespace Remade.Game.Render;

/// <summary>
/// A colonist in the first prototype's readable, slightly stylised look (RimworldTest): a big head with eyes that
/// blink, hair styles, a rounded torso, two-segment arms and legs on pivots. Coloured by skin, hair and the apparel
/// actually worn (white T-shirt with short sleeves, jeans, bare feet), holding what is in the hands.
/// Poses are animated procedurally from the simulation state every frame.
/// Joint convention: a positive X rotation swings a hanging limb FORWARD (towards −Z, the facing); knees bend with
/// negative X (the foot goes back), elbows with positive X (the hand comes forward / up).
/// </summary>
public partial class PawnModel : Node3D
{
    /// <summary>Overall size: a little larger than life next to the trees, so colonists read well from above.</summary>
    public const float DisplayScale = 1.15f;

    Node3D _yaw, _body, _head, _eyes, _shoulderL, _shoulderR, _elbowL, _elbowR, _thighL, _thighR, _kneeL, _kneeR, _handL, _handR;
    MeshInstance3D _held, _arrowNocked;
    bool _heldIsBow;
    float _phase, _breath, _blink = 2f;
    PawnAnim _lastAnim;
    string _outfitKey = "";

    const float BodyY = 0.56f;

    static readonly StandardMaterial3D Vc = new() { VertexColorUseAsAlbedo = true, VertexColorIsSrgb = true, Roughness = 0.8f, Rim = 0.25f, RimTint = 0.4f, RimEnabled = true };
    static readonly StandardMaterial3D VcGloss = new() { VertexColorUseAsAlbedo = true, VertexColorIsSrgb = true, Roughness = 0.25f };

    public PawnModel(Pawn p)
    {
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
        return s + "|" + (p.Held?.Def.Id ?? "-");
    }

    public void Rebuild(Pawn p)
    {
        foreach (var c in GetChildren()) { RemoveChild(c); c.QueueFree(); }
        _outfitKey = OutfitKey(p);
        var skin = Hex(p.SkinColor);
        var hair = Hex(p.HairColor);
        var shirt = LayerColor(p, BodyRegion.Torso, skin);
        var sleeve = LayerColor(p, BodyRegion.ShoulderL, skin);
        var pants = LayerColor(p, BodyRegion.LegL, skin);
        var feet = LayerColor(p, BodyRegion.FootL, skin * 0.92f);
        bool female = p.Sex == Sex.Female;
        float scale = DisplayScale * p.HeightM / 1.75f;
        float bulk = Math.Clamp(p.BodyMassKg / (p.HeightM * p.HeightM) / 23f, 0.88f, 1.2f);

        _yaw = new Node3D();
        AddChild(_yaw);
        _yaw.Scale = new Vector3(scale * bulk, scale, scale * bulk);
        _body = Joint(_yaw, new Vector3(0, BodyY, 0));
        Part(_body, b =>
        {
            b.Color = shirt;
            b.Ellipsoid(new Vector3(0, 0.25f, 0), new Vector3(female ? 0.18f : 0.2f, 0.27f, 0.15f), 14, 9);
            b.Color = pants;
            b.Ellipsoid(new Vector3(0, 0.05f, 0), new Vector3(0.17f, 0.09f, 0.13f), 12, 6);
            b.Color = skin;
            b.Ellipsoid(new Vector3(0, 0.5f, 0), new Vector3(0.07f, 0.06f, 0.07f), 8, 5);
        });

        _head = Joint(_body, new Vector3(0, 0.67f, 0));
        Part(_head, b =>
        {
            b.Color = skin;
            b.Ellipsoid(Vector3.Zero, new Vector3(0.165f, 0.175f, 0.16f), 16, 12);
            b.Color = skin * 0.95f;
            b.Ellipsoid(new Vector3(-0.16f, -0.01f, 0.01f), new Vector3(0.03f, 0.045f, 0.03f), 6, 4);
            b.Ellipsoid(new Vector3(0.16f, -0.01f, 0.01f), new Vector3(0.03f, 0.045f, 0.03f), 6, 4);
            b.Color = skin * 0.92f;
            b.Ellipsoid(new Vector3(0, -0.02f, -0.16f), new Vector3(0.025f, 0.03f, 0.025f), 6, 4);
            b.Color = hair;
            switch (p.HairStyle % 4)
            {
                case 0: // short crop
                    b.Ellipsoid(new Vector3(0, 0.05f, 0.025f), new Vector3(0.178f, 0.16f, 0.172f), 14, 8);
                    break;
                case 1: // long
                    b.Ellipsoid(new Vector3(0, 0.045f, 0.03f), new Vector3(0.18f, 0.165f, 0.175f), 14, 8);
                    b.Ellipsoid(new Vector3(0, -0.12f, 0.1f), new Vector3(0.17f, 0.2f, 0.1f), 12, 8);
                    break;
                case 2: // bun
                    b.Ellipsoid(new Vector3(0, 0.05f, 0.025f), new Vector3(0.176f, 0.158f, 0.17f), 14, 8);
                    b.Ellipsoid(new Vector3(0, 0.15f, 0.12f), new Vector3(0.08f, 0.075f, 0.08f), 10, 6);
                    break;
                default: // tousled
                    b.Ellipsoid(new Vector3(0, 0.06f, 0.02f), new Vector3(0.182f, 0.16f, 0.176f), 14, 8);
                    b.Ellipsoid(new Vector3(0.05f, 0.15f, -0.06f), new Vector3(0.08f, 0.05f, 0.08f), 8, 5);
                    break;
            }
        });
        _eyes = Joint(_head, Vector3.Zero);
        var eb = new MeshBuilder { Color = new Color(0.08f, 0.06f, 0.06f) };
        eb.Ellipsoid(new Vector3(-0.06f, 0.015f, -0.148f), new Vector3(0.022f, 0.03f, 0.012f), 6, 4);
        eb.Ellipsoid(new Vector3(0.06f, 0.015f, -0.148f), new Vector3(0.022f, 0.03f, 0.012f), 6, 4);
        _eyes.AddChild(new MeshInstance3D { Mesh = eb.Commit(VcGloss) });

        (_shoulderL, _elbowL, _handL) = Arm(-1, sleeve, skin);
        (_shoulderR, _elbowR, _handR) = Arm(1, sleeve, skin);
        (_thighL, _kneeL) = Leg(-1, pants, feet);
        (_thighR, _kneeR) = Leg(1, pants, feet);

        // what is held: the bow in the left (bow) hand, anything else in the right hand
        _held = null; _arrowNocked = null; _heldIsBow = false;
        if (p.Held != null)
        {
            _heldIsBow = p.Held.Def.Weapon == WeaponKind.Bow;
            if (_heldIsBow)
            {
                _held = new MeshInstance3D { Mesh = Models.HeldBow };
                _handL.AddChild(_held);
                _arrowNocked = new MeshInstance3D { Mesh = Models.FlyingArrow, Visible = false };
                _handL.AddChild(_arrowNocked);
            }
            else
            {
                _held = new MeshInstance3D { Mesh = Models.Item(p.Held.Def), Scale = Vector3.One * 0.7f };
                _handR.AddChild(_held);
            }
        }
    }

    (Node3D shoulder, Node3D elbow, Node3D hand) Arm(int side, Color sleeve, Color skin)
    {
        var sh = Joint(_body, new Vector3(side * 0.215f, 0.43f, 0));
        Part(sh, b =>
        {
            b.Color = sleeve;
            b.Ellipsoid(new Vector3(0, -0.02f, 0), new Vector3(0.072f, 0.072f, 0.072f), 8, 6);
            b.Tube(Vector3.Zero, new Vector3(0, -0.12f, 0), 0.066f, 0.062f, 8);   // short sleeve
            b.Color = skin;
            b.Tube(new Vector3(0, -0.11f, 0), new Vector3(0, -0.2f, 0), 0.055f, 0.052f, 8);
            b.Ellipsoid(new Vector3(0, -0.2f, 0), new Vector3(0.052f, 0.052f, 0.052f), 6, 4);
        });
        var el = Joint(sh, new Vector3(0, -0.2f, 0));
        Part(el, b =>
        {
            b.Color = skin;
            b.Tube(Vector3.Zero, new Vector3(0, -0.17f, 0), 0.05f, 0.046f, 8);
            b.Ellipsoid(new Vector3(0, -0.2f, 0), new Vector3(0.052f, 0.058f, 0.05f), 8, 6);
        });
        var hand = Joint(el, new Vector3(0, -0.2f, 0));
        return (sh, el, hand);
    }

    (Node3D thigh, Node3D knee) Leg(int side, Color pants, Color feet)
    {
        var th = Joint(_body, new Vector3(side * 0.085f, 0.0f, 0));
        Part(th, b => { b.Color = pants; b.Tube(Vector3.Zero, new Vector3(0, -0.26f, 0), 0.075f, 0.066f, 8); });
        var kn = Joint(th, new Vector3(0, -0.26f, 0));
        Part(kn, b =>
        {
            b.Color = pants;
            b.Tube(new Vector3(0, 0.02f, 0), new Vector3(0, -0.22f, 0), 0.066f, 0.06f, 8);
            b.Color = feet;
            b.Box(new Vector3(0, -0.27f, -0.035f), new Vector3(0.05f, 0.035f, 0.085f));
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
        if (anim != _lastAnim) _lastAnim = anim;
        float speed = p.Velocity.Length() * 60f; // cells per second at 1x
        _phase += speed * dt * simSpeed * 1.25f;
        _breath += dt;
        _blink -= dt;
        if (_blink < -0.12f) _blink = 2f + (float)GD.Randf() * 3f;
        bool eyesClosed = _blink < 0 || anim is PawnAnim.Sleep or PawnAnim.Dead;
        _eyes.Scale = new Vector3(1, eyesClosed ? 0.15f : 1f, 1);

        Pose pose = default;
        float sw = Mathf.Sin(_phase * Mathf.Pi);
        float sw2 = Mathf.Sin(_phase * Mathf.Pi * 2f);
        switch (anim)
        {
            case PawnAnim.Walk:
                pose.ThighL = sw * 0.6f; pose.ThighR = -sw * 0.6f;
                pose.KneeL = -Mathf.Max(0, -sw) * 0.7f; pose.KneeR = -Mathf.Max(0, sw) * 0.7f;
                pose.ShoulderL = -sw * 0.5f; pose.ShoulderR = sw * 0.5f;
                pose.ElbowL = pose.ElbowR = 0.25f;
                pose.Bob = Mathf.Abs(sw2) * 0.035f;
                pose.Lean = 0.06f;
                break;
            case PawnAnim.Run:
                pose.ThighL = sw * 0.95f; pose.ThighR = -sw * 0.95f;
                pose.KneeL = -(Mathf.Max(0, -sw) * 1.3f + 0.2f); pose.KneeR = -(Mathf.Max(0, sw) * 1.3f + 0.2f);
                pose.ShoulderL = -sw * 0.85f; pose.ShoulderR = sw * 0.85f;
                pose.ElbowL = pose.ElbowR = 1.3f;
                pose.Lean = 0.2f;
                pose.Bob = Mathf.Abs(sw2) * 0.06f;
                break;
            case PawnAnim.Aim:
            case PawnAnim.Shoot:
            {
                if (speed > 0.3f)
                {
                    pose.ThighL = sw * 0.35f; pose.ThighR = -sw * 0.35f;
                    pose.KneeL = -Mathf.Max(0, -sw) * 0.5f; pose.KneeR = -Mathf.Max(0, sw) * 0.5f;
                }
                // bow arm (left) straight forward at shoulder height; the draw hand (right) pulled back to the cheek,
                // snapping forward for a moment on release
                bool released = anim == PawnAnim.Shoot && p.AnimTime < 12;
                pose.ShoulderL = 1.55f; pose.ShoulderLz = 0.12f; pose.ElbowL = 0f;
                pose.ShoulderR = 1.5f; pose.ShoulderRz = -0.55f;
                pose.ElbowR = released ? 0.6f : 2.5f;
                pose.TorsoYaw = -0.25f;
                pose.Arrow = anim == PawnAnim.Aim && p.Arrows > 0;
                pose.BowUp = true;
                break;
            }
            case PawnAnim.Swing:
            {
                float t = Mathf.Clamp(p.AnimTime / 20f, 0f, 1f);
                pose.ShoulderR = Mathf.Lerp(2.7f, 0.4f, t); pose.ElbowR = 0.4f;
                pose.TorsoYaw = Mathf.Lerp(-0.5f, 0.4f, t);
                pose.ThighL = 0.3f; pose.ThighR = -0.25f;
                pose.Lean = Mathf.Lerp(0.0f, 0.2f, t);
                break;
            }
            case PawnAnim.Work:
            case PawnAnim.PickUp:
            {
                float w = Mathf.Sin(_breath * 6f) * 0.25f;
                pose.Lean = 0.5f;
                pose.ThighL = pose.ThighR = 0.9f; pose.KneeL = pose.KneeR = -1.3f;
                pose.Root = -0.1f;
                pose.ShoulderL = 1.0f + w; pose.ShoulderR = 1.1f - w;
                pose.ElbowL = pose.ElbowR = 0.4f;
                break;
            }
            case PawnAnim.Drink:
                pose.Lean = 0.85f;
                pose.ThighL = pose.ThighR = 1.5f; pose.KneeL = pose.KneeR = -2.3f;
                pose.Root = -0.24f;
                pose.ShoulderL = pose.ShoulderR = 1.3f; pose.ElbowL = pose.ElbowR = 0.6f;
                break;
            case PawnAnim.Eat:
                pose.ShoulderR = 1.0f + Mathf.Sin(_breath * 5f) * 0.15f; pose.ShoulderRz = -0.35f; pose.ElbowR = 2.0f;
                pose.HeadTilt = 0.2f;
                break;
            case PawnAnim.Sleep:
            case PawnAnim.Dead:
                pose.LieDown = true;
                pose.ShoulderL = 0.1f; pose.ShoulderR = 0.1f; pose.ThighL = 0.1f; pose.KneeL = -0.25f;
                break;
            default: // idle: breathing and a slight sway
                pose.ShoulderL = Mathf.Sin(_breath * 1.1f) * 0.04f;
                pose.ShoulderR = -pose.ShoulderL;
                pose.ElbowL = pose.ElbowR = 0.12f;
                pose.Bob = Mathf.Sin(_breath * 1.6f) * 0.008f;
                break;
        }
        Apply(pose, dt);
    }

    struct Pose
    {
        public float Root, Bob, Lean, TorsoYaw, HeadTilt;
        public float ThighL, ThighR, KneeL, KneeR;
        public float ShoulderL, ShoulderR, ShoulderLz, ShoulderRz, ElbowL, ElbowR;
        public bool LieDown, Arrow, BowUp;
    }

    void Apply(Pose p, float dt)
    {
        float k = Mathf.Min(1f, dt * 14f);
        static Vector3 L(Vector3 a, Vector3 b, float t) => a.Lerp(b, t);
        if (p.LieDown)
        {
            // lying on the back: the whole body tips over around the hips
            _body.Position = L(_body.Position, new Vector3(0, 0.16f, 0.25f), k);
            _body.Rotation = L(_body.Rotation, new Vector3(Mathf.Pi * 0.5f, 0, 0), k);
        }
        else
        {
            _body.Position = L(_body.Position, new Vector3(0, BodyY + p.Bob + p.Root, 0), k);
            _body.Rotation = L(_body.Rotation, new Vector3(-p.Lean, p.TorsoYaw, 0), k);
        }
        _head.Rotation = L(_head.Rotation, new Vector3(-p.HeadTilt + p.Lean * 0.4f, -p.TorsoYaw * 0.6f, 0), k);
        _thighL.Rotation = L(_thighL.Rotation, new Vector3(p.ThighL + p.Lean, 0, 0), k);
        _thighR.Rotation = L(_thighR.Rotation, new Vector3(p.ThighR + p.Lean, 0, 0), k);
        _kneeL.Rotation = L(_kneeL.Rotation, new Vector3(p.KneeL, 0, 0), k);
        _kneeR.Rotation = L(_kneeR.Rotation, new Vector3(p.KneeR, 0, 0), k);
        _shoulderL.Rotation = L(_shoulderL.Rotation, new Vector3(p.ShoulderL + p.Lean * 0.3f, 0, -0.08f + p.ShoulderLz), k);
        _shoulderR.Rotation = L(_shoulderR.Rotation, new Vector3(p.ShoulderR + p.Lean * 0.3f, 0, 0.08f + p.ShoulderRz), k);
        _elbowL.Rotation = L(_elbowL.Rotation, new Vector3(p.ElbowL, 0, 0), k);
        _elbowR.Rotation = L(_elbowR.Rotation, new Vector3(p.ElbowR, 0, 0), k);

        if (_held == null) return;
        if (_heldIsBow)
        {
            // the bow is oriented from the body, not the arm chain: upright with its belly forward while aiming,
            // carried hanging along the leg (tilted forward) otherwise
            var yawBasis = _yaw.GlobalBasis.Orthonormalized();
            var s = _yaw.Scale.Y;
            var bowBasis = p.BowUp ? yawBasis : yawBasis * new Basis(Vector3.Right, 0.35f);
            _held.GlobalBasis = bowBasis.Scaled(Vector3.One * s);
            _held.Position = new Vector3(0, 0.02f, 0);
            if (_arrowNocked != null)
            {
                _arrowNocked.Visible = p.Arrow;
                _arrowNocked.GlobalBasis = yawBasis.Scaled(Vector3.One * s);
                // the shaft runs from the grip back to the draw hand
                _arrowNocked.GlobalPosition = _held.GlobalPosition + yawBasis.Z * 0.24f * s + yawBasis.Y * 0.0f;
            }
        }
        else
        {
            _held.GlobalBasis = _yaw.GlobalBasis.Orthonormalized().Scaled(Vector3.One * 0.7f * _yaw.Scale.Y);
        }
    }
}
