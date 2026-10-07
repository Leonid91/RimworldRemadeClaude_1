using System;
using System.Collections.Generic;
using Godot;
using Remade.Core;
using Remade.Diagnostics;
using Remade.Map;
using Remade.Sim;

namespace Remade.Game;

/// <summary>
/// Procedurally synthesised sound (no audio files): ambience loops that react to the environment — wind follows the
/// planet's wind speed, rain its intensity, the river/lake loop how much water is near the camera, leaf rustle the
/// wind and nearby trees, birds sing by day (less in rain and winter), crickets on warm nights — and positional
/// one-shots for actions (bow, arrow hits, doors, pick-ups, drinking, gathering, footsteps).
/// </summary>
public partial class Audio3D : Node3D
{
    const int Rate = 22050;
    static readonly Dictionary<string, AudioStreamWav> Bank = new();
    static int _ambBus = -1, _fxBus = -1;

    AudioStreamPlayer _wind, _rain, _water, _leaves, _crickets;
    readonly List<AudioStreamPlayer3D> _pool = new();
    readonly List<AudioStreamPlayer> _pool2D = new();
    Vector3 _focus;
    double _birdTimer = 2;
    Rng _rng = new(1234); // mutable struct: must not be readonly or it never advances
    GameSim _sim;

    public static void EnsureBuses()
    {
        if (_ambBus >= 0) return;
        _ambBus = AddBus("Ambience");
        _fxBus = AddBus("Effects");
        ApplyVolumes();
    }

    static int AddBus(string name)
    {
        int idx = AudioServer.GetBusIndex(name);
        if (idx >= 0) return idx;
        AudioServer.AddBus();
        idx = AudioServer.BusCount - 1;
        AudioServer.SetBusName(idx, name);
        AudioServer.SetBusSend(idx, "Master");
        return idx;
    }

    public static void ApplyVolumes()
    {
        AudioServer.SetBusVolumeDb(0, Mathf.LinearToDb(Mathf.Max(0.0001f, Settings.MasterVolume)));
        if (_ambBus >= 0) AudioServer.SetBusVolumeDb(_ambBus, Mathf.LinearToDb(Mathf.Max(0.0001f, Settings.AmbienceVolume)));
        if (_fxBus >= 0) AudioServer.SetBusVolumeDb(_fxBus, Mathf.LinearToDb(Mathf.Max(0.0001f, Settings.EffectsVolume)));
    }

    public Audio3D(GameSim sim)
    {
        _sim = sim;
        EnsureBuses();
        using (Log.Time("Audio synthesis", 1500)) Synthesize();
        _wind = Loop("wind"); _rain = Loop("rain"); _water = Loop("water"); _leaves = Loop("leaves"); _crickets = Loop("crickets");
        for (int i = 0; i < 12; i++)
        {
            var p = new AudioStreamPlayer3D { Bus = "Effects", UnitSize = 6, MaxDistance = 90, AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.InverseDistance };
            AddChild(p);
            _pool.Add(p);
        }
        for (int i = 0; i < 4; i++)
        {
            var p = new AudioStreamPlayer { Bus = "Effects" };
            AddChild(p);
            _pool2D.Add(p);
        }
    }

    /// <summary>
    /// Non-positional one-shot for sounds the player must always hear clearly (a bow shot): the overhead camera is
    /// tens of metres away, so 3D attenuation would bury them. Fades only with the distance from the camera focus.
    /// </summary>
    public void PlayNear(string name, Vector3 at, float volumeDb = 0, float pitch = 1)
    {
        if (!Bank.TryGetValue(name, out var s)) throw new KeyNotFoundException($"No sound '{name}'");
        AudioStreamPlayer free = null;
        foreach (var p in _pool2D) if (!p.Playing) { free = p; break; }
        free ??= _pool2D[_rng.Range(0, _pool2D.Count)];
        float d = new Vector2(at.X - _focus.X, at.Z - _focus.Z).Length();
        free.Stream = s;
        free.VolumeDb = volumeDb - Mathf.Clamp(d * 0.25f, 0f, 24f);
        free.PitchScale = pitch;
        free.Play();
    }

