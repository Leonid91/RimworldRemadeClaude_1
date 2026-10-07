using System;
using System.Collections.Generic;
using Godot;
using Remade.Core;
using Remade.Diagnostics;
using Remade.Map;
using Remade.Sim;

namespace Remade.Game.Render;

/// <summary>
/// Trees and bushes as MultiMeshes per 64×64-cell region and mesh variant (detailed mesh near the camera, low LOD
/// beyond, cross-faded with visibility ranges), berries as a per-region MultiMesh whose custom data hides picked
/// berries, and a camera-centred GPU grass field.
/// </summary>
public partial class FloraRenderer : Node3D
{
    const int Region = 128; // large regions: few MultiMesh draws (and shadow draws) per frame
    readonly GameSim _sim;
    readonly LocalMap _map;
    readonly int _rw, _rh;
    readonly RegionNode[] _regions;
    readonly Models.Tree[] _oaks = new Models.Tree[PlantInfo.OakVariants];
    readonly ArrayMesh[] _bushes = new ArrayMesh[PlantInfo.BushVariants];
    readonly ArrayMesh _berries;
    MultiMeshInstance3D _grass;
    ShaderMaterial _grassMat;
    int _grassR;
    public float DetailDistance = 60f;
    public int Instances { get; private set; }

    sealed class RegionNode
    {
        public int X, Y;
        public Node3D Root;
        public bool Built, Dirty;
        public long LastSeen;
        public MultiMeshInstance3D Berries;
        public List<int> BerryCells;
    }

    public FloraRenderer(GameSim sim, Texture2D heightTex)
    {
        _sim = sim;
        _map = sim.Map;
        _rw = (_map.Width + Region - 1) / Region;
        _rh = (_map.Height + Region - 1) / Region;
        _regions = new RegionNode[_rw * _rh];
        for (int i = 0; i < _regions.Length; i++) _regions[i] = new RegionNode { X = i % _rw, Y = i / _rw };
        using (Log.Time("Flora models", 800))
        {
            for (int v = 0; v < _oaks.Length; v++) _oaks[v] = Models.Oak(v);
            for (int v = 0; v < _bushes.Length; v++) _bushes[v] = Models.Bush(v);
            _berries = Models.Berries();
        }
        BuildGrass(heightTex);
    }

    // ------------------------------------------------------------------ regions

