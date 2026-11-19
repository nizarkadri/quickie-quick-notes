// v0.2.2+ bug-fix gate (bug 1: BAR FADE RE-ENTRY) — the 2s linear exit fade must CANCEL when the
// cursor re-enters any app surface (bar returns to opacity 1), and the hit tracker must keep polling
// while it is visible so the re-entry is actually observed mid-fade (the legacy ExitGrace path must
// never freeze IsCursorInside). Real ShellCoordinator + real HitRegionTracker + the real 50ms poll
// timer; the cursor pose is injected purely (a test host owns no cursor) and the fade clock is
// backdated through the coordinator's _outsideSinceUtc, so no test sleeps for the 2s fade.
//
// NOTE the exit needs TWO consecutive outside polls (OutsidePollsBeforeExit): the cursor crosses the gap
// between two app surfaces in well under 100ms, and a single stale sample must never start the fade. A
// lone Tick therefore proves nothing about the fade — every assertion below feeds the hysteresis its two
// polls (or backdates a fade that is already running).
using System;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StickyNotes.App.Interop;
using StickyNotes.App.State;
using StickyNotes.App.Windows;
using Xunit;

namespace StickyNotes.Tests;

[Trait("Task", "v0.2.2")]
[Collection("WpfUi")]
public class BarFadeReentryTests
{
    private static readonly MonitorBounds Bounds = new(new PxRect(0, 0, 1920, 1080), new PxRect(0, 0, 1920, 1040));
    private static readonly PxRect BarRegion = new(0, 0, 800, 44);

    private static object? PrivateField(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target);

    private static void SetPrivateField(object target, string name, object? value) =>
        target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);

    private static object? InvokePrivate(object target, string name, params object?[] args) =>
        target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, args);

    /// <summary>Drive the real poll callback once (private; the 50ms timer calls it in production).</summary>
    private static void Poll(HitRegionTracker tracker) =>
        typeof(HitRegionTracker).GetMethod("PollTick", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(tracker, new object?[] { null });

    private static void Tick(ShellCoordinator coordinator) =>
        InvokePrivate(coordinator, "OnTick", null, EventArgs.Empty);

    private static double BarOpacity(BarWindow bar) =>
        ((Border)WinWindowTestSupport.FindByName(bar, "Root")).Opacity;

    [Fact]
    public void Reentry_during_the_2s_fade_cancels_it_and_restores_full_opacity()
    {
        WinWindowTestSupport.RunOnSta(() =>
        {
            using var t = new TempCore();
            using var coordinator = new ShellCoordinator(t.DbPath);
            coordinator.Start();
            try
            {
                var bar = (BarWindow)PrivateField(coordinator, "_bar")!;
                bar.ShowOnMonitor(Bounds, nint.Zero, WinWindowTestSupport.ActualDpi(bar));
                WinWindowTestSupport.Pump();

                var tracker = (HitRegionTracker)PrivateField(coordinator, "_tracker")!;
                bool inside = false;
                tracker.SetCursorPose(() => inside); // pure pose; the OS cursor probe is unavailable headless
                tracker.SetVisible(true);            // production does this inside ShowBar

                Tick(coordinator); // poll 1: outside, but the 2-poll hysteresis is not satisfied yet
                Assert.Null(PrivateField(coordinator, "_outsideSinceUtc"));

                Tick(coordinator); // poll 2: hysteresis complete → the 2s exit fade starts
                Assert.NotNull(PrivateField(coordinator, "_outsideSinceUtc"));

                SetPrivateField(coordinator, "_outsideSinceUtc", DateTime.UtcNow.AddMilliseconds(-1000));
                Tick(coordinator); // mid-fade: linear ramp 1s into the 2s fade → 50%
                Assert.Equal(0.5, BarOpacity(bar), 10);

                inside = true; // the cursor re-enters an app surface DURING the fade
                Assert.True(SpinWait.SpinUntil(() => tracker.IsCursorInside, 2000),
                    "the poll must keep running while the tracker is visible so the re-entry is observed");
                Assert.True(tracker.IsCursorInside);

                Tick(coordinator); // the Entered edge cancels the fade and restores opacity
                Assert.Equal(1.0, BarOpacity(bar), 10);
                Assert.Null(PrivateField(coordinator, "_outsideSinceUtc"));
                Assert.Equal(Visibility.Visible, bar.Visibility);

                Tick(coordinator); Tick(coordinator); // stays cancelled — the fade must not restart while the cursor stays inside
                Assert.Equal(1.0, BarOpacity(bar), 10);
                Assert.Null(PrivateField(coordinator, "_outsideSinceUtc"));
            }
            finally
            {
                coordinator.Shutdown();
            }
        });
    }

    [Fact]
    public void The_legacy_grace_elapse_never_freezes_the_poll_or_IsCursorInside()
    {
        using var tracker = new HitRegionTracker();
        tracker.SetSurfaces(new[] { BarRegion });
        bool inside = true;
        tracker.SetCursorPose(() => inside); // pure pose; the OS cursor probe is unavailable headless
        tracker.SetVisible(true);
        Poll(tracker);
        Assert.True(tracker.IsCursorInside);

        bool completed = false;
        tracker.ExitCompleted += (_, _) => completed = true;

        // Only a legacy caller could still arm the grace flag (the v0.2.2 fix parks the timer), so
        // fire the callback manually: it must neither stop the poll nor hide the tracker. The real
        // 50ms poll resets _outside every tick, so arm + fire atomically under the tracker's own
        // _sync (Monitor is re-entrant — GraceElapsed still takes it and still fires the event).
        object sync = PrivateField(tracker, "_sync")!;
        lock (sync)
        {
            SetPrivateField(tracker, "_outside", true);
            InvokePrivate(tracker, "GraceElapsed", (object?)null);
        }
        Assert.True(completed);  // the legacy event still fires when genuinely armed
        Assert.True(tracker.IsVisible);

        inside = false;
        Poll(tracker);
        Assert.False(tracker.IsCursorInside); // the poll was NOT frozen by the grace elapse

        inside = true;
        Poll(tracker);
        Assert.True(tracker.IsCursorInside);  // and it keeps tracking the cursor in both directions
    }

    [Fact]
    public void The_reentry_edge_resets_the_fade_ramp_at_the_pure_seams()
    {
        // The coordinator composes the two pure seams: ExitPoseTracker supplies the edges,
        // BarExitPolicy the opacity curve. Re-entry (Entered) maps to "elapsed reset → opaque".
        var pose = new ExitPoseTracker();
        Assert.Equal(ExitPoseTransition.None, pose.Update(false));   // starts outside; no edge yet
        Assert.Equal(ExitPoseTransition.Entered, pose.Update(true));
        Assert.Equal(ExitPoseTransition.Left, pose.Update(false));   // fade starts

        Assert.Equal(0.5, BarExitPolicy.OpacityAt(BarExitPolicy.FadeDurationMs / 2), 10);
        Assert.False(BarExitPolicy.ShouldHide(BarExitPolicy.FadeDurationMs / 2));

        Assert.Equal(ExitPoseTransition.Entered, pose.Update(true)); // re-entry mid-fade → cancel
        Assert.True(pose.IsInside);

        Assert.Equal(1.0, BarExitPolicy.OpacityAt(0), 10);           // elapsed reset → opaque again
        Assert.False(BarExitPolicy.ShouldHide(0));
    }
}
