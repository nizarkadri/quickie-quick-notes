// v0.2.2+ bug-fix gate - the pure hover-dwell detector behind "group list opens ON HOVER".
// Boundary: dwell completes at exactly 180ms; a quick pass (<180ms) never fires; leaving resets;
// Ready fires exactly once until re-entered.
using StickyNotes.App.State;
using Xunit;

namespace StickyNotes.Tests;

[Trait("Task", "v0.2.2")]
public class HoverDwellTrackerTests
{
    [Fact]
    public void Dwell_is_180ms()
    {
        Assert.Equal(180, HoverDwellTracker.DwellMs);
    }

    [Fact]
    public void Starts_outside_and_reports_none()
    {
        var t = new HoverDwellTracker();
        Assert.False(t.IsHovering);
        Assert.Equal(HoverDwellTracker.HoverStage.None, t.Poll(false, 0));
        Assert.False(t.IsHovering);
    }

    [Fact]
    public void Entering_reports_Started_once()
    {
        var t = new HoverDwellTracker();

        Assert.Equal(HoverDwellTracker.HoverStage.Started, t.Poll(true, 1000));
        Assert.True(t.IsHovering);
        Assert.Equal(HoverDwellTracker.HoverStage.None, t.Poll(true, 1010)); // still dwelling
    }

    [Fact]
    public void Ready_fires_exactly_at_the_dwell_boundary()
    {
        var t = new HoverDwellTracker();

        t.Poll(true, 1000);
        Assert.Equal(HoverDwellTracker.HoverStage.None, t.Poll(true, 1179)); // 179ms — too soon
        Assert.Equal(HoverDwellTracker.HoverStage.Ready, t.Poll(true, 1180)); // exactly 180ms
        Assert.Equal(HoverDwellTracker.HoverStage.Ready, t.Poll(true, 1190)); // keeps reporting Ready while hovering (caller opens once, then re-arms)
    }

    [Fact]
    public void Leaving_cancels_the_dwell()
    {
        var t = new HoverDwellTracker();

        t.Poll(true, 1000);
        Assert.Equal(HoverDwellTracker.HoverStage.None, t.Poll(false, 1100));
        Assert.False(t.IsHovering);

        // A re-entry starts a fresh dwell — the interrupted time does not count.
        Assert.Equal(HoverDwellTracker.HoverStage.Started, t.Poll(true, 2000));
        Assert.Equal(HoverDwellTracker.HoverStage.None, t.Poll(true, 2100));
        Assert.Equal(HoverDwellTracker.HoverStage.Ready, t.Poll(true, 2180));
    }

    [Fact]
    public void A_quick_pass_never_opens()
    {
        var t = new HoverDwellTracker();

        // Ten 10ms sweeps across the chip: 100ms total, in and out, repeatedly.
        for (int i = 0; i < 10; i++)
        {
            Assert.Equal(HoverDwellTracker.HoverStage.Started, t.Poll(true, i * 20));
            Assert.Equal(HoverDwellTracker.HoverStage.None, t.Poll(false, i * 20 + 10));
        }

        Assert.False(t.IsHovering);
    }

    [Fact]
    public void Reset_cancels_an_in_flight_dwell()
    {
        var t = new HoverDwellTracker();
        t.Poll(true, 0);
        t.Reset();

        Assert.False(t.IsHovering);
        Assert.Equal(HoverDwellTracker.HoverStage.Started, t.Poll(true, 10_000)); // fresh dwell, not Ready
    }
}
