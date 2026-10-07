using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using Remade.Core;
using Remade.Diagnostics;
using Remade.Map;
using Remade.Pawns;
using Remade.Sim;
using Remade.Things;
using Remade.World;

namespace Remade.Save;

public sealed class SaveFormatException : Exception
{
    public SaveFormatException(string message, Exception inner = null) : base(message, inner) { }
}

/// <summary>Uncompressed save header, readable quickly for the load menu.</summary>
public sealed class SaveHeader
{
    public int Version;
    public string ColonyName;
    public DateTime SavedUtc;
    public long Tick;
    public int WorldSeed, Frequency, Tile, MapSize, MapSeed;
    public string Colonists;
    public float Latitude;
    public string Path;
    public long FileBytes;
}

/// <summary>
/// Binary save files: "RRSV" magic, version, uncompressed header, then a Brotli-compressed body holding the world
/// parameters, climate state, the map's mutable and generated layers and every entity. The planet geography is
/// regenerated from its seed (deterministic), so only its dynamic state is stored. Any inconsistency while reading
/// throws <see cref="SaveFormatException"/> with the section and offset; nothing is silently repaired.
/// </summary>
public static class SaveGame
{
    const uint Magic = 0x56535252; // "RRSV"
    /// <summary>2: single colonist inventory + hands slot, trait set with stat effects. Version 1 saves are not readable.</summary>
    public const int CurrentVersion = 3;

    // ------------------------------------------------------------------ write

