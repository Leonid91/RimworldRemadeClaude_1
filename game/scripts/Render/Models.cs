using System;
using System.Collections.Generic;
using Godot;
using Remade.Core;
using Remade.Things;

namespace Remade.Game.Render;

/// <summary>
/// Procedural models. Everything faces −Z, stands on y = 0, 1 unit = 1 metre.
/// Oaks (first prototype's model): a short trunk, 4–6 limbs and round clusters of many painted leaf cards, each
/// variant with a detailed and a low LOD mesh, two surfaces (bark, leaves); per-tree leaf tint comes from the
/// MultiMesh custom data. Deer: a vertex-animated mesh (bone id in UV2.x). Items: bow, arrows, venison, berries…
/// </summary>
public static class Models
{
    public sealed class Tree
    {
        public ArrayMesh Detail, Low;
        public float Height, Crown;
    }

    static ShaderMaterial _barkMat, _leafMat;
    public static ShaderMaterial BarkMaterial => _barkMat ??= MakeBark();
    public static ShaderMaterial LeafMaterial => _leafMat ??= MakeLeaves();

    static ShaderMaterial MakeBark()
    {
        var m = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/bark.gdshader") };
        m.SetShaderParameter("noise_tex", ProcTextures.Noise);
        return m;
    }

    static ShaderMaterial MakeLeaves()
    {
        var m = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/foliage.gdshader") };
        m.SetShaderParameter("leaf_tex", ProcTextures.Leaves);
        m.SetShaderParameter("noise_tex", ProcTextures.Noise);
        return m;
    }

    /// <summary>Oak leaf tint (sRGB) for the MultiMesh custom data; alpha = sway phase × 0.5.</summary>
    public static readonly Color OakLeaf = new(0.30f, 0.56f, 0.12f);
    public static readonly Color BushLeaf = new(0.22f, 0.44f, 0.12f);

    // ------------------------------------------------------------------ oak

    public static Tree Oak(int variant)
    {
        ulong seed = 9000UL + (ulong)variant * 7919UL;
        var tree = new Tree();
        var r1 = new Rng(seed);
        tree.Detail = BuildOak(ref r1, detailed: true, out tree.Height, out tree.Crown);
        var r2 = new Rng(seed);
        tree.Low = BuildOak(ref r2, detailed: false, out _, out _);
        return tree;
    }

