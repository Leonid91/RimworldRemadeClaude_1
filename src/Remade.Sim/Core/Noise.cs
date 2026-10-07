using System;
using System.Runtime.CompilerServices;

namespace Remade.Core;

/// <summary>
/// Seeded simplex noise (2D and 3D) with fractal helpers. Output of the raw functions is roughly in [-1, 1].
/// Thread-safe after construction (read-only permutation table).
/// </summary>
public sealed class Noise
{
    readonly byte[] _perm = new byte[512];
    readonly byte[] _perm12 = new byte[512];

    static readonly float[] G3 =
    {
        1,1,0, -1,1,0, 1,-1,0, -1,-1,0,
        1,0,1, -1,0,1, 1,0,-1, -1,0,-1,
        0,1,1, 0,-1,1, 0,1,-1, 0,-1,-1,
    };

    public readonly int Seed;

    public Noise(int seed)
    {
        Seed = seed;
        var p = new byte[256];
        for (int i = 0; i < 256; i++) p[i] = (byte)i;
        var rng = new Rng((ulong)seed * 0x9E3779B97F4A7C15UL + 12345);
        for (int i = 255; i > 0; i--)
        {
            int j = rng.Range(0, i + 1);
            (p[i], p[j]) = (p[j], p[i]);
        }
        for (int i = 0; i < 512; i++)
        {
            _perm[i] = p[i & 255];
            _perm12[i] = (byte)(_perm[i] % 12);
        }
    }

    const float F2 = 0.36602540378f; // (sqrt(3)-1)/2
    const float G2 = 0.21132486540f; // (3-sqrt(3))/6

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int FastFloor(float x) { int i = (int)x; return x < i ? i - 1 : i; }

    public float Get(float x, float y)
    {
        float s = (x + y) * F2;
        int i = FastFloor(x + s), j = FastFloor(y + s);
        float t = (i + j) * G2;
        float x0 = x - (i - t), y0 = y - (j - t);
        int i1, j1;
        if (x0 > y0) { i1 = 1; j1 = 0; } else { i1 = 0; j1 = 1; }
        float x1 = x0 - i1 + G2, y1 = y0 - j1 + G2;
        float x2 = x0 - 1f + 2f * G2, y2 = y0 - 1f + 2f * G2;
        int ii = i & 255, jj = j & 255;
        float n = 0;
        float t0 = 0.5f - x0 * x0 - y0 * y0;
        if (t0 > 0) { int g = _perm12[ii + _perm[jj]] * 3; t0 *= t0; n += t0 * t0 * (G3[g] * x0 + G3[g + 1] * y0); }
        float t1 = 0.5f - x1 * x1 - y1 * y1;
        if (t1 > 0) { int g = _perm12[ii + i1 + _perm[jj + j1]] * 3; t1 *= t1; n += t1 * t1 * (G3[g] * x1 + G3[g + 1] * y1); }
        float t2 = 0.5f - x2 * x2 - y2 * y2;
        if (t2 > 0) { int g = _perm12[ii + 1 + _perm[jj + 1]] * 3; t2 *= t2; n += t2 * t2 * (G3[g] * x2 + G3[g + 1] * y2); }
        return 70f * n;
    }

    const float F3 = 1f / 3f, G3c = 1f / 6f;

