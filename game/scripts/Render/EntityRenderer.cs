using System;
using System.Collections.Generic;
using Godot;
using Remade.Map;
using Remade.Pawns;
using Remade.Sim;
using Remade.Things;
using SV2 = System.Numerics.Vector2;

namespace Remade.Game.Render;

/// <summary>
/// Draws the moving world: deer (two vertex-animated MultiMeshes, one draw call each regardless of count), colonists
/// (jointed puppets), ground items, arrows in flight, the selection ring, the highlighted interaction target and the
/// red aim line of the colonist under direct control. Visual positions ease toward the simulation positions so
/// movement stays smooth between ticks.
/// </summary>
public partial class EntityRenderer : Node3D
{
    readonly GameSim _sim;
    readonly LocalMap _map;
    readonly MultiMeshInstance3D _does, _stags;
    readonly Dictionary<int, Vector3> _visualPos = new();
    readonly Dictionary<int, float> _gaitPhase = new();
    readonly Dictionary<int, PawnModel> _pawns = new();
    readonly Dictionary<int, MeshInstance3D> _items = new();
    readonly List<MeshInstance3D> _arrows = new();
    readonly MeshInstance3D _selRing, _targetRing;
    readonly MeshInstance3D _aimLine;
    readonly ImmediateMesh _aimMesh = new();
    float[] _bufDoe = Array.Empty<float>(), _bufStag = Array.Empty<float>();

    public Pawn Selected;
    public Interaction Target;
    public bool ShowAim;
    public float AimRange;
    /// <summary>Camera distance: the aim line keeps a constant on-screen width (it would vanish under a pixel when zoomed out).</summary>
    public float CamDistance = 34f;

    public EntityRenderer(GameSim sim)
    {
        _sim = sim;
        _map = sim.Map;
        var deerMat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/deer.gdshader") };
        _does = new MultiMeshInstance3D { Multimesh = NewDeerMM(Models.Deer(false, deerMat)) };
        _stags = new MultiMeshInstance3D { Multimesh = NewDeerMM(Models.Deer(true, deerMat)) };
        AddChild(_does); AddChild(_stags);

        _selRing = new MeshInstance3D { Mesh = Ring(0.55f, 0.07f), MaterialOverride = Glow(new Color(0.45f, 1f, 0.9f), 2f), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = false };
        _targetRing = new MeshInstance3D { Mesh = Ring(0.5f, 0.05f), MaterialOverride = Glow(new Color(1f, 0.8f, 0.35f), 2.5f), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = false };
        AddChild(_selRing); AddChild(_targetRing);
        var aimMat = Glow(new Color(1f, 0.12f, 0.08f), 3f);
        aimMat.NoDepthTest = true;   // drawn over trees and terrain
        aimMat.RenderPriority = 20;
        _aimLine = new MeshInstance3D { Mesh = _aimMesh, MaterialOverride = aimMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(_aimLine);
    }

    static MultiMesh NewDeerMM(Mesh mesh) => new() { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true, Mesh = mesh, InstanceCount = 0 };

    static StandardMaterial3D Glow(Color c, float energy) => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = c, EmissionEnabled = true, Emission = c,
        EmissionEnergyMultiplier = energy, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };

