using System;
using System.Threading.Tasks;
using Godot;
using Remade.Core;
using Remade.Diagnostics;
using Remade.Map;

namespace Remade.Game.Render;

/// <summary>
/// Procedurally painted, seamlessly tiling textures generated at startup: the shared RGBA noise texture (terrain,
/// grass, water), an oak leaf-cluster card, bark, and a granite detail map.
/// </summary>
public static class ProcTextures
{
    static ImageTexture _leaves, _bark, _granite;

    public static ImageTexture Leaves => _leaves ??= BuildLeaves();
    public static ImageTexture Bark => _bark ??= BuildBark();
    public static ImageTexture Granite => _granite ??= BuildGranite();
    static ImageTexture _noise;
    /// <summary>RGBA seamless noise (first prototype): R fbm, G cellular distance, B fine value noise, A low-frequency fbm.</summary>
    public static ImageTexture Noise => _noise ??= BuildNoise(1234);

    static ImageTexture BuildNoise(int seed)
    {
        using var _ = Log.Time("ProcTextures.Noise", 500);
        const int S = 256;
        var r = Channel(new FastNoiseLite { Seed = seed, NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin, Frequency = 0.03f, FractalOctaves = 4 }, S);
        var g = Channel(new FastNoiseLite
        {
            Seed = seed + 1, NoiseType = FastNoiseLite.NoiseTypeEnum.Cellular, Frequency = 0.125f, FractalType = FastNoiseLite.FractalTypeEnum.None,
            CellularReturnType = FastNoiseLite.CellularReturnTypeEnum.Distance, CellularJitter = 0.9f,
        }, S);
        var b = Channel(new FastNoiseLite { Seed = seed + 2, NoiseType = FastNoiseLite.NoiseTypeEnum.Value, Frequency = 0.25f, FractalOctaves = 2 }, S);
        var a = Channel(new FastNoiseLite { Seed = seed + 3, NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth, Frequency = 0.0157f, FractalOctaves = 4 }, S);
        var data = new byte[S * S * 4];
        for (int i = 0; i < S * S; i++)
        {
            data[i * 4] = r[i]; data[i * 4 + 1] = g[i]; data[i * 4 + 2] = b[i]; data[i * 4 + 3] = a[i];
        }
        var img = Image.CreateFromData(S, S, false, Image.Format.Rgba8, data);
        img.GenerateMipmaps();
        return ImageTexture.CreateFromImage(img);
    }

    static byte[] Channel(FastNoiseLite n, int s)
    {
        var img = n.GetSeamlessImage(s, s, false, false, 0.1f, true);
        if (img.GetFormat() != Image.Format.L8) img.Convert(Image.Format.L8);
        var d = img.GetData();
        Invariant.Check(d.Length == s * s, $"noise channel has {d.Length} bytes, expected {s * s}");
        return d;
    }

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

    // ---------------------------------------------------------------- foliage

    /// <summary>
    /// A round cluster of broad leaves on a few twigs (first prototype), almost white so the shader can tint it per
    /// tree; transparent background with colour bleeding so mipmaps keep no dark fringes.
    /// </summary>
    static ImageTexture BuildLeaves()
    {
        using var _ = Log.Time("ProcTextures.Leaves", 800);
        const int S = 512;
        var cv = new Canvas(S, S);
        var rng = new Rng(4011);
        var center = new Vector2(S / 2f, S / 2f);
        for (int k = 0; k < 7; k++)
        {
            float a = rng.NextFloat() * Mathf.Tau;
            var end = center + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * rng.Range(120f, 200f);
            cv.Segment(center, end, 4f, 1.5f, new Color(0.35f, 0.27f, 0.2f), new Color(0.4f, 0.32f, 0.22f));
        }
        for (int k = 0; k < 95; k++)
        {
            float a = rng.NextFloat() * Mathf.Tau;
            float rr = Mathf.Sqrt(rng.NextFloat()) * 175f;
            var basePt = center + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * rr;
            float ang = a + rng.Range(-0.8f, 0.8f);
            float len = rng.Range(48f, 74f) * (1.1f - rr / 400f);
            float wid = len * rng.Range(0.28f, 0.38f);
            float v = rng.Range(0.72f, 1.0f);
            var c = new Color(v * rng.Range(0.92f, 1.06f), v, v * rng.Range(0.8f, 0.95f));
            cv.Leaf(basePt, ang, len, wid, c, c * 0.72f);
        }
        return cv.ToTexture(new Color(0.8f, 0.8f, 0.75f));
    }

