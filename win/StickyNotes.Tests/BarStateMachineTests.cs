// TASK 5 gate - trigger / bar visibility state machine, tested with a VIRTUAL clock (no WPF, no real timers).
//
// CONTRACT THE BLUEPRINT MUST ADOPT (extract this pure class from ShellCoordinator):
//
//   public enum BarState { Hidden, DwellPending, OpenHover, ExitGrace, OpenFocusedLock }
//   public enum ShellActionKind { ShowBar, FlushAndHideAll }
//   public readonly record struct ShellAction(ShellActionKind Kind, nint Monitor);
//   public readonly record struct BarProbe(bool CursorInTrigger, bool CursorInSurfaces, bool FullscreenOnMonitor);
//
//   public sealed class BarStateMachine {
//       public BarState State { get; }
//       public bool PollingActive { get; }            // false when Hidden (=> 0% idle CPU)
//       public void TriggerEntered(nint monitor);
//       public void TriggerLeft();
//       public void EditorFocused();
//       public void EditorDeactivated(bool toExternalWindow, bool cursorInSurfaces);
//       public void DisplaySettingsChanged();
//       public void AppExit();
//       public void Advance(int ms, BarProbe probe);  // virtual time; probe constant over the interval
//       public IReadOnlyList<ShellAction> DrainActions();
//   }
//
// Timings: dwell 180 ms, poll 50 ms, exit grace 200 ms. Boundary checks allow one 10 ms step of slack.
using StickyNotes.App.State;
using Xunit;

namespace StickyNotes.Tests;

[Trait("Task", "5")]
public class BarStateMachineTests
{
    static readonly BarProbe Hovering = new(CursorInTrigger: true, CursorInSurfaces: true, FullscreenOnMonitor: false);
    static readonly BarProbe Inside = new(false, true, false);
    static readonly BarProbe Outside = new(false, false, false);
    static readonly BarProbe Fullscreen = new(true, true, true);

    static void Run(BarStateMachine sm, int totalMs, BarProbe probe)
    {
        for (int t = 0; t < totalMs; t += 10) sm.Advance(10, probe);
    }

    static BarStateMachine OpenBar(nint monitor = 1)
    {
        var sm = new BarStateMachine();
        sm.TriggerEntered(monitor);
        Run(sm, 200, Hovering);
        Assert.Equal(BarState.OpenHover, sm.State);
        sm.DrainActions();
        return sm;
    }

    static void RunUntil(BarStateMachine sm, BarState target, BarProbe probe, int maxMs = 500)
    {
        for (int t = 0; t < maxMs && sm.State != target; t += 10) sm.Advance(10, probe);
        Assert.Equal(target, sm.State);
    }

    // ---------- reveal ----------
    [Fact]
    public void Starts_hidden_with_no_polling()
    {
        var sm = new BarStateMachine();
        Assert.Equal(BarState.Hidden, sm.State);
        Assert.False(sm.PollingActive);
    }

    [Fact]
    public void Entering_the_trigger_starts_a_dwell()
    {
        var sm = new BarStateMachine();
        sm.TriggerEntered(1);
        Assert.Equal(BarState.DwellPending, sm.State);
    }

    [Fact]
    public void Fast_sweep_under_dwell_never_opens_the_bar()
    {
        var sm = new BarStateMachine();
        sm.TriggerEntered(1);
        Run(sm, 120, Hovering);
        sm.TriggerLeft();
        Run(sm, 500, Outside);
        Assert.Equal(BarState.Hidden, sm.State);
        Assert.Empty(sm.DrainActions());
    }

    [Fact]
    public void Still_pending_just_before_180ms_and_open_by_200ms()
    {
        var sm = new BarStateMachine();
        sm.TriggerEntered(1);
        Run(sm, 170, Hovering);
        Assert.Equal(BarState.DwellPending, sm.State);
        Assert.Empty(sm.DrainActions());
        Run(sm, 30, Hovering);
        Assert.Equal(BarState.OpenHover, sm.State);
        var a = Assert.Single(sm.DrainActions());
        Assert.Equal(ShellActionKind.ShowBar, a.Kind);
        Assert.Equal((nint)1, a.Monitor);
    }

    [Fact]
    public void Fullscreen_on_the_monitor_suppresses_the_reveal()
    {
        var sm = new BarStateMachine();
        sm.TriggerEntered(1);
        Run(sm, 400, Fullscreen);
        Assert.Equal(BarState.Hidden, sm.State);
        Assert.Empty(sm.DrainActions());
    }

    [Fact]
    public void Cursor_that_left_the_trigger_before_dwell_ends_does_not_reveal()
    {
        var sm = new BarStateMachine();
        sm.TriggerEntered(1);
        Run(sm, 200, Outside); // dwell elapses but cursor is no longer in the trigger
        Assert.Equal(BarState.Hidden, sm.State);
        Assert.Empty(sm.DrainActions());
    }