    /// <summary>
    /// A short trunk (1.9–2.6 m) leaning a little, roots, 4–6 limbs with a side branch each, and round leaf clusters at
    /// the limb ends plus a crown on top (about 4.5 m in all). The low LOD has the same skeleton with a quarter of the
    /// leaf cards, 1.6× larger.
    /// </summary>
    static ArrayMesh BuildOak(ref Rng r, bool detailed, out float height, out float crown)
    {
        var bark = new MeshBuilder { Color = Colors.White };
        var leaves = new MeshBuilder();
        int sides = detailed ? 9 : 5;
        float trunkH = r.Range(1.9f, 2.6f), r0 = r.Range(0.17f, 0.23f);
        var lean = new Vector3(r.Range(-0.25f, 0.25f), 0, r.Range(-0.25f, 0.25f));
        var p0 = new Vector3(0, -0.15f, 0);
        var p1 = new Vector3(lean.X * 0.3f, trunkH * 0.5f, lean.Z * 0.3f);
        var p2 = new Vector3(lean.X, trunkH, lean.Z);
        bark.Tube(p0, p1, r0 * 1.2f, r0 * 0.85f, sides, 0, (p1 - p0).Length());
        bark.Tube(p1, p2, r0 * 0.85f, r0 * 0.6f, sides, (p1 - p0).Length(), (p1 - p0).Length() + (p2 - p1).Length());
        int roots = r.Range(3, 6);
        for (int i = 0; i < roots; i++)
        {
            float a = (i + r.NextFloat() * 0.5f) / roots * Mathf.Tau;
            var dir = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
            if (detailed) bark.Tube(new Vector3(0, 0.35f, 0) + dir * r0 * 0.3f, dir * (r0 * 2.6f) + Vector3.Down * 0.08f, r0 * 0.55f, r0 * 0.12f, 5, 0, 0.6f);
        }

        var clusters = new List<(Vector3 c, float rad)>();
        int nb = r.Range(4, 7);
        float a0 = r.NextFloat() * Mathf.Tau;
        for (int i = 0; i < nb; i++)
        {
            float a = a0 + (i + r.Range(-0.25f, 0.25f)) / nb * Mathf.Tau;
            var start = p1.Lerp(p2, r.Range(0.55f, 1f));
            var dir = new Vector3(Mathf.Cos(a) * 0.8f, r.Range(0.5f, 0.9f), Mathf.Sin(a) * 0.8f).Normalized();
            float len = r.Range(1.0f, 1.6f);
            var end = start + dir * len;
            bark.Tube(start, end, r0 * 0.5f, r0 * 0.16f, detailed ? 6 : 4, 0, len);
            var mid = start.Lerp(end, 0.55f);
            var sdir = (dir + new Vector3(r.Range(-0.7f, 0.7f), 0.3f, r.Range(-0.7f, 0.7f))).Normalized();
            var send = mid + sdir * len * 0.55f;
            if (detailed) bark.Tube(mid, send, r0 * 0.25f, r0 * 0.08f, 5, 0, len * 0.55f);
            clusters.Add((end + Vector3.Up * 0.25f, r.Range(0.85f, 1.15f)));
            clusters.Add((send + Vector3.Up * 0.15f, r.Range(0.6f, 0.8f)));
        }
        clusters.Add((p2 + Vector3.Up * 0.85f, r.Range(1.2f, 1.45f)));

        var cc = Vector3.Zero;
        foreach (var c in clusters) cc += c.c;
        cc /= clusters.Count;
        cc.Y -= 0.2f;
        float cr = 0, bottom = 99, top = -99;
        foreach (var c in clusters)
        {
            cr = Mathf.Max(cr, (c.c - cc).Length() + c.rad);
            bottom = Mathf.Min(bottom, c.c.Y - c.rad); top = Mathf.Max(top, c.c.Y + c.rad);
        }
        foreach (var c in clusters)
        {
            int cards = (int)(c.rad * c.rad * 30 + 8);
            float size = r.Range(0.85f, 1.05f);
            if (!detailed) { cards = Math.Max(3, cards / 4); size *= 1.6f; }
            LeafCluster(ref r, leaves, c.c, c.rad, cc, cr, cards, size, bottom, top);
        }
        height = top;
        crown = cr;
        var mesh = new ArrayMesh();
        bark.CommitTo(mesh, BarkMaterial);
        leaves.CommitTo(mesh, LeafMaterial);
        return mesh;
    }

    static Vector3 RandDir(ref Rng r)
    {
        while (true)
        {
            var v = new Vector3(r.Range(-1f, 1f), r.Range(-1f, 1f), r.Range(-1f, 1f));
            float l = v.LengthSquared();
            if (l > 0.01f && l <= 1f) return v / Mathf.Sqrt(l);
        }
    }

    /// <summary>
    /// A cluster of leaf cards. Normals point outward from the canopy centre (soft volume look); COLOR.r = canopy
    /// ambient occlusion (darker deep inside and low down).
    /// </summary>
    static void LeafCluster(ref Rng r, MeshBuilder mb, Vector3 center, float radius, Vector3 canopyC, float canopyR,
        int cards, float cardSize, float bottomY, float topY)
    {
        for (int k = 0; k < cards; k++)
        {
            var d = RandDir(ref r);
            d.Y = d.Y * 0.75f + 0.12f;
            var pos = center + d * radius * Mathf.Pow(r.NextFloat(), 0.35f) * 0.9f;
            var outward = (pos - canopyC).Normalized();
            var cn = (RandDir(ref r) + outward * 0.9f + Vector3.Up * 0.4f).Normalized();
            var tmp = Mathf.Abs(cn.Y) < 0.9f ? Vector3.Up : Vector3.Right;
            var right = cn.Cross(tmp).Normalized();
            var up = right.Cross(cn).Normalized();
            float ang = r.NextFloat() * Mathf.Tau;
            var rr = right * Mathf.Cos(ang) + up * Mathf.Sin(ang);
            var uu = cn.Cross(rr);
            float s = cardSize * r.Range(0.8f, 1.15f) * 0.5f;
            Vector3[] corners = { pos - rr * s - uu * s, pos + rr * s - uu * s, pos + rr * s + uu * s, pos - rr * s + uu * s };
            Vector2[] uvs = { new(0, 1), new(1, 1), new(1, 0), new(0, 0) };
            int start = mb.Count;
            for (int i = 0; i < 4; i++)
            {
                var p = corners[i];
                // each clump is lit as its own ball (round, distinct clumps), a little of the whole crown's roundness
                var n = ((p - center).Normalized() * 0.65f + (p - canopyC).Normalized() * 0.35f + Vector3.Up * 0.3f).Normalized();
                float depth = Mathf.Clamp((p - canopyC).Length() / canopyR, 0f, 1.2f);
                float inClump = Mathf.Clamp((p - center).Length() / Mathf.Max(radius, 0.05f), 0f, 1.2f);
                float hy = Mathf.Clamp((p.Y - bottomY) / Mathf.Max(topY - bottomY, 0.1f), 0f, 1f);
                float ao = Mathf.Lerp(0.5f, 1.05f, depth * 0.45f + hy * 0.3f + inClump * 0.25f);
                mb.Add(p, n, uvs[i], new Color(ao, 0, 0));
            }
            mb.Tri(start, start + 2, start + 1);
            mb.Tri(start, start + 3, start + 2);
        }
    }