    AudioStreamPlayer Loop(string name)
    {
        var p = new AudioStreamPlayer { Stream = Bank[name], Bus = "Ambience", VolumeDb = -80, Autoplay = true };
        AddChild(p);
        return p;
    }

    // ------------------------------------------------------------------ runtime

    public void Update(float dt, Vector3 focus, Vector3 listener, float daylight)
    {
        _focus = focus;
        var w = _sim.Weather;
        Position = focus;
        float wind = Mathf.Clamp(w.WindSpeed / 14f, 0f, 1f);
        Fade(_wind, 0.12f + wind * 0.75f, dt);
        _wind.PitchScale = 0.85f + wind * 0.35f;
        Fade(_rain, Mathf.Clamp(w.Rain * 1.3f, 0f, 1f) * 0.9f, dt);
        // water and trees near the camera
        var map = _sim.Map;
        int cx = (int)focus.X, cz = (int)focus.Z, water = 0, trees = 0, n = 0;
        for (int y = cz - 14; y <= cz + 14; y += 2)
            for (int x = cx - 14; x <= cx + 14; x += 2)
            {
                if (!map.InBounds(x, y)) continue;
                int i = map.Index(x, y);
                n++;
                if (map.TerrainAt(i).Water) water++;
                if (map.Plants[i] == Plant.Oak) trees++;
            }
        float waterK = n > 0 ? Mathf.Clamp(water / (float)n * 4f, 0f, 1f) : 0f;
        float treeK = n > 0 ? Mathf.Clamp(trees / (float)n * 8f, 0f, 1f) : 0f;
        Fade(_water, waterK * 0.8f, dt);
        Fade(_leaves, treeK * (0.15f + wind * 0.7f) * (GameTime.SeasonAt(_sim.Tick, _sim.Latitude) == Season.Winter ? 0.3f : 1f), dt);
        var season = GameTime.SeasonAt(_sim.Tick, _sim.Latitude);
        float cricketK = (1f - daylight) * Mathf.Clamp((w.Temperature - 8f) / 10f, 0f, 1f) * (1f - w.Rain) * (season == Season.Winter ? 0f : 1f);
        Fade(_crickets, cricketK * 0.5f, dt);

        // birds: random calls around the listener by day
        _birdTimer -= dt;
        if (_birdTimer <= 0)
        {
            float activity = daylight * (1f - w.Rain * 0.85f) * (season == Season.Winter ? 0.25f : 1f) * (0.4f + treeK);
            _birdTimer = activity > 0.05f ? _rng.Range(0.6f, 3.5f) / Mathf.Max(0.2f, activity) : 3;
            if (activity > 0.05f && _rng.Chance(Mathf.Clamp(activity, 0f, 1f)))
            {
                var at = focus + new Vector3(_rng.Range(-25f, 25f), _rng.Range(4f, 10f), _rng.Range(-25f, 25f));
                Play("bird" + _rng.Range(0, 4), at, -10f + _rng.Range(-4f, 2f), _rng.Range(0.92f, 1.1f));
            }
        }
    }

    static void Fade(AudioStreamPlayer p, float vol, float dt)
    {
        float target = vol <= 0.001f ? -80f : Mathf.LinearToDb(vol);
        p.VolumeDb = Mathf.Lerp(p.VolumeDb, target, Mathf.Min(1f, dt * 1.5f));
    }

    public void Play(string name, Vector3 at, float volumeDb = 0, float pitch = 1)
    {
        if (!Bank.TryGetValue(name, out var s)) throw new KeyNotFoundException($"No sound '{name}'");
        AudioStreamPlayer3D free = null;
        foreach (var p in _pool) if (!p.Playing) { free = p; break; }
        free ??= _pool[_rng.Range(0, _pool.Count)];
        free.Stream = s;
        free.GlobalPosition = at;
        free.VolumeDb = volumeDb;
        free.PitchScale = pitch;
        free.Play();
    }

