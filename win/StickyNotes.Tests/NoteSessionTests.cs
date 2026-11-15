// TASKS 7 and 8 gates - note-window and quick-note LOGIC, tested against the real core (no WPF).
//
// CONTRACT THE BLUEPRINT MUST ADOPT (pure classes the windows delegate to):
//
//   public enum NoteCloseOutcome { Saved, Unchanged, DeletedEmptyNew }
//   public sealed class NoteEditSession {
//       public const int AutosaveMs = 350;
//       public NoteEditSession(IStickyCoreClient core, Note note, bool createdThisOpen);
//       public void TextChanged(string body);
//       public void Advance(int ms);                 // virtual clock for the debounce
//       public void Flush();                         // synchronous save if dirty
//       public NoteCloseOutcome Close();             // flushes first; deletes if createdThisOpen && empty && !pinned
//   }                                                // (pinned state is read from the core at close time)
//
//   public enum QuickNoteClose { CtrlEnter, ClickOutside, Escape }
//   public enum QuickNoteOutcome { Committed, Discarded, NothingToCommit }
//   public sealed class QuickNoteSession {
//       public QuickNoteSession(IStickyCoreClient core);
//       public string Text { get; set; }
//       public QuickCaptureTarget Target { get; set; }   // defaults to Standalone
//       public QuickNoteOutcome Close(QuickNoteClose reason);
//   }
using StickyNotes.App.Core;
using StickyNotes.App.State;
using Xunit;

namespace StickyNotes.Tests;

[Trait("Task", "7")]
public class NoteEditSessionTests
{
    static (TempCore t, Note n) NewNote(string body = "", bool pinned = false)
    {
        var t = new TempCore();
        var n = t.Client.CreateNote(null, body);
        if (pinned) t.Client.SetNotePinned(n.Id, true);
        return (t, n);
    }

    [Fact]
    public void Nothing_is_saved_before_the_350ms_debounce()
    {
        var (t, n) = NewNote();
        using var _ = t;
        var s = new NoteEditSession(t.Client, n, createdThisOpen: false);
        s.TextChanged("abc");
        s.Advance(349);
        Assert.Equal("", t.Client.GetNote(n.Id, false).Body);
        s.Advance(1);
        Assert.Equal("abc", t.Client.GetNote(n.Id, false).Body);
    }

    [Fact]
    public void Every_keystroke_restarts_the_debounce()
    {
        var (t, n) = NewNote();
        using var _ = t;
        var s = new NoteEditSession(t.Client, n, false);
        s.TextChanged("a");
        s.Advance(300);
        s.TextChanged("ab");
        s.Advance(300);
        Assert.Equal("", t.Client.GetNote(n.Id, false).Body);
        s.Advance(50);
        Assert.Equal("ab", t.Client.GetNote(n.Id, false).Body);
    }

    [Fact]
    public void Flush_saves_immediately_without_waiting()
    {
        var (t, n) = NewNote();
        using var _ = t;
        var s = new NoteEditSession(t.Client, n, false);
        s.TextChanged("urgent");
        s.Flush();
        Assert.Equal("urgent", t.Client.GetNote(n.Id, false).Body);
    }

    [Fact]
    public void Flush_when_clean_does_not_write()
    {
        var (t, n) = NewNote("same");
        using var _ = t;
        var before = t.Client.GetNote(n.Id, false).UpdatedAtMs;
        TempCore.Tick();
        var s = new NoteEditSession(t.Client, n, false);
        s.Flush();
        Assert.Equal(before, t.Client.GetNote(n.Id, false).UpdatedAtMs);
    }

    [Fact]
    public void Pending_text_is_saved_when_the_window_closes_inside_the_debounce()
    {
        var (t, n) = NewNote();
        using var _ = t;
        var s = new NoteEditSession(t.Client, n, false);
        s.TextChanged("typed then closed fast");
        s.Advance(100);
        Assert.Equal(NoteCloseOutcome.Saved, s.Close());
        Assert.Equal("typed then closed fast", t.Client.GetNote(n.Id, false).Body);
    }

    [Fact]
    public void New_empty_unpinned_note_is_deleted_on_close()
    {
        var (t, n) = NewNote();
        using var _ = t;
        var s = new NoteEditSession(t.Client, n, createdThisOpen: true);
        Assert.Equal(NoteCloseOutcome.DeletedEmptyNew, s.Close());
        Assert.Throws<StickyCoreException>(() => t.Client.GetNote(n.Id, false));
    }

    [Fact]
    public void New_note_containing_only_whitespace_counts_as_empty()
    {
        var (t, n) = NewNote();
        using var _ = t;
        var s = new NoteEditSession(t.Client, n, true);
        s.TextChanged("  \n\t ");
        Assert.Equal(NoteCloseOutcome.DeletedEmptyNew, s.Close());
    }