    [Fact]
    public void Moving_to_another_monitors_trigger_restarts_the_dwell_there()
    {
        var sm = new BarStateMachine();
        sm.TriggerEntered(1);
        Run(sm, 100, Hovering);
        sm.TriggerEntered(2);
        Run(sm, 100, Hovering);
        Assert.Equal(BarState.DwellPending, sm.State);
        Run(sm, 100, Hovering);
        var a = Assert.Single(sm.DrainActions());
        Assert.Equal((nint)2, a.Monitor);
    }

    // ---------- polling / idle cost ----------
    [Fact]
    public void Polling_is_active_only_while_the_bar_is_open()
    {
        var sm = OpenBar();
        Assert.True(sm.PollingActive);
        RunUntil(sm, BarState.ExitGrace, Outside);
        Assert.True(sm.PollingActive);
        RunUntil(sm, BarState.Hidden, Outside);
        Assert.False(sm.PollingActive);
    }

    // ---------- keep-open / exit grace ----------
    [Fact]
    public void Bar_stays_open_while_the_cursor_is_over_any_surface()
    {
        var sm = OpenBar();
        Run(sm, 10_000, Inside);
        Assert.Equal(BarState.OpenHover, sm.State);
        Assert.Empty(sm.DrainActions());
    }

    [Fact]
    public void Leaving_all_surfaces_starts_grace_and_hides_after_about_200ms()
    {
        var sm = OpenBar();
        RunUntil(sm, BarState.ExitGrace, Outside);
        Run(sm, 150, Outside);
        Assert.Equal(BarState.ExitGrace, sm.State);
        Run(sm, 70, Outside);
        Assert.Equal(BarState.Hidden, sm.State);
        var a = Assert.Single(sm.DrainActions());
        Assert.Equal(ShellActionKind.FlushAndHideAll, a.Kind);
    }

    [Fact]
    public void Returning_during_grace_cancels_the_hide()
    {
        var sm = OpenBar();
        RunUntil(sm, BarState.ExitGrace, Outside);
        Run(sm, 100, Inside);
        Assert.Equal(BarState.OpenHover, sm.State);
        Run(sm, 1000, Inside);
        Assert.Equal(BarState.OpenHover, sm.State);
        Assert.Empty(sm.DrainActions());
    }

    // ---------- focus lock (editors) ----------
    [Fact]
    public void Focused_editor_prevents_hiding_no_matter_where_the_cursor_goes()
    {
        var sm = OpenBar();
        sm.EditorFocused();
        Assert.Equal(BarState.OpenFocusedLock, sm.State);
        Run(sm, 5_000, Outside);
        Assert.Equal(BarState.OpenFocusedLock, sm.State);
        Assert.Empty(sm.DrainActions());
    }

    [Fact]
    public void Editor_focus_during_grace_cancels_the_pending_hide()
    {
        var sm = OpenBar();
        RunUntil(sm, BarState.ExitGrace, Outside);
        sm.EditorFocused();
        Run(sm, 1_000, Outside);
        Assert.Equal(BarState.OpenFocusedLock, sm.State);
    }

    [Fact]
    public void Click_outside_while_editing_flushes_and_hides()
    {
        var sm = OpenBar();
        sm.EditorFocused();
        sm.EditorDeactivated(toExternalWindow: true, cursorInSurfaces: false);
        Assert.Equal(BarState.Hidden, sm.State);
        var a = Assert.Single(sm.DrainActions());
        Assert.Equal(ShellActionKind.FlushAndHideAll, a.Kind);
    }

    [Fact]
    public void Editor_deactivated_with_cursor_still_on_our_surfaces_keeps_the_bar_open()
    {
        var sm = OpenBar();
        sm.EditorFocused();
        sm.EditorDeactivated(toExternalWindow: true, cursorInSurfaces: true);
        Assert.Equal(BarState.OpenHover, sm.State);
        Assert.Empty(sm.DrainActions());
    }

    // ---------- system events ----------
    [Fact]
    public void Display_change_flushes_and_hides_from_any_open_state()
    {
        var sm = OpenBar();
        sm.DisplaySettingsChanged();
        Assert.Equal(BarState.Hidden, sm.State);
        Assert.Equal(ShellActionKind.FlushAndHideAll, Assert.Single(sm.DrainActions()).Kind);

        var locked = OpenBar();
        locked.EditorFocused();
        locked.DisplaySettingsChanged();
        Assert.Equal(BarState.Hidden, locked.State);
    }

    [Fact]
    public void App_exit_flushes_unsaved_work()
    {
        var sm = OpenBar();
        sm.AppExit();
        Assert.Equal(ShellActionKind.FlushAndHideAll, Assert.Single(sm.DrainActions()).Kind);
    }

    // ---------- invalid transitions are ignored ----------
    [Fact]
    public void Events_that_make_no_sense_while_hidden_do_nothing()
    {
        var sm = new BarStateMachine();
        sm.TriggerLeft();
        sm.EditorFocused();
        sm.EditorDeactivated(true, false);
        Run(sm, 1_000, Outside);
        Assert.Equal(BarState.Hidden, sm.State);
        Assert.Empty(sm.DrainActions());
    }
}