    // ------------------------------------------------------------------ berry bush

    /// <summary>A round bush of 3–5 leaf clusters (first prototype's model), about 0.9 m tall.</summary>
    public static ArrayMesh Bush(int variant)
    {
        var r = new Rng(5000UL + (ulong)variant);
        var leaves = new MeshBuilder();
        const float scale = 1.15f;
        var cc = new Vector3(0, 0.3f * scale, 0);
        int n = r.Range(3, 6);
        var clusters = new List<(Vector3, float)>();
        for (int i = 0; i < n; i++)
        {
            float a = r.NextFloat() * Mathf.Tau;
            var c = new Vector3(Mathf.Cos(a) * 0.25f, r.Range(0.3f, 0.5f), Mathf.Sin(a) * 0.25f) * scale;
            clusters.Add((c, r.Range(0.32f, 0.48f) * scale));
        }
        foreach (var (c, rad) in clusters)
            LeafCluster(ref r, leaves, c, rad, cc, 0.75f * scale, (int)(rad * rad * 110 + 6), 0.55f * scale, 0f, 0.9f * scale);
        var m = new ArrayMesh();
        leaves.CommitTo(m, LeafMaterial);
        return m;
    }

    /// <summary>12 berries arranged on a bush surface; UV2.x = berry index (the shader hides the picked ones).</summary>
    public static ArrayMesh Berries()
    {
        var rng = new Rng(77);
        var b = new MeshBuilder();
        for (int k = 0; k < 12; k++)
        {
            var d = new Vector3(rng.Range(-1f, 1f), rng.Range(0f, 1f), rng.Range(-1f, 1f)).Normalized();
            b.Uv2 = new Vector2(k, 0);
            b.Color = new Color(0.32f + rng.Range(-0.05f, 0.05f), 0.08f, 0.36f);
            b.Ellipsoid(new Vector3(0, 0.42f, 0) + new Vector3(d.X * 0.52f, d.Y * 0.36f, d.Z * 0.52f), new Vector3(0.045f, 0.045f, 0.045f), 6, 4);
        }
        var shader = new Shader
        {
            Code = @"shader_type spatial;
render_mode diffuse_burley;
varying float hide;
void vertex() {
	// INSTANCE_CUSTOM.r = berries left / 12
	hide = step(INSTANCE_CUSTOM.r * 12.0, UV2.x + 0.5);
	VERTEX *= 1.0 - hide;
}
void fragment() {
	ALBEDO = pow(COLOR.rgb, vec3(2.2));
	ROUGHNESS = 0.25;
	SPECULAR = 0.7;
}",
        };
        return b.Commit(new ShaderMaterial { Shader = shader });
    }

    // ------------------------------------------------------------------ deer

