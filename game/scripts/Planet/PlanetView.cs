using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using Remade.Diagnostics;
using WorldPlanet = Remade.World.Planet;
using V3 = System.Numerics.Vector3;

namespace Remade.Game.Planet3D;

/// <summary>
/// The 3D globe as a UI control: its own sub-viewport and world (space sky, sun, globe, clouds, atmosphere).
/// Orbit with the mouse, wheel to zoom; hovering shows the hexagon under the cursor (and a fading ring of its
/// neighbours); clicking selects a tile (pulsing outline + beacon). Overlays show live climate data.
/// </summary>
public partial class PlanetView : SubViewportContainer
{
    public enum CameraMode { Orbit, MenuLimb }

    public WorldPlanet Planet { get; private set; }
    public bool Ready3D { get; private set; }
    public int HoverTile { get; private set; } = -1;
    public int SelectedTile { get; private set; } = -1;
    public bool Interactive = true;
    public CameraMode Mode = CameraMode.Orbit;
    public event Action<int> TileSelected;
    public event Action<int> TileHovered;

    SubViewport _vp;
    Node3D _globeRoot;
    Camera3D _cam;
    DirectionalLight3D _sun;
    MeshInstance3D _globe, _clouds, _atmo, _hexHover, _hexSel, _beacon;
    ShaderMaterial _surfaceMat, _cloudMat, _atmoMat;
    PlanetBaker.OverlayKind _overlay = PlanetBaker.OverlayKind.None;
    int _overlayClimateVersion = -1;
    Image _dataImg;          // per tile: r = overlay value 0..1, g = 1 for water
    ImageTexture _dataTex;
    const int DataWidth = 512;
    MultiMeshInstance3D _icons;
    Task<PlanetBaker> _bakeTask;
    Action _onReady;

    float _yaw = 0.6f, _pitch = 0.35f, _dist = 3.1f;
    float _targetDist = 3.1f;
    bool _dragging;
    Vector2 _dragStart, _lastMouse;
    float _time;
    float _cloudOpacity = 0.85f;
    readonly int _bakeSize;

    public PlanetView(int bakeSize = 768)
    {
        _bakeSize = bakeSize;
        Stretch = true;
        MouseFilter = MouseFilterEnum.Stop;
        _vp = new SubViewport
        {
            OwnWorld3D = true,
            TransparentBg = false,
            Msaa3D = Viewport.Msaa.Msaa4X,
            HandleInputLocally = false,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        };
        AddChild(_vp);
        BuildScene();
    }

    void BuildScene()
    {
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = new Sky { SkyMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/space_sky.gdshader") }, RadianceSize = Sky.RadianceSizeEnum.Size64, ProcessMode = Sky.ProcessModeEnum.Incremental },
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.06f, 0.08f, 0.12f),
            AmbientLightEnergy = 1f,
            TonemapMode = Godot.Environment.ToneMapper.Agx,
            GlowEnabled = true,
            GlowIntensity = 0.9f,
            GlowBloom = 0.08f,
            GlowHdrThreshold = 0.9f,
        };
        _vp.AddChild(new WorldEnvironment { Environment = env });

        _sun = new DirectionalLight3D { LightEnergy = 2.4f, LightColor = new Color(1f, 0.96f, 0.9f), ShadowEnabled = false };
        _vp.AddChild(_sun);

        _cam = new Camera3D { Fov = 34, Near = 0.01f, Far = 100f, Current = true };
        _vp.AddChild(_cam);

        _globeRoot = new Node3D();
        _vp.AddChild(_globeRoot);

        _surfaceMat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/planet_surface.gdshader") };
        if (Args.Has("globe-debug")) _surfaceMat.SetShaderParameter("debug_mode", 1);
        _globe = new MeshInstance3D { Mesh = new SphereMesh { Radius = 1f, Height = 2f, RadialSegments = 256, Rings = 128 }, MaterialOverride = _surfaceMat, Visible = false };
        _globeRoot.AddChild(_globe);

