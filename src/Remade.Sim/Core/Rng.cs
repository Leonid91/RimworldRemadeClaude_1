using System;
using System.Runtime.CompilerServices;

namespace Remade.Core;

/// <summary>Deterministic, fast, serialisable random generator (xoshiro256**). A struct: copy = fork.</summary>
public struct Rng
{
    ulong _s0, _s1, _s2, _s3;

    public Rng(ulong seed)
    {
        ulong x = seed;
        _s0 = SplitMix(ref x); _s1 = SplitMix(ref x); _s2 = SplitMix(ref x); _s3 = SplitMix(ref x);
        if ((_s0 | _s1 | _s2 | _s3) == 0) _s0 = 1;
    }

    public static Rng FromParts(params long[] parts)
    {
        ulong h = 0x9E3779B97F4A7C15UL;
        foreach (long p in parts) h = Hash.Mix(h ^ (ulong)p);
        return new Rng(h);
    }

    static ulong SplitMix(ref ulong x)
    {
        ulong z = (x += 0x9E3779B97F4A7C15UL);
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong NextULong()
    {
        ulong result = RotL(_s1 * 5, 7) * 9;
        ulong t = _s1 << 17;
        _s2 ^= _s0; _s3 ^= _s1; _s1 ^= _s2; _s0 ^= _s3;
        _s2 ^= t;
        _s3 = RotL(_s3, 45);
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ulong RotL(ulong x, int k) => (x << k) | (x >> (64 - k));

    /// <summary>[0,1)</summary>
    public float NextFloat() => (NextULong() >> 40) * (1.0f / (1 << 24));
    public double NextDouble() => (NextULong() >> 11) * (1.0 / (1UL << 53));
    public float Range(float min, float max) => min + (max - min) * NextFloat();

    /// <summary>[min, max)</summary>
    public int Range(int min, int max)
    {
        if (max <= min) throw new ArgumentException($"Rng.Range: empty range [{min},{max})");
        return min + (int)((NextULong() >> 33) % (ulong)(max - min));
    }

    public bool Chance(float p) => NextFloat() < p;

    /// <summary>Standard normal sample (Box-Muller).</summary>
    public float Gaussian()
    {
        double u1 = 1.0 - NextDouble(), u2 = NextDouble();
        return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2));
    }

    public T Pick<T>(T[] arr) => arr[Range(0, arr.Length)];

    public void GetState(out ulong a, out ulong b, out ulong c, out ulong d) { a = _s0; b = _s1; c = _s2; d = _s3; }
    public static Rng FromState(ulong a, ulong b, ulong c, ulong d)
    {
        var r = new Rng { _s0 = a, _s1 = b, _s2 = c, _s3 = d };
        if ((a | b | c | d) == 0) throw new ArgumentException("Rng state cannot be all zero");
        return r;
    }
}

public static class Hash
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Mix(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Cell(int x, int y, int seed)
    {
        uint h = (uint)seed * 0x27d4eb2dU;
        h ^= (uint)x * 0x85ebca6bU;
        h = (h << 13) | (h >> 19);
        h ^= (uint)y * 0xc2b2ae35U;
        h *= 0x9E3779B1U;
        h ^= h >> 16;
        h *= 0x7feb352dU;
        h ^= h >> 15;
        return h;
    }

    /// <summary>Deterministic [0,1) value for an integer cell.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Cell01(int x, int y, int seed) => (Cell(x, y, seed) >> 8) * (1.0f / (1 << 24));
}
