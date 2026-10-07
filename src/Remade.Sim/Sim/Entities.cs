using System;
using System.Collections.Generic;
using System.Numerics;
using Remade.Pawns;
using Remade.Things;

namespace Remade.Pawns
{
    public enum JobKind : byte { Wait, Goto, Wander, PickUp, Gather, Drink, ToggleDoor, Eat, Sleep, Hunt, Melee, Mine }

    /// <summary>What a pawn is doing. Stage 0 = moving to the work spot, stage 1 = working.</summary>
    public sealed class Job
    {
        public JobKind Kind;
        public int TargetCell = -1;
        public Item TargetItem;
        public Sim.Animal TargetAnimal;
        public Vector2 TargetPos;
        public int Stage;
        public int Timer;
        public int WorkTicks;
        /// <summary>Ordered by the player (not chosen by the AI).</summary>
        public bool Forced;
        /// <summary>Pick-up job that eats the food right after picking it up.</summary>
        public bool EatAfter;
        public string Label;

        public override string ToString() => Label ?? Kind.ToString();
    }
}

namespace Remade.Sim
{
    public enum AnimalState : byte { Graze, Wander, Flee, Rest, Dead }

    public sealed class Animal
    {
        public int Id;
        public string Kind = "Deer";
        public readonly Health Health = new(BodyDef.Deer);
        public Vector2 Position;
        public float Facing;
        public Vector2 Velocity;
        public AnimalState State;
        public int StateTimer;
        public int ThinkTimer;
        public int Herd;
        public bool Male;
        public float Size = 1f;
        public int EmbeddedArrows;
        public long DeathTick = -1;
        public float AnimTime;
        public readonly List<Vector2> Path = new();
        public int PathIndex;
        /// <summary>Simulation level of detail: far animals think less often.</summary>
        public bool Far;

        public const float Radius = 0.45f;
        public const float WalkSpeed = 2.2f / 60f, FleeSpeed = 9.5f / 60f;
        public bool Dead => Health.Dead;
        public string Label => Male ? "Deer (stag)" : "Deer (doe)";
        public override string ToString() => $"{Kind}#{Id}";
    }

    public sealed class Herd
    {
        public int Id;
        public Vector2 Anchor;
        public int MoveTimer;
    }

    public sealed class Projectile
    {
        public Vector2 Start, Pos, Dir;
        public float Speed;         // cells per tick
        public float MaxDist;
        public float Traveled;
        public float Damage;
        public Pawn Shooter;
        public bool Done;
        /// <summary>0..1 progress, for the visual arc.</summary>
        public float Progress => MaxDist <= 0 ? 1f : Math.Clamp(Traveled / MaxDist, 0f, 1f);
    }

    public enum SimEventKind : byte
    {
        Message, ArrowFired, ArrowHit, ArrowMissed, AnimalKilled, ItemPickedUp, ItemDropped, DoorToggled, Gathered, Drank, Ate,
        Swing, MeleeHit, AnimalFled, PawnDied, ItemSpawned, ItemDespawned, AnimalDespawned, MiningHit, Mined,
    }

    public struct SimEvent
    {
        public SimEventKind Kind;
        public Vector2 Pos;
        public string Text;
        public int Id;
        public SimEvent(SimEventKind k, Vector2 pos, string text = null, int id = 0) { Kind = k; Pos = pos; Text = text; Id = id; }
    }

    /// <summary>Per-tick input for the directly controlled pawn, written by the game layer.</summary>
    public sealed class DirectInput
    {
        /// <summary>Desired movement in map space (x east, y south), length ≤ 1.</summary>
        public Vector2 Move;
        public bool Sprint;
        public bool Aim;
        /// <summary>Aim point in map space.</summary>
        public Vector2 AimPoint;
        /// <summary>Edge-triggered: consumed by the next tick.</summary>
        public bool Fire;
    }
}