        _cloudMat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/planet_clouds.gdshader") };
        _clouds = new MeshInstance3D { Mesh = new SphereMesh { Radius = 1.012f, Height = 2.024f, RadialSegments = 160, Rings = 80 }, MaterialOverride = _cloudMat, Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        _globeRoot.AddChild(_clouds);

        _atmoMat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/planet_atmosphere.gdshader") };
        _atmo = new MeshInstance3D { Mesh = new SphereMesh { Radius = 1.07f, Height = 2.14f, RadialSegments = 128, Rings = 64 }, MaterialOverride = _atmoMat, Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        _globeRoot.AddChild(_atmo);

        var hexMat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/planet_hex.gdshader") };
        _hexHover = new MeshInstance3D { MaterialOverride = hexMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        _globeRoot.AddChild(_hexHover);
        var selMat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/planet_hex.gdshader") };
        selMat.SetShaderParameter("pulse", 1f);
        _hexSel = new MeshInstance3D { MaterialOverride = selMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        _globeRoot.AddChild(_hexSel);

        var beaconMat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color(1f, 0.75f, 0.35f), EmissionEnabled = true,
            Emission = new Color(1f, 0.6f, 0.2f), EmissionEnergyMultiplier = 2.5f,
        };
        _beacon = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.0012f, BottomRadius = 0.004f, Height = 0.09f, RadialSegments = 8, Rings = 1 }, MaterialOverride = beaconMat, Visible = false };
        _globeRoot.AddChild(_beacon);
    }

    /// <summary>Starts baking the globe textures for a planet on a worker thread; the globe appears when done.</summary>
    public void SetPlanet(WorldPlanet planet, Action onReady = null)
    {
        Planet = planet;
        Ready3D = false;
        _onReady = onReady;
        HoverTile = SelectedTile = -1;
        _bakeTask = Task.Run(() =>
        {
            var b = new PlanetBaker(planet, _bakeSize, natural: Mode == CameraMode.MenuLimb);
            b.Bake();
            return b;
        });
    }

    public override void _Process(double delta)
    {
        _time += (float)delta;
        if (_bakeTask != null && _bakeTask.IsCompleted)
        {
            var t = _bakeTask;
            _bakeTask = null;
            if (t.IsFaulted) throw new InvalidOperationException("Planet texture bake failed", t.Exception);
            ApplyBake(t.Result);
        }
        UpdateCamera((float)delta);
        if (Ready3D)
        {
            _cloudMat.SetShaderParameter("time_offset", _time * 0.012f);
            // clouds fade away while a data overlay is shown, so the data is readable
            _cloudOpacity = Mathf.MoveToward(_cloudOpacity, _overlay == PlanetBaker.OverlayKind.None ? 0.85f : 0.08f, (float)delta * 2f);
            _cloudMat.SetShaderParameter("opacity", _cloudOpacity);
            if (Mode == CameraMode.MenuLimb) _globeRoot.RotateY((float)delta * 0.012f);
            if (_overlay is PlanetBaker.OverlayKind.Temperature or PlanetBaker.OverlayKind.Precipitation && Planet.Climate.Version != _overlayClimateVersion)
                UpdateOverlayData();
        }
    }

    void ApplyBake(PlanetBaker b)
    {
        using var _ = Log.Time("PlanetView.ApplyBake (texture upload)", 400);
        _surfaceMat.SetShaderParameter("albedo_map", MakeCube(b.Albedo, b.Size, Image.Format.Rgba8, mipmaps: true));
        _surfaceMat.SetShaderParameter("normal_map", MakeCube(b.Normal, b.Size, Image.Format.Rgba8, mipmaps: true));
        if (!b.Natural)
        {
            _surfaceMat.SetShaderParameter("tile_ids", MakeCube(b.Ids, b.Size, Image.Format.Rgba8, mipmaps: false));
            int rows = (Planet.TileCount + DataWidth - 1) / DataWidth;
            _dataImg = Image.CreateEmpty(DataWidth, rows, false, Image.Format.Rgf);
            _dataTex = ImageTexture.CreateFromImage(_dataImg);
            _surfaceMat.SetShaderParameter("tile_data", _dataTex);
            _surfaceMat.SetShaderParameter("data_width", DataWidth);
            _surfaceMat.SetShaderParameter("tile_angle", Planet.Grid.TileAngle);
            BuildIcons();
        }
        _surfaceMat.SetShaderParameter("map_style", Mode != CameraMode.MenuLimb);
        _globe.Visible = _atmo.Visible = true;
        _clouds.Visible = Mode == CameraMode.MenuLimb; // clouds only on the menu backdrop: they would hide the terrain
        Ready3D = true;
        Log.Info($"Globe ready ({b.Size}px faces, {Planet.TileCount} tiles)");
        _onReady?.Invoke();
    }

    static Cubemap MakeCube(byte[][] faces, int size, Image.Format fmt, bool mipmaps)
    {
        var imgs = new Godot.Collections.Array<Image>();
        for (int f = 0; f < 6; f++)
        {
            var img = Image.CreateFromData(size, size, false, fmt, faces[f]);
            if (mipmaps) img.GenerateMipmaps();
            imgs.Add(img);
        }
        var cube = new Cubemap();
        var err = cube.CreateFromImages(imgs);
        if (err != Error.Ok) throw new InvalidOperationException($"Cubemap creation failed: {err}");
        return cube;
    }