    static ArrayMesh Ring(float r, float w)
    {
        var b = new MeshBuilder();
        const int n = 40;
        for (int i = 0; i < n; i++)
        {
            float a0 = i / (float)n * Mathf.Tau, a1 = (i + 1) / (float)n * Mathf.Tau;
            var d0 = new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0)); var d1 = new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1));
            int i0 = b.Add(d0 * (r - w), Vector3.Up, Vector2.Zero), i1 = b.Add(d0 * (r + w), Vector3.Up, Vector2.Zero);
            int i2 = b.Add(d1 * (r + w), Vector3.Up, Vector2.Zero), i3 = b.Add(d1 * (r - w), Vector3.Up, Vector2.Zero);
            b.Tri(i0, i2, i1); b.Tri(i0, i3, i2);
        }
        return b.Commit();
    }

    Vector3 Ground(SV2 p, float lift = 0) => new(p.X, _map.StandHeight(p.X, p.Y) + lift, p.Y);

    Vector3 Smooth(int key, Vector3 target, float dt)
    {
        if (!_visualPos.TryGetValue(key, out var v) || v.DistanceTo(target) > 3f) v = target;
        else v = v.Lerp(target, 1f - Mathf.Exp(-dt * 22f));
        _visualPos[key] = v;
        return v;
    }

    public void Update(float dt)
    {
        float simSpeed = Remade.Core.GameTime.SpeedMultipliers[_sim.SpeedIndex];
        UpdateDeer(dt, simSpeed);
        UpdatePawns(dt, simSpeed);
        UpdateItems();
        UpdateArrows();
        UpdateMarkers(dt);
    }

    // ------------------------------------------------------------------ deer

    void UpdateDeer(float dt, float simSpeed)
    {
        int nd = 0, ns = 0;
        foreach (var a in _sim.Animals) if (a.Male) ns++; else nd++;
        if (_bufDoe.Length != nd * 16) _bufDoe = new float[nd * 16];
        if (_bufStag.Length != ns * 16) _bufStag = new float[ns * 16];
        int id = 0, is_ = 0;
        foreach (var a in _sim.Animals)
        {
            var pos = Smooth(a.Id, Ground(a.Position), dt);
            float speed = a.Velocity.Length() * 60f;
            _gaitPhase.TryGetValue(a.Id, out float ph);
            ph += speed * dt * simSpeed * (a.State == AnimalState.Flee ? 0.32f : 0.75f);
            _gaitPhase[a.Id] = ph;
            int state = a.Dead ? 5 : a.State switch
            {
                AnimalState.Graze => 3, AnimalState.Rest => 4,
                AnimalState.Flee => speed > 0.5f ? 2 : 0,
                AnimalState.Wander => speed > 0.2f ? 1 : 0,
                _ => 0,
            };
            float yaw = -a.Facing - Mathf.Pi * 0.5f;
            var basis = new Basis(Vector3.Up, yaw);
            if (a.Dead) basis = basis * new Basis(Vector3.Forward, Mathf.Pi * 0.5f); // lying on its side
            basis = basis.Scaled(Vector3.One * a.Size * 0.95f);
            var origin = a.Dead ? pos + new Vector3(0, 0.25f, 0) : pos;
            var buf = a.Male ? _bufStag : _bufDoe;
            int o = (a.Male ? is_++ : id++) * 16;
            buf[o] = basis.X.X; buf[o + 1] = basis.Y.X; buf[o + 2] = basis.Z.X; buf[o + 3] = origin.X;
            buf[o + 4] = basis.X.Y; buf[o + 5] = basis.Y.Y; buf[o + 6] = basis.Z.Y; buf[o + 7] = origin.Y;
            buf[o + 8] = basis.X.Z; buf[o + 9] = basis.Y.Z; buf[o + 10] = basis.Z.Z; buf[o + 11] = origin.Z;
            buf[o + 12] = ph % 1f; buf[o + 13] = state; buf[o + 14] = 0; buf[o + 15] = (a.Id * 0.618f) % 1f;
        }
        Upload(_does.Multimesh, _bufDoe, nd);
        Upload(_stags.Multimesh, _bufStag, ns);
    }

    static void Upload(MultiMesh mm, float[] buf, int count)
    {
        if (mm.InstanceCount != count) mm.InstanceCount = count;
        if (count > 0) RenderingServer.MultimeshSetBuffer(mm.GetRid(), buf);
    }

    // ------------------------------------------------------------------ colonists

    void UpdatePawns(float dt, float simSpeed)
    {
        foreach (var p in _sim.Pawns)
        {
            if (!_pawns.TryGetValue(p.Id, out var model))
            {
                model = new PawnModel(p);
                _pawns[p.Id] = model;
                AddChild(model);
            }
            var pos = Smooth(-p.Id - 1, Ground(p.Position), dt);
            model.Animate(p, pos, dt, Math.Max(1f, simSpeed));
        }
    }

    public Vector3 PawnVisualPos(Pawn p) => _visualPos.TryGetValue(-p.Id - 1, out var v) ? v : Ground(p.Position);

    // ------------------------------------------------------------------ items

    readonly HashSet<int> _seen = new();

    void UpdateItems()
    {
        _seen.Clear();
        foreach (var it in _sim.Items)
        {
            _seen.Add(it.Id);
            if (_items.ContainsKey(it.Id)) continue;
            var mi = new MeshInstance3D { Mesh = Models.Item(it.Def) };
            mi.Position = Ground(it.Position, 0.01f);
            mi.Rotation = new Vector3(0, it.Rotation, 0);
            AddChild(mi);
            _items[it.Id] = mi;
        }
        if (_items.Count != _seen.Count)
        {
            var gone = new List<int>();
            foreach (var kv in _items) if (!_seen.Contains(kv.Key)) gone.Add(kv.Key);
            foreach (int k in gone) { _items[k].QueueFree(); _items.Remove(k); }
        }
    }

    // ------------------------------------------------------------------ arrows in flight

    void UpdateArrows()
    {
        var list = _sim.Projectiles;
        while (_arrows.Count < list.Count)
        {
            var mi = new MeshInstance3D { Mesh = Models.FlyingArrow, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            AddChild(mi);
            _arrows.Add(mi);
        }
        for (int i = 0; i < _arrows.Count; i++)
        {
            var mi = _arrows[i];
            if (i >= list.Count) { mi.Visible = false; continue; }
            var pr = list[i];
            mi.Visible = true;
            float t = pr.Progress;
            float arc = pr.MaxDist * 0.035f;
            float start = _map.StandHeight(pr.Start.X, pr.Start.Y) + 1.45f;
            float y = Mathf.Lerp(start, _map.StandHeight(pr.Pos.X, pr.Pos.Y) + 0.4f, t * t) + 4f * arc * t * (1f - t);
            var pos = new Vector3(pr.Pos.X, y, pr.Pos.Y);
            float slope = arc * 4f * (1f - 2f * t) / Math.Max(1f, pr.MaxDist) - 0.05f * t;
            var dir = new Vector3(pr.Dir.X, slope, pr.Dir.Y).Normalized();
            mi.Position = pos;
            mi.Basis = Basis.LookingAt(dir, Vector3.Up);
        }
    }

    // ------------------------------------------------------------------ markers

    void UpdateMarkers(float dt)
    {
        if (Selected != null && !Selected.Dead)
        {
            _selRing.Visible = true;
            _selRing.Position = PawnVisualPos(Selected) + new Vector3(0, 0.05f, 0);
            _selRing.RotateY(dt * 0.6f);
        }
        else _selRing.Visible = false;

        if (Target != null)
        {
            _targetRing.Visible = true;
            float pulse = 1f + Mathf.Sin(Time.GetTicksMsec() * 0.008f) * 0.12f;
            _targetRing.Position = Ground(Target.Position, 0.06f);
            _targetRing.Scale = Vector3.One * pulse;
        }
        else _targetRing.Visible = false;

        _aimMesh.ClearSurfaces();
        if (ShowAim && Selected != null)
        {
            var p = Selected;
            var from = p.Position;
            var dir = p.AimDir;
            float halfWidth = Math.Max(0.035f, CamDistance * 0.0016f);
            var side = new SV2(-dir.Y, dir.X) * halfWidth;
            _aimMesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
            int steps = Math.Max(2, (int)(AimRange / 0.5f));
            for (int s = 0; s < steps; s++)
            {
                float t0 = 0.6f + (AimRange - 0.6f) * s / steps, t1 = 0.6f + (AimRange - 0.6f) * (s + 1) / steps;
                var a = from + dir * t0; var b = from + dir * t1;
                float ha = _map.StandHeight(a.X, a.Y) + 0.07f, hb = _map.StandHeight(b.X, b.Y) + 0.07f;
                Quad(new Vector3(a.X - side.X, ha, a.Y - side.Y), new Vector3(a.X + side.X, ha, a.Y + side.Y),
                     new Vector3(b.X + side.X, hb, b.Y + side.Y), new Vector3(b.X - side.X, hb, b.Y - side.Y));
            }
            // range tick at the end
            var end = from + dir * AimRange;
            var tick = new SV2(-dir.Y, dir.X) * Math.Max(0.35f, halfWidth * 8f);
            var along = dir * Math.Max(0.05f, halfWidth * 1.4f);
            float he = _map.StandHeight(end.X, end.Y) + 0.07f;
            Quad(new Vector3(end.X - tick.X - along.X, he, end.Y - tick.Y - along.Y), new Vector3(end.X + tick.X - along.X, he, end.Y + tick.Y - along.Y),
                 new Vector3(end.X + tick.X + along.X, he, end.Y + tick.Y + along.Y), new Vector3(end.X - tick.X + along.X, he, end.Y - tick.Y + along.Y));
            _aimMesh.SurfaceEnd();
        }
    }

    void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        _aimMesh.SurfaceAddVertex(a); _aimMesh.SurfaceAddVertex(c); _aimMesh.SurfaceAddVertex(b);
        _aimMesh.SurfaceAddVertex(a); _aimMesh.SurfaceAddVertex(d); _aimMesh.SurfaceAddVertex(c);
    }
}
