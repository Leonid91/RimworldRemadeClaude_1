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
/// berries, and a camera-centred GPU grass field (plain clumps plus two flower fields).
/// </summary>
public partial class FloraRenderer : Node3D
{
    // Godot culls and picks LOD per MultiMesh, never per instance: small regions let the camera frustum, the shadow
    // frustum and the detail/low switch work tree by tree (128-cell regions drew thousands of off-screen trees in full
    // detail, twice for the shadow splits)
    const int Region = 32;
    /// <summary>Distinct oak meshes (map variants are folded onto them): fewer MultiMeshes per region.</summary>
    const int OakMeshes = 8;
    readonly GameSim _sim;
    readonly LocalMap _map;
    readonly int _rw, _rh;
    readonly RegionNode[] _regions;
    readonly Models.Tree[] _oaks = new Models.Tree[OakMeshes];
    readonly ArrayMesh[] _bushes = new ArrayMesh[PlantInfo.BushVariants];
    readonly ArrayMesh[] _bushShadows = new ArrayMesh[PlantInfo.BushVariants];
    readonly ArrayMesh _berries;
    // grass field: [0] plain clumps (whole-cell slots), [1] yellow/white and [2] purple/blue flowers (2×2-cell slots),
    // each split into Tiles×Tiles MultiMeshes so the camera frustum culls the off-screen parts of the field
    const int Tiles = 4;
    readonly MultiMeshInstance3D[][] _grass = new MultiMeshInstance3D[3][];
    int _grassSlots;
    readonly ShaderMaterial[] _grassMat = new ShaderMaterial[3];
    int _grassR;
    public float DetailDistance = 48f;
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
            for (int v = 0; v < _bushes.Length; v++) { _bushes[v] = Models.Bush(v); _bushShadows[v] = Models.Bush(v, shadowProxy: true); }
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
        var oakXf = new List<Transform3D>[OakMeshes];
        var oakTint = new List<Color>[OakMeshes];
        var bushXf = new List<Transform3D>[PlantInfo.BushVariants];
        var bushTint = new List<Color>[PlantInfo.BushVariants];
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
                // leaf tint varies per plant (brightness and a slight hue shift); alpha = sway phase × 0.5
                float v = 0.82f + 0.36f * Hash.Cell01(x, y, 63), hue = (Hash.Cell01(x, y, 64) - 0.5f) * 0.12f;
                var baseLeaf = p == Plant.Oak ? Models.OakLeaf : Models.BushLeaf;
                var tint = new Color(baseLeaf.R * v * (1 + hue), baseLeaf.G * v, baseLeaf.B * v * (1 - hue), Hash.Cell01(x, y, 65) * 0.499f);
                if (p == Plant.Oak)
                {
                    float s = (0.32f + grow * 0.78f) * (0.9f + Hash.Cell01(x, y, 62) * 0.24f);
                    float sx = s * (0.85f + Hash.Cell01(x, y, 14) * 0.35f), sy = s * (0.88f + Hash.Cell01(x, y, 15) * 0.28f);
                    var basis = new Basis(Vector3.Up, yaw).Scaled(new Vector3(sx, sy, sx * (0.9f + Hash.Cell01(x, y, 16) * 0.2f)));
                    int ov = _map.PlantVariant[i] % OakMeshes;
                    (oakXf[ov] ??= new List<Transform3D>()).Add(new Transform3D(basis, new Vector3(px, g - 0.05f, pz)));
                    (oakTint[ov] ??= new List<Color>()).Add(tint);
                }
                else
                {
                    float s = (0.55f + grow * 0.5f) * (0.85f + 0.3f * Hash.Cell01(x, y, 62));
                    var t = new Transform3D(new Basis(Vector3.Up, yaw).Scaled(new Vector3(s, s, s)), new Vector3(px, g, pz));
                    int bv = _map.PlantVariant[i] % PlantInfo.BushVariants;
                    (bushXf[bv] ??= new List<Transform3D>()).Add(t);
                    (bushTint[bv] ??= new List<Color>()).Add(tint);
                    berryXf.Add(t);
                    r.BerryCells.Add(i);
                }
            }
        for (int v = 0; v < oakXf.Length; v++)
        {
            if (oakXf[v] == null) continue;
            // near: detailed mesh, shadows cast by the cheap low-LOD mesh (shadow-only); far: low-LOD mesh
            // detail near, low far (cross-faded); shadows from the sparse proxy at every distance
            AddMM(r.Root, _oaks[v].Detail, oakXf[v], 0, DetailDistance, false, oakTint[v]);
            AddMM(r.Root, _oaks[v].Low, oakXf[v], DetailDistance - 8, 0, false, oakTint[v]);
            AddMM(r.Root, _oaks[v].Shadow, oakXf[v], 0, 0, true, oakTint[v], shadowOnly: true);
        }
        for (int v = 0; v < bushXf.Length; v++)
        {
            if (bushXf[v] == null) continue;
            AddMM(r.Root, _bushes[v], bushXf[v], 0, 160, false, bushTint[v]);
            AddMM(r.Root, _bushShadows[v], bushXf[v], 0, 90, true, bushTint[v], shadowOnly: true);
        }
        if (berryXf.Count > 0)
        {
            r.Berries = AddMM(r.Root, _berries, berryXf, 0, 90, false, null, berryCustom: true);
            UpdateBerries(r);
        }
        r.Built = true; r.Dirty = false;
    }

    /// <summary>A MultiMesh of one mesh; <paramref name="tints"/> go to the custom data (leaf tint + sway phase).</summary>
    MultiMeshInstance3D AddMM(Node3D parent, Mesh mesh, List<Transform3D> xf, float visBegin, float visEnd, bool shadows, List<Color> tints, bool shadowOnly = false, bool berryCustom = false)
    {
        bool custom = tints != null || berryCustom;
        Invariant.Check(tints == null || tints.Count == xf.Count, $"{tints?.Count} tints for {xf.Count} instances");
        var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = custom, Mesh = mesh, InstanceCount = xf.Count };
        for (int k = 0; k < xf.Count; k++)
        {
            mm.SetInstanceTransform(k, xf[k]);
            if (tints != null) mm.SetInstanceCustomData(k, tints[k]);
        }
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
        var shader = GD.Load<Shader>("res://shaders/grass.gdshader");
        for (int k = 0; k < 3; k++)
        {
            _grassMat[k] = new ShaderMaterial { Shader = shader };
            _grassMat[k].SetShaderParameter("height_map", heightTex);
            _grassMat[k].SetShaderParameter("noise_tex", ProcTextures.Noise);
            _grassMat[k].SetShaderParameter("kind", k);
            _grassMat[k].SetShaderParameter("slot_cells", k == 0 ? 1f : 2f);
            _grass[k] = new MultiMeshInstance3D[Tiles * Tiles];
            for (int t = 0; t < Tiles * Tiles; t++)
            {
                _grass[k][t] = new MultiMeshInstance3D { CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, MaterialOverride = _grassMat[k], Name = $"Grass{k}_{t}" };
                AddChild(_grass[k][t]);
            }
        }
        SetGrassRadius(40);
    }

    /// <summary>
    /// (Re)creates the slot pattern for a window radius (cells); density from the graphics settings. Slots are whole
    /// cells (pairs of cells for flowers) with a slot index in the custom data; the shader hashes everything else from
    /// the world cell, so the field is identical wherever the window sits. Within a tile the instances are ordered slot
    /// by slot (all first slots, then all second slots…), so drawing only the first instances thins the field evenly.
    /// </summary>
    public void SetGrassRadius(int r)
    {
        _grassR = r;
        float dens = Settings.GrassDensity;
        bool on = dens > 0.01f;
        for (int k = 0; k < 3; k++) foreach (var g in _grass[k]) g.Visible = on;
        if (!on) return;
        _grassSlots = dens > 1.1f ? 4 : 3;
        int side = r * 2;
        for (int k = 0; k < 3; k++)
        {
            int step = k == 0 ? 1 : 2, slots = k == 0 ? _grassSlots : 1;
            int n = side / step / Tiles; // slots per tile side
            for (int t = 0; t < Tiles * Tiles; t++)
            {
                var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true, Mesh = GrassClumps[k], InstanceCount = n * n * slots };
                var buf = new float[mm.InstanceCount * 16];
                int i = 0;
                for (int j = 0; j < slots; j++)
                    for (int y = 0; y < n; y++)
                        for (int x = 0; x < n; x++)
                        {
                            // basis = identity, origin = slot corner within the tile; custom.r = slot index / 8
                            int o = i * 16;
                            buf[o] = 1; buf[o + 3] = x * step;
                            buf[o + 5] = 1;
                            buf[o + 10] = 1; buf[o + 11] = y * step;
                            buf[o + 12] = j / 8f;
                            i++;
                        }
                mm.Buffer = buf;
                _grass[k][t].Multimesh = mm;
                _grass[k][t].CustomAabb = new Aabb(new Vector3(-2, -20, -2), new Vector3(n * step + 4, 80, n * step + 4));
            }
            _grassMat[k].SetShaderParameter("radius", (float)r);
            _grassMat[k].SetShaderParameter("slots_per_cell", (float)slots);
            _grassMat[k].SetShaderParameter("density_scale", Math.Min(1f, dens));
        }
    }

    static ArrayMesh[] _clumps;
    static ArrayMesh[] GrassClumps => _clumps ??= new[] { GrassClump(101, 0), GrassClump(102, 1), GrassClump(103, 2) };

    /// <summary>
    /// A grass clump (first prototype's model): tapered 3-segment blades, 0.13–0.36 m, leaning and bending at
    /// random; kinds 1 and 2 add a few flowers (stem + five-petal star, colour in COLOR with alpha 1).
    /// COLOR.r of blades = per-blade shade, UV.y = height along the blade.
    /// </summary>
    static ArrayMesh GrassClump(int seed, int kind)
    {
        var rng = new Rng((ulong)seed);
        var b = new MeshBuilder();
        int blades = kind == 0 ? 12 : 9;
        for (int k = 0; k < blades; k++)
        {
            float a = rng.Range(0f, Mathf.Tau);
            float rad = MathF.Sqrt(rng.NextFloat()) * 0.34f;
            var basep = new Vector3(MathF.Cos(a) * rad, 0, MathF.Sin(a) * rad);
            float h = rng.Range(0.13f, 0.36f) * (rng.Chance(0.15f) ? 1.35f : 1f);
            float w = rng.Range(0.026f, 0.042f);
            float bendA = rng.Range(0f, Mathf.Tau);
            var bendDir = new Vector3(MathF.Cos(bendA), 0, MathF.Sin(bendA));
            float bend = rng.Range(0.04f, 0.2f) * h * 3f;
            float faceA = rng.Range(0f, Mathf.Tau);
            var side = new Vector3(MathF.Cos(faceA), 0, MathF.Sin(faceA));
            var nrm = side.Cross(Vector3.Up).Normalized();
            const int seg = 3;
            int prevL = -1, prevR = -1;
            for (int sgi = 0; sgi <= seg; sgi++)
            {
                float t = sgi / (float)seg;
                var p = basep + Vector3.Up * h * t + bendDir * bend * t * t;
                float ww = w * (1f - t * 0.92f);
                float shade = rng.Range(0.85f, 1.1f);
                int l = b.Add(p - side * ww, nrm, new Vector2(0, t), new Color(shade, 1, 1, 0));
                int rr = b.Add(p + side * ww, nrm, new Vector2(1, t), new Color(shade, 1, 1, 0));
                if (prevL >= 0) b.Quad(prevL, l, rr, prevR);
                prevL = l; prevR = rr;
            }
        }
        if (kind > 0)
        {
            int flowers = rng.Range(2, 5);
            for (int f = 0; f < flowers; f++)
            {
                var p = new Vector3(rng.Range(-0.2f, 0.2f), rng.Range(0.22f, 0.36f), rng.Range(-0.2f, 0.2f));
                Color c = kind == 1
                    ? (rng.Chance(0.6f) ? new Color(1f, 0.82f, 0.18f, 1) : new Color(0.97f, 0.96f, 0.92f, 1))
                    : (rng.Chance(0.6f) ? new Color(0.62f, 0.38f, 0.85f, 1) : new Color(0.45f, 0.6f, 0.95f, 1));
                var sd = new Vector3(0.008f, 0, 0);
                int i0 = b.Add(new Vector3(p.X, 0, p.Z) - sd, Vector3.Back, new Vector2(0, 0), new Color(1, 1, 1, 0));
                int i1 = b.Add(new Vector3(p.X, 0, p.Z) + sd, Vector3.Back, new Vector2(1, 0), new Color(1, 1, 1, 0));
                int i2 = b.Add(p + sd, Vector3.Back, new Vector2(1, 0.95f), new Color(1, 1, 1, 0));
                int i3 = b.Add(p - sd, Vector3.Back, new Vector2(0, 0.95f), new Color(1, 1, 1, 0));
                b.Quad(i0, i3, i2, i1);
                int ctr = b.Add(p + Vector3.Up * 0.01f, Vector3.Up, new Vector2(0.5f, 1), new Color(c.R * 0.8f, c.G * 0.7f, c.B * 0.4f, 1));
                int first = -1, prev = -1;
                for (int q = 0; q <= 5; q++)
                {
                    float an = q / 5f * Mathf.Tau;
                    var pt = p + new Vector3(MathF.Cos(an), 0.15f, MathF.Sin(an)) * 0.045f;
                    int vi = q == 5 ? first : b.Add(pt, Vector3.Up, new Vector2(0.5f, 1), c);
                    if (q == 0) first = vi;
                    if (prev >= 0) b.Tri(ctr, vi, prev);
                    prev = vi;
                }
            }
        }
        return b.Commit();
    }

    /// <summary>Moves the grass window with the camera focus and hides it when zoomed far out.</summary>
    public bool GrassEnabled = true;

    public void UpdateGrass(Vector3 focus, float camDistance)
    {
        if (_grass[0][0].Multimesh == null) return;
        bool show = GrassEnabled && camDistance < 95f && Settings.GrassDensity > 0.01f;
        // zoomed out the blades are only a few pixels tall: fewer clumps per cell look the same and cost far less
        int slots = camDistance < 34f ? _grassSlots : camDistance < 60f ? Math.Min(2, _grassSlots) : 1;
        for (int k = 0; k < 3; k++)
        {
            float step = k == 0 ? 1f : 2f;
            // snap to whole slots (1 cell, or 2 cells for flowers) so slots always sit on the same world cells
            var origin = new Vector3(Mathf.Floor((focus.X - _grassR) / step) * step, 0, Mathf.Floor((focus.Z - _grassR) / step) * step);
            float tileSize = _grassR * 2f / Tiles;
            for (int t = 0; t < Tiles * Tiles; t++)
            {
                var g = _grass[k][t];
                g.Visible = show;
                if (!show) continue;
                g.Position = origin + new Vector3(t % Tiles * tileSize, 0, t / Tiles * tileSize);
                var mm = g.Multimesh;
                int perSlot = mm.InstanceCount / (k == 0 ? _grassSlots : 1);
                mm.VisibleInstanceCount = k == 0 ? perSlot * slots : -1;
            }
        }
    }
}