    sealed class Canvas
    {
        readonly int W, H;
        readonly float[] _px; // rgba
        public Canvas(int w, int h) { W = w; H = h; _px = new float[w * h * 4]; }

        void Blend(int x, int y, Color c, float a)
        {
            if (x < 0 || y < 0 || x >= W || y >= H) return;
            int i = (y * W + x) * 4;
            _px[i] = _px[i] * (1 - a) + c.R * a;
            _px[i + 1] = _px[i + 1] * (1 - a) + c.G * a;
            _px[i + 2] = _px[i + 2] * (1 - a) + c.B * a;
            _px[i + 3] = Mathf.Max(_px[i + 3], a);
        }

        public void Segment(Vector2 a, Vector2 b, float r0, float r1, Color c0, Color c1)
        {
            int x0 = (int)(Mathf.Min(a.X, b.X) - r0 - 2), x1 = (int)(Mathf.Max(a.X, b.X) + r0 + 2);
            int y0 = (int)(Mathf.Min(a.Y, b.Y) - r0 - 2), y1 = (int)(Mathf.Max(a.Y, b.Y) + r0 + 2);
            var ab = b - a;
            float len2 = Mathf.Max(ab.LengthSquared(), 1e-4f);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    var p = new Vector2(x + 0.5f, y + 0.5f);
                    float t = Mathf.Clamp((p - a).Dot(ab) / len2, 0f, 1f);
                    float d = (a + ab * t - p).Length();
                    float rad = Mathf.Lerp(r0, r1, t);
                    float cov = Mathf.Clamp(rad - d + 0.5f, 0f, 1f);
                    if (cov > 0) Blend(x, y, c0.Lerp(c1, t), cov);
                }
        }

        /// <summary>Pointed leaf from its base towards the tip, half-width profile sin(πu)^0.75, a lighter mid-rib.</summary>
        public void Leaf(Vector2 b, float angle, float len, float wid, Color c, Color rib)
        {
            var dir = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
            var nrm = new Vector2(-dir.Y, dir.X);
            float ext = len + wid + 2;
            int x0 = (int)(b.X - ext), x1 = (int)(b.X + ext), y0 = (int)(b.Y - ext), y1 = (int)(b.Y + ext);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    var p = new Vector2(x + 0.5f, y + 0.5f) - b;
                    float u = p.Dot(dir) / len;
                    if (u <= 0f || u >= 1f) continue;
                    float s = p.Dot(nrm);
                    float hw = wid * Mathf.Pow(Mathf.Sin(Mathf.Pi * Mathf.Pow(u, 0.8f)), 0.75f);
                    float edge = hw - Mathf.Abs(s);
                    if (edge <= -0.5f) continue;
                    float cov = Mathf.Clamp(edge + 0.5f, 0f, 1f);
                    float ribK = Mathf.Clamp(1f - Mathf.Abs(s) / 1.3f, 0f, 1f) * (1f - u * 0.6f);
                    float sideShade = 0.86f + 0.14f * Mathf.Sign(s) + 0.1f * (1f - Mathf.Abs(s) / Mathf.Max(hw, 0.01f));
                    var col = (c * sideShade).Lerp(rib, ribK * 0.6f);
                    col *= 0.85f + 0.2f * u;
                    Blend(x, y, col, cov);
                }
        }

        public ImageTexture ToTexture(Color bleed)
        {
            var data = new byte[W * H * 4];
            for (int i = 0; i < W * H; i++)
            {
                float a = _px[i * 4 + 3];
                float r = a > 0.01f ? _px[i * 4] : bleed.R, g = a > 0.01f ? _px[i * 4 + 1] : bleed.G, b = a > 0.01f ? _px[i * 4 + 2] : bleed.B;
                data[i * 4] = (byte)(Mathf.Clamp(r, 0, 1) * 255);
                data[i * 4 + 1] = (byte)(Mathf.Clamp(g, 0, 1) * 255);
                data[i * 4 + 2] = (byte)(Mathf.Clamp(b, 0, 1) * 255);
                data[i * 4 + 3] = (byte)(Mathf.Clamp(a, 0, 1) * 255);
            }
            var img = Image.CreateFromData(W, H, false, Image.Format.Rgba8, data);
            img.GenerateMipmaps();
            return ImageTexture.CreateFromImage(img);
        }
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