    /// <summary>Plays sounds for simulation events.</summary>
    public void OnEvent(SimEvent e, Func<System.Numerics.Vector2, Vector3> toWorld)
    {
        var at = toWorld(e.Pos) + new Vector3(0, 1f, 0);
        switch (e.Kind)
        {
            case SimEventKind.ArrowFired: PlayNear("bow", at, 0, _rng.Range(0.96f, 1.04f)); break;
            case SimEventKind.ArrowHit: Play("thud", at, 0, _rng.Range(0.9f, 1.1f)); break;
            case SimEventKind.ArrowMissed: Play(e.Text == "ground" ? "thud_soft" : "tok", at, -4, _rng.Range(0.9f, 1.15f)); break;
            case SimEventKind.DoorToggled: Play("door", at, -3, e.Text == "open" ? 1f : 0.85f); break;
            case SimEventKind.ItemPickedUp: Play("rustle", at, -6, 1.2f); break;
            case SimEventKind.Gathered: Play("rustle", at, -3, 0.9f); break;
            case SimEventKind.Drank: Play("splash", at, -5, _rng.Range(0.9f, 1.1f)); break;
            case SimEventKind.Swing: Play("swish", at, -4, _rng.Range(0.9f, 1.1f)); break;
            case SimEventKind.MeleeHit: Play("thud", at, -2, 0.8f); break;
            case SimEventKind.AnimalFled: Play("hooves", at, -6, _rng.Range(0.9f, 1.1f)); break;
            case SimEventKind.AnimalKilled: Play("thud_soft", at, 0, 0.7f); break;
            case SimEventKind.MiningHit: Play("tok", at, -3, _rng.Range(0.45f, 0.6f)); break;
            case SimEventKind.Mined: Play("thud", at, 0, 0.6f); break;
        }
    }

    // ------------------------------------------------------------------ synthesis

    static void Synthesize()
    {
        if (Bank.Count > 0) return;
        var r = new Rng(777);
        Bank["wind"] = LoopWav(Wind(ref r, 8f));
        Bank["rain"] = LoopWav(Rain(ref r, 5f));
        Bank["water"] = LoopWav(Water(ref r, 6f));
        Bank["leaves"] = LoopWav(Leaves(ref r, 6f));
        Bank["crickets"] = LoopWav(Crickets(ref r, 4f));
        for (int i = 0; i < 4; i++) Bank["bird" + i] = Wav(Bird(ref r, i));
        Bank["bow"] = Wav(Bow(ref r));
        Bank["thud"] = Wav(Thud(ref r, 0.25f, 90f));
        Bank["thud_soft"] = Wav(Thud(ref r, 0.18f, 140f));
        Bank["tok"] = Wav(Tok(ref r));
        Bank["door"] = Wav(Creak(ref r));
        Bank["rustle"] = Wav(Rustle(ref r, 0.35f));
        Bank["splash"] = Wav(Splash(ref r));
        Bank["swish"] = Wav(Swish(ref r));
        Bank["hooves"] = Wav(Hooves(ref r));
    }

    static AudioStreamWav Wav(float[] s, bool loop = false)
    {
        var bytes = new byte[s.Length * 2];
        for (int i = 0; i < s.Length; i++)
        {
            short v = (short)Math.Clamp((int)(s[i] * 32767f), -32768, 32767);
            bytes[i * 2] = (byte)(v & 0xff);
            bytes[i * 2 + 1] = (byte)((v >> 8) & 0xff);
        }
        var w = new AudioStreamWav { Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = Rate, Stereo = false, Data = bytes };
        if (loop) { w.LoopMode = AudioStreamWav.LoopModeEnum.Forward; w.LoopBegin = 0; w.LoopEnd = s.Length; }
        return w;
    }

    /// <summary>Crossfades the tail into the head so the loop has no click.</summary>
    static AudioStreamWav LoopWav(float[] s)
    {
        int fade = Rate / 2;
        var o = new float[s.Length - fade];
        for (int i = 0; i < o.Length; i++) o[i] = s[i];
        for (int i = 0; i < fade; i++)
        {
            float t = i / (float)fade;
            o[i] = s[i] * t + s[o.Length + i] * (1 - t);
        }
        Normalize(o, 0.7f);
        return Wav(o, loop: true);
    }