    [Fact]
    public void New_note_with_content_is_kept()
    {
        var (t, n) = NewNote();
        using var _ = t;
        var s = new NoteEditSession(t.Client, n, true);
        s.TextChanged("real content");
        Assert.Equal(NoteCloseOutcome.Saved, s.Close());
        Assert.Equal("real content", t.Client.GetNote(n.Id, false).Body);
    }

    [Fact]
    public void New_empty_note_that_was_pinned_is_kept()
    {
        var (t, n) = NewNote(pinned: true);
        using var _ = t;
        var s = new NoteEditSession(t.Client, n, createdThisOpen: true);
        s.Close();
        Assert.True(t.Client.GetNote(n.Id, false).IsPinned);
    }

    [Fact]
    public void Existing_note_cleared_by_the_user_is_kept_empty()
    {
        var (t, n) = NewNote("had text");
        using var _ = t;
        var s = new NoteEditSession(t.Client, n, createdThisOpen: false);
        s.TextChanged("");
        s.Close();
        Assert.Equal("", t.Client.GetNote(n.Id, false).Body);
    }
}

[Trait("Task", "8")]
public class QuickNoteSessionTests
{
    [Fact]
    public void CtrlEnter_commits_a_standalone_note_by_default()
    {
        using var t = new TempCore();
        var q = new QuickNoteSession(t.Client) { Text = "idea" };
        Assert.Equal(QuickNoteOutcome.Committed, q.Close(QuickNoteClose.CtrlEnter));
        var recent = t.Client.GetBarSnapshot().RecentNotes.Single();
        Assert.Equal("idea", t.Client.GetNote(recent.Id, false).Body);
    }

    [Fact]
    public void Click_outside_commits()
    {
        using var t = new TempCore();
        var q = new QuickNoteSession(t.Client) { Text = "kept" };
        Assert.Equal(QuickNoteOutcome.Committed, q.Close(QuickNoteClose.ClickOutside));
        Assert.Single(t.Client.GetBarSnapshot().RecentNotes);
    }

    [Fact]
    public void Escape_discards_and_never_touches_the_database()
    {
        using var t = new TempCore();
        var q = new QuickNoteSession(t.Client) { Text = "oops" };
        Assert.Equal(QuickNoteOutcome.Discarded, q.Close(QuickNoteClose.Escape));
        Assert.Empty(t.Client.GetBarSnapshot().RecentNotes);
    }

    [Fact]
    public void Escape_in_append_mode_leaves_the_target_note_unchanged()
    {
        using var t = new TempCore();
        var target = t.Client.CreateNote(null, "original");
        var q = new QuickNoteSession(t.Client) { Text = "wrong target", Target = QuickCaptureTarget.Append(target.Id) };
        q.Close(QuickNoteClose.Escape);
        Assert.Equal("original", t.Client.GetNote(target.Id, false).Body);
    }

    [Theory]
    [InlineData(QuickNoteClose.CtrlEnter)]
    [InlineData(QuickNoteClose.ClickOutside)]
    public void Empty_or_whitespace_text_commits_nothing(QuickNoteClose reason)
    {
        using var t = new TempCore();
        var q = new QuickNoteSession(t.Client) { Text = "  \n " };
        Assert.Equal(QuickNoteOutcome.NothingToCommit, q.Close(reason));
        Assert.Empty(t.Client.GetBarSnapshot().RecentNotes);
    }

    [Fact]
    public void Group_target_creates_the_note_inside_the_chosen_group()
    {
        using var t = new TempCore();
        var g = t.Client.CreateGroup("Cheats", true);
        var q = new QuickNoteSession(t.Client) { Text = "git stash", Target = QuickCaptureTarget.Group(g.Id) };
        q.Close(QuickNoteClose.CtrlEnter);
        Assert.Equal(1, t.Client.ListGroupNotes(g.Id).Count);
    }

    [Fact]
    public void Append_adds_after_a_newline_and_moves_the_target_to_the_top_of_recents()
    {
        using var t = new TempCore();
        var target = t.Client.CreateNote(null, "line1");
        TempCore.Tick();
        t.Client.CreateNote(null, "newer note");
        TempCore.Tick();

        var q = new QuickNoteSession(t.Client) { Text = "line2", Target = QuickCaptureTarget.Append(target.Id) };
        Assert.Equal(QuickNoteOutcome.Committed, q.Close(QuickNoteClose.CtrlEnter));

        Assert.Equal("line1\nline2", t.Client.GetNote(target.Id, false).Body);
        Assert.Equal(target.Id, t.Client.GetBarSnapshot().RecentNotes[0].Id);
    }
}