    public float Get(float x, float y, float z)
    {
        float s = (x + y + z) * F3;
        int i = FastFloor(x + s), j = FastFloor(y + s), k = FastFloor(z + s);
        float t = (i + j + k) * G3c;
        float x0 = x - (i - t), y0 = y - (j - t), z0 = z - (k - t);
        int i1, j1, k1, i2, j2, k2;
        if (x0 >= y0)
        {
            if (y0 >= z0) { i1 = 1; j1 = 0; k1 = 0; i2 = 1; j2 = 1; k2 = 0; }
            else if (x0 >= z0) { i1 = 1; j1 = 0; k1 = 0; i2 = 1; j2 = 0; k2 = 1; }
            else { i1 = 0; j1 = 0; k1 = 1; i2 = 1; j2 = 0; k2 = 1; }
        }
        else
        {
            if (y0 < z0) { i1 = 0; j1 = 0; k1 = 1; i2 = 0; j2 = 1; k2 = 1; }
            else if (x0 < z0) { i1 = 0; j1 = 1; k1 = 0; i2 = 0; j2 = 1; k2 = 1; }
            else { i1 = 0; j1 = 1; k1 = 0; i2 = 1; j2 = 1; k2 = 0; }
        }
        float x1 = x0 - i1 + G3c, y1 = y0 - j1 + G3c, z1 = z0 - k1 + G3c;
        float x2 = x0 - i2 + 2f * G3c, y2 = y0 - j2 + 2f * G3c, z2 = z0 - k2 + 2f * G3c;
        float x3 = x0 - 1f + 3f * G3c, y3 = y0 - 1f + 3f * G3c, z3 = z0 - 1f + 3f * G3c;
        int ii = i & 255, jj = j & 255, kk = k & 255;
        float n = 0;
        float t0 = 0.6f - x0 * x0 - y0 * y0 - z0 * z0;
        if (t0 > 0) { int g = _perm12[ii + _perm[jj + _perm[kk]]] * 3; t0 *= t0; n += t0 * t0 * (G3[g] * x0 + G3[g + 1] * y0 + G3[g + 2] * z0); }
        float t1 = 0.6f - x1 * x1 - y1 * y1 - z1 * z1;
        if (t1 > 0) { int g = _perm12[ii + i1 + _perm[jj + j1 + _perm[kk + k1]]] * 3; t1 *= t1; n += t1 * t1 * (G3[g] * x1 + G3[g + 1] * y1 + G3[g + 2] * z1); }
        float t2 = 0.6f - x2 * x2 - y2 * y2 - z2 * z2;
        if (t2 > 0) { int g = _perm12[ii + i2 + _perm[jj + j2 + _perm[kk + k2]]] * 3; t2 *= t2; n += t2 * t2 * (G3[g] * x2 + G3[g + 1] * y2 + G3[g + 2] * z2); }
        float t3 = 0.6f - x3 * x3 - y3 * y3 - z3 * z3;
        if (t3 > 0) { int g = _perm12[ii + 1 + _perm[jj + 1 + _perm[kk + 1]]] * 3; t3 *= t3; n += t3 * t3 * (G3[g] * x3 + G3[g + 1] * y3 + G3[g + 2] * z3); }
        return 32f * n;
    }

    /// <summary>Fractal Brownian motion, normalised to roughly [-1,1].</summary>
    public float Fbm(float x, float y, int octaves, float lacunarity = 2f, float gain = 0.5f)
    {
        float sum = 0, amp = 1, norm = 0;
        for (int o = 0; o < octaves; o++)
        {
            sum += Get(x, y) * amp;
            norm += amp;
            amp *= gain;
            x *= lacunarity; y *= lacunarity;
            x += 17.13f; y += 9.71f;
        }
        return sum / norm;
    }

    public float Fbm(float x, float y, float z, int octaves, float lacunarity = 2f, float gain = 0.5f)
    {
        float sum = 0, amp = 1, norm = 0;
        for (int o = 0; o < octaves; o++)
        {
            sum += Get(x, y, z) * amp;
            norm += amp;
            amp *= gain;
            x *= lacunarity; y *= lacunarity; z *= lacunarity;
            x += 17.13f; y += 9.71f; z += 3.37f;
        }
        return sum / norm;
    }

    /// <summary>Ridged multifractal in [0,1]: sharp crests, used for mountain ranges.</summary>
    public float Ridged(float x, float y, float z, int octaves, float lacunarity = 2f, float gain = 0.5f)
    {
        float sum = 0, amp = 1, norm = 0, weight = 1;
        for (int o = 0; o < octaves; o++)
        {
            float n = 1f - MathF.Abs(Get(x, y, z));
            n *= n;
            n *= weight;
            weight = Math.Clamp(n * 2f, 0f, 1f);
            sum += n * amp;
            norm += amp;
            amp *= gain;
            x *= lacunarity; y *= lacunarity; z *= lacunarity;
        }
        return sum / norm;
    }

    public float Ridged(float x, float y, int octaves, float lacunarity = 2f, float gain = 0.5f)
    {
        float sum = 0, amp = 1, norm = 0, weight = 1;
        for (int o = 0; o < octaves; o++)
        {
            float n = 1f - MathF.Abs(Get(x, y));
            n *= n;
            n *= weight;
            weight = Math.Clamp(n * 2f, 0f, 1f);
            sum += n * amp;
            norm += amp;
            amp *= gain;
            x *= lacunarity; y *= lacunarity;
        }
        return sum / norm;
    }
}