    static void Normalize(float[] s, float peak)
    {
        float m = 1e-6f;
        foreach (float v in s) m = Math.Max(m, Math.Abs(v));
        for (int i = 0; i < s.Length; i++) s[i] *= peak / m;
    }

    static float[] Buf(float seconds) => new float[(int)(seconds * Rate)];

    // one-pole filters
    static float Lp(ref float state, float x, float a) => state += a * (x - state);

    static float[] Wind(ref Rng r, float sec)
    {
        var s = Buf(sec); float lp1 = 0, lp2 = 0, gust = 0;
        for (int i = 0; i < s.Length; i++)
        {
            float t = i / (float)Rate;
            float n = r.NextFloat() * 2 - 1;
            float g = 0.55f + 0.45f * MathF.Sin(t * MathF.Tau / sec * 2f) * MathF.Sin(t * 0.37f * MathF.Tau + 1.3f);
            gust = Lp(ref gust, g, 0.0005f);
            float a = 0.02f + gust * 0.05f;
            float v = Lp(ref lp1, n, a);
            v = Lp(ref lp2, v, a * 1.5f);
            s[i] = v * (0.4f + gust);
        }
        return s;
    }

    static float[] Rain(ref Rng r, float sec)
    {
        var s = Buf(sec); float lp = 0, hp = 0;
        for (int i = 0; i < s.Length; i++)
        {
            float n = r.NextFloat() * 2 - 1;
            float low = Lp(ref lp, n, 0.35f);
            float high = n - Lp(ref hp, n, 0.05f);
            float v = low * 0.5f + high * 0.35f;
            if (r.Chance(0.0015f)) v += (r.NextFloat() * 2 - 1) * 1.2f; // close drops
            s[i] = v;
        }
        return s;
    }

    /// <summary>Flowing water: band-passed noise with a slow, irregular swell (no bubbling blips).</summary>
    static float[] Water(ref Rng r, float sec)
    {
        var s = Buf(sec); float lp = 0, bp = 0, env = 0;
        for (int i = 0; i < s.Length; i++)
        {
            float n = r.NextFloat() * 2 - 1;
            float v = Lp(ref lp, n, 0.12f);
            v -= Lp(ref bp, v, 0.01f);
            float t = i / (float)Rate;
            float target = 0.75f + 0.25f * MathF.Sin(t * MathF.Tau / sec * 2f) * MathF.Sin(t * MathF.Tau / sec * 3f + 1f);
            env = Lp(ref env, target, 0.0005f);
            s[i] = v * env;
        }
        return s;
    }

    static float[] Leaves(ref Rng r, float sec)
    {
        var s = Buf(sec); float hp = 0, env = 0;
        for (int i = 0; i < s.Length; i++)
        {
            float n = r.NextFloat() * 2 - 1;
            float v = n - Lp(ref hp, n, 0.25f);
            float target = 0.3f + 0.7f * (0.5f + 0.5f * MathF.Sin(i / (float)Rate * 1.7f) * MathF.Sin(i / (float)Rate * 0.63f + 2f));
            env = Lp(ref env, target, 0.0003f);
            s[i] = v * env * (0.6f + 0.4f * r.NextFloat());
        }
        return s;
    }

    static float[] Crickets(ref Rng r, float sec)
    {
        var s = Buf(sec);
        double ph = 0;
        for (int i = 0; i < s.Length; i++)
        {
            float t = i / (float)Rate;
            float chirpRate = 3.3f;
            float inChirp = (t * chirpRate) % 1f;
            float env = inChirp < 0.35f ? MathF.Sin(inChirp / 0.35f * MathF.PI) : 0f;
            float pulse = 0.5f + 0.5f * MathF.Sin(t * 30f * MathF.Tau);
            ph += 4400.0 / Rate * Math.Tau;
            s[i] = (float)Math.Sin(ph) * env * pulse * 0.6f;
            // a second, farther cricket
            float t2 = t + 0.13f;
            float in2 = (t2 * 2.9f) % 1f;
            float e2 = in2 < 0.3f ? MathF.Sin(in2 / 0.3f * MathF.PI) : 0f;
            s[i] += MathF.Sin(t * 4950f * MathF.Tau) * e2 * 0.25f;
        }
        return s;
    }

