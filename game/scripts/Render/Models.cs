using System;
using System.Collections.Generic;
using Godot;
using Remade.Core;
using Remade.Things;

namespace Remade.Game.Render;

/// <summary>
/// Procedural models. Everything faces −Z, stands on y = 0, 1 unit = 1 metre.
/// Oaks: 12 variants of 4 archetypes (broad, tall, young, gnarled), each with a detailed and a low LOD mesh, two
/// surfaces (bark, leaves). Deer: a vertex-animated mesh (bone id in UV2.x). Items: bow, arrows, venison, berries…
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
        m.SetShaderParameter("bark", ProcTextures.Bark);
        return m;
    }

    static ShaderMaterial MakeLeaves()
    {
        var m = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/foliage.gdshader") };
        m.SetShaderParameter("leaves", ProcTextures.Leaves);
        return m;
    }

    // ------------------------------------------------------------------ oak

    public static Tree Oak(int variant)
    {
        var rng = new Rng(9000UL + (ulong)variant * 7919UL);
        int arche = variant % 4;
        float H = arche switch { 0 => rng.Range(7f, 9f), 1 => rng.Range(9f, 11f), 2 => rng.Range(4.5f, 6f), _ => rng.Range(6f, 8f) };
        float crown = arche switch { 0 => H * 0.5f, 1 => H * 0.36f, 2 => H * 0.32f, _ => H * 0.62f };
        float trunkR = arche switch { 0 => 0.32f, 1 => 0.3f, 2 => 0.14f, _ => 0.42f } * rng.Range(0.85f, 1.15f);
        float trunkTop = H * (arche == 3 ? 0.38f : arche == 1 ? 0.62f : 0.5f);
        var tree = new Tree { Height = H, Crown = crown };
        var crownCenter = new Vector3(0, trunkTop + (H - trunkTop) * 0.45f, 0);
        tree.Detail = BuildOak(ref rng, H, crown, trunkR, trunkTop, crownCenter, arche, detailed: true);
        var rng2 = new Rng(9000UL + (ulong)variant * 7919UL);
        tree.Low = BuildOak(ref rng2, H, crown, trunkR, trunkTop, crownCenter, arche, detailed: false);
        return tree;
    }

    static ArrayMesh BuildOak(ref Rng rng, float H, float crown, float trunkR, float trunkTop, Vector3 crownCenter, int arche, bool detailed)
    {
        var bark = new MeshBuilder();
        var leaves = new MeshBuilder();
        int sides = detailed ? 8 : 5;
        // trunk: a few bent segments with root flare
        var lean = new Vector3(rng.Range(-0.25f, 0.25f), 0, rng.Range(-0.25f, 0.25f)) * (arche == 3 ? 1.6f : 1f);
        Vector3 prev = Vector3.Zero;
        float prevR = trunkR * 1.45f;
        int segs = detailed ? 5 : 3;
        var trunkPts = new List<Vector3> { prev };
        for (int s = 1; s <= segs; s++)
        {
            float t = s / (float)segs;
            Vector3 p = new Vector3(0, trunkTop * t, 0) + lean * t * t + new Vector3(rng.Range(-0.08f, 0.08f), 0, rng.Range(-0.08f, 0.08f));
            float r = trunkR * (1f - 0.35f * t) * (s == 1 ? 1.1f : 1f);
            bark.Tube(prev, p, prevR, r, sides, (s - 1) * 0.5f, s * 0.5f, Flex(prev.Y, H), Flex(p.Y, H));
            prev = p; prevR = r;
            trunkPts.Add(p);
        }
        // primary limbs radiate from the upper trunk; oaks branch wide and early
        int limbs = (arche == 2 ? rng.Range(3, 5) : rng.Range(4, 7)) - (detailed ? 0 : 1);
        float baseAngle = rng.Range(0f, Mathf.Tau);
        for (int b = 0; b < limbs; b++)
        {
            float t = rng.Range(0.55f, 1f);
            Vector3 start = trunkPts[Math.Clamp((int)(t * segs), 1, segs)];
            float az = baseAngle + b / (float)limbs * Mathf.Tau + rng.Range(-0.4f, 0.4f);
            float tilt = (arche == 1 ? rng.Range(0.35f, 0.7f) : rng.Range(0.6f, 1.15f)); // radians from vertical
            var dir = new Vector3(Mathf.Sin(tilt) * Mathf.Cos(az), Mathf.Cos(tilt), Mathf.Sin(tilt) * Mathf.Sin(az));
            float len = crown * rng.Range(0.75f, 1.05f) / Mathf.Max(0.5f, Mathf.Sin(tilt) + 0.3f);
            Branch(ref rng, bark, leaves, start, dir, len, trunkR * rng.Range(0.45f, 0.6f), 0, H, crownCenter, crown, detailed, sides);
        }
        // the leader continues into the crown
        Branch(ref rng, bark, leaves, prev, new Vector3(lean.X * 0.3f, 1, lean.Z * 0.3f).Normalized(), (H - trunkTop) * 0.8f, prevR * 0.8f, 0, H, crownCenter, crown, detailed, sides);
        // fill clusters in the crown volume for density
        int fill = detailed ? (int)(crown * crown * 0.9f) + 6 : (int)(crown * crown * 0.35f) + 3;
        for (int k = 0; k < fill; k++)
        {
            var d = new Vector3(rng.Range(-1f, 1f), rng.Range(-0.35f, 0.9f), rng.Range(-1f, 1f));
            if (d.LengthSquared() > 1f) { k--; continue; }
            var p = crownCenter + new Vector3(d.X * crown, d.Y * (H - trunkTop) * 0.55f, d.Z * crown);
            LeafCluster(ref rng, leaves, p, rng.Range(1.3f, 2.0f) * (detailed ? 1f : 1.45f), crownCenter, H);
        }
        var mesh = new ArrayMesh();
        bark.CommitTo(mesh, BarkMaterial);
        leaves.CommitTo(mesh, LeafMaterial);
        return mesh;
    }

    static Color Flex(float y, float H) => new(Mathf.Clamp(y / H, 0f, 1f), 0.5f, 0.5f);

    static void Branch(ref Rng rng, MeshBuilder bark, MeshBuilder leaves, Vector3 start, Vector3 dir, float len, float r, int depth,
        float H, Vector3 crownCenter, float crown, bool detailed, int sides)
    {
        int segs = detailed ? 3 : 2;
        Vector3 p = start;
        float segLen = len / segs;
        for (int s = 0; s < segs; s++)
        {
            // oak limbs wander: bend each segment randomly, droop slightly with distance
            dir = (dir + new Vector3(rng.Range(-0.35f, 0.35f), rng.Range(-0.15f, 0.25f), rng.Range(-0.35f, 0.35f))).Normalized();
            Vector3 q = p + dir * segLen;
            float r1 = r * (1f - 0.55f * (s + 1) / segs);
            bark.Tube(p, q, r * (1f - 0.55f * s / segs), r1, Math.Max(4, sides - 2 - depth * 2), 0, segLen * 0.5f, Flex(p.Y, H), Flex(q.Y, H));
            // twigs with leaves along the outer part
            if (depth >= 1 || s == segs - 1)
                LeafCluster(ref rng, leaves, q + new Vector3(rng.Range(-0.3f, 0.3f), rng.Range(0f, 0.4f), rng.Range(-0.3f, 0.3f)), rng.Range(1.3f, 2.1f) * (detailed ? 1f : 1.4f), crownCenter, H);
            if (depth < (detailed ? 2 : 1) && s >= 1 && rng.Chance(0.75f))
            {
                var sideDir = (dir + new Vector3(rng.Range(-1f, 1f), rng.Range(0.1f, 0.6f), rng.Range(-1f, 1f))).Normalized();
                Branch(ref rng, bark, leaves, q, sideDir, len * rng.Range(0.4f, 0.6f), r1 * 0.8f, depth + 1, H, crownCenter, crown, detailed, sides);
            }
            p = q;
        }
    }

    /// <summary>Three crossed leaf cards with normals spherized from the crown centre (soft, volumetric lighting).</summary>
    static void LeafCluster(ref Rng rng, MeshBuilder lb, Vector3 c, float size, Vector3 crownCenter, float H)
    {
        float yaw0 = rng.Range(0f, Mathf.Pi);
        for (int k = 0; k < 3; k++)
        {
            float yaw = yaw0 + k * Mathf.Pi / 3f;
            float pitch = k == 2 ? Mathf.Pi * 0.5f * rng.Range(0.7f, 1f) : rng.Range(-0.35f, 0.35f);
            var right = new Vector3(Mathf.Cos(yaw), 0, Mathf.Sin(yaw));
            var up = new Vector3(0, Mathf.Cos(pitch), 0) + new Vector3(-Mathf.Sin(yaw), 0, Mathf.Cos(yaw)) * Mathf.Sin(pitch);
            float h = size * 0.5f;
            Vector3[] corners = { c - right * h - up * h, c + right * h - up * h, c + right * h + up * h, c - right * h + up * h };
            Vector2[] uvs = { new(0, 1), new(1, 1), new(1, 0), new(0, 0) };
            int start = lb.Count;
            for (int i = 0; i < 4; i++)
            {
                var n = (corners[i] - crownCenter).Normalized() * 0.75f + new Vector3(0, 0.35f, 0);
                lb.Add(corners[i], n.Normalized(), uvs[i], new Color(Mathf.Clamp(corners[i].Y / H, 0.3f, 1f), rng.NextFloat(), 0.5f));
            }
            lb.Tri(start, start + 2, start + 1);
            lb.Tri(start, start + 3, start + 2);
        }
    }

    // ------------------------------------------------------------------ berry bush

    static StandardMaterial3D _berryMat;

    public static ArrayMesh Bush(int variant)
    {
        var rng = new Rng(5000UL + (ulong)variant);
        var leaves = new MeshBuilder();
        var stems = new MeshBuilder { Color = new Color(0.2f, 0.5f, 0.5f) };
        var center = new Vector3(0, 0.5f, 0);
        for (int k = 0; k < 4; k++)
        {
            var tip = new Vector3(rng.Range(-0.4f, 0.4f), rng.Range(0.5f, 0.9f), rng.Range(-0.4f, 0.4f));
            stems.Tube(Vector3.Zero, tip, 0.035f, 0.015f, 4, 0, 1, new Color(0, 0.4f, 0.5f), new Color(0.6f, 0.4f, 0.5f));
        }
        for (int k = 0; k < 9; k++)
        {
            var d = new Vector3(rng.Range(-1f, 1f), rng.Range(-0.2f, 1f), rng.Range(-1f, 1f)).Normalized();
            LeafCluster(ref rng, leaves, center + new Vector3(d.X * 0.45f, d.Y * 0.35f, d.Z * 0.45f), rng.Range(0.65f, 0.9f), center - new Vector3(0, 0.3f, 0), 1.2f);
        }
        var m = new ArrayMesh();
        stems.CommitTo(m, BarkMaterial);
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
            b.Ellipsoid(new Vector3(0, 0.5f, 0) + new Vector3(d.X * 0.5f, d.Y * 0.38f, d.Z * 0.5f), new Vector3(0.05f, 0.05f, 0.05f), 6, 4);
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
	ALBEDO = COLOR.rgb;
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
        var mat = new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 0.75f };
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
            _flyingArrow = b.Commit(new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 0.6f });
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
            _heldBow = b.Commit(new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 0.6f });
            return _heldBow;
        }
    }
}
