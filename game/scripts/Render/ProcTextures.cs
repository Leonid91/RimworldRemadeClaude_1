using System;
using System.Threading.Tasks;
using Godot;
using Remade.Core;
using Remade.Diagnostics;
using Remade.Map;

namespace Remade.Game.Render;

/// <summary>
/// Procedurally painted, seamlessly tiling textures generated at startup: the shared RGBA noise texture (terrain,
/// grass, water, rock, bark) and an oak leaf-cluster card.
/// </summary>
public static class ProcTextures
{
    static ImageTexture _leaves;

    public static ImageTexture Leaves => _leaves ??= BuildLeaves();
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
}
