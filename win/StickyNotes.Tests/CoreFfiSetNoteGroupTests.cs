// F1 gate - Move an existing note into a new/existing group, or to Standalone.
//
// Tested through the real sticky_core.dll via StickyCoreClient (same style as
// CoreFfiIntegrationTests). The UI wires this up as NoteWindow -> 'Move to group'.
// A move is not an open and not an edit: LastOpenedAtMs (recents) and updated_at_ms
// must survive a move.
using StickyNotes.App.Core;
using Xunit;

namespace StickyNotes.Tests;

[Trait("Task", "F1")]
public class CoreFfiSetNoteGroupTests
{
    [Fact]
    public void SetNoteGroup_moves_the_note_into_the_group()
    {
        using var t = new TempCore();
        var g = t.Client.CreateGroup("Target", pinned: true);
        var n = t.Client.CreateNote(null, "movable");

        Note moved = t.Client.SetNoteGroup(n.Id, g.Id);
        Assert.Equal(g.Id, moved.GroupId);
        Assert.Equal(g.Id, t.Client.GetNote(n.Id, markOpened: false).GroupId);
        Assert.Contains(t.Client.ListGroupNotes(g.Id), x => x.Id == n.Id);
    }

    [Fact]
    public void SetNoteGroup_to_standalone_clears_the_group()
    {
        using var t = new TempCore();
        var g = t.Client.CreateGroup("G", pinned: true);
        var n = t.Client.CreateNote(g.Id, "x");

        Note moved = t.Client.SetNoteGroup(n.Id, null);
        Assert.Null(moved.GroupId);
        Assert.Null(t.Client.GetNote(n.Id, markOpened: false).GroupId);
        Assert.Empty(t.Client.ListGroupNotes(g.Id));
    }

    [Fact]
    public void SetNoteGroup_round_trips_standalone_to_group_and_back()
    {
        using var t = new TempCore();
        var g = t.Client.CreateGroup("G", pinned: true);
        var n = t.Client.CreateNote(null, "x");

        Assert.Equal(g.Id, t.Client.SetNoteGroup(n.Id, g.Id).GroupId);
        Assert.Equal(g.Id, t.Client.GetNote(n.Id, markOpened: false).GroupId);

        Assert.Null(t.Client.SetNoteGroup(n.Id, null).GroupId);
        Assert.Null(t.Client.GetNote(n.Id, markOpened: false).GroupId);
    }

    [Fact]
    public void Missing_group_maps_to_status_1()
    {
        using var t = new TempCore();
        var n = t.Client.CreateNote(null, "x");

        var ex = Assert.Throws<StickyCoreException>(() => t.Client.SetNoteGroup(n.Id, 999_999));
        Assert.Equal(1, ex.Status);
        Assert.Null(t.Client.GetNote(n.Id, markOpened: false).GroupId); // untouched on failure
    }

    [Fact]
    public void Missing_note_maps_to_status_1()
    {
        using var t = new TempCore();
        var g = t.Client.CreateGroup("G", pinned: true);

        var ex = Assert.Throws<StickyCoreException>(() => t.Client.SetNoteGroup(999_999, g.Id));
        Assert.Equal(1, ex.Status);
        Assert.Throws<StickyCoreException>(() => t.Client.SetNoteGroup(999_999, null));
    }

    [Fact]
    public void Move_does_not_bump_last_opened_at_ms_or_reorder_recents()
    {
        using var t = new TempCore();
        var a = t.Client.CreateNote(null, "note A");
        TempCore.Tick();
        var b = t.Client.CreateNote(null, "note B"); // unpinned; newer
        Assert.Equal(new long[] { b.Id, a.Id }, t.Client.GetBarSnapshot().RecentNotes.Select(x => x.Id).ToArray());

        var g = t.Client.CreateGroup("G", pinned: true);
        TempCore.Tick();
        long before = t.Client.GetNote(a.Id, markOpened: false).LastOpenedAtMs;
        TempCore.Tick();

        t.Client.SetNoteGroup(a.Id, g.Id);

        Assert.Equal(before, t.Client.GetNote(a.Id, markOpened: false).LastOpenedAtMs);
        // Group membership does not affect recents eligibility (only pinning does).
        Assert.Equal(new long[] { b.Id, a.Id }, t.Client.GetBarSnapshot().RecentNotes.Select(x => x.Id).ToArray());
    }

    [Fact]
    public void Move_does_not_count_as_an_edit()
    {
        using var t = new TempCore();
        var g = t.Client.CreateGroup("G", pinned: true);
        var n = t.Client.CreateNote(null, "body");
        TempCore.Tick();
        long before = t.Client.GetNote(n.Id, markOpened: false).UpdatedAtMs;
        TempCore.Tick();

        t.Client.SetNoteGroup(n.Id, g.Id);

        Assert.Equal(before, t.Client.GetNote(n.Id, markOpened: false).UpdatedAtMs);
    }

    [Fact]
    public void Move_to_the_same_group_is_a_no_op_but_succeeds()
    {
        using var t = new TempCore();
        var g = t.Client.CreateGroup("G", pinned: true);
        var n = t.Client.CreateNote(g.Id, "x");

        Assert.Equal(g.Id, t.Client.SetNoteGroup(n.Id, g.Id).GroupId);
        Assert.Equal(g.Id, t.Client.GetNote(n.Id, markOpened: false).GroupId);
    }
}