    public void UpdateStreaming(Rect2 view, long frame, double budgetMs = 3)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int x0 = Math.Max(0, (int)(view.Position.X / Region) - 1), y0 = Math.Max(0, (int)(view.Position.Y / Region) - 1);
        int x1 = Math.Min(_rw - 1, (int)(view.End.X / Region) + 1), y1 = Math.Min(_rh - 1, (int)(view.End.Y / Region) + 1);
        var center = view.GetCenter();
        var todo = new List<RegionNode>();
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                var r = _regions[y * _rw + x];
                r.LastSeen = frame;
                if (!r.Built || r.Dirty) todo.Add(r);
            }
        todo.Sort((a, b) => Dist(a, center).CompareTo(Dist(b, center)));
        foreach (var r in todo)
        {
            Build(r);
            if (sw.Elapsed.TotalMilliseconds > budgetMs) break;
        }
        if (frame % 90 == 0)
            foreach (var r in _regions)
                if (r.Built && frame - r.LastSeen > 900) Free(r);
    }

    static float Dist(RegionNode r, Vector2 p) => new Vector2((r.X + 0.5f) * Region, (r.Y + 0.5f) * Region).DistanceSquaredTo(p);

    public void OnCellsChanged(List<CellChange> changes)
    {
        foreach (var ch in changes)
        {
            if ((ch.Layer & (MapLayer.Plant | MapLayer.Building)) == 0) continue;
            _map.XY(ch.Cell, out int x, out int y);
            var r = _regions[(y / Region) * _rw + x / Region];
            if (!r.Built) continue;
            // berry-only changes just update the berry custom data; anything else rebuilds the region
            if (_map.Plants[ch.Cell] == Plant.BerryBush && r.BerryCells != null && r.BerryCells.Contains(ch.Cell)) UpdateBerries(r);
            else r.Dirty = true;
        }
    }

    void Free(RegionNode r)
    {
        Instances -= r.Root.GetChildCount() > 0 ? CountInstances(r.Root) : 0;
        r.Root.QueueFree();
        r.Root = null;
        r.Built = false;
        r.Berries = null;
    }

    static int CountInstances(Node root)
    {
        int n = 0;
        foreach (var c in root.GetChildren()) if (c is MultiMeshInstance3D m) n += m.Multimesh.InstanceCount;
        return n;
    }

    void Build(RegionNode r)
    {
        if (r.Built) Free(r);
        r.Root = new Node3D();
        AddChild(r.Root);
        int x0 = r.X * Region, y0 = r.Y * Region, x1 = Math.Min(_map.Width, x0 + Region), y1 = Math.Min(_map.Height, y0 + Region);
        var oakXf = new List<Transform3D>[PlantInfo.OakVariants];
        var bushXf = new List<Transform3D>[PlantInfo.BushVariants];
        var berryXf = new List<Transform3D>();
        r.BerryCells = new List<int>();
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = y * _map.Width + x;
                var p = _map.Plants[i];
                if (p == Plant.None) continue;
                float jx = (Hash.Cell01(x, y, 11) - 0.5f) * 0.3f, jz = (Hash.Cell01(x, y, 12) - 0.5f) * 0.3f;
                float px = x + 0.5f + jx, pz = y + 0.5f + jz;
                float g = _map.GroundHeight(px, pz);
                float yaw = Hash.Cell01(x, y, 13) * Mathf.Tau;
                float grow = _map.PlantGrowth[i] / 255f;
                if (p == Plant.Oak)
                {
                    float s = 0.28f + grow * 0.9f;
                    float sx = s * (0.85f + Hash.Cell01(x, y, 14) * 0.35f), sy = s * (0.88f + Hash.Cell01(x, y, 15) * 0.28f);
                    var basis = new Basis(Vector3.Up, yaw).Scaled(new Vector3(sx, sy, sx * (0.9f + Hash.Cell01(x, y, 16) * 0.2f)));
                    int v = _map.PlantVariant[i] % PlantInfo.OakVariants;
                    (oakXf[v] ??= new List<Transform3D>()).Add(new Transform3D(basis, new Vector3(px, g - 0.05f, pz)));
                }
                else
                {
                    float s = 0.6f + grow * 0.6f;
                    var t = new Transform3D(new Basis(Vector3.Up, yaw).Scaled(new Vector3(s, s, s)), new Vector3(px, g, pz));
                    int v = _map.PlantVariant[i] % PlantInfo.BushVariants;
                    (bushXf[v] ??= new List<Transform3D>()).Add(t);
                    berryXf.Add(t);
                    r.BerryCells.Add(i);
                }
            }
        for (int v = 0; v < oakXf.Length; v++)
        {
            if (oakXf[v] == null) continue;
            // near: detailed mesh, shadows cast by the cheap low-LOD mesh (shadow-only); far: low-LOD mesh
            AddMM(r.Root, _oaks[v].Detail, oakXf[v], 0, DetailDistance, false);
            AddMM(r.Root, _oaks[v].Low, oakXf[v], 0, DetailDistance, true, shadowOnly: true);
            AddMM(r.Root, _oaks[v].Low, oakXf[v], DetailDistance - 8, 0, true);
        }
        for (int v = 0; v < bushXf.Length; v++)
            if (bushXf[v] != null) AddMM(r.Root, _bushes[v], bushXf[v], 0, 160, true);
        if (berryXf.Count > 0)
        {
            r.Berries = AddMM(r.Root, _berries, berryXf, 0, 90, false, custom: true);
            UpdateBerries(r);
        }
        r.Built = true; r.Dirty = false;
    }

    MultiMeshInstance3D AddMM(Node3D parent, Mesh mesh, List<Transform3D> xf, float visBegin, float visEnd, bool shadows, bool custom = false, bool shadowOnly = false)
    {
        var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = custom, Mesh = mesh, InstanceCount = xf.Count };
        for (int k = 0; k < xf.Count; k++) mm.SetInstanceTransform(k, xf[k]);
        var mmi = new MultiMeshInstance3D
        {
            Multimesh = mm,
            CastShadow = shadowOnly ? GeometryInstance3D.ShadowCastingSetting.ShadowsOnly : shadows ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off,
            VisibilityRangeBegin = visBegin, VisibilityRangeEnd = visEnd,
            VisibilityRangeBeginMargin = visBegin > 0 ? 8 : 0, VisibilityRangeEndMargin = visEnd > 0 ? 8 : 0,
            VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Self,
        };
        parent.AddChild(mmi);
        Instances += xf.Count;
        return mmi;
    }

    void UpdateBerries(RegionNode r)
    {
        if (r.Berries == null) return;
        var mm = r.Berries.Multimesh;
        for (int k = 0; k < r.BerryCells.Count; k++)
        {
            int c = r.BerryCells[k];
            float frac = _map.Plants[c] == Plant.BerryBush ? _map.Berries[c] / (float)PlantInfo.MaxBerries : 0f;
            mm.SetInstanceCustomData(k, new Color(frac, 0, 0, 0));
        }
    }

    // ------------------------------------------------------------------ grass

    void BuildGrass(Texture2D heightTex)
    {
        _grassMat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/grass.gdshader") };
        _grassMat.SetShaderParameter("height_map", heightTex);
        _grass = new MultiMeshInstance3D { CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, MaterialOverride = _grassMat };
        AddChild(_grass);
        SetGrassRadius(42);
    }

    /// <summary>(Re)creates the clump pattern for a window radius (cells); density from the graphics settings.</summary>
    public void SetGrassRadius(int r)
    {
        _grassR = r;
        float dens = Settings.GrassDensity;
        _grass.Visible = dens > 0.01f;
        if (!_grass.Visible) return;
        int perCell = dens > 1.1f ? 4 : 3;
        int side = r * 2;
        var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = GrassClump(), InstanceCount = side * side * perCell };
        var buf = new float[mm.InstanceCount * 12];
        int k = 0;
        for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
                for (int j = 0; j < perCell; j++)
                {
                    float ox = Hash.Cell01(x * 4 + j, y, 91), oz = Hash.Cell01(x * 4 + j, y, 92);
                    // basis = identity, origin = (x + ox, 0, y + oz)
                    int o = k * 12;
                    buf[o] = 1; buf[o + 1] = 0; buf[o + 2] = 0; buf[o + 3] = x + ox;
                    buf[o + 4] = 0; buf[o + 5] = 1; buf[o + 6] = 0; buf[o + 7] = 0;
                    buf[o + 8] = 0; buf[o + 9] = 0; buf[o + 10] = 1; buf[o + 11] = y + oz;
                    k++;
                }
        mm.Buffer = buf;
        _grass.Multimesh = mm;
        _grass.CustomAabb = new Aabb(new Vector3(0, -20, 0), new Vector3(side, 80, side));
        _grassMat.SetShaderParameter("radius", (float)r);
        _grassMat.SetShaderParameter("density_scale", Math.Min(1f, dens));
    }

    static ArrayMesh _clump;
    static ArrayMesh GrassClump()
    {
        if (_clump != null) return _clump;
        var b = new MeshBuilder();
        var rng = new Rng(55);
        for (int k = 0; k < 6; k++)
        {
            float ang = rng.Range(0f, Mathf.Tau);
            var dir = new Vector3(Mathf.Cos(ang), 0, Mathf.Sin(ang));
            var side = new Vector3(-dir.Z, 0, dir.X);
            var basePos = dir * rng.Range(0f, 0.18f);
            float h = rng.Range(0.3f, 0.6f), w = rng.Range(0.025f, 0.04f);
            float bend = rng.Range(0.05f, 0.18f);
            Vector3 P(float t) => basePos + dir * bend * t * t + Vector3.Up * h * t;
            var n = (Vector3.Up * 0.8f + dir * 0.4f).Normalized();
            int i0 = b.Add(P(0) - side * w, n, new Vector2(0, 1)), i1 = b.Add(P(0) + side * w, n, new Vector2(1, 1));
            int i2 = b.Add(P(0.5f) - side * w * 0.7f, n, new Vector2(0, 0.5f)), i3 = b.Add(P(0.5f) + side * w * 0.7f, n, new Vector2(1, 0.5f));
            int i4 = b.Add(P(1f), n, new Vector2(0.5f, 0));
            b.Tri(i0, i2, i1); b.Tri(i1, i2, i3); b.Tri(i2, i4, i3);
        }
        _clump = b.Commit();
        return _clump;
    }

    /// <summary>Moves the grass window with the camera focus and hides it when zoomed far out.</summary>
    public bool GrassEnabled = true;

    public void UpdateGrass(Vector3 focus, float camDistance)
    {
        if (_grass.Multimesh == null) return;
        bool show = GrassEnabled && camDistance < 95f && Settings.GrassDensity > 0.01f;
        _grass.Visible = show;
        if (!show) return;
        _grass.Position = new Vector3(Mathf.Floor(focus.X) - _grassR, 0, Mathf.Floor(focus.Z) - _grassR);
    }
}
