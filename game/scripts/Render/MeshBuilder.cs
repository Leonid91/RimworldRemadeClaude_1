using System;
using System.Collections.Generic;
using Godot;

namespace Remade.Game.Render;

/// <summary>Accumulates triangles (with normals, colours, UVs and a second UV channel) and commits ArrayMesh surfaces.</summary>
public sealed class MeshBuilder
{
    public readonly List<Vector3> V = new();
    public readonly List<Vector3> N = new();
    public readonly List<Color> C = new();
    public readonly List<Vector2> UV = new();
    public readonly List<Vector2> UV2 = new();
    public readonly List<int> I = new();

    public int Count => V.Count;
    public Color Color = Colors.White;
    public Vector2 Uv2;

    public int Add(Vector3 p, Vector3 n, Vector2 uv)
    {
        V.Add(p); N.Add(n); C.Add(Color); UV.Add(uv); UV2.Add(Uv2);
        return V.Count - 1;
    }

    public int Add(Vector3 p, Vector3 n, Vector2 uv, Color c)
    {
        V.Add(p); N.Add(n); C.Add(c); UV.Add(uv); UV2.Add(Uv2);
        return V.Count - 1;
    }

    /// <summary>Triangle with the given indices; Godot front faces are clockwise when seen from the front.</summary>
    public void Tri(int a, int b, int c) { I.Add(a); I.Add(b); I.Add(c); }

    public void Quad(int a, int b, int c, int d) { Tri(a, b, c); Tri(a, c, d); }

    /// <summary>Flat-shaded quad from 4 corners given counter-clockwise when seen from the front.</summary>
    public void QuadFlat(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        Vector3 n = (b - a).Cross(c - a).Normalized();
        int ia = Add(a, n, new Vector2(0, 0)), ib = Add(b, n, new Vector2(1, 0)), ic = Add(c, n, new Vector2(1, 1)), id = Add(d, n, new Vector2(0, 1));
        // counter-clockwise from the front → reverse to Godot's clockwise
        Tri(ia, ic, ib); Tri(ia, id, ic);
    }

    /// <summary>Axis-aligned box centred at c with half extents h.</summary>
    public void Box(Vector3 c, Vector3 h)
    {
        Vector3 Pt(float x, float y, float z) => c + new Vector3(x * h.X, y * h.Y, z * h.Z);
        QuadFlat(Pt(-1, -1, 1), Pt(1, -1, 1), Pt(1, 1, 1), Pt(-1, 1, 1));    // +Z
        QuadFlat(Pt(1, -1, -1), Pt(-1, -1, -1), Pt(-1, 1, -1), Pt(1, 1, -1)); // -Z
        QuadFlat(Pt(1, -1, 1), Pt(1, -1, -1), Pt(1, 1, -1), Pt(1, 1, 1));    // +X
        QuadFlat(Pt(-1, -1, -1), Pt(-1, -1, 1), Pt(-1, 1, 1), Pt(-1, 1, -1)); // -X
        QuadFlat(Pt(-1, 1, 1), Pt(1, 1, 1), Pt(1, 1, -1), Pt(-1, 1, -1));    // +Y
        QuadFlat(Pt(-1, -1, -1), Pt(1, -1, -1), Pt(1, -1, 1), Pt(-1, -1, 1)); // -Y
    }

    /// <summary>Tapered tube between two points (smooth normals), optional caps.</summary>
    public void Tube(Vector3 a, Vector3 b, float ra, float rb, int sides, float v0 = 0, float v1 = 1, Color? ca = null, Color? cb = null, bool capTop = false)
    {
        Vector3 axis = (b - a);
        float len = axis.Length();
        if (len < 1e-5f) return;
        axis /= len;
        Vector3 side = Mathf.Abs(axis.Dot(Vector3.Up)) > 0.95f ? Vector3.Right : Vector3.Up;
        Vector3 x = axis.Cross(side).Normalized();
        Vector3 z = axis.Cross(x).Normalized();
        int start = Count;
        for (int ring = 0; ring < 2; ring++)
        {
            Vector3 center = ring == 0 ? a : b;
            float r = ring == 0 ? ra : rb;
            Color col = ring == 0 ? (ca ?? Color) : (cb ?? Color);
            for (int s = 0; s <= sides; s++)
            {
                float ang = s / (float)sides * Mathf.Tau;
                Vector3 n = x * Mathf.Cos(ang) + z * Mathf.Sin(ang);
                Add(center + n * r, n, new Vector2(s / (float)sides, ring == 0 ? v0 : v1), col);
            }
        }
        int stride = sides + 1;
        for (int s = 0; s < sides; s++)
        {
            int i0 = start + s, i1 = start + s + 1, j0 = start + stride + s, j1 = start + stride + s + 1;
            Tri(i0, j0, i1);
            Tri(i1, j0, j1);
        }
        if (capTop)
        {
            int c = Add(b, axis, new Vector2(0.5f, v1), cb ?? Color);
            for (int s = 0; s < sides; s++) Tri(c, start + stride + s + 1, start + stride + s);
        }
    }

    /// <summary>UV sphere / ellipsoid.</summary>
    public void Ellipsoid(Vector3 c, Vector3 r, int seg = 10, int rings = 7)
    {
        int start = Count;
        for (int j = 0; j <= rings; j++)
        {
            float v = j / (float)rings;
            float phi = v * Mathf.Pi;
            for (int i = 0; i <= seg; i++)
            {
                float u = i / (float)seg;
                float th = u * Mathf.Tau;
                var n = new Vector3(Mathf.Sin(phi) * Mathf.Cos(th), Mathf.Cos(phi), Mathf.Sin(phi) * Mathf.Sin(th));
                Vector3 p = c + new Vector3(n.X * r.X, n.Y * r.Y, n.Z * r.Z);
                Vector3 nn = new Vector3(n.X / r.X, n.Y / r.Y, n.Z / r.Z).Normalized();
                Add(p, nn, new Vector2(u, v));
            }
        }
        int stride = seg + 1;
        for (int j = 0; j < rings; j++)
            for (int i = 0; i < seg; i++)
            {
                int a = start + j * stride + i, b = a + 1, d = a + stride, e = d + 1;
                Tri(a, d, b);
                Tri(b, d, e);
            }
    }

    public Godot.Collections.Array Arrays()
    {
        var arr = new Godot.Collections.Array();
        arr.Resize((int)Mesh.ArrayType.Max);
        arr[(int)Mesh.ArrayType.Vertex] = V.ToArray();
        arr[(int)Mesh.ArrayType.Normal] = N.ToArray();
        arr[(int)Mesh.ArrayType.Color] = C.ToArray();
        arr[(int)Mesh.ArrayType.TexUV] = UV.ToArray();
        arr[(int)Mesh.ArrayType.TexUV2] = UV2.ToArray();
        arr[(int)Mesh.ArrayType.Index] = I.ToArray();
        return arr;
    }

    public void CommitTo(ArrayMesh mesh, Material mat = null)
    {
        if (I.Count == 0) return;
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, Arrays());
        if (mat != null) mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, mat);
    }

    public ArrayMesh Commit(Material mat = null)
    {
        var m = new ArrayMesh();
        CommitTo(m, mat);
        return m;
    }

    public void Clear() { V.Clear(); N.Clear(); C.Clear(); UV.Clear(); UV2.Clear(); I.Clear(); }
}
