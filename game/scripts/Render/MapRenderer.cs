using System;
using System.Collections.Generic;
using System.Numerics;
using Godot;
using Remade.Core;
using Remade.Diagnostics;
using Remade.Map;
using Remade.Sim;
using GV3 = Godot.Vector3;
using GV2 = Godot.Vector2;

namespace Remade.Game.Render;

/// <summary>
/// Renders the static map: ground, granite massifs and water as 64×64-cell chunks. Chunks are built lazily when they
/// come near the view (a few per frame, nearest first), rebuilt when cells change, and freed when far away, so huge
/// maps cost memory only where the player looks. Also owns the map data textures sampled by the shaders.
/// </summary>
public partial class MapRenderer : Node3D
{
    public const int ChunkSize = 64;
    readonly GameSim _sim;
    readonly LocalMap _map;
    readonly int _cw, _ch;
    readonly Chunk[] _chunks;
    readonly ShaderMaterial _terrainMat, _rockMat, _waterMat, _wallMat, _doorMat;
    Image _dataImg;
    ImageTexture _dataTex, _heightTex;
    readonly byte[] _flow; // per cell: packed flow direction (x,y) and speed for rivers
    readonly Remade.Core.Noise _noise;
    bool _dataDirty;
    public int BuiltChunks { get; private set; }
    public readonly Dictionary<int, Node3D> Doors = new();

    sealed class Chunk
    {
        public int X, Y;
        public MeshInstance3D Ground, Rock, Water, Walls;
        public bool Built, Dirty;
        public long LastSeenFrame;
    }

    public MapRenderer(GameSim sim)
    {
        _sim = sim;
        _map = sim.Map;
        _cw = (_map.Width + ChunkSize - 1) / ChunkSize;
        _ch = (_map.Height + ChunkSize - 1) / ChunkSize;
        _chunks = new Chunk[_cw * _ch];
        for (int y = 0; y < _ch; y++)
            for (int x = 0; x < _cw; x++)
                _chunks[y * _cw + x] = new Chunk { X = x, Y = y };
        _noise = new Remade.Core.Noise(_map.Seed + 4242);

        _terrainMat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/terrain.gdshader") };
        _terrainMat.SetShaderParameter("noise_tex", ProcTextures.Noise);
        _rockMat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/rock.gdshader") };
        _rockMat.SetShaderParameter("granite", ProcTextures.Granite);
        _waterMat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/water.gdshader") };
        _waterMat.SetShaderParameter("noise_tex", ProcTextures.Noise);
        _waterMat.RenderPriority = 1;
        _wallMat = new ShaderMaterial { Shader = WallShader() };
        _doorMat = new ShaderMaterial { Shader = WallShader() };

        _flow = ComputeFlow();
        _waterMat.SetShaderParameter("river_dir", RiverDirection());
        BuildDataTextures();
    }

    /// <summary>Mean downstream direction of the map's river (the water shader scrolls river water along it).</summary>
    GV2 RiverDirection()
    {
        float sx = 0, sy = 0;
        for (int i = 0; i < _map.CellCount; i++)
        {
            if (_map.Terrain[i] is not (Terrain.RiverShallow or Terrain.RiverDeep)) continue;
            sx += _flow[i * 2] / 255f * 2f - 1f; sy += _flow[i * 2 + 1] / 255f * 2f - 1f;
        }
        var d = new GV2(sx, sy);
        return d.LengthSquared() < 1e-6f ? new GV2(0, 1) : d.Normalized();
    }

