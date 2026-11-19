// v0.2.2 gate - HideNow is ADDITIVE: hiding from an open bar flushes + hides immediately;
// from Hidden it is a no-op. Existing transitions are covered by BarStateMachineTests.
using StickyNotes.App.State;
using Xunit;

namespace StickyNotes.Tests;

[Trait("Task", "v0.2.2")]
public class BarStateMachineHideNowTests
{
    private static readonly BarProbe Hovering = new(CursorInTrigger: true, FullscreenOnMonitor: false);

    private static BarStateMachine OpenHoverMachine(nint monitor = 7)
    {
        var sm = new BarStateMachine();
        sm.TriggerEntered(monitor);
        sm.Advance(BarStateMachine.DwellMs, Hovering);
        Assert.Equal(BarState.OpenHover, sm.State);
        Assert.Single(sm.DrainActions()); // ShowBar from the dwell completion
        return sm;
    }

    [Fact]
    public void HideNow_from_OpenHover_hides_and_flushes()
    {
        BarStateMachine sm = OpenHoverMachine();

        sm.HideNow();

        Assert.Equal(BarState.Hidden, sm.State);
        Assert.False(sm.PollingActive);
        IReadOnlyList<ShellAction> actions = sm.DrainActions();
        Assert.Single(actions);
        Assert.Equal(ShellActionKind.FlushAndHideAll, actions[0].Kind);
        Assert.Equal(7, actions[0].Monitor);
    }

    [Fact]
    public void HideNow_from_Hidden_emits_nothing()
    {
        var sm = new BarStateMachine();
        Assert.Equal(BarState.Hidden, sm.State);

        sm.HideNow();

        Assert.Equal(BarState.Hidden, sm.State);
        Assert.Empty(sm.DrainActions());
    }

    [Fact]
    public void HideNow_twice_emits_the_flush_only_once()
    {
        BarStateMachine sm = OpenHoverMachine();

        sm.HideNow();
        sm.HideNow();

        Assert.Equal(BarState.Hidden, sm.State);
        Assert.Single(sm.DrainActions());
    }
}