    // ------------------------------------------------------------------ overlays

    public PlanetBaker.OverlayKind Overlay => _overlay;

    public void SetOverlay(PlanetBaker.OverlayKind kind)
    {
        _overlay = kind;
        Log.Action($"planet overlay {kind}");
        if (kind != PlanetBaker.OverlayKind.None) UpdateOverlayData();
        _surfaceMat.SetShaderParameter("overlay_mode", kind switch { PlanetBaker.OverlayKind.Temperature => 1, PlanetBaker.OverlayKind.Elevation => 2, PlanetBaker.OverlayKind.Precipitation => 3, _ => 0 });
    }

    /// <summary>Writes every tile's overlay value (and whether it is water) into the per-tile data texture.</summary>
    void UpdateOverlayData()
    {
        if (_dataImg == null) throw new InvalidOperationException("overlays need the map-style globe (tile ids)");
        var p = Planet;
        _overlayClimateVersion = p.Climate.Version;
        int rows = _dataImg.GetHeight();
        var buf = new float[DataWidth * rows * 2];
        for (int t = 0; t < p.TileCount; t++)
        {
            buf[t * 2] = PlanetBaker.OverlayValue(p, t, _overlay);
            buf[t * 2 + 1] = p.Water[t] != Remade.World.WaterBody.None ? 1f : 0f;
        }
        var bytes = new byte[buf.Length * 4];
        Buffer.BlockCopy(buf, 0, bytes, 0, bytes.Length);
        _dataImg.SetData(DataWidth, rows, false, Image.Format.Rgf, bytes);
        _dataTex.Update(_dataImg);
    }

    // ------------------------------------------------------------------ relief icons