    static float[] Bird(ref Rng r, int species)
    {
        // songs built from frequency-glided, amplitude-shaped notes
        var notes = new List<(float f0, float f1, float dur, float gap)>();
        switch (species)
        {
            case 0: for (int k = 0; k < 6; k++) notes.Add((3200 + k * 80, 4200 + k * 60, 0.05f, 0.04f)); break;              // trill
            case 1: notes.Add((2400, 3600, 0.28f, 0.1f)); notes.Add((3600, 2600, 0.22f, 0.0f)); break;                        // whistle
            case 2: for (int k = 0; k < 3; k++) notes.Add((4800, 2800, 0.09f, 0.07f)); notes.Add((2600, 5200, 0.18f, 0)); break; // chirps + rise
            default: notes.Add((1800, 2100, 0.35f, 0.15f)); notes.Add((1600, 1700, 0.45f, 0)); break;                          // dove-like coo
        }
        float total = 0; foreach (var n in notes) total += n.dur + n.gap;
        var s = Buf(total + 0.1f);
        int pos = 0; double ph = 0;
        foreach (var (f0, f1, dur, gap) in notes)
        {
            int len = (int)(dur * Rate);
            for (int i = 0; i < len && pos + i < s.Length; i++)
            {
                float t = i / (float)len;
                float f = f0 + (f1 - f0) * t + MathF.Sin(t * 40f) * 60f;
                ph += f / Rate * Math.Tau;
                float env = MathF.Sin(t * MathF.PI);
                s[pos + i] = ((float)Math.Sin(ph) + 0.25f * (float)Math.Sin(ph * 2)) * env * 0.5f;
            }
            pos += len + (int)(gap * Rate);
        }
        return s;
    }

    /// <summary>
    /// Bow release: a sharp string slap (a pitch-dropping twang with harmonics), the knock of the limbs as they stop,
    /// and the arrow's short whoosh leaving.
    /// </summary>
    static float[] Bow(ref Rng r)
    {
        var s = Buf(0.75f); float lp = 0, wlp = 0, whp = 0;
        for (int i = 0; i < s.Length; i++)
        {
            float t = i / (float)Rate;
            // twang: fast attack, the string's pitch drops as it settles
            float f = 165f * (1f + 0.9f * MathF.Exp(-t * 45f));
            float twang = (MathF.Sin(t * f * MathF.Tau) + 0.6f * MathF.Sin(t * f * 2.02f * MathF.Tau) + 0.35f * MathF.Sin(t * f * 3.05f * MathF.Tau)
                           + 0.2f * MathF.Sin(t * f * 4.1f * MathF.Tau)) * MathF.Exp(-t * 7f) * MathF.Min(1f, t * 900f);
            // slap: a click of broadband noise in the first milliseconds
            float slap = Lp(ref lp, r.NextFloat() * 2 - 1, 0.5f) * MathF.Exp(-t * 90f) * 2.2f;
            // limb knock: a low, short wooden thump
            float knock = MathF.Sin(t * 95f * MathF.Tau) * MathF.Exp(-t * 35f) * 0.9f;
            // whoosh: band-passed noise swelling and fading over a quarter of a second
            float n = r.NextFloat() * 2 - 1;
            float w = Lp(ref wlp, n, 0.18f);
            w -= Lp(ref whp, w, 0.04f);
            float wEnv = MathF.Max(0f, MathF.Sin(MathF.Min(1f, MathF.Max(0f, (t - 0.02f) / 0.3f)) * MathF.PI));
            s[i] = twang * 0.7f + slap + knock + w * wEnv * 1.6f;
        }
        Normalize(s, 0.95f);
        return s;
    }

