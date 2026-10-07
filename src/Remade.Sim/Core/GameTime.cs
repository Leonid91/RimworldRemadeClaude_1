using System;

namespace Remade.Core;

public enum Season : byte { Spring, Summer, Autumn, Winter }

/// <summary>
/// Game calendar. 60 ticks per real second at 1x speed; one day = 60 000 ticks (≈16.7 real minutes at 1x),
/// one year = 60 days (four 15-day seasons), like RimWorld. Tick 0 = first day of spring, 00:00.
/// </summary>
public static class GameTime
{
    public const int TicksPerSecondAt1x = 60;
    public const int TicksPerDay = 60000;
    public const int TicksPerHour = TicksPerDay / 24;      // 2500
    public const int DaysPerSeason = 15;
    public const int DaysPerYear = 60;
    public const long TicksPerYear = (long)TicksPerDay * DaysPerYear;
    /// <summary>Game seconds per tick (86 400 / 60 000).</summary>
    public const float GameSecondsPerTick = 86400f / TicksPerDay;

    public static readonly int[] SpeedMultipliers = { 0, 1, 3, 6, 15 };
    public static readonly string[] SpeedNames = { "Paused", "Normal", "Fast", "Faster", "Ultra" };

    public static float HourOfDay(long tick) => (tick % TicksPerDay) / (float)TicksPerHour;
    public static int DayOfYear(long tick) => (int)(tick / TicksPerDay % DaysPerYear);
    public static int Year(long tick) => (int)(tick / TicksPerYear) + 1;
    public static float YearFraction(long tick) => (tick % TicksPerYear) / (float)TicksPerYear;
    public static float Days(long tick) => tick / (float)TicksPerDay;

    /// <summary>Season at a latitude (southern hemisphere is shifted by half a year).</summary>
    public static Season SeasonAt(long tick, float latitude)
    {
        int day = DayOfYear(tick);
        if (latitude < 0) day = (day + DaysPerYear / 2) % DaysPerYear;
        return (Season)(day / DaysPerSeason);
    }

    public static string SeasonName(Season s) => s switch
    {
        Season.Spring => "Spring",
        Season.Summer => "Summer",
        Season.Autumn => "Autumn",
        Season.Winter => "Winter",
        _ => throw new ArgumentOutOfRangeException(nameof(s), s, null),
    };

    public static string ClockString(long tick)
    {
        float h = HourOfDay(tick);
        int hh = (int)h, mm = (int)((h - hh) * 60);
        return $"{hh:00}:{mm:00}";
    }

    public static string DateString(long tick, float latitude)
    {
        int dayInSeason = DayOfYear(tick) % DaysPerSeason + 1;
        return $"Day {dayInSeason} of {SeasonName(SeasonAt(tick, latitude))}, year {Year(tick)}";
    }
}