    /// <summary>
    /// Hills, mountains and impassable mountains (flat plains and plateaus have none) drawn as small painted icons lying on their hexagon (one MultiMesh
    /// quad per hilly land tile, kept well inside the hexagon).
    /// </summary>
    void BuildIcons()
    {
        var p = Planet;
        var tiles = new List<int>();
        for (int t = 0; t < p.TileCount; t++)
            if (p.Water[t] == Remade.World.WaterBody.None && p.Hills[t] != Remade.World.Hilliness.Flat) tiles.Add(t);
        var quad = new QuadMesh { Size = new Vector2(1, 1), Orientation = PlaneMesh.OrientationEnum.Z };
        var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true, Mesh = quad, InstanceCount = tiles.Count };
        float size = p.Grid.TileAngle * 0.78f;
        for (int k = 0; k < tiles.Count; k++)
        {
            var c = p.Grid.Centers[tiles[k]];
            var n = new Vector3(c.X, c.Y, c.Z).Normalized();
            var side = Mathf.Abs(n.Dot(Vector3.Up)) > 0.95f ? Vector3.Right : Vector3.Up;
            // the icon's up points to the planet's north so the drawings stand upright on screen
            var east = side.Cross(n).Normalized();
            var north = n.Cross(east).Normalized();
            var basis = new Basis(east * size, north * size, n * size);
            mm.SetInstanceTransform(k, new Transform3D(basis, n * 1.0018f));
            int kind = (int)p.Hills[tiles[k]] - 1; // 0 hills, 1 mountains, 2 impassable
            mm.SetInstanceCustomData(k, new Color(kind / 3f + 0.01f, 0, 0, 0));
        }
        var mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/planet_icons.gdshader") };
        mat.SetShaderParameter("atlas", ReliefIcons.Atlas);
        _icons?.QueueFree();
        _icons = new MultiMeshInstance3D { Multimesh = mm, MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Name = "ReliefIcons" };
        _globeRoot.AddChild(_icons);
        Log.Info($"Globe relief icons: {tiles.Count} tiles");
    }

    // ------------------------------------------------------------------ camera & lighting

    public void FocusTile(int tile, float dist = 2.4f)
    {
        var c = Planet.Grid.Centers[tile];
        _yaw = MathF.Atan2(c.X, c.Z);
        _pitch = MathF.Asin(Math.Clamp(c.Y, -1f, 1f));
        _targetDist = dist;
    }

    void UpdateCamera(float dt)
    {
        _dist += (_targetDist - _dist) * Math.Min(1f, dt * 6f);
        if (Mode == CameraMode.MenuLimb)
        {
            // low orbit over the limb: the planet fills the lower screen, the sun rises behind it
            // low orbit over mid-latitudes: the limb arcs across the upper screen, the sun is just behind it
            _cam.Position = new Vector3(0, 0.12f, 1.78f);
            _cam.LookAt(new Vector3(0.0f, 0.93f, 0.0f), Vector3.Up);
            // the sun is low on the right, slightly behind: a lit crescent, a long terminator and a glowing limb
            var sunDir = new Vector3(0.9f, 0.32f, -0.35f).Normalized();
            AimSun(sunDir);
            return;
        }
        if (!_dragging && !Interactive) _yaw += dt * 0.03f;
        var dir = new Vector3(MathF.Cos(_pitch) * MathF.Sin(_yaw), MathF.Sin(_pitch), MathF.Cos(_pitch) * MathF.Cos(_yaw));
        _cam.Position = dir * _dist;
        _cam.LookAt(Vector3.Zero, Vector3.Up);
        // light the face toward the camera, from the upper left, leaving a visible terminator
        var right = _cam.GlobalTransform.Basis.X;
        var up = _cam.GlobalTransform.Basis.Y;
        AimSun((dir * 0.8f - right * 0.75f + up * 0.35f).Normalized());
    }

    void AimSun(Vector3 sunDir)
    {
        _sun.LookAtFromPosition(Vector3.Zero, -sunDir, Mathf.Abs(sunDir.Y) > 0.99f ? Vector3.Right : Vector3.Up);
        _cloudMat.SetShaderParameter("sun_dir", sunDir);
        _atmoMat.SetShaderParameter("sun_dir", sunDir);
    }

    // ------------------------------------------------------------------ input

    public override void _GuiInput(InputEvent e)
    {
        if (!Interactive || !Ready3D) return;
        if (e is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex == MouseButton.WheelUp && mb.Pressed) _targetDist = Math.Max(1.35f, _targetDist * 0.9f);
            else if (mb.ButtonIndex == MouseButton.WheelDown && mb.Pressed) _targetDist = Math.Min(5f, _targetDist * 1.1f);
            else if (mb.ButtonIndex == MouseButton.Left)
            {
                if (mb.Pressed) { _dragging = true; _dragStart = mb.Position; _lastMouse = mb.Position; }
                else
                {
                    _dragging = false;
                    if (mb.Position.DistanceTo(_dragStart) < 5f)
                    {
                        int t = PickTile(mb.Position);
                        if (t >= 0) Select(t);
                    }
                }
            }
            AcceptEvent();
        }
        else if (e is InputEventMouseMotion mm)
        {
            if (_dragging)
            {
                Vector2 d = mm.Position - _lastMouse;
                _lastMouse = mm.Position;
                float k = 0.0045f * (_dist - 0.9f);
                _yaw -= d.X * k;
                _pitch = Math.Clamp(_pitch + d.Y * k, -1.45f, 1.45f);
            }
            int t = PickTile(mm.Position);
            if (t != HoverTile)
            {
                HoverTile = t;
                RebuildHover();
                TileHovered?.Invoke(t);
            }
        }
    }

    public override void _Notification(int what)
    {
        if (what == NotificationMouseExit && HoverTile >= 0)
        {
            HoverTile = -1;
            RebuildHover();
            TileHovered?.Invoke(-1);
        }
    }

    /// <summary>Tile under a point of this control, or -1 (ray against the unit sphere in globe space).</summary>
    public int PickTile(Vector2 local)
    {
        if (!Ready3D) return -1;
        Vector2 vpPos = local * new Vector2(_vp.Size.X / Size.X, _vp.Size.Y / Size.Y);
        Vector3 o = _cam.ProjectRayOrigin(vpPos), d = _cam.ProjectRayNormal(vpPos);
        var inv = _globeRoot.GlobalTransform.AffineInverse();
        o = inv * o; d = (inv.Basis * d).Normalized();
        float b = o.Dot(d), c = o.Dot(o) - 1f;
        float disc = b * b - c;
        if (disc < 0) return -1;
        float t = -b - MathF.Sqrt(disc);
        if (t < 0) return -1;
        Vector3 p = o + d * t;
        return Planet.Grid.Nearest(new V3(p.X, p.Y, p.Z), HoverTile >= 0 ? HoverTile : 0);
    }

    /// <summary>Hovers a tile programmatically (autopilot): same effect as moving the cursor over it.</summary>
    public void Hover(int tile)
    {
        HoverTile = tile;
        RebuildHover();
        TileHovered?.Invoke(tile);
    }

    public void Select(int tile)
    {
        SelectedTile = tile;
        Log.Action($"planet tile {tile} selected ({Planet.Biomes[tile]})");
        RebuildSelection();
        TileSelected?.Invoke(tile);
    }

    // ------------------------------------------------------------------ hexagon outlines

    void RebuildHover()
    {
        if (HoverTile < 0) { _hexHover.Mesh = null; return; }
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        var g = Planet.Grid;
        float w = 0.0011f * _dist;
        // the hovered hexagon, its neighbours and their neighbours, fading out
        var ring1 = new HashSet<int>();
        foreach (int n in g.Neighbors(HoverTile)) ring1.Add(n);
        var ring2 = new HashSet<int>();
        foreach (int n in ring1) foreach (int m in g.Neighbors(n)) if (m != HoverTile && !ring1.Contains(m)) ring2.Add(m);
        foreach (int t in ring2) AddOutline(st, t, w * 0.7f, new Color(0.55f, 0.95f, 0.88f, 0.18f));
        foreach (int t in ring1) AddOutline(st, t, w * 0.8f, new Color(0.55f, 0.95f, 0.88f, 0.4f));
        AddOutline(st, HoverTile, w * 1.4f, new Color(0.75f, 1f, 0.95f, 1f));
        AddFill(st, HoverTile, new Color(0.4f, 0.9f, 0.8f, 0.10f));
        _hexHover.Mesh = st.Commit();
    }

    void RebuildSelection()
    {
        if (SelectedTile < 0) { _hexSel.Mesh = null; _beacon.Visible = false; return; }
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        AddOutline(st, SelectedTile, 0.0028f, new Color(1f, 0.68f, 0.28f, 1f));
        AddFill(st, SelectedTile, new Color(1f, 0.6f, 0.25f, 0.22f));
        _hexSel.Mesh = st.Commit();
        var c = Planet.Grid.Centers[SelectedTile];
        var n = new Vector3(c.X, c.Y, c.Z);
        _beacon.Visible = true;
        _beacon.Position = n * 1.045f;
        // cylinder's axis is +Y: align it with the surface normal
        var up = n.Normalized();
        var side = Mathf.Abs(up.Dot(Vector3.Up)) > 0.95f ? Vector3.Right : Vector3.Up;
        var x = side.Cross(up).Normalized();
        var z = x.Cross(up).Normalized();
        _beacon.Basis = new Basis(x, up, z);
    }

    void AddOutline(SurfaceTool st, int tile, float width, Color col)
    {
        const float r = 1.0135f;
        var corners = Planet.Grid.TileCorners(tile);
        var center = Planet.Grid.Centers[tile];
        int n = corners.Length;
        for (int k = 0; k < n; k++)
        {
            V3 a = corners[k], b = corners[(k + 1) % n];
            // inset towards the centre so neighbouring outlines do not overlap
            V3 ai = V3.Normalize(V3.Lerp(a, center, 0.06f)), bi = V3.Normalize(V3.Lerp(b, center, 0.06f));
            V3 ao = V3.Normalize(ai + V3.Normalize(ai - center) * width), bo = V3.Normalize(bi + V3.Normalize(bi - center) * width);
            V3 ain = V3.Normalize(ai - V3.Normalize(ai - center) * width), bin = V3.Normalize(bi - V3.Normalize(bi - center) * width);
            Quad(st, ToG(ain) * r, ToG(ao) * r, ToG(bo) * r, ToG(bin) * r, col);
        }
    }

    void AddFill(SurfaceTool st, int tile, Color col)
    {
        const float r = 1.0132f;
        var corners = Planet.Grid.TileCorners(tile);
        var c = ToG(Planet.Grid.Centers[tile]) * r;
        int n = corners.Length;
        for (int k = 0; k < n; k++)
        {
            st.SetColor(col); st.AddVertex(c);
            st.SetColor(col); st.AddVertex(ToG(corners[k]) * r);
            st.SetColor(col); st.AddVertex(ToG(corners[(k + 1) % n]) * r);
        }
    }

    static void Quad(SurfaceTool st, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color col)
    {
        st.SetColor(col); st.AddVertex(a); st.SetColor(col); st.AddVertex(b); st.SetColor(col); st.AddVertex(c);
        st.SetColor(col); st.AddVertex(a); st.SetColor(col); st.AddVertex(c); st.SetColor(col); st.AddVertex(d);
    }

    static Vector3 ToG(V3 v) => new(v.X, v.Y, v.Z);
}
