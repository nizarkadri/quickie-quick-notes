// v0.2.2+ bug-fix gate - the hit tracker keeps polling while a caller keeps it visible, so a
// cursor re-entry during the 2s bar fade is observed (bug 1: fade never stopped on re-entry).
// Real timers are NOT used: the poll/grace callbacks are driven directly.
using System.Reflection;
using StickyNotes.App.Interop;
using Xunit;

namespace StickyNotes.Tests;

[Trait("Task", "v0.2.2")]
public class HitRegionTrackerPollContinuityTests
{
    private static readonly PxRect Bar = new(0, 0, 800, 44);

    /// <summary>Drive the real poll callback once (it is private; the 50ms timer calls it in production).</summary>
    private static void Poll(HitRegionTracker tracker) =>
        typeof(HitRegionTracker).GetMethod("PollTick", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(tracker, new object?[] { null });

    [Fact]
    public void IsCursorInside_tracks_the_cursor_across_many_outside_ticks()
    {
        using var tracker = new HitRegionTracker();
        tracker.SetSurfaces(new[] { Bar });
        // The OS cursor probe is unavailable on a headless test host; inject the pure pose instead.
        bool inside = true;
        tracker.SetCursorPose(() => inside);
        tracker.SetVisible(true);

        Poll(tracker);
        Assert.True(tracker.IsCursorInside);

        // Cursor outside for a long stretch (way past the legacy 200ms grace) — the poll must keep
        // running so a re-entry during the 2s fade is observed.
        inside = false;
        for (int i = 0; i < 100; i++) // 100 poll ticks — the fade is 2s = 40 ticks
        {
            Poll(tracker);
            Assert.False(tracker.IsCursorInside);
        }

        // Re-entry: the next poll sees the surface again (no SetVisible round-trip needed).
        inside = true;
        Poll(tracker);
        Assert.True(tracker.IsCursorInside);
    }

    [Fact]
    public void ExitCompleted_never_fires_while_the_caller_keeps_the_tracker_visible()
    {
        using var tracker = new HitRegionTracker();
        bool completed = false;
        tracker.ExitCompleted += (_, _) => completed = true;
        tracker.SetSurfaces(Array.Empty<PxRect>()); // cursor outside everything
        tracker.SetCursorPose(() => false);        // pure pose: the OS probe is unavailable headless
        tracker.SetVisible(true);

        for (int i = 0; i < 1000; i++)
        {
            Poll(tracker);
        }

        Assert.False(completed); // the coordinator's fade owns the exit timing; the legacy event stays inert
        Assert.True(tracker.IsVisible);
    }

    [Fact]
    public void SetVisible_false_stops_the_poll()
    {
        using var tracker = new HitRegionTracker();
        tracker.SetSurfaces(new[] { Bar });
        tracker.SetCursorPose(() => true);
        tracker.SetVisible(true);
        Poll(tracker);
        Assert.True(tracker.IsCursorInside);

        tracker.SetVisible(false); // HideAll is the ONLY legitimate stop
        Assert.False(tracker.IsVisible);

        // Last-known inside pose is stale-but-safe (true) until the next poll — the coordinator never
        // polls once hidden, so this cannot keep a fade alive.
        Assert.True(tracker.IsCursorInside);
    }
}