    /// <summary>Deer mesh (doe or stag). UV2.x = bone: 0 body, 1 neck+head, 2 front-left, 3 front-right, 4 hind-left, 5 hind-right, 6 tail.</summary>
    public static ArrayMesh Deer(bool stag, Material mat)
    {
        var b = new MeshBuilder();
        Color hide = new(0.46f, 0.31f, 0.19f), belly = new(0.80f, 0.72f, 0.60f), dark = new(0.25f, 0.17f, 0.11f), white = new(0.92f, 0.90f, 0.86f);

        b.Uv2 = new Vector2(0, 0);
        int s0 = b.Count;
        b.Ellipsoid(new Vector3(0, 0.98f, 0), new Vector3(0.21f, 0.26f, 0.62f), 14, 9);
        for (int i = s0; i < b.Count; i++)
        {
            float t = (b.V[i].Y - 0.98f) / 0.26f;
            b.C[i] = t < -0.45f ? belly : hide.Lerp(hide * 0.8f, Mathf.Clamp(t, 0f, 1f));
        }
        // shoulder and haunch masses
        b.Color = hide;
        b.Ellipsoid(new Vector3(0, 1.02f, -0.42f), new Vector3(0.2f, 0.24f, 0.22f), 10, 7);
        b.Ellipsoid(new Vector3(0, 1.0f, 0.44f), new Vector3(0.21f, 0.25f, 0.24f), 10, 7);

        // neck and head
        b.Uv2 = new Vector2(1, 0);
        b.Tube(new Vector3(0, 1.05f, -0.52f), new Vector3(0, 1.45f, -0.74f), 0.12f, 0.075f, 8, 0, 1, hide, hide);
        b.Color = hide;
        b.Ellipsoid(new Vector3(0, 1.52f, -0.83f), new Vector3(0.085f, 0.1f, 0.16f), 10, 7);
        b.Color = hide * 0.85f;
        b.Ellipsoid(new Vector3(0, 1.47f, -0.98f), new Vector3(0.055f, 0.06f, 0.09f), 8, 5);
        b.Color = dark;
        b.Ellipsoid(new Vector3(0, 1.48f, -1.07f), new Vector3(0.03f, 0.025f, 0.02f), 6, 4); // nose
        b.Color = hide;
        b.Ellipsoid(new Vector3(-0.09f, 1.64f, -0.76f), new Vector3(0.025f, 0.08f, 0.045f), 6, 4);
        b.Ellipsoid(new Vector3(0.09f, 1.64f, -0.76f), new Vector3(0.025f, 0.08f, 0.045f), 6, 4);
        b.Color = dark;
        b.Ellipsoid(new Vector3(-0.07f, 1.56f, -0.9f), new Vector3(0.018f, 0.018f, 0.018f), 5, 3);
        b.Ellipsoid(new Vector3(0.07f, 1.56f, -0.9f), new Vector3(0.018f, 0.018f, 0.018f), 5, 3);
        if (stag)
        {
            var antler = new Color(0.62f, 0.55f, 0.44f);
            foreach (float side in new[] { -1f, 1f })
            {
                Vector3 a0 = new(side * 0.05f, 1.62f, -0.82f);
                Vector3 a1 = a0 + new Vector3(side * 0.12f, 0.22f, 0.05f);
                Vector3 a2 = a1 + new Vector3(side * 0.08f, 0.2f, -0.08f);
                Vector3 a3 = a2 + new Vector3(side * 0.02f, 0.16f, -0.12f);
                b.Tube(a0, a1, 0.025f, 0.02f, 5, 0, 1, antler, antler);
                b.Tube(a1, a2, 0.02f, 0.015f, 5, 0, 1, antler, antler);
                b.Tube(a2, a3, 0.015f, 0.008f, 5, 0, 1, antler, antler);
                b.Tube(a1, a1 + new Vector3(side * 0.02f, 0.12f, -0.12f), 0.014f, 0.006f, 4, 0, 1, antler, antler);
                b.Tube(a2, a2 + new Vector3(side * 0.1f, 0.1f, 0.04f), 0.012f, 0.005f, 4, 0, 1, antler, antler);
            }
        }

        // legs (front: straight; hind: hock bends backward)
        float[] xs = { -0.11f, 0.11f, -0.12f, 0.12f };
        float[] zs = { -0.42f, -0.42f, 0.42f, 0.42f };
        for (int leg = 0; leg < 4; leg++)
        {
            b.Uv2 = new Vector2(2 + leg, 0);
            bool hind = leg >= 2;
            Vector3 hip = new(xs[leg], hind ? 0.92f : 0.86f, zs[leg]);
            Vector3 knee = hip + (hind ? new Vector3(0, -0.36f, 0.12f) : new Vector3(0, -0.42f, 0.02f));
            Vector3 hoof = new(xs[leg], 0.04f, zs[leg] + (hind ? 0.06f : 0f));
            b.Tube(hip, knee, hind ? 0.085f : 0.065f, 0.045f, 6, 0, 1, hide, hide * 0.85f);
            b.Tube(knee, hoof, 0.04f, 0.03f, 6, 0, 1, hide * 0.85f, dark);
            b.Color = dark;
            b.Ellipsoid(hoof - new Vector3(0, 0.02f, 0.01f), new Vector3(0.035f, 0.03f, 0.045f), 6, 3);
        }
        // tail with a white underside
        b.Uv2 = new Vector2(6, 0);
        b.Color = hide;
        b.Ellipsoid(new Vector3(0, 1.08f, 0.64f), new Vector3(0.05f, 0.09f, 0.05f), 6, 4);
        b.Color = white;
        b.Ellipsoid(new Vector3(0, 1.03f, 0.66f), new Vector3(0.04f, 0.07f, 0.035f), 6, 4);
        return b.Commit(mat);
    }

