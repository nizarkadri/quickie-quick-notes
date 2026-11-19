// v0.2.2+ gate - the pure "did this Deactivated really mean click-outside?" policy behind the
// new-note-close fix (a spurious Deactivated must never flush a freshly opened editor).
// Decision table: false when closed || suppressDeactivate; false when !everActivated (an activation
// bounce, not a click-away); false when isActive || ownsForeground (focus came back / OS foreground);
// false when the foreground is NOT another process (our own non-activating windows, or none at all -
// a bounce); true only when the deactivation has settled AND the focus truly left for an external app.
// No WPF, no clock, no STA - and the 64-row sweep pins the whole table (both externalForeground values)
// against an independent restatement of the doc.
using StickyNotes.App.State;
using Xunit;

namespace StickyNotes.Tests;

[Trait("Task", "v0.2.2")]
public class NoteClosePolicyTests
{
    /// <summary>The documented contract restated in words (not copied from production code).</summary>
    private static bool Oracle(bool closed, bool suppressDeactivate, bool everActivated, bool isActive, bool ownsForeground, bool externalForeground) =>
        !closed && !suppressDeactivate && everActivated && !isActive && !ownsForeground && externalForeground;

    [Fact]
    public void Closes_when_the_note_was_activated_and_has_settled_truly_inactive_not_foreground()
    {
        Assert.True(NoteClosePolicy.ShouldCloseOnDeactivate(
            closed: false, suppressDeactivate: false, everActivated: true, isActive: false, ownsForeground: false));
    }

    [Fact]
    public void Never_closes_a_window_that_is_already_closed()
    {
        Assert.False(NoteClosePolicy.ShouldCloseOnDeactivate(
            closed: true, suppressDeactivate: false, everActivated: true, isActive: false, ownsForeground: false));
    }

    [Fact]
    public void Never_closes_while_the_move_menu_suppresses_deactivation()
    {
        Assert.False(NoteClosePolicy.ShouldCloseOnDeactivate(
            closed: false, suppressDeactivate: true, everActivated: true, isActive: false, ownsForeground: false));
    }

    [Fact]
    public void Never_closes_a_note_that_was_never_really_activated()
    {
        Assert.False(NoteClosePolicy.ShouldCloseOnDeactivate(
            closed: false, suppressDeactivate: false, everActivated: false, isActive: false, ownsForeground: false));
    }

    [Fact]
    public void Never_closes_while_WPF_still_reports_the_window_active()
    {
        Assert.False(NoteClosePolicy.ShouldCloseOnDeactivate(
            closed: false, suppressDeactivate: false, everActivated: true, isActive: true, ownsForeground: false));
    }

    [Fact]
    public void Never_closes_while_the_window_is_the_OS_foreground_window()
    {
        Assert.False(NoteClosePolicy.ShouldCloseOnDeactivate(
            closed: false, suppressDeactivate: false, everActivated: true, isActive: false, ownsForeground: true));
    }

    [Fact]
    public void Deactivate_to_our_own_window_or_no_foreground_never_closes_the_note()
    {
        // externalForeground=false covers BOTH bounce sources the caller collapses: fg == 0 (no window
        // took the foreground) and fg is one of our own non-activating windows (bar / dropdown / side list).
        Assert.False(NoteClosePolicy.ShouldCloseOnDeactivate(
            closed: false, suppressDeactivate: false, everActivated: true, isActive: false, ownsForeground: false, externalForeground: false));
    }

    [Fact]
    public void Deactivate_to_a_real_external_app_closes_the_note()
    {
        Assert.True(NoteClosePolicy.ShouldCloseOnDeactivate(
            closed: false, suppressDeactivate: false, everActivated: true, isActive: false, ownsForeground: false, externalForeground: true));
    }

    [Fact]
    public void Each_guard_alone_vetoes_an_otherwise_settled_click_outside()
    {
        Assert.False(NoteClosePolicy.ShouldCloseOnDeactivate(true, false, true, false, false));   // closed
        Assert.False(NoteClosePolicy.ShouldCloseOnDeactivate(false, true, true, false, false));   // suppress (move menu)
        Assert.False(NoteClosePolicy.ShouldCloseOnDeactivate(false, false, false, false, false)); // never activated
        Assert.False(NoteClosePolicy.ShouldCloseOnDeactivate(false, false, true, true, false));   // still active
        Assert.False(NoteClosePolicy.ShouldCloseOnDeactivate(false, false, true, false, true));   // owns foreground
    }

    [Fact]
    public void Full_64_row_decision_table_matches_the_documented_contract()
    {
        // 6 inputs = 64 rows, externalForeground on BOTH values: the oracle distinguishes them, so an
        // inverted 6th argument (or any wiring slip) fails roughly half the table.
        for (int bits = 0; bits < 64; bits++)
        {
            bool closed = (bits & 1) != 0;
            bool suppressDeactivate = (bits & 2) != 0;
            bool everActivated = (bits & 4) != 0;
            bool isActive = (bits & 8) != 0;
            bool ownsForeground = (bits & 16) != 0;
            bool externalForeground = (bits & 32) != 0;

            Assert.Equal(Oracle(closed, suppressDeactivate, everActivated, isActive, ownsForeground, externalForeground),
                NoteClosePolicy.ShouldCloseOnDeactivate(closed, suppressDeactivate, everActivated, isActive, ownsForeground, externalForeground));
        }
    }
}
