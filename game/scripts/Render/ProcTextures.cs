using System;
using System.Threading.Tasks;
using Godot;
using Remade.Core;
using Remade.Diagnostics;
using Remade.Map;

namespace Remade.Game.Render;

/// <summary>
/// Procedurally painted, seamlessly tiling textures generated at startup (parallel): one albedo layer per terrain
/// type (a Texture2DArray indexed by terrain id), an oak leaf-cluster card, bark, and a granite detail map.
/// </summary>
public static class ProcTextures
{
    public const int TerrainSize = 512;
    static Texture2DArray _terrain;
    static ImageTexture _leaves, _bark, _granite;

    public static Texture2DArray Terrain => _terrain ??= BuildTerrain();
    public static ImageTexture Leaves => _leaves ??= BuildLeaves();
    public static ImageTexture Bark => _bark ??= BuildBark();
    public static ImageTexture Granite => _granite ??= BuildGranite();

    // ---------------------------------------------------------------- tileable noise

    /// <summary>Periodic value noise (period p cells) so textures tile without seams.</summary>
    static float PNoise(float x, float y, int p, int seed)
    {
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
        float fx = x - x0, fy = y - y0;
        fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy);
        float H(int ix, int iy) => Hash.Cell01(((ix % p) + p) % p, ((iy % p) + p) % p, seed);
        float a = H(x0, y0), b = H(x0 + 1, y0), c = H(x0, y0 + 1), d = H(x0 + 1, y0 + 1);
        return (a + (b - a) * fx) + ((c + (d - c) * fx) - (a + (b - a) * fx)) * fy;
    }

    static float PFbm(float u, float v, int basePeriod, int octaves, int seed)
    {
        float s = 0, amp = 0.5f, norm = 0;
        int p = basePeriod;
        for (int o = 0; o < octaves; o++)
        {
            s += PNoise(u * p, v * p, p, seed + o * 31) * amp;
            norm += amp;
            amp *= 0.5f;
            p *= 2;
        }
        return s / norm;
    }

    /// <summary>Periodic Worley (cellular) distance: pebbles and stones.</summary>
    static float PWorley(float u, float v, int p, int seed, out float cellHash)
    {
        float x = u * p, y = v * p;
        int cx = (int)MathF.Floor(x), cy = (int)MathF.Floor(y);
        float best = 9f; cellHash = 0;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int ix = cx + dx, iy = cy + dy;
                int wx = ((ix % p) + p) % p, wy = ((iy % p) + p) % p;
                float px = ix + Hash.Cell01(wx, wy, seed), py = iy + Hash.Cell01(wx, wy, seed + 1);
                float d = (px - x) * (px - x) + (py - y) * (py - y);
                if (d < best) { best = d; cellHash = Hash.Cell01(wx, wy, seed + 2); }
            }
        return MathF.Sqrt(best);
    }

    static Color Lerp(Color a, Color b, float t) => a.Lerp(b, Math.Clamp(t, 0f, 1f));

    // ---------------------------------------------------------------- terrain

    static Texture2DArray BuildTerrain()
    {
        using var _ = Log.Time("ProcTextures.Terrain", 1500);
        int n = (int)Remade.Map.Terrain.Count;
        var images = new Image[n];
        Parallel.For(0, n, t => images[t] = PaintTerrain((Terrain)t));
        var arr = new Godot.Collections.Array<Image>();
        foreach (var img in images) { img.GenerateMipmaps(); arr.Add(img); }
        var tex = new Texture2DArray();
        var err = tex.CreateFromImages(arr);
        if (err != Error.Ok) throw new InvalidOperationException($"Terrain texture array: {err}");
        return tex;
    }

    static Image PaintTerrain(Terrain t)
    {
        int S = TerrainSize;
        var data = new byte[S * S * 4];
        int seed = 100 + (int)t * 17;
        for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                float u = x / (float)S, v = y / (float)S;
                Color c = TerrainPixel(t, u, v, seed);
                int i = (y * S + x) * 4;
                data[i] = (byte)(Math.Clamp(c.R, 0, 1) * 255); data[i + 1] = (byte)(Math.Clamp(c.G, 0, 1) * 255);
                data[i + 2] = (byte)(Math.Clamp(c.B, 0, 1) * 255); data[i + 3] = (byte)(Math.Clamp(c.A, 0, 1) * 255);
            }
        return Image.CreateFromData(S, S, false, Image.Format.Rgba8, data);
    }

    /// <summary>Albedo rgb, alpha = height (for blending/parallax-free relief cues).</summary>
    static Color TerrainPixel(Terrain t, float u, float v, int seed)
    {
        float n1 = PFbm(u, v, 4, 5, seed);
        float n2 = PFbm(u, v, 16, 4, seed + 7);
        float fine = PNoise(u * 128, v * 128, 128, seed + 9);
        switch (t)
        {
            case Remade.Map.Terrain.Soil:
            case Remade.Map.Terrain.RichSoil:
            {
                bool rich = t == Remade.Map.Terrain.RichSoil;
                Color a = rich ? new Color(0.20f, 0.14f, 0.09f) : new Color(0.33f, 0.25f, 0.17f);
                Color b = rich ? new Color(0.13f, 0.09f, 0.06f) : new Color(0.24f, 0.18f, 0.12f);
                Color c = Lerp(a, b, n1 * 1.2f - 0.1f);
                c = Lerp(c, c * 0.75f, n2);
                float peb = PWorley(u, v, 40, seed + 3, out float ph);
                if (peb < 0.18f && ph > 0.6f) c = Lerp(c, new Color(0.45f, 0.42f, 0.38f), (0.18f - peb) * 6f);
                // leaf litter / twigs
                if (fine > 0.86f) c = Lerp(c, new Color(0.30f, 0.24f, 0.12f), 0.5f);
                c.A = 0.4f + n2 * 0.4f;
                return c * (0.92f + fine * 0.16f);
            }
            case Remade.Map.Terrain.Gravel:
            {
                float d = PWorley(u, v, 26, seed, out float h);
                Color stone = Lerp(new Color(0.42f, 0.40f, 0.37f), new Color(0.58f, 0.55f, 0.50f), h);
                Color gap = new Color(0.20f, 0.18f, 0.15f);
                Color c = Lerp(stone, gap, Math.Clamp((d - 0.35f) * 3f, 0f, 1f));
                c.A = 1f - d;
                return c * (0.9f + fine * 0.2f);
            }
            case Remade.Map.Terrain.Sand:
            case Remade.Map.Terrain.OceanShallow:
            case Remade.Map.Terrain.OceanDeep:
            {
                float ripple = MathF.Sin((v * 32f + n1 * 3f) * MathF.Tau) * 0.5f + 0.5f;
                Color c = Lerp(new Color(0.76f, 0.68f, 0.52f), new Color(0.66f, 0.58f, 0.43f), n2 * 0.8f + ripple * 0.25f);
                if (t != Remade.Map.Terrain.Sand) c *= 0.75f;
                c.A = ripple * 0.5f + n2 * 0.3f;
                return c * (0.94f + fine * 0.12f);
            }
            case Remade.Map.Terrain.Mud:
            case Remade.Map.Terrain.LakeShallow:
            case Remade.Map.Terrain.LakeDeep:
            {
                Color c = Lerp(new Color(0.22f, 0.17f, 0.12f), new Color(0.14f, 0.11f, 0.08f), n1);
                c = Lerp(c, new Color(0.26f, 0.22f, 0.16f), n2 * 0.5f);
                c.A = n1 * 0.5f;
                return c;
            }
            case Remade.Map.Terrain.Marsh:
            {
                Color c = Lerp(new Color(0.18f, 0.20f, 0.11f), new Color(0.12f, 0.12f, 0.08f), n1);
                c = Lerp(c, new Color(0.25f, 0.28f, 0.14f), Math.Clamp(n2 * 2f - 0.8f, 0f, 1f));
                c.A = n2 * 0.6f;
                return c;
            }
            case Remade.Map.Terrain.RoughGranite:
            {
                float d = PWorley(u, v, 10, seed, out float h);
                Color c = Lerp(new Color(0.50f, 0.46f, 0.45f), new Color(0.38f, 0.35f, 0.35f), n1 + h * 0.3f);
                if (fine > 0.8f) c = Lerp(c, new Color(0.72f, 0.60f, 0.58f), 0.6f); // feldspar
                if (fine < 0.12f) c = Lerp(c, new Color(0.12f, 0.12f, 0.13f), 0.6f); // biotite
                c = Lerp(c, c * 0.6f, Math.Clamp((d - 0.5f) * 4f, 0f, 1f));
                c.A = 1f - d;
                return c;
            }
            case Remade.Map.Terrain.RiverShallow:
            case Remade.Map.Terrain.RiverDeep:
            {
                float d = PWorley(u, v, 18, seed, out float h);
                Color stone = Lerp(new Color(0.40f, 0.38f, 0.32f), new Color(0.52f, 0.48f, 0.40f), h);
                Color c = Lerp(stone, new Color(0.24f, 0.21f, 0.16f), Math.Clamp((d - 0.4f) * 3f, 0f, 1f));
                c = Lerp(c, new Color(0.22f, 0.26f, 0.14f), n2 * 0.35f); // algae
                c.A = 1f - d;
                return c;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(t), t, "no texture painter");
        }
    }

    // ---------------------------------------------------------------- foliage

    /// <summary>An oak leaf cluster card: lobed leaves on transparent background (alpha-scissored in the shader).</summary>
    static ImageTexture BuildLeaves()
    {
        const int S = 256;
        var img = Image.CreateEmpty(S, S, false, Image.Format.Rgba8);
        img.Fill(new Color(0.18f, 0.28f, 0.10f, 0f));
        var rng = new Rng(77);
        // many small lobed leaves scattered in a disc, darker towards the centre
        for (int k = 0; k < 150; k++)
        {
            float r = MathF.Sqrt(rng.NextFloat()) * 0.42f;
            float a = rng.Range(0f, MathF.Tau);
            float cx = 0.5f + MathF.Cos(a) * r, cy = 0.5f + MathF.Sin(a) * r;
            float ang = rng.Range(0f, MathF.Tau);
            float len = rng.Range(0.055f, 0.085f), wid = len * 0.42f;
            float shade = rng.Range(0.7f, 1.15f) * (1f - r * 0.6f);
            var baseCol = new Color(0.23f, 0.36f, 0.12f) * shade;
            int x0 = (int)((cx - len) * S), x1 = (int)((cx + len) * S), y0 = (int)((cy - len) * S), y1 = (int)((cy + len) * S);
            for (int y = Math.Max(0, y0); y <= Math.Min(S - 1, y1); y++)
                for (int x = Math.Max(0, x0); x <= Math.Min(S - 1, x1); x++)
                {
                    float px = x / (float)S - cx, py = y / (float)S - cy;
                    float lx = px * MathF.Cos(ang) + py * MathF.Sin(ang), ly = -px * MathF.Sin(ang) + py * MathF.Cos(ang);
                    float t = lx / len; // -1..1 along the leaf
                    if (t < -1 || t > 1) continue;
                    // oak lobes: width modulated along the length
                    float w = wid * MathF.Sqrt(1 - t * t) * (0.75f + 0.25f * MathF.Cos(t * 9.5f));
                    if (MathF.Abs(ly) > w) continue;
                    float vein = MathF.Abs(ly) < wid * 0.08f ? 0.82f : 1f;
                    float edge = 1f - MathF.Abs(ly) / MathF.Max(w, 1e-4f) * 0.25f;
                    var c = baseCol * vein * edge;
                    c.A = 1f;
                    img.SetPixel(x, y, c);
                }
        }
        img.GenerateMipmaps();
        return ImageTexture.CreateFromImage(img);
    }

    static ImageTexture BuildBark()
    {
        const int S = 256;
        var data = new byte[S * S * 4];
        Parallel.For(0, S, y =>
        {
            for (int x = 0; x < S; x++)
            {
                float u = x / (float)S, v = y / (float)S;
                // vertical furrows typical of oak bark
                float f = PFbm(u * 1f, v * 0.25f, 8, 4, 501);
                float ridges = MathF.Abs(MathF.Sin((u * 10f + f * 2.2f) * MathF.PI));
                float n = PFbm(u, v, 16, 3, 503);
                var c = new Color(0.30f, 0.25f, 0.20f).Lerp(new Color(0.12f, 0.10f, 0.08f), (1 - ridges) * 0.8f + n * 0.2f);
                if (n > 0.7f) c = c.Lerp(new Color(0.30f, 0.36f, 0.20f), (n - 0.7f) * 2f); // lichen
                int i = (y * S + x) * 4;
                data[i] = (byte)(c.R * 255); data[i + 1] = (byte)(c.G * 255); data[i + 2] = (byte)(c.B * 255); data[i + 3] = (byte)(ridges * 255);
            }
        });
        var img = Image.CreateFromData(S, S, false, Image.Format.Rgba8, data);
        img.GenerateMipmaps();
        return ImageTexture.CreateFromImage(img);
    }

    /// <summary>Granite detail: rgb albedo (speckled pink/grey/black crystals), alpha = crack/cavity mask.</summary>
    static ImageTexture BuildGranite()
    {
        const int S = 512;
        var data = new byte[S * S * 4];
        Parallel.For(0, S, y =>
        {
            for (int x = 0; x < S; x++)
            {
                float u = x / (float)S, v = y / (float)S;
                float n = PFbm(u, v, 4, 5, 701);
                float grain = PNoise(u * 160, v * 160, 160, 703);
                float grain2 = PNoise(u * 90, v * 90, 90, 705);
                var c = new Color(0.56f, 0.52f, 0.50f).Lerp(new Color(0.44f, 0.41f, 0.40f), n);
                if (grain > 0.78f) c = c.Lerp(new Color(0.78f, 0.64f, 0.60f), 0.7f);
                if (grain2 < 0.16f) c = c.Lerp(new Color(0.10f, 0.10f, 0.11f), 0.75f);
                if (grain < 0.08f) c = c.Lerp(new Color(0.88f, 0.87f, 0.84f), 0.6f);
                float d = PWorley(u, v, 6, 707, out _);
                float crack = Math.Clamp((d - 0.62f) * 8f, 0f, 1f);
                int i = (y * S + x) * 4;
                data[i] = (byte)(c.R * 255); data[i + 1] = (byte)(c.G * 255); data[i + 2] = (byte)(c.B * 255); data[i + 3] = (byte)((1 - crack) * 255);
            }
        });
        var img = Image.CreateFromData(S, S, false, Image.Format.Rgba8, data);
        img.GenerateMipmaps();
        return ImageTexture.CreateFromImage(img);
    }
}