    // ------------------------------------------------------------------ items

    static readonly Dictionary<string, ArrayMesh> _items = new();

    public static ArrayMesh Item(ThingDef def)
    {
        if (_items.TryGetValue(def.Id, out var m)) return m;
        var b = new MeshBuilder();
        var mat = new StandardMaterial3D { VertexColorUseAsAlbedo = true, VertexColorIsSrgb = true, Roughness = 0.75f };
        switch (def.Id)
        {
            case "bow":
                BowShape(b, lying: true);
                break;
            case "arrow":
                for (int k = 0; k < 5; k++) ArrowShape(b, new Vector3((k - 2) * 0.035f, 0.025f + (k % 2) * 0.02f, 0), Vector3.Forward, rot: k * 0.05f);
                break;
            case "venison":
            {
                var rng = new Rng(31);
                for (int k = 0; k < 4; k++)
                {
                    b.Color = new Color(0.55f, 0.12f, 0.10f).Lerp(new Color(0.7f, 0.25f, 0.22f), rng.NextFloat());
                    b.Ellipsoid(new Vector3(rng.Range(-0.12f, 0.12f), 0.06f + k * 0.015f, rng.Range(-0.1f, 0.1f)), new Vector3(rng.Range(0.07f, 0.11f), 0.05f, rng.Range(0.06f, 0.1f)), 8, 5);
                    b.Color = new Color(0.92f, 0.86f, 0.80f);
                    b.Ellipsoid(new Vector3(rng.Range(-0.1f, 0.1f), 0.09f + k * 0.015f, rng.Range(-0.08f, 0.08f)), new Vector3(0.03f, 0.015f, 0.05f), 6, 3);
                }
                mat.Roughness = 0.35f;
                break;
            }
            case "granite_chunk":
            {
                var rng = new Rng(33);
                b.Color = new Color(0.55f, 0.52f, 0.5f);
                b.Ellipsoid(new Vector3(0, 0.14f, 0), new Vector3(0.24f, 0.15f, 0.2f), 7, 4);
                // jag the surface so it reads as broken stone
                for (int i = 0; i < b.V.Count; i++) b.V[i] = b.V[i] * new Vector3(1 + rng.Range(-0.12f, 0.12f), 1 + rng.Range(-0.15f, 0.1f), 1 + rng.Range(-0.12f, 0.12f));
                for (int i = 0; i < b.C.Count; i++) b.C[i] = b.C[i] * rng.Range(0.8f, 1.1f);
                mat.Roughness = 0.9f;
                break;
            }
            case "berries":
            {
                b.Color = new Color(0.22f, 0.35f, 0.12f);
                b.Ellipsoid(new Vector3(0, 0.01f, 0), new Vector3(0.14f, 0.01f, 0.1f), 8, 3);
                var rng = new Rng(32);
                for (int k = 0; k < 14; k++)
                {
                    b.Color = new Color(0.30f, 0.07f, 0.34f);
                    b.Ellipsoid(new Vector3(rng.Range(-0.08f, 0.08f), 0.03f + rng.Range(0f, 0.03f), rng.Range(-0.06f, 0.06f)), new Vector3(0.025f, 0.025f, 0.025f), 5, 3);
                }
                mat.Roughness = 0.3f;
                break;
            }
            default:
            {
                // folded cloth / leather goods
                var c = Remade.Game.UI.UiKit.Rgb(def.Color);
                b.Color = c;
                b.Box(new Vector3(0, 0.05f, 0), new Vector3(0.18f, 0.05f, 0.14f));
                b.Color = c * 0.85f;
                b.Box(new Vector3(0, 0.11f, 0.02f), new Vector3(0.15f, 0.015f, 0.1f));
                break;
            }
        }
        m = b.Commit(mat);
        _items[def.Id] = m;
        return m;
    }