    static Shader WallShader() => new()
    {
        Code = @"shader_type spatial;
render_mode diffuse_burley;
#include ""res://shaders/world_common.gdshaderinc""
varying vec3 wpos;
varying vec3 wn;
void vertex() { wpos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz; wn = (MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz; }
void fragment() {
	// stacked logs: rounded horizontal bands with dark gaps, wood grain along the log
	float band = fract(wpos.y / 0.28);
	float round_ = sin(band * 3.14159);
	float along = abs(wn.x) > abs(wn.z) ? wpos.z : wpos.x;
	float grain = fbm2(vec2(along * 3.0, wpos.y * 30.0));
	vec3 wood = mix(vec3(0.36, 0.24, 0.14), vec3(0.50, 0.35, 0.20), grain) * (0.6 + 0.4 * round_);
	if (band < 0.06 || band > 0.94) wood *= 0.35;
	// tops of walls: end grain
	if (wn.y > 0.7) wood = mix(vec3(0.55, 0.42, 0.28), vec3(0.42, 0.30, 0.18), fbm2(wpos.xz * 8.0));
	wood = mix(wood, vec3(0.9, 0.92, 0.95), snow_cover * step(0.7, wn.y));
	ALBEDO = wood * COLOR.rgb * cloud_shadow(wpos.xz);
	ROUGHNESS = 0.85;
}",
    };

    // ------------------------------------------------------------------ data textures

    void BuildDataTextures()
    {
        int W = _map.Width, H = _map.Height;
        var data = new byte[W * H * 4];
        for (int i = 0; i < _map.CellCount; i++) WriteCell(data, i);
        _dataImg = Image.CreateFromData(W, H, false, Image.Format.Rgba8, data);
        _dataTex = ImageTexture.CreateFromImage(_dataImg);
        RenderingServer.GlobalShaderParameterSet("map_data", _dataTex);
        RenderingServer.GlobalShaderParameterSet("map_size", new GV2(W, H));

        var heights = new float[(W + 1) * (H + 1)];
        Array.Copy(_map.Ground, heights, heights.Length);
        var hb = new byte[heights.Length * 4];
        Buffer.BlockCopy(heights, 0, hb, 0, hb.Length);
        _heightTex = ImageTexture.CreateFromImage(Image.CreateFromData(W + 1, H + 1, false, Image.Format.Rf, hb));
    }

    public Texture2D HeightTexture => _heightTex;

    /// <summary>Zoomed out beyond the grass blades, the ground's meadow tint takes over their colour.</summary>
    public void SetCameraDistance(float camDistance) => _terrainMat.SetShaderParameter("far_view", Mathf.SmoothStep(85f, 95f, camDistance));

    void WriteCell(byte[] data, int i)
    {
        data[i * 4] = (byte)_map.Terrain[i];
        data[i * 4 + 1] = _map.Plants[i] == Plant.BerryBush ? (byte)(_map.Grass[i] / 2) : _map.Grass[i];
        data[i * 4 + 2] = _map.Buildings[i] == Building.Granite ? (byte)255 : (byte)0;
        data[i * 4 + 3] = 255;
    }

    void UpdateDataCell(int i)
    {
        _map.XY(i, out int x, out int y);
        float terrain = (byte)_map.Terrain[i] / 255f;
        float grass = (_map.Plants[i] == Plant.BerryBush ? _map.Grass[i] / 2 : _map.Grass[i]) / 255f;
        float rock = _map.Buildings[i] == Building.Granite ? 1f : 0f;
        _dataImg.SetPixel(x, y, new Color(terrain, grass, rock, 1f));
        _dataDirty = true;
    }

    // ------------------------------------------------------------------ river flow

    byte[] ComputeFlow()
    {
        int W = _map.Width, H = _map.Height;
        var flow = new byte[W * H * 2];
        var feat = TileFeatures.From(_sim.Planet, _map.PlanetTile);
        if (feat.RiverSize == 0) return flow;
        var outDir = feat.RiverOut ?? (feat.SeaDirs.Count > 0 ? feat.SeaDirs[0] : new System.Numerics.Vector2(0, 1));
        // blurred river mask; the channel tangent is perpendicular to its gradient
        var mask = new float[W * H];
        for (int i = 0; i < mask.Length; i++) mask[i] = _map.Terrain[i] is Terrain.RiverShallow or Terrain.RiverDeep ? 1f : 0f;
        mask = Blur(mask, W, H, 4);
        for (int y = 1; y < H - 1; y++)
            for (int x = 1; x < W - 1; x++)
            {
                int i = y * W + x;
                if (_map.Terrain[i] is not (Terrain.RiverShallow or Terrain.RiverDeep)) continue;
                float gx = mask[i + 1] - mask[i - 1], gy = mask[i + W] - mask[i - W];
                var t = new System.Numerics.Vector2(-gy, gx);
                if (t.LengthSquared() < 1e-6f) t = outDir;
                t = System.Numerics.Vector2.Normalize(t);
                if (System.Numerics.Vector2.Dot(t, outDir) < 0) t = -t;
                flow[i * 2] = (byte)((t.X * 0.5f + 0.5f) * 255);
                flow[i * 2 + 1] = (byte)((t.Y * 0.5f + 0.5f) * 255);
            }
        return flow;
    }

    static float[] Blur(float[] src, int W, int H, int r)
    {
        var tmp = new float[src.Length];
        var dst = new float[src.Length];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                float s = 0; int n = 0;
                for (int k = -r; k <= r; k++) { int xx = x + k; if (xx < 0 || xx >= W) continue; s += src[y * W + xx]; n++; }
                tmp[y * W + x] = s / n;
            }
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                float s = 0; int n = 0;
                for (int k = -r; k <= r; k++) { int yy = y + k; if (yy < 0 || yy >= H) continue; s += tmp[yy * W + x]; n++; }
                dst[y * W + x] = s / n;
            }
        return dst;
    }

    // ------------------------------------------------------------------ streaming

    /// <summary>Builds chunks near the view (within a frame budget), frees distant ones. viewRect = x0, y0, x1, y1 in cells.</summary>
    public void UpdateStreaming(Rect2 viewRect, long frame, double budgetMs = 4)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int cx0 = Math.Max(0, (int)(viewRect.Position.X / ChunkSize) - 1), cy0 = Math.Max(0, (int)(viewRect.Position.Y / ChunkSize) - 1);
        int cx1 = Math.Min(_cw - 1, (int)(viewRect.End.X / ChunkSize) + 1), cy1 = Math.Min(_ch - 1, (int)(viewRect.End.Y / ChunkSize) + 1);
        GV2 center = viewRect.GetCenter();
        // nearest missing/dirty chunks first
        var todo = new List<Chunk>();
        for (int y = cy0; y <= cy1; y++)
            for (int x = cx0; x <= cx1; x++)
            {
                var c = _chunks[y * _cw + x];
                c.LastSeenFrame = frame;
                if (!c.Built || c.Dirty) todo.Add(c);
            }
        todo.Sort((a, b) => DistTo(a, center).CompareTo(DistTo(b, center)));
        foreach (var c in todo)
        {
            Build(c);
            if (sw.Elapsed.TotalMilliseconds > budgetMs) break;
        }
        // free chunks not seen for a while (keeps memory bounded on huge maps)
        if (frame % 60 == 0 && BuiltChunks > 160)
            foreach (var c in _chunks)
                if (c.Built && frame - c.LastSeenFrame > 600) Free(c);
        if (_dataDirty)
        {
            _dataTex.Update(_dataImg);
            _dataDirty = false;
        }
    }

    static float DistTo(Chunk c, GV2 p) => new GV2((c.X + 0.5f) * ChunkSize, (c.Y + 0.5f) * ChunkSize).DistanceSquaredTo(p);

    /// <summary>Applies map changes: marks the affected chunks (and neighbours on borders) for rebuild.</summary>
    public void OnCellsChanged(List<CellChange> changes)
    {
        foreach (var ch in changes)
        {
            _map.XY(ch.Cell, out int x, out int y);
            if ((ch.Layer & (MapLayer.Terrain | MapLayer.Building | MapLayer.Grass | MapLayer.Plant)) != 0) UpdateDataCell(ch.Cell);
            if ((ch.Layer & (MapLayer.Terrain | MapLayer.Building)) != 0)
            {
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (!_map.InBounds(nx, ny)) continue;
                        _chunks[(ny / ChunkSize) * _cw + nx / ChunkSize].Dirty = true;
                    }
            }
            if ((ch.Layer & MapLayer.Door) != 0 && Doors.TryGetValue(ch.Cell, out var door)) door.SetMeta("open", _map.DoorOpen[ch.Cell]);
        }
    }

    void Free(Chunk c)
    {
        foreach (var mi in new[] { c.Ground, c.Rock, c.Water, c.Walls }) mi?.QueueFree();
        c.Ground = c.Rock = c.Water = c.Walls = null;
        // doors in this chunk
        for (int y = c.Y * ChunkSize; y < Math.Min(_map.Height, (c.Y + 1) * ChunkSize); y++)
            for (int x = c.X * ChunkSize; x < Math.Min(_map.Width, (c.X + 1) * ChunkSize); x++)
            {
                int i = _map.Index(x, y);
                if (Doors.TryGetValue(i, out var d)) { d.QueueFree(); Doors.Remove(i); }
            }
        c.Built = false;
        BuiltChunks--;
    }

    void Build(Chunk c)
    {
        if (c.Built) Free(c);
        int x0 = c.X * ChunkSize, y0 = c.Y * ChunkSize;
        int x1 = Math.Min(_map.Width, x0 + ChunkSize), y1 = Math.Min(_map.Height, y0 + ChunkSize);
        c.Ground = Attach(BuildGround(x0, y0, x1, y1), _terrainMat, false); // receives shadows; gentle hills rarely cast visible ones
        c.Rock = Attach(BuildRock(x0, y0, x1, y1), _rockMat, true);
        c.Water = Attach(BuildWater(x0, y0, x1, y1), _waterMat, false);
        c.Walls = Attach(BuildWalls(x0, y0, x1, y1), _wallMat, true);
        c.Built = true; c.Dirty = false;
        BuiltChunks++;
    }

    public bool WaterVisible = true;

    MeshInstance3D Attach(ArrayMesh mesh, Material mat, bool shadows)
    {
        if (mesh == null) return null;
        if (mat == _waterMat && !WaterVisible) return null;
        var mi = new MeshInstance3D
        {
            Mesh = mesh, MaterialOverride = mat,
            CastShadow = shadows ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(mi);
        return mi;
    }

    // ------------------------------------------------------------------ ground

    ArrayMesh BuildGround(int x0, int y0, int x1, int y1)
    {
        int w = x1 - x0 + 1, h = y1 - y0 + 1;
        var verts = new GV3[w * h];
        var norms = new GV3[w * h];
        int W1 = _map.Width + 1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int gx = x0 + x, gy = y0 + y;
                float hgt = _map.Ground[gy * W1 + gx];
                verts[y * w + x] = new GV3(gx, hgt, gy);
                float hl = _map.Ground[gy * W1 + Math.Max(0, gx - 1)], hr = _map.Ground[gy * W1 + Math.Min(_map.Width, gx + 1)];
                float hu = _map.Ground[Math.Max(0, gy - 1) * W1 + gx], hd = _map.Ground[Math.Min(_map.Height, gy + 1) * W1 + gx];
                norms[y * w + x] = new GV3(hl - hr, 2f, hu - hd).Normalized();
            }
        var idx = new int[(w - 1) * (h - 1) * 6];
        int k = 0;
        for (int y = 0; y < h - 1; y++)
            for (int x = 0; x < w - 1; x++)
            {
                int a = y * w + x, b = a + 1, d = a + w, e = d + 1;
                // clockwise from above (Godot front faces)
                idx[k++] = a; idx[k++] = b; idx[k++] = d;
                idx[k++] = b; idx[k++] = e; idx[k++] = d;
            }
        var arr = new Godot.Collections.Array();
        arr.Resize((int)Mesh.ArrayType.Max);
        arr[(int)Mesh.ArrayType.Vertex] = verts;
        arr[(int)Mesh.ArrayType.Normal] = norms;
        arr[(int)Mesh.ArrayType.Index] = idx;
        var m = new ArrayMesh();
        m.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arr);
        return m;
    }

    // ------------------------------------------------------------------ rock

    bool IsRock(int x, int y) => _map.InBounds(x, y) && _map.Buildings[_map.Index(x, y)] == Building.Granite;

    /// <summary>Displaced top corner of the massif at grid corner (cx, cy): average height of the adjacent rock cells.</summary>
    GV3 RockCorner(int cx, int cy, out bool boundary)
    {
        float hsum = 0; int n = 0; boundary = false;
        for (int dy = -1; dy <= 0; dy++)
            for (int dx = -1; dx <= 0; dx++)
            {
                int x = cx + dx, y = cy + dy;
                if (IsRock(x, y)) { hsum += _map.RockHeight[_map.Index(x, y)]; n++; }
                else boundary = true;
            }
        float ground = _map.Ground[Math.Clamp(cy, 0, _map.Height) * (_map.Width + 1) + Math.Clamp(cx, 0, _map.Width)];
        float hgt = n > 0 ? hsum / n : 0;
        if (boundary) hgt *= 0.82f;
        var p = new GV3(cx, ground + hgt, cy);
        // jagged rims: push boundary corners sideways with noise
        float jx = _noise.Get(cx * 0.61f, cy * 0.61f, 1.3f), jz = _noise.Get(cx * 0.61f, cy * 0.61f, 7.9f);
        p += new GV3(jx, _noise.Get(cx * 0.9f, cy * 0.9f) * 0.6f, jz) * (boundary ? 0.32f : 0.12f);
        return p;
    }

    ArrayMesh BuildRock(int x0, int y0, int x1, int y1)
    {
        var b = new MeshBuilder();
        bool any = false;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                if (!IsRock(x, y)) continue;
                any = true;
                GV3 c00 = RockCorner(x, y, out _), c10 = RockCorner(x + 1, y, out _), c01 = RockCorner(x, y + 1, out _), c11 = RockCorner(x + 1, y + 1, out _);
                // top (two flat-shaded triangles, split along the shorter diagonal)
                b.Color = new Color(1, 1, 1);
                TriFlat(b, c00, c10, c01);
                TriFlat(b, c10, c11, c01);
                // cliff walls toward non-rock neighbours
                if (!IsRock(x, y - 1)) Wall(b, c10, c00, new GV2(0, -1), x + 1, y, x, y);
                if (!IsRock(x, y + 1)) Wall(b, c01, c11, new GV2(0, 1), x, y + 1, x + 1, y + 1);
                if (!IsRock(x - 1, y)) Wall(b, c00, c01, new GV2(-1, 0), x, y, x, y + 1);
                if (!IsRock(x + 1, y)) Wall(b, c11, c10, new GV2(1, 0), x + 1, y + 1, x + 1, y);
            }
        return any ? b.Commit() : null;
    }

    static void TriFlat(MeshBuilder b, GV3 a, GV3 c1, GV3 c2)
    {
        // a, c1, c2 counter-clockwise from above → emit clockwise
        var n = (c2 - a).Cross(c1 - a).Normalized();
        if (n.Y < 0) n = -n;
        int ia = b.Add(a, n, new GV2(a.X, a.Z)), i1 = b.Add(c1, n, new GV2(c1.X, c1.Z)), i2 = b.Add(c2, n, new GV2(c2.X, c2.Z));
        b.Tri(ia, i1, i2);
    }

    /// <summary>A cliff face from two top corners down to the ground, in three bulging rows (talus at the foot).</summary>
    void Wall(MeshBuilder b, GV3 topA, GV3 topB, GV2 outward, int ax, int ay, int bx, int by)
    {
        const int rows = 3;
        var o3 = new GV3(outward.X, 0, outward.Y);
        GV3 Row(GV3 top, int gx, int gy, int r)
        {
            float t = r / (float)rows; // 0 top .. 1 bottom
            float ground = _map.Ground[Math.Clamp(gy, 0, _map.Height) * (_map.Width + 1) + Math.Clamp(gx, 0, _map.Width)] - 0.15f;
            var bottom = new GV3(top.X, ground, top.Z) + o3 * 0.28f;
            var p = top.Lerp(bottom, t);
            float bulge = _noise.Get(top.X * 0.8f + r * 3.1f, top.Z * 0.8f, r * 1.7f) * 0.22f * MathF.Sin(t * MathF.PI);
            return p + o3 * (bulge + t * t * 0.18f);
        }
        for (int r = 0; r < rows; r++)
        {
            GV3 a0 = Row(topA, ax, ay, r), b0 = Row(topB, bx, by, r), a1 = Row(topA, ax, ay, r + 1), b1 = Row(topB, bx, by, r + 1);
            float ao0 = 1f - r / (float)rows * 0.6f, ao1 = 1f - (r + 1) / (float)rows * 0.6f;
            var n = (b0 - a0).Cross(a1 - a0).Normalized();
            if (n.Dot(o3) < 0) n = -n;
            int i0 = b.Add(a0, n, GV2.Zero, new Color(ao0, ao0, ao0)), i1 = b.Add(b0, n, GV2.Zero, new Color(ao0, ao0, ao0));
            int i2 = b.Add(b1, n, GV2.Zero, new Color(ao1, ao1, ao1)), i3 = b.Add(a1, n, GV2.Zero, new Color(ao1, ao1, ao1));
            // the face must look outward: choose winding by the normal
            var geomN = (b0 - a0).Cross(b1 - a0);
            if (geomN.Dot(o3) > 0) { b.Tri(i0, i2, i1); b.Tri(i0, i3, i2); }
            else { b.Tri(i0, i1, i2); b.Tri(i0, i2, i3); }
        }
    }

    // ------------------------------------------------------------------ water

    ArrayMesh BuildWater(int x0, int y0, int x1, int y1)
    {
        var b = new MeshBuilder();
        int cw = x1 - x0 + 1;
        var corner = new int[cw * (y1 - y0 + 1)];
        Array.Fill(corner, -1);
        bool any = false;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                // water cells, plus land cells touching water (the surface covers the sloping shore)
                bool wet = false;
                for (int dy = -1; dy <= 1 && !wet; dy++)
                    for (int dx = -1; dx <= 1 && !wet; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (_map.InBounds(nx, ny) && _map.TerrainAt(_map.Index(nx, ny)).Water) wet = true;
                    }
                if (!wet || _map.Buildings[_map.Index(x, y)] == Building.Granite) continue;
                any = true;
                int a = Corner(x, y), bb = Corner(x + 1, y), c = Corner(x + 1, y + 1), d = Corner(x, y + 1);
                b.Tri(a, bb, d); b.Tri(bb, c, d);
            }
        return any ? b.Commit() : null;

        // shared corner vertices; flow = average of the river cells around the corner, so it varies smoothly
        int Corner(int cx, int cy)
        {
            int k = (cy - y0) * cw + (cx - x0);
            if (corner[k] >= 0) return corner[k];
            float fx = 0, fy = 0; int n = 0, cells = 0;
            for (int dy = -1; dy <= 0; dy++)
                for (int dx = -1; dx <= 0; dx++)
                {
                    int x = cx + dx, y = cy + dy;
                    if (!_map.InBounds(x, y)) continue;
                    cells++;
                    int i = _map.Index(x, y);
                    if (_map.Terrain[i] is not (Terrain.RiverDeep or Terrain.RiverShallow)) continue;
                    fx += _flow[i * 2] / 255f * 2f - 1f; fy += _flow[i * 2 + 1] / 255f * 2f - 1f; n++;
                }
            var f = n > 0 ? new Color((fx / n) * 0.5f + 0.5f, (fy / n) * 0.5f + 0.5f, 0.25f * n / Math.Max(1, cells)) : new Color(0.5f, 0.5f, 0f);
            corner[k] = b.Add(new GV3(cx, LocalMap.WaterLevel, cy), GV3.Up, new GV2(cx, cy), f);
            return corner[k];
        }
    }

    // ------------------------------------------------------------------ walls and doors

    ArrayMesh BuildWalls(int x0, int y0, int x1, int y1)
    {
        var b = new MeshBuilder();
        bool any = false;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = _map.Index(x, y);
                var bt = _map.Buildings[i];
                if (bt == Building.WoodWall)
                {
                    any = true;
                    float g = _map.GroundHeight(x + 0.5f, y + 0.5f);
                    b.Color = new Color(1, 1, 1);
                    b.Box(new GV3(x + 0.5f, g + 1.1f, y + 0.5f), new GV3(0.5f, 1.25f, 0.5f));
                }
                else if (bt == Building.Door) MakeDoor(i, x, y);
            }
        return any ? b.Commit() : null;
    }

    void MakeDoor(int cell, int x, int y)
    {
        if (Doors.ContainsKey(cell)) return;
        // orientation: the door spans between the two wall neighbours
        bool alongX = IsWallish(x - 1, y) || IsWallish(x + 1, y);
        float g = _map.GroundHeight(x + 0.5f, y + 0.5f);
        var root = new Node3D { Position = new GV3(x + 0.5f, g, y + 0.5f), Rotation = new GV3(0, alongX ? 0 : Mathf.Pi * 0.5f, 0) };
        var frame = new MeshBuilder { Color = new Color(0.8f, 0.8f, 0.8f) };
        frame.Box(new GV3(-0.46f, 1.15f, 0), new GV3(0.06f, 1.2f, 0.16f));
        frame.Box(new GV3(0.46f, 1.15f, 0), new GV3(0.06f, 1.2f, 0.16f));
        frame.Box(new GV3(0, 2.3f, 0), new GV3(0.52f, 0.1f, 0.16f));
        root.AddChild(new MeshInstance3D { Mesh = frame.Commit(), MaterialOverride = _doorMat });
        var hinge = new Node3D { Position = new GV3(-0.4f, 0, 0), Name = "Hinge" };
        root.AddChild(hinge);
        var leaf = new MeshBuilder { Color = new Color(0.75f, 0.62f, 0.5f) };
        leaf.Box(new GV3(0.4f, 1.05f, 0), new GV3(0.4f, 1.05f, 0.05f));
        leaf.Color = new Color(0.25f, 0.25f, 0.25f);
        leaf.Box(new GV3(0.7f, 1.05f, -0.07f), new GV3(0.03f, 0.03f, 0.03f));
        hinge.AddChild(new MeshInstance3D { Mesh = leaf.Commit(new StandardMaterial3D { VertexColorUseAsAlbedo = true, VertexColorIsSrgb = true, Roughness = 0.8f }) });
        root.SetMeta("open", _map.DoorOpen[cell]);
        AddChild(root);
        Doors[cell] = root;
    }

    bool IsWallish(int x, int y) => _map.InBounds(x, y) && _map.Buildings[_map.Index(x, y)] is Building.WoodWall or Building.Granite;

    /// <summary>Swings door leaves toward their open/closed state.</summary>
    public void AnimateDoors(float dt)
    {
        foreach (var kv in Doors)
        {
            var hinge = kv.Value.GetNode<Node3D>("Hinge");
            bool open = (bool)kv.Value.GetMeta("open");
            float target = open ? -Mathf.Pi * 0.5f : 0f;
            var r = hinge.Rotation;
            r.Y = Mathf.MoveToward(r.Y, target, dt * 4f);
            hinge.Rotation = r;
        }
    }
}
