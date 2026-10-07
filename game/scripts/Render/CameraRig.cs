using System;
using Godot;

namespace Remade.Game.Render;

/// <summary>
/// RimWorld-style overhead camera: a focus point on the ground, a distance (zoom) and a yaw. The pitch steepens as
/// you zoom out. Pans with the move keys / arrows / middle mouse / screen edges; follows a colonist under direct
/// control. Everything is smoothed.
/// </summary>
public partial class CameraRig : Node3D
{
    public readonly Camera3D Camera;
    public Vector3 Focus;
    public float Distance = 38f, Yaw;
    public float MinDistance = 7f, MaxDistance = 170f;
    public Vector2 MapSize;
    public Func<float, float, float> HeightAt;
    Vector3 _focusTarget;
    float _distTarget = 38f, _yawTarget;
    public bool Follow;
    public Vector3 FollowTarget;

    public CameraRig()
    {
        Camera = new Camera3D { Fov = 40, Near = 0.3f, Far = 900f, Current = true };
        AddChild(Camera);
    }

    public void JumpTo(Vector3 p)
    {
        _focusTarget = Focus = p;
    }

    public void Zoom(float factor) => _distTarget = Mathf.Clamp(_distTarget * factor, MinDistance, MaxDistance);
    public void Rotate(float radians) => _yawTarget += radians;

    /// <summary>Pans in screen-aligned directions (x right, y down) in cells.</summary>
    public void Pan(Vector2 screenDelta)
    {
        var right = new Vector3(Mathf.Cos(Yaw), 0, -Mathf.Sin(Yaw));
        var down = new Vector3(Mathf.Sin(Yaw), 0, Mathf.Cos(Yaw));
        _focusTarget += right * screenDelta.X + down * screenDelta.Y;
        Follow = false;
    }

    public float PanSpeed => _distTarget * 0.95f + 6f;

    /// <summary>Converts a screen-aligned direction (x right, y down) into map space (x east, y south).</summary>
    public Vector2 ScreenToMapDir(Vector2 v)
    {
        var right = new Vector2(Mathf.Cos(Yaw), -Mathf.Sin(Yaw));
        var down = new Vector2(Mathf.Sin(Yaw), Mathf.Cos(Yaw));
        return right * v.X + down * v.Y;
    }

    public void UpdateRig(float dt)
    {
        if (Follow) _focusTarget = FollowTarget;
        _focusTarget.X = Mathf.Clamp(_focusTarget.X, 0, MapSize.X);
        _focusTarget.Z = Mathf.Clamp(_focusTarget.Z, 0, MapSize.Y);
        if (HeightAt != null) _focusTarget.Y = HeightAt(_focusTarget.X, _focusTarget.Z);
        float k = 1f - Mathf.Exp(-dt * 10f);
        Focus = Focus.Lerp(_focusTarget, Follow ? 1f - Mathf.Exp(-dt * 7f) : k);
        Distance = Mathf.Lerp(Distance, _distTarget, k);
        Yaw = Mathf.Lerp(Yaw, _yawTarget, k);
        // closer → more oblique view, further → more top-down
        float t = Mathf.Clamp((Distance - MinDistance) / (MaxDistance - MinDistance), 0f, 1f);
        float pitch = Mathf.DegToRad(Mathf.Lerp(42f, 68f, Mathf.Sqrt(t)));
        var back = new Vector3(Mathf.Sin(Yaw), 0, Mathf.Cos(Yaw));
        var pos = Focus + back * Mathf.Cos(pitch) * Distance + Vector3.Up * Mathf.Sin(pitch) * Distance;
        Camera.GlobalPosition = pos;
        Camera.LookAt(Focus, Vector3.Up);
    }

    /// <summary>Ground point under a screen position (ray against the horizontal plane at the focus height).</summary>
    public Vector3? GroundPoint(Vector2 screen, float planeY)
    {
        var o = Camera.ProjectRayOrigin(screen);
        var d = Camera.ProjectRayNormal(screen);
        if (Mathf.Abs(d.Y) < 1e-4f) return null;
        float t = (planeY - o.Y) / d.Y;
        if (t < 0) return null;
        return o + d * t;
    }

    /// <summary>Ground point refined against a height function (a few fixed-point iterations).</summary>
    public Vector3? GroundPoint(Vector2 screen)
    {
        var o = Camera.ProjectRayOrigin(screen);
        var d = Camera.ProjectRayNormal(screen);
        if (d.Y > -1e-4f) return null;
        float y = Focus.Y;
        Vector3 p = default;
        for (int i = 0; i < 6; i++)
        {
            float t = (y - o.Y) / d.Y;
            p = o + d * t;
            if (HeightAt == null) break;
            y = HeightAt(p.X, p.Z);
        }
        return p;
    }

    /// <summary>Approximate ground rectangle visible on screen (cells), for streaming.</summary>
    public Rect2 VisibleRect(Vector2 viewport)
    {
        float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
        foreach (var c in new[] { Vector2.Zero, new Vector2(viewport.X, 0), viewport, new Vector2(0, viewport.Y), viewport * 0.5f })
        {
            var p = GroundPoint(c, Focus.Y) ?? (Focus + (Camera.ProjectRayNormal(c) with { Y = 0 }).Normalized() * Distance * 3f);
            minX = Mathf.Min(minX, p.X); maxX = Mathf.Max(maxX, p.X);
            minZ = Mathf.Min(minZ, p.Z); maxZ = Mathf.Max(maxZ, p.Z);
        }
        return new Rect2(minX, minZ, maxX - minX, maxZ - minZ);
    }
}