    /// <summary>A recurve bow. Lying: flat on the ground along X; otherwise upright along Y (held).</summary>
    public static void BowShape(MeshBuilder b, bool lying)
    {
        var wood = new Color(0.45f, 0.28f, 0.14f);
        var grip = new Color(0.20f, 0.13f, 0.08f);
        const int n = 10;
        Vector3 Pt(float t)
        {
            // t in [-1, 1] along the bow; recurve tips flick forward
            float y = t * 0.72f;
            float z = -0.12f * (1 - t * t) + 0.05f * MathF.Pow(MathF.Abs(t), 6f);
            return lying ? new Vector3(y, 0.03f, z) : new Vector3(0, y, z);
        }
        for (int i = 0; i < n; i++)
        {
            float t0 = -1 + 2f * i / n, t1 = -1 + 2f * (i + 1) / n;
            float r0 = 0.018f * (1.1f - MathF.Abs(t0) * 0.5f), r1 = 0.018f * (1.1f - MathF.Abs(t1) * 0.5f);
            var c = MathF.Abs(t0) < 0.15f ? grip : wood;
            b.Tube(Pt(t0), Pt(t1), r0, r1, 6, 0, 1, c, c);
        }
        var str = new Color(0.85f, 0.82f, 0.74f);
        b.Tube(Pt(-0.98f), Pt(0.98f) , 0.003f, 0.003f, 3, 0, 1, str, str);
    }

    public static void ArrowShape(MeshBuilder b, Vector3 at, Vector3 dir, float rot = 0)
    {
        dir = dir.Normalized();
        var shaft = new Color(0.72f, 0.60f, 0.42f);
        Vector3 tail = at - dir * 0.38f, tip = at + dir * 0.36f;
        b.Tube(tail, tip, 0.006f, 0.006f, 4, 0, 1, shaft, shaft);
        var iron = new Color(0.30f, 0.30f, 0.32f);
        b.Tube(tip, tip + dir * 0.06f, 0.014f, 0.001f, 4, 0, 1, iron, iron);
        var fl = new Color(0.85f, 0.25f, 0.18f);
        Vector3 side = dir.Cross(Vector3.Up).LengthSquared() < 0.01f ? Vector3.Right : dir.Cross(Vector3.Up).Normalized();
        for (int k = 0; k < 3; k++)
        {
            float a = rot + k * Mathf.Tau / 3f;
            Vector3 o = side.Rotated(dir, a) * 0.025f;
            b.Color = fl;
            b.QuadFlat(tail, tail + dir * 0.1f, tail + dir * 0.08f + o, tail + o * 0.6f);
            b.QuadFlat(tail + o * 0.6f, tail + dir * 0.08f + o, tail + dir * 0.1f, tail);
        }
    }

    static ArrayMesh _flyingArrow;
    public static ArrayMesh FlyingArrow
    {
        get
        {
            if (_flyingArrow != null) return _flyingArrow;
            var b = new MeshBuilder();
            ArrowShape(b, Vector3.Zero, Vector3.Forward);
            _flyingArrow = b.Commit(new StandardMaterial3D { VertexColorUseAsAlbedo = true, VertexColorIsSrgb = true, Roughness = 0.6f });
            return _flyingArrow;
        }
    }

    static ArrayMesh _heldBow;
    public static ArrayMesh HeldBow
    {
        get
        {
            if (_heldBow != null) return _heldBow;
            var b = new MeshBuilder();
            BowShape(b, lying: false);
            _heldBow = b.Commit(new StandardMaterial3D { VertexColorUseAsAlbedo = true, VertexColorIsSrgb = true, Roughness = 0.6f });
            return _heldBow;
        }
    }
}
