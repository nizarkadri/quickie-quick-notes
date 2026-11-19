// v0.2.2 gate - the pure cursor-pose edge detector (starts outside, reports only crossings).
using StickyNotes.App.State;
using Xunit;

namespace StickyNotes.Tests;

[Trait("Task", "v0.2.2")]
public class ExitPoseTrackerTests
{
    [Fact]
    public void Starts_outside()
    {
        var tracker = new ExitPoseTracker();
        Assert.False(tracker.IsInside);
    }

    [Fact]
    public void Reports_entered_and_left_exactly_once_per_crossing()
    {
        var tracker = new ExitPoseTracker();

        Assert.Equal(ExitPoseTransition.None, tracker.Update(false));
        Assert.False(tracker.IsInside);

        Assert.Equal(ExitPoseTransition.Entered, tracker.Update(true));
        Assert.True(tracker.IsInside);

        Assert.Equal(ExitPoseTransition.None, tracker.Update(true));
        Assert.True(tracker.IsInside);

        Assert.Equal(ExitPoseTransition.Left, tracker.Update(false));
        Assert.False(tracker.IsInside);

        Assert.Equal(ExitPoseTransition.None, tracker.Update(false));
        Assert.False(tracker.IsInside);
    }

    [Fact]
    public void Reset_returns_the_pose_to_outside()
    {
        var tracker = new ExitPoseTracker();
        Assert.Equal(ExitPoseTransition.Entered, tracker.Update(true));
        Assert.True(tracker.IsInside);

        tracker.Reset(); // e.g. ShellCoordinator.HideAll after a mid-fade hide
        Assert.False(tracker.IsInside);
        Assert.Equal(ExitPoseTransition.Entered, tracker.Update(true)); // re-entry is a real edge again
    }

    [Fact]
    public void Repeats_cleanly_for_every_later_crossing()
    {
        var tracker = new ExitPoseTracker();
        for (int cycle = 0; cycle < 3; cycle++)
        {
            Assert.Equal(ExitPoseTransition.Entered, tracker.Update(true));
            Assert.Equal(ExitPoseTransition.None, tracker.Update(true));
            Assert.Equal(ExitPoseTransition.Left, tracker.Update(false));
            Assert.Equal(ExitPoseTransition.None, tracker.Update(false));
        }

        Assert.False(tracker.IsInside);
    }
}
