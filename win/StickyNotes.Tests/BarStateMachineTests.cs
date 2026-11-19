// TASK 5 gate - trigger / bar visibility state machine, tested with a VIRTUAL clock (no WPF, no real timers).
//
// CONTRACT THE BLUEPRINT MUST ADOPT (extract this pure class from ShellCoordinator):
//
//   public enum BarState { Hidden, DwellPending, OpenHover, OpenFocusedLock }
//   public enum ShellActionKind { ShowBar, FlushAndHideAll }
//   public readonly record struct ShellAction(ShellActionKind Kind, nint Monitor);
//   public readonly record struct BarProbe(bool CursorInTrigger, bool FullscreenOnMonitor);
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
// Timings: dwell 180 ms, poll 50 ms. Boundary checks allow one 10 ms step of slack.
//
// EXIT IS NOT THE MACHINE'S BUSINESS (v0.2.2+): the 2s linear bar fade (BarExitPolicy, driven by
// ShellCoordinator) is the single exit policy. The machine used to time a 200ms ExitGrace of its own,
// which the coordinator had to park by passing a half-true CursorInSurfaces — a second policy that could
// hide everything behind the coordinator's back (a new note vanished as the cursor left the bar for it).
// BarState.ExitGrace and BarProbe.CursorInSurfaces are gone; leaving every surface is now unobservable here.
using StickyNotes.App.State;
using Xunit;

namespace StickyNotes.Tests;

[Trait("Task", "5")]
public class BarStateMachineTests
{
    static readonly BarProbe Hovering = new(CursorInTrigger: true, FullscreenOnMonitor: false);
    static readonly BarProbe Outside = new(false, false);
    static readonly BarProbe Fullscreen = new(true, true);

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
        Run(sm, 10_000, Outside); // the machine never exits on its own, so polling never lapses here
        Assert.True(sm.PollingActive);
        sm.HideNow();
        Assert.Equal(BarState.Hidden, sm.State);
        Assert.False(sm.PollingActive);
    }

    // ---------- exit belongs to the coordinator's fade, not to the machine ----------
    [Fact]
    public void Leaving_all_surfaces_does_not_hide_the_machine()
    {
        var sm = OpenBar();
        Run(sm, 10_000, Outside);
        Assert.Equal(BarState.OpenHover, sm.State);
        Assert.True(sm.PollingActive);
        Assert.Empty(sm.DrainActions()); // no 200ms grace, no hidden-behind-your-back FlushAndHideAll
    }

    [Fact]
    public void Cursor_position_is_irrelevant_to_the_machine()
    {
        // The machine learns only "dwell done" and "monitor went fullscreen"; everything cursor-shaped is
        // the fade's. Alternating probes must not change anything once the bar is open.
        var sm = OpenBar();
        for (int i = 0; i < 200; i++)
        {
            sm.Advance(50, i % 2 == 0 ? Outside : Hovering);
        }

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
    public void Editor_focus_from_open_hover_locks_the_bar()
    {
        var sm = OpenBar();
        sm.EditorFocused();
        Assert.Equal(BarState.OpenFocusedLock, sm.State);
        Run(sm, 1_000, Outside);
        Assert.Equal(BarState.OpenFocusedLock, sm.State);
        Assert.Empty(sm.DrainActions());
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
