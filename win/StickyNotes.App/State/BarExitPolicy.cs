using System;

namespace StickyNotes.App.State;

/// <summary>
/// Pure timing curve for the 2s linear bar fade after the cursor leaves every app surface
/// (v0.2.2 "consistent close"). No WPF and no clock: the coordinator feeds elapsed milliseconds
/// from its existing 50ms tick, and unit tests drive it with a virtual clock.
/// </summary>
public static class BarExitPolicy
{
    /// <summary>Total fade length in milliseconds (opacity 1 -> 0, linear).</summary>
    public const int FadeDurationMs = 2000;

    /// <summary>
    /// Bar opacity <paramref name="elapsedMs"/> after the cursor left every surface:
    /// 1.0 before the fade starts, a linear 1 -> 0 ramp during it, 0.0 once it has elapsed.
    /// </summary>
    public static double OpacityAt(long elapsedMs)
    {
        if (elapsedMs <= 0)
        {
            return 1.0;
        }

        if (elapsedMs >= FadeDurationMs)
        {
            return 0.0;
        }

        return 1.0 - (elapsedMs / (double)FadeDurationMs);
    }

    /// <summary>True once the fade has run its full duration — the caller flushes + hides everything.</summary>
    public static bool ShouldHide(long elapsedMs) => elapsedMs >= FadeDurationMs;
}