    public static void Write(GameSim sim, string path)
    {
        using var corr = Log.Correlate($"Save {path}");
        using var _t = Log.Time("SaveGame.Write", 1500);
        string tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var w = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(Magic);
            w.Write(CurrentVersion);
            WriteHeader(w, sim);
            w.Flush();
            using var br = new BrotliStream(fs, CompressionLevel.Fastest, leaveOpen: true);
            using var bw = new BinaryWriter(br, Encoding.UTF8, leaveOpen: true);
            WriteBody(bw, sim);
            bw.Write(0xC0FFEEu); // end marker
        }
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
        Log.Info($"Saved game to {path} ({new FileInfo(path).Length / 1024} KiB) at tick {sim.Tick}");
    }

    static void WriteHeader(BinaryWriter w, GameSim sim)
    {
        w.Write(sim.ColonyName);
        w.Write(DateTime.UtcNow.Ticks);
        w.Write(sim.Tick);
        var p = sim.Planet.Params;
        w.Write(p.Seed); w.Write(p.Frequency);
        w.Write(sim.Map.PlanetTile); w.Write(sim.Map.Width); w.Write(sim.Map.Seed);
        var names = new List<string>();
        foreach (var pawn in sim.Pawns) names.Add(pawn.Name);
        w.Write(string.Join(", ", names));
        w.Write(sim.Latitude);
    }

    static void WriteBody(BinaryWriter w, GameSim sim)
    {
        var p = sim.Planet.Params;
        Section(w, "world");
        w.Write(p.Seed); w.Write(p.Frequency); w.Write(p.OceanCoverage); w.Write(p.TemperatureOffset); w.Write(p.RainfallScale);
        sim.Planet.Climate.Write(w);

        Section(w, "map");
        var m = sim.Map;
        w.Write(m.Width); w.Write(m.Height); w.Write(m.PlanetTile); w.Write(m.Seed);
        WriteBytes(w, MemoryCast(m.Terrain));
        WriteBytes(w, MemoryCast(m.Buildings));
        WriteArray(w, m.BuildingHp);
        WriteBytes(w, MemoryCast(m.Plants));
        WriteBytes(w, m.PlantVariant);
        WriteBytes(w, m.PlantGrowth);
        WriteBytes(w, m.Berries);
        WriteBytes(w, m.Grass);
        WriteArray(w, m.Ground);
        WriteArray(w, m.RockHeight);
        w.Write(m.DoorOpen.Count);
        foreach (var kv in m.DoorOpen) { w.Write(kv.Key); w.Write(kv.Value); }

        Section(w, "sim");
        w.Write(sim.ColonyName);
        w.Write(sim.Tick);
        w.Write(sim.SpeedIndex);
        sim.Rng.GetState(out ulong a, out ulong b, out ulong c, out ulong d);
        w.Write(a); w.Write(b); w.Write(c); w.Write(d);
        w.Write(sim.PeekNextId);
        sim.Weather.Write(w);

        Section(w, "herds");
        w.Write(sim.Herds.Count);
        foreach (var h in sim.Herds) { w.Write(h.Id); WriteV(w, h.Anchor); w.Write(h.MoveTimer); }

        Section(w, "animals");
        int alive = 0;
        foreach (var an in sim.Animals) if (an.DeathTick != -2) alive++;
        w.Write(alive);
        foreach (var an in sim.Animals)
        {
            if (an.DeathTick == -2) continue;
            w.Write(an.Id); w.Write(an.Kind); WriteV(w, an.Position); w.Write(an.Facing); w.Write((byte)an.State);
            w.Write(an.StateTimer); w.Write(an.Herd); w.Write(an.Male); w.Write(an.Size); WriteStuck(w, an.Embedded); w.Write(an.DeathTick);
            WriteHealth(w, an.Health);
            w.Write(an.ThinkTimer); w.Write(an.AnimTime); w.Write(an.Far);
            WritePath(w, an.Path, an.PathIndex);
        }

        Section(w, "items");
        w.Write(sim.Items.Count);
        foreach (var it in sim.Items) { WriteItem(w, it); WriteV(w, it.Position); w.Write(it.Rotation); w.Write(it.StuckZ); WriteV3(w, it.StuckDir); }

        Section(w, "pawns");
        w.Write(sim.Pawns.Count);
        foreach (var pawn in sim.Pawns) { WritePawn(w, pawn); WritePawnRuntime(w, pawn); }

        Section(w, "runtime");
        w.Write(sim.Projectiles.Count);
        foreach (var pr in sim.Projectiles)
        {
            WriteV3(w, pr.Start); WriteV3(w, pr.Pos); WriteV3(w, pr.Vel);
            w.Write(pr.Traveled); w.Write(pr.Age); w.Write(pr.Damage); w.Write(pr.Shooter?.Id ?? -1);
        }
        var doors = sim.AutoDoorsSnapshot();
        w.Write(doors.Count);
        foreach (var kv in doors) { w.Write(kv.Key); w.Write(kv.Value); }
        // the RNG goes last so that nothing during loading can consume it
        sim.Rng.GetState(out ulong r0, out ulong r1, out ulong r2, out ulong r3);
        w.Write(r0); w.Write(r1); w.Write(r2); w.Write(r3);
    }

    static void WritePath(BinaryWriter w, List<Vector2> path, int index)
    {
        w.Write(path.Count); w.Write(index);
        foreach (var v in path) WriteV(w, v);
    }

    static void WritePawnRuntime(BinaryWriter w, Pawn p)
    {
        w.Write(p.WeaponCooldown); w.Write((byte)p.Anim); w.Write(p.AnimTime); w.Write(p.Aiming); WriteV(w, p.AimDir);
        WriteStuck(w, p.Embedded);
        w.Write(p.LastJobLabel ?? "");
        WritePath(w, p.Path, p.PathIndex);
        var j = p.Job;
        w.Write(j != null);
        if (j == null) return;
        w.Write((byte)j.Kind); w.Write(j.TargetCell); w.Write(j.TargetItem?.Id ?? -1); w.Write(j.TargetAnimal?.Id ?? -1);
        WriteV(w, j.TargetPos); w.Write(j.Stage); w.Write(j.Timer); w.Write(j.WorkTicks); w.Write(j.Forced); w.Write(j.EatAfter); w.Write(j.Label ?? "");
    }

    public static void WritePawn(BinaryWriter w, Pawn p)
    {
        w.Write(p.Id); w.Write(p.FirstName); w.Write(p.NickName ?? ""); w.Write(p.LastName);
        w.Write((byte)p.Sex); w.Write(p.BioAge); w.Write(p.ChronoAge); w.Write(p.BodyMassKg); w.Write(p.HeightM);
        w.Write(p.SkinColor); w.Write(p.HairColor); w.Write(p.HairStyle);
        w.Write(p.Traits.Count);
        foreach (var t in p.Traits) w.Write(t.Id);
        w.Write(p.Skills);
        WriteHealth(w, p.Health);
        w.Write(p.Needs.Food); w.Write(p.Needs.Thirst); w.Write(p.Needs.Rest);
        w.Write(p.Apparel.Count);
        foreach (var a in p.Apparel) WriteItem(w, a);
        w.Write(p.Held != null);
        if (p.Held != null) WriteItem(w, p.Held);
        w.Write(p.Inventory.W); w.Write(p.Inventory.H);
        w.Write(p.Inventory.Entries.Count);
        foreach (var e in p.Inventory.Entries) { w.Write(e.X); w.Write(e.Y); w.Write(e.Rotated); WriteItem(w, e.Item); }
        WriteV(w, p.Position); w.Write(p.Facing); w.Write((byte)p.Mode);
    }

    static void WriteItem(BinaryWriter w, Item it)
    {
        w.Write(it.Id); w.Write(it.Def.Id); w.Write(it.Count);
        w.Write(it.Contents != null);
        if (it.Contents == null) return;
        w.Write(it.Contents.Entries.Count);
        foreach (var e in it.Contents.Entries)
        {
            w.Write(e.X); w.Write(e.Y); w.Write(e.Rotated);
            WriteItem(w, e.Item);
        }
    }

    static void WriteHealth(BinaryWriter w, Health h)
    {
        w.Write(h.Body.Name);
        w.Write(h.Hp.Length);
        foreach (float v in h.Hp) w.Write(v);
        w.Write(h.Injuries.Count);
        foreach (var j in h.Injuries) { w.Write(j.Part); w.Write(j.Damage); w.Write(j.BleedPerHour); w.Write(j.Tick); }
        w.Write(h.BloodLoss);
    }

    static void Section(BinaryWriter w, string name) => w.Write("§" + name);
    static void WriteV(BinaryWriter w, Vector2 v) { w.Write(v.X); w.Write(v.Y); }
    static void WriteV3(BinaryWriter w, Vector3 v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }

    static void WriteStuck(BinaryWriter w, List<StuckArrow> list)
    {
        w.Write(list.Count);
        foreach (var s in list) { WriteV3(w, s.Local); WriteV3(w, s.Dir); w.Write(s.Part); }
    }

    static void ReadStuck(BinaryReader r, List<StuckArrow> list, int parts, string who)
    {
        int n = r.ReadInt32();
        if (n < 0 || n > 200) throw new SaveFormatException($"{who}: {n} stuck arrows");
        for (int i = 0; i < n; i++)
        {
            var s = new StuckArrow { Local = ReadV3(r), Dir = ReadV3(r), Part = r.ReadInt32() };
            if (s.Part < 0 || s.Part >= parts) throw new SaveFormatException($"{who}: stuck arrow in part {s.Part} of {parts}");
            list.Add(s);
        }
    }
    static void WriteBytes(BinaryWriter w, byte[] a) { w.Write(a.Length); w.Write(a); }
    static void WriteArray(BinaryWriter w, ushort[] a)
    {
        w.Write(a.Length);
        w.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(a.AsSpan()));
    }
    static void WriteArray(BinaryWriter w, float[] a)
    {
        w.Write(a.Length);
        w.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(a.AsSpan()));
    }
    static byte[] MemoryCast<T>(T[] a) where T : unmanaged
        => System.Runtime.InteropServices.MemoryMarshal.AsBytes(a.AsSpan()).ToArray();

    // ------------------------------------------------------------------ read

    public static SaveHeader ReadHeader(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var r = new BinaryReader(fs, Encoding.UTF8);
        var h = ReadHeaderInternal(r, path);
        h.FileBytes = fs.Length;
        return h;
    }

    static SaveHeader ReadHeaderInternal(BinaryReader r, string path)
    {
        try
        {
            uint magic = r.ReadUInt32();
            if (magic != Magic) throw new SaveFormatException($"{path}: not a save file (magic 0x{magic:X8})");
            int version = r.ReadInt32();
            if (version != CurrentVersion) throw new SaveFormatException($"{path}: save version {version} is not supported by this build (it reads version {CurrentVersion}); saves from earlier versions cannot be loaded");
            return new SaveHeader
            {
                Version = version, ColonyName = r.ReadString(), SavedUtc = new DateTime(r.ReadInt64(), DateTimeKind.Utc), Tick = r.ReadInt64(),
                WorldSeed = r.ReadInt32(), Frequency = r.ReadInt32(), Tile = r.ReadInt32(), MapSize = r.ReadInt32(), MapSeed = r.ReadInt32(),
                Colonists = r.ReadString(), Latitude = r.ReadSingle(), Path = path,
            };
        }
        catch (EndOfStreamException ex) { throw new SaveFormatException($"{path}: truncated header", ex); }
    }

    /// <summary>Loads a save. The planet is regenerated from its seed (or reused if the caller passes a matching one).</summary>
    public static GameSim Read(string path, Planet reusePlanet = null)
    {
        using var corr = Log.Correlate($"Load {path}");
        using var _t = Log.Time("SaveGame.Read", 3000);
        Log.GameState = "loading";
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var hr = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);
        var header = ReadHeaderInternal(hr, path);
        Log.Info($"Loading '{header.ColonyName}' v{header.Version}: tick {header.Tick}, world seed {header.WorldSeed}, tile {header.Tile}, map {header.MapSize}");
        using var br = new BrotliStream(fs, CompressionMode.Decompress);
        using var r = new BinaryReader(br, Encoding.UTF8);
        string section = "world";
        try
        {
            Expect(r, "world");
            var wp = new WorldParams { Seed = r.ReadInt32(), Frequency = r.ReadInt32(), OceanCoverage = r.ReadSingle(), TemperatureOffset = r.ReadSingle(), RainfallScale = r.ReadSingle() };
            Planet planet = reusePlanet != null && SameParams(reusePlanet.Params, wp) ? reusePlanet : new Planet(wp);
            planet.Climate.Read(r);

            section = "map"; Expect(r, "map");
            int w = r.ReadInt32(), h = r.ReadInt32(), tile = r.ReadInt32(), seed = r.ReadInt32();
            if (tile < 0 || tile >= planet.TileCount) throw new SaveFormatException($"map tile {tile} outside planet (0..{planet.TileCount - 1})");
            var m = new LocalMap(w, h, tile, seed);
            ReadInto(r, m.Terrain, "terrain");
            ReadInto(r, m.Buildings, "buildings");
            ReadUShorts(r, m.BuildingHp, "building hp");
            ReadInto(r, m.Plants, "plants");
            ReadBytes(r, m.PlantVariant, "plant variant");
            ReadBytes(r, m.PlantGrowth, "plant growth");
            ReadBytes(r, m.Berries, "berries");
            ReadBytes(r, m.Grass, "grass");
            ReadFloats(r, m.Ground, "ground");
            ReadFloats(r, m.RockHeight, "rock height");
            int doors = r.ReadInt32();
            for (int i = 0; i < doors; i++)
            {
                int cell = r.ReadInt32(); bool open = r.ReadBoolean();
                if ((uint)cell >= (uint)m.CellCount || m.Buildings[cell] != Building.Door)
                    throw new SaveFormatException($"door entry {i} at cell {cell} is not a door ({((uint)cell < (uint)m.CellCount ? m.Buildings[cell].ToString() : "out of range")})");
                m.DoorOpen[cell] = open;
            }
            for (int i = 0; i < m.CellCount; i++)
            {
                if ((byte)m.Terrain[i] >= (byte)Terrain.Count) throw new SaveFormatException($"cell {i}: invalid terrain {(byte)m.Terrain[i]}");
                if (m.Buildings[i] == Building.Door && !m.DoorOpen.ContainsKey(i)) throw new SaveFormatException($"cell {i}: door without state");
            }

            section = "sim"; Expect(r, "sim");
            var sim = new GameSim(planet, m, seed);
            sim.ColonyName = r.ReadString();
            long tick = r.ReadInt64();
            sim.SetTick(tick);
            sim.SpeedIndex = Math.Clamp(r.ReadInt32(), 0, GameTime.SpeedMultipliers.Length - 1);
            sim.Rng = Rng.FromState(r.ReadUInt64(), r.ReadUInt64(), r.ReadUInt64(), r.ReadUInt64());
            int nextId = r.ReadInt32();
            sim.Weather.Read(r);

            section = "herds"; Expect(r, "herds");
            int herds = r.ReadInt32();
            for (int i = 0; i < herds; i++) sim.Herds.Add(new Herd { Id = r.ReadInt32(), Anchor = ReadV(r), MoveTimer = r.ReadInt32() });

            section = "animals"; Expect(r, "animals");
            int animals = r.ReadInt32();
            for (int i = 0; i < animals; i++)
            {
                var a = new Animal
                {
                    Id = r.ReadInt32(), Kind = r.ReadString(), Position = ReadV(r), Facing = r.ReadSingle(), State = (AnimalState)r.ReadByte(),
                    StateTimer = r.ReadInt32(), Herd = r.ReadInt32(), Male = r.ReadBoolean(), Size = r.ReadSingle(),
                };
                ReadStuck(r, a.Embedded, a.Health.Body.Count, $"animal {a.Id}");
                a.DeathTick = r.ReadInt64();
                ReadHealth(r, a.Health, $"animal {a.Id}");
                a.ThinkTimer = r.ReadInt32(); a.AnimTime = r.ReadSingle(); a.Far = r.ReadBoolean();
                a.PathIndex = ReadPath(r, a.Path);
                if (a.Herd < 0 || a.Herd >= sim.Herds.Count) throw new SaveFormatException($"animal {a.Id} references herd {a.Herd} of {sim.Herds.Count}");
                if (a.Dead != (a.State == AnimalState.Dead)) throw new SaveFormatException($"animal {a.Id}: state {a.State} but dead={a.Dead}");
                sim.Animals.Add(a);
            }

            section = "items"; Expect(r, "items");
            int items = r.ReadInt32();
            for (int i = 0; i < items; i++)
            {
                var it = ReadItem(r);
                var pos = ReadV(r);
                float rot = r.ReadSingle();
                float stuckZ = r.ReadSingle();
                var stuckDir = ReadV3(r);
                sim.PlaceOnGround(it, pos, exact: !float.IsNaN(stuckZ));
                it.Rotation = rot;
                it.StuckZ = stuckZ;
                it.StuckDir = stuckDir;
                if (it.Stuck && (it.Def != Defs.Arrow || it.Count != 1)) throw new SaveFormatException($"item {it}: only single arrows can be stuck");
            }

            section = "pawns"; Expect(r, "pawns");
            int pawns = r.ReadInt32();
            var jobRefs = new List<(Pawn pawn, int item, int animal)>();
            for (int i = 0; i < pawns; i++)
            {
                var pw = ReadPawn(r);
                var (item, animal) = ReadPawnRuntime(r, pw);
                jobRefs.Add((pw, item, animal));
                sim.Pawns.Add(pw);
            }
            foreach (var (pw, itemId, animalId) in jobRefs)
            {
                if (pw.Job == null) continue;
                if (itemId >= 0)
                {
                    pw.Job.TargetItem = sim.Items.Find(x => x.Id == itemId) ?? FindCarried(pw, itemId)
                        ?? throw new SaveFormatException($"{pw.Name}: job target item {itemId} not found");
                }
                if (animalId >= 0)
                    pw.Job.TargetAnimal = sim.Animals.Find(x => x.Id == animalId) ?? throw new SaveFormatException($"{pw.Name}: job target animal {animalId} not found");
            }

            section = "runtime"; Expect(r, "runtime");
            int projectiles = r.ReadInt32();
            for (int i = 0; i < projectiles; i++)
            {
                var pr = new Projectile { Start = ReadV3(r), Pos = ReadV3(r), Vel = ReadV3(r), Traveled = r.ReadSingle(), Age = r.ReadInt32(), Damage = r.ReadSingle() };
                int shooter = r.ReadInt32();
                pr.Shooter = shooter < 0 ? null : sim.Pawns.Find(x => x.Id == shooter) ?? throw new SaveFormatException($"projectile shooter {shooter} not found");
                sim.Projectiles.Add(pr);
            }
            int autoDoors = r.ReadInt32();
            for (int i = 0; i < autoDoors; i++)
            {
                int cell = r.ReadInt32(), timer = r.ReadInt32();
                if (!m.DoorOpen.ContainsKey(cell)) throw new SaveFormatException($"auto-door {cell} is not a door");
                sim.RestoreAutoDoor(cell, timer);
            }
            sim.Rng = Rng.FromState(r.ReadUInt64(), r.ReadUInt64(), r.ReadUInt64(), r.ReadUInt64());
            int direct = 0;
            foreach (var pw in sim.Pawns) if (pw.Mode == ControlMode.Direct) direct++;
            if (direct > 1) throw new SaveFormatException($"{direct} pawns under direct control (max 1)");

            section = "end";
            uint end = r.ReadUInt32();
            if (end != 0xC0FFEEu) throw new SaveFormatException($"missing end marker (0x{end:X})");
            sim.SetNextId(nextId);
            sim.Events.Clear();
            m.ClearChanges();
            Log.GameState = "playing";
            Log.Info($"Loaded '{sim.ColonyName}': {sim.Pawns.Count} colonists, {sim.Animals.Count} animals, {sim.Items.Count} items");
            return sim;
        }
        catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException or ArgumentException or KeyNotFoundException or InvalidOperationException)
        {
            throw new SaveFormatException($"{path}: corrupt save in section '{section}': {ex.Message}", ex);
        }
    }

    static Item FindCarried(Pawn p, int id)
    {
        if (p.Held?.Id == id) return p.Held;
        foreach (var e in p.Inventory.Entries) if (e.Item.Id == id) return e.Item;
        return null;
    }

    static int ReadPath(BinaryReader r, List<Vector2> path)
    {
        int n = r.ReadInt32(), idx = r.ReadInt32();
        if (n < 0 || n > 1_000_000 || idx < 0 || idx > n) throw new InvalidDataException($"path of {n} points, index {idx}");
        path.Clear();
        for (int i = 0; i < n; i++) path.Add(ReadV(r));
        return idx;
    }

    static (int item, int animal) ReadPawnRuntime(BinaryReader r, Pawn p)
    {
        p.WeaponCooldown = r.ReadInt32(); p.Anim = (PawnAnim)r.ReadByte(); p.AnimTime = r.ReadSingle(); p.Aiming = r.ReadBoolean(); p.AimDir = ReadV(r);
        ReadStuck(r, p.Embedded, p.Health.Body.Count, p.Name);
        p.LastJobLabel = r.ReadString();
        p.PathIndex = ReadPath(r, p.Path);
        if (!r.ReadBoolean()) return (-1, -1);
        var j = new Job { Kind = (JobKind)r.ReadByte(), TargetCell = r.ReadInt32() };
        int item = r.ReadInt32(), animal = r.ReadInt32();
        j.TargetPos = ReadV(r); j.Stage = r.ReadInt32(); j.Timer = r.ReadInt32(); j.WorkTicks = r.ReadInt32();
        j.Forced = r.ReadBoolean(); j.EatAfter = r.ReadBoolean(); j.Label = r.ReadString();
        if (!Enum.IsDefined(j.Kind)) throw new InvalidDataException($"{p.Name}: unknown job kind {(byte)j.Kind}");
        p.Job = j;
        return (item, animal);
    }

    static bool SameParams(WorldParams a, WorldParams b)
        => a.Seed == b.Seed && a.Frequency == b.Frequency && a.OceanCoverage == b.OceanCoverage && a.TemperatureOffset == b.TemperatureOffset && a.RainfallScale == b.RainfallScale;

    public static Pawn ReadPawn(BinaryReader r)
    {
        var p = new Pawn
        {
            Id = r.ReadInt32(), FirstName = r.ReadString(), NickName = r.ReadString(), LastName = r.ReadString(),
            Sex = (Sex)r.ReadByte(), BioAge = r.ReadInt32(), ChronoAge = r.ReadInt32(), BodyMassKg = r.ReadSingle(), HeightM = r.ReadSingle(),
            SkinColor = r.ReadUInt32(), HairColor = r.ReadUInt32(), HairStyle = r.ReadByte(),
        };
        if (p.BodyMassKg < 20 || p.BodyMassKg > 300) throw new InvalidDataException($"pawn {p.Id}: body mass {p.BodyMassKg}");
        int traits = r.ReadInt32();
        for (int i = 0; i < traits; i++) p.Traits.Add(Traits.Get(r.ReadString()));
        var skills = r.ReadBytes(p.Skills.Length);
        if (skills.Length != p.Skills.Length) throw new EndOfStreamException("skills");
        for (int i = 0; i < skills.Length; i++)
        {
            if (skills[i] > 20) throw new InvalidDataException($"pawn {p.Id}: skill {(SkillId)i} = {skills[i]}");
            p.Skills[i] = skills[i];
        }
        ReadHealth(r, p.Health, $"pawn {p.Id}");
        p.Needs.Food = r.ReadSingle(); p.Needs.Thirst = r.ReadSingle(); p.Needs.Rest = r.ReadSingle();
        int apparel = r.ReadInt32();
        for (int i = 0; i < apparel; i++) p.Wear(ReadItem(r));
        if (r.ReadBoolean()) p.Held = ReadItem(r);
        int iw = r.ReadInt32(), ih = r.ReadInt32();
        if (iw < 1 || iw > 20 || ih < 1 || ih > 20) throw new InvalidDataException($"pawn {p.Id}: inventory {iw}x{ih}");
        p.CreateInventory(iw, ih);
        int n = r.ReadInt32();
        for (int i = 0; i < n; i++)
        {
            int x = r.ReadInt32(), y = r.ReadInt32(); bool rot = r.ReadBoolean();
            var it = ReadItem(r);
            var e = new GridEntry { Item = it, X = x, Y = y, Rotated = rot };
            if (!p.Inventory.Fits(x, y, e.W, e.H)) throw new InvalidDataException($"pawn {p.Id}: {it} does not fit at {x},{y}");
            p.Inventory.Place(it, x, y, rot);
        }
        p.Position = ReadV(r); p.Facing = r.ReadSingle(); p.Mode = (ControlMode)r.ReadByte();
        return p;
    }

    static Item ReadItem(BinaryReader r)
    {
        int id = r.ReadInt32();
        var def = Defs.Get(r.ReadString());
        int count = r.ReadInt32();
        var it = new Item(id, def, count);
        bool hasContents = r.ReadBoolean();
        if (hasContents != (it.Contents != null)) throw new InvalidDataException($"item {id} ({def.Id}): container flag mismatch");
        if (!hasContents) return it;
        int n = r.ReadInt32();
        for (int i = 0; i < n; i++)
        {
            int x = r.ReadInt32(), y = r.ReadInt32(); bool rot = r.ReadBoolean();
            var inner = ReadItem(r);
            var e = new GridEntry { Item = inner, X = x, Y = y, Rotated = rot };
            if (!it.Contents.Fits(x, y, e.W, e.H)) throw new InvalidDataException($"{inner} does not fit at {x},{y} in {it.Contents.Label}");
            it.Contents.Place(inner, x, y, rot);
        }
        return it;
    }

    static void ReadHealth(BinaryReader r, Health h, string who)
    {
        string body = r.ReadString();
        if (body != h.Body.Name) throw new InvalidDataException($"{who}: body '{body}' but expected '{h.Body.Name}'");
        int n = r.ReadInt32();
        if (n != h.Hp.Length) throw new InvalidDataException($"{who}: {n} body parts, expected {h.Hp.Length}");
        for (int i = 0; i < n; i++) h.Hp[i] = r.ReadSingle();
        int inj = r.ReadInt32();
        for (int i = 0; i < inj; i++)
        {
            var j = new Injury { Part = r.ReadInt32(), Damage = r.ReadSingle(), BleedPerHour = r.ReadSingle(), Tick = r.ReadInt64() };
            if ((uint)j.Part >= (uint)n) throw new InvalidDataException($"{who}: injury on part {j.Part}");
            h.Injuries.Add(j);
        }
        h.BloodLoss = r.ReadSingle();
        h.Revalidate();
    }

    static void Expect(BinaryReader r, string name)
    {
        string s = r.ReadString();
        if (s != "§" + name) throw new InvalidDataException($"expected section '{name}' but found '{s}'");
    }

    static Vector2 ReadV(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle());
    static Vector3 ReadV3(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

    static void ReadBytes(BinaryReader r, byte[] into, string what)
    {
        int n = r.ReadInt32();
        if (n != into.Length) throw new InvalidDataException($"{what}: {n} entries, expected {into.Length}");
        ReadFully(r, into, what);
    }

    static void ReadInto<T>(BinaryReader r, T[] into, string what) where T : unmanaged
    {
        var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(into.AsSpan());
        int n = r.ReadInt32();
        if (n != bytes.Length) throw new InvalidDataException($"{what}: {n} bytes, expected {bytes.Length}");
        ReadFully(r, bytes, what);
    }

    static void ReadUShorts(BinaryReader r, ushort[] into, string what)
    {
        int n = r.ReadInt32();
        if (n != into.Length) throw new InvalidDataException($"{what}: {n} entries, expected {into.Length}");
        ReadFully(r, System.Runtime.InteropServices.MemoryMarshal.AsBytes(into.AsSpan()), what);
    }

    static void ReadFloats(BinaryReader r, float[] into, string what)
    {
        int n = r.ReadInt32();
        if (n != into.Length) throw new InvalidDataException($"{what}: {n} entries, expected {into.Length}");
        ReadFully(r, System.Runtime.InteropServices.MemoryMarshal.AsBytes(into.AsSpan()), what);
        foreach (float f in into) if (!float.IsFinite(f)) throw new InvalidDataException($"{what}: non-finite value");
    }

    static void ReadFully(BinaryReader r, Span<byte> dst, string what)
    {
        while (dst.Length > 0)
        {
            int k = r.BaseStream.Read(dst);
            if (k == 0) throw new EndOfStreamException(what);
            dst = dst[k..];
        }
    }
}