    static float[] Thud(ref Rng r, float dur, float freq)
    {
        var s = Buf(dur); float lp = 0;
        for (int i = 0; i < s.Length; i++)
        {
            float t = i / (float)Rate;
            float env = MathF.Exp(-t * 22f);
            float n = Lp(ref lp, r.NextFloat() * 2 - 1, 0.08f);
            s[i] = (MathF.Sin(t * freq * MathF.Tau * (1 + MathF.Exp(-t * 40))) * 0.7f + n * 2f) * env;
        }
        Normalize(s, 0.8f);
        return s;
    }

    static float[] Tok(ref Rng r)
    {
        var s = Buf(0.15f);
        for (int i = 0; i < s.Length; i++)
        {
            float t = i / (float)Rate;
            s[i] = (MathF.Sin(t * 900f * MathF.Tau) + 0.5f * MathF.Sin(t * 1730f * MathF.Tau)) * MathF.Exp(-t * 45f) + (r.NextFloat() * 2 - 1) * MathF.Exp(-t * 200f) * 0.5f;
        }
        Normalize(s, 0.6f);
        return s;
    }

    static float[] Creak(ref Rng r)
    {
        var s = Buf(0.7f); float lp = 0; double ph = 0;
        for (int i = 0; i < s.Length; i++)
        {
            float t = i / (float)Rate;
            float f = 180f + 90f * MathF.Sin(t * 5f) + 40f * MathF.Sin(t * 23f);
            ph += f / Rate * Math.Tau;
            float saw = (float)(ph / Math.Tau % 1.0) * 2 - 1;
            float stick = (MathF.Sin(t * 60f * MathF.Tau) > 0.6f) ? 1f : 0.5f;
            float v = Lp(ref lp, saw * stick, 0.25f);
            float env = MathF.Min(1f, t * 20f) * MathF.Exp(-t * 3.5f);
            s[i] = v * env;
        }
        Normalize(s, 0.5f);
        return s;
    }

    static float[] Rustle(ref Rng r, float dur)
    {
        var s = Buf(dur); float hp = 0;
        for (int i = 0; i < s.Length; i++)
        {
            float t = i / (float)Rate;
            float n = r.NextFloat() * 2 - 1;
            float v = n - Lp(ref hp, n, 0.2f);
            float env = MathF.Sin(MathF.PI * t / dur) * (0.6f + 0.4f * MathF.Sin(t * 70f));
            s[i] = v * env;
        }
        Normalize(s, 0.5f);
        return s;
    }

    static float[] Splash(ref Rng r)
    {
        var s = Buf(0.5f); float lp = 0; double ph = 0;
        for (int i = 0; i < s.Length; i++)
        {
            float t = i / (float)Rate;
            float n = Lp(ref lp, r.NextFloat() * 2 - 1, 0.2f);
            ph += (300 + 900 * t) / Rate * Math.Tau;
            s[i] = n * MathF.Exp(-t * 9f) + (float)Math.Sin(ph) * MathF.Exp(-t * 14f) * 0.3f;
        }
        Normalize(s, 0.5f);
        return s;
    }

    static float[] Swish(ref Rng r)
    {
        var s = Buf(0.25f); float bp = 0, lp = 0;
        for (int i = 0; i < s.Length; i++)
        {
            float t = i / (float)Rate;
            float n = r.NextFloat() * 2 - 1;
            float a = 0.05f + 0.4f * MathF.Sin(MathF.PI * t / 0.25f);
            float v = Lp(ref lp, n, a);
            v -= Lp(ref bp, v, 0.02f);
            s[i] = v * MathF.Sin(MathF.PI * t / 0.25f);
        }
        Normalize(s, 0.5f);
        return s;
    }

    static float[] Hooves(ref Rng r)
    {
        var s = Buf(0.9f);
        for (int k = 0; k < 6; k++)
        {
            int start = (int)((k * 0.14f + r.Range(0f, 0.03f)) * Rate);
            for (int i = 0; i < Rate * 0.06f && start + i < s.Length; i++)
            {
                float t = i / (float)Rate;
                s[start + i] += (MathF.Sin(t * 160f * MathF.Tau) * 0.6f + (r.NextFloat() * 2 - 1) * 0.4f) * MathF.Exp(-t * 60f);
            }
        }
        Normalize(s, 0.5f);
        return s;
    }
}
