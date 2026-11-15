using System;
using System.Collections.Generic;

namespace StickyNotes.App.Core;

// ---------------------------------------------------------------------------
// Task 1 stubs — managed mirror of the Rust DTOs (blueprint §6).
// Task 3 wires these to System.Text.Json; the shapes are fixed here so the
// whole solution compiles.
// ---------------------------------------------------------------------------

public sealed class Note
{
    public long Id { get; set; }
    public long? GroupId { get; set; }
    public string Body { get; set; } = "";
    public string TitlePreview { get; set; } = "";
    public bool IsPinned { get; set; }
    public long? PinnedAtMs { get; set; }
    public long CreatedAtMs { get; set; }
    public long UpdatedAtMs { get; set; }
    public long LastOpenedAtMs { get; set; }
}

public sealed class NoteSummary
{
    public long Id { get; set; }
    public long? GroupId { get; set; }
    public string TitlePreview { get; set; } = "";
    public bool IsEmpty { get; set; }
    public bool IsPinned { get; set; }
    public long? PinnedAtMs { get; set; }
    public long LastOpenedAtMs { get; set; }
}

public sealed class GroupSummary
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public int NoteCount { get; set; }
    public bool IsPinned { get; set; }
    public long? PinnedAtMs { get; set; }
    public long CreatedAtMs { get; set; }
}

public sealed class BarSnapshot
{
    public List<NoteSummary> RecentNotes { get; set; } = new();
    public List<GroupSummary> AllGroups { get; set; } = new();
}

/// <summary>
/// Serializes to the Rust <c>QuickCaptureTarget</c> serde representation:
/// <c>{"mode":"standalone"}</c>, <c>{"mode":"group","group_id":N}</c>,
/// <c>{"mode":"append","note_id":N}</c>.
/// </summary>
public sealed class QuickCaptureTarget
{
    private QuickCaptureTarget(string mode, long? groupId, long? noteId)
    {
        Mode = mode;
        GroupId = groupId;
        NoteId = noteId;
    }

    public string Mode { get; }
    public long? GroupId { get; }
    public long? NoteId { get; }

    public static QuickCaptureTarget Standalone() => new("standalone", null, null);

    public static QuickCaptureTarget Group(long groupId) => new("group", groupId, null);

    public static QuickCaptureTarget Append(long noteId) => new("append", null, noteId);
}

public sealed class StickyCoreException : Exception
{
    public StickyCoreException(int status, string message) : base(message) => Status = status;

    /// <summary>0 = OK, 1 = NotFound, 2 = Validation, 3 = Sqlite, 4 = UnsupportedSchema, 5 = Internal/panic.</summary>
    public int Status { get; }
}

public interface IStickyCoreClient : IDisposable
{
    Note CreateNote(long? groupId, string body);
    Note GetNote(long noteId, bool markOpened);
    void UpdateNoteBody(long noteId, string body);
    void DeleteNote(long noteId);
    NoteSummary SetNotePinned(long noteId, bool pinned);
    GroupSummary CreateGroup(string name, bool pinned);
    GroupSummary RenameGroup(long groupId, string name);
    void DeleteGroup(long groupId);
    GroupSummary SetGroupPinned(long groupId, bool pinned);
    BarSnapshot GetBarSnapshot();
    IReadOnlyList<NoteSummary> ListGroupNotes(long groupId);
    IReadOnlyList<NoteSummary> ListAppendTargets(int limit);
    Note QuickCapture(string text, QuickCaptureTarget target);
}
