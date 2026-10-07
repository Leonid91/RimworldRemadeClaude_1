using System;
using Godot;

namespace Remade.Game.Planet3D;

/// <summary>
/// Painted relief icons for the globe, drawn once in code into a 4×1 atlas (128 px each): small hills (two low
/// humps), large hills (three tall humps), mountains (a peak with a snowy tip) and impassable mountains (a jagged
/// double peak with large snowfields). Each drawing has a dark outline so it reads on any biome colour, and stays
/// inside a circle of 0.45 of its cell so it never leaves its hexagon.
/// </summary>
public static class ReliefIcons
{
    const int S = 128;
    static ImageTexture _atlas;
    public static ImageTexture Atlas => _atlas ??= Build();

    static ImageTexture Build()
    {
        var img = Image.CreateEmpty(S * 4, S, false, Image.Format.Rgba8);
        img.Fill(new Color(0, 0, 0, 0));
        for (int k = 0; k < 4; k++) Paint(img, k);
        img.GenerateMipmaps();
        return ImageTexture.CreateFromImage(img);
    }

    // signed distance helpers in icon space (0..1, y up)
    static float Hump(Vector2 p, float cx, float w, float h, float baseY)
    {
        // inside a half ellipse standing on baseY
        var d = new Vector2((p.X - cx) / w, (p.Y - baseY) / h);
        if (p.Y < baseY) return 1f;
        return (d.Length() - 1f) * MathF.Min(w, h);
    }

    static float Peak(Vector2 p, float cx, float w, float h, float baseY, float skew)
    {
        // triangle with apex (cx + skew, baseY + h), base from cx - w to cx + w
        var a = new Vector2(cx - w, baseY); var b = new Vector2(cx + w, baseY); var c = new Vector2(cx + skew, baseY + h);
        float e1 = Edge(p, a, c), e2 = Edge(p, c, b), e3 = baseY - p.Y;
        return MathF.Max(MathF.Max(e1, e2), e3);
    }

    static float Edge(Vector2 p, Vector2 a, Vector2 b)
    {
        var d = (b - a).Normalized();
        var n = new Vector2(-d.Y, d.X); // outward: the outline runs clockwise (left base, apex, right base)
        return (p - a).Dot(n);
    }

    static void Paint(Image img, int kind)
    {
        var outline = new Color(0.10f, 0.08f, 0.06f);
        for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                var p = new Vector2((x + 0.5f) / S, 1f - (y + 0.5f) / S);
                float dist = 1f; Color fill = default; float shade = 0;
                switch (kind)
                {
                    case 0: // small hills
                    {
                        float a = Hump(p, 0.38f, 0.2f, 0.17f, 0.3f), b = Hump(p, 0.64f, 0.17f, 0.13f, 0.3f);
                        dist = MathF.Min(a, b);
                        fill = new Color(0.55f, 0.47f, 0.30f);
                        shade = p.X < (a < b ? 0.38f : 0.64f) ? 0.12f : -0.1f;
                        break;
                    }
                    case 1: // large hills
                    {
                        float a = Hump(p, 0.32f, 0.2f, 0.26f, 0.24f), b = Hump(p, 0.56f, 0.22f, 0.34f, 0.24f), h3 = Hump(p, 0.75f, 0.14f, 0.2f, 0.24f);
                        dist = MathF.Min(a, MathF.Min(b, h3));
                        fill = new Color(0.50f, 0.42f, 0.27f);
                        float cx = a <= b && a <= h3 ? 0.32f : b <= h3 ? 0.56f : 0.75f;
                        shade = p.X < cx ? 0.12f : -0.12f;
                        break;
                    }
                    case 2: // mountain
                    {
                        dist = Peak(p, 0.5f, 0.32f, 0.5f, 0.22f, 0.02f);
                        fill = new Color(0.47f, 0.45f, 0.43f);
                        shade = p.X < 0.52f - (p.Y - 0.22f) * 0.04f ? 0.14f : -0.12f;
                        if (p.Y > 0.58f) fill = new Color(0.93f, 0.94f, 0.97f);
                        break;
                    }
                    default: // impassable: jagged double peak, big snowfields
                    {
                        float a = Peak(p, 0.38f, 0.26f, 0.6f, 0.2f, -0.03f), b = Peak(p, 0.66f, 0.22f, 0.46f, 0.2f, 0.03f);
                        dist = MathF.Min(a, b);
                        fill = new Color(0.30f, 0.29f, 0.30f);
                        float cx = a <= b ? 0.35f : 0.69f;
                        shade = p.X < cx ? 0.12f : -0.1f;
                        if (p.Y > (a <= b ? 0.55f : 0.5f)) fill = new Color(0.95f, 0.96f, 0.99f);
                        break;
                    }
                }
                // keep the drawing inside a circle of radius 0.45 around the cell centre
                float circle = (p - new Vector2(0.5f, 0.5f)).Length() - 0.45f;
                if (circle > 0) continue;
                float px = 1f / S;
                float edge = 2.2f * px; // outline width
                if (dist > edge + px) continue;
                float cover = Math.Clamp((edge + px - dist) / px, 0f, 1f);
                Color c = dist > 0 ? outline : (fill + new Color(shade, shade, shade)).Clamp();
                if (dist > -px && dist <= 0) c = outline.Lerp(c, Math.Clamp(-dist / px, 0f, 1f));
                img.SetPixel(kind * S + x, y, new Color(c, cover));
            }
    }
}
