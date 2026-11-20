using System;
using StickyNotes.App.Core;
using StickyNotes.App.State;
using Xunit;

namespace StickyNotes.Tests;

[Trait("Feature", "F2")]
public class NoteTitleTests
{
    static (TempCore t, Note n) NewNote(string body = "")
    {
        var t = new TempCore();
        var n = t.Client.CreateNote(null, body);
        return (t, n);
    }

    [Fact]
    public void SetNoteTitle_round_trips_through_ffi()
    {
        var (t, n) = NewNote("first line\nsecond");
        using var _ = t;

        var updated = t.Client.SetNoteTitle(n.Id, "Shopping");
        var fetched = t.Client.GetNote(n.Id, false);

        Assert.Equal("Shopping", updated.Title);
        Assert.Equal("Shopping", fetched.Title);
        Assert.Equal("first line", fetched.TitlePreview);
        Assert.Equal("first line\nsecond", fetched.Body);
    }

    [Fact]
    public void SetNoteTitle_null_and_empty_clear()
    {
        var (t, n) = NewNote("body");
        using var _ = t;

        t.Client.SetNoteTitle(n.Id, "Shopping");

        var clearedByNull = t.Client.SetNoteTitle(n.Id, null);
        Assert.Null(clearedByNull.Title);

        var clearedByEmpty = t.Client.SetNoteTitle(n.Id, "");
        Assert.Null(clearedByEmpty.Title);

        var fetched = t.Client.GetNote(n.Id, false);
        Assert.Null(fetched.Title);
        Assert.Equal("body", fetched.TitlePreview);
    }

    [Fact]
    public void SetNoteTitle_missing_note_throws_not_found()
    {
        var t = new TempCore();
        using var _ = t;

        var ex = Assert.Throws<StickyCoreException>(() => t.Client.SetNoteTitle(999_999, "x"));

        Assert.Equal(1, ex.Status);
    }

    [Fact]
    public void DisplayTitle_prefers_title_on_note_and_summary()
    {
        var titled = new Note { TitlePreview = "preview", Title = "title" };
        var untitled = new Note { TitlePreview = "preview", Title = null };
        Assert.Equal("title", titled.DisplayTitle);
        Assert.Equal("preview", untitled.DisplayTitle);

        var titledSummary = new NoteSummary { TitlePreview = "preview", Title = "title", IsEmpty = false };
        var untitledSummary = new NoteSummary { TitlePreview = "preview", Title = null, IsEmpty = false };
        Assert.Equal("title", titledSummary.DisplayTitle);
        Assert.Equal("preview", untitledSummary.DisplayTitle);
    }

    [Fact]
    public void NoteSummary_carries_title_through_snapshot()
    {
        var (t, n) = NewNote("body");
        using var _ = t;
        t.Client.SetNoteTitle(n.Id, "Shopping");

        var summary = t.Client.GetBarSnapshot().RecentNotes[0];

        Assert.Equal("Shopping", summary.Title);
        Assert.Equal("Shopping", summary.DisplayTitle);
    }

    [Fact]
    public void NoteEditSession_saves_title_on_flush()
    {
        var (t, n) = NewNote("body");
        using var _ = t;
        var s = new NoteEditSession(t.Client, n, createdThisOpen: false);

        s.TitleChanged("Shopping");
        s.Advance(NoteEditSession.AutosaveMs);

        assert_title(t, n.Id, "Shopping");
    }

    [Fact]
    public void NoteEditSession_title_debounce_does_not_save_early()
    {
        var (t, n) = NewNote("body");
        using var _ = t;
        var s = new NoteEditSession(t.Client, n, createdThisOpen: false);

        s.TitleChanged("Shopping");
        s.Advance(NoteEditSession.AutosaveMs - 1);
        Assert.Null(t.Client.GetNote(n.Id, false).Title);

        s.Advance(1);
        assert_title(t, n.Id, "Shopping");
    }

    [Fact]
    public void NoteEditSession_saves_title_on_close()
    {
        var (t, n) = NewNote("body");
        using var _ = t;
        var s = new NoteEditSession(t.Client, n, createdThisOpen: false);

        s.TitleChanged("Shopping");
        var outcome = s.Close();

        Assert.Equal(NoteCloseOutcome.Saved, outcome);
        assert_title(t, n.Id, "Shopping");
    }

    [Fact]
    public void NoteEditSession_title_only_new_note_is_kept()
    {
        var (t, n) = NewNote("");
        using var _ = t;
        var s = new NoteEditSession(t.Client, n, createdThisOpen: true);

        s.TitleChanged("Only a title");
        var outcome = s.Close();

        Assert.Equal(NoteCloseOutcome.Saved, outcome);
        var fetched = t.Client.GetNote(n.Id, false);
        assert_title(t, n.Id, "Only a title");
        Assert.Equal("", fetched.Body);
    }

    [Fact]
    public void NoteEditSession_clearing_title_persists_null()
    {
        var (t, n) = NewNote("body");
        using var _ = t;
        t.Client.SetNoteTitle(n.Id, "Shopping");
        var refreshed = t.Client.GetNote(n.Id, false);
        var s = new NoteEditSession(t.Client, refreshed, createdThisOpen: false);

        s.TitleChanged(null);
        s.Close();

        Assert.Null(t.Client.GetNote(n.Id, false).Title);
    }

    static void assert_title(TempCore t, long noteId, string expected)
    {
        var fetched = t.Client.GetNote(noteId, false);
        Assert.Equal(expected, fetched.Title);
        Assert.Equal(expected, fetched.DisplayTitle);
    }
}