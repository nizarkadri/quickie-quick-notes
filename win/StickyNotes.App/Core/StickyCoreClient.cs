using System;
using System.Collections.Generic;

namespace StickyNotes.App.Core;

/// <summary>
/// Task 1 stub. Task 3 replaces the bodies with P/Invoke + try/finally
/// <c>sn_string_free</c> marshaling and status→exception mapping (blueprint §7).
/// </summary>
public sealed class StickyCoreClient : IStickyCoreClient
{
    private readonly string _dbPath;
    private bool _disposed;

    public StickyCoreClient(string dbPath) => _dbPath = dbPath;

    /// <summary>Path to the SQLite file. No DB is opened by the Task 1 stub.</summary>
    public string DbPath => _dbPath;

    public Note CreateNote(long? groupId, string body) => throw new NotImplementedException();

    public Note GetNote(long noteId, bool markOpened) => throw new NotImplementedException();

    public void UpdateNoteBody(long noteId, string body) => throw new NotImplementedException();

    public void DeleteNote(long noteId) => throw new NotImplementedException();

    public NoteSummary SetNotePinned(long noteId, bool pinned) => throw new NotImplementedException();

    public GroupSummary CreateGroup(string name, bool pinned) => throw new NotImplementedException();

    public GroupSummary RenameGroup(long groupId, string name) => throw new NotImplementedException();

    public void DeleteGroup(long groupId) => throw new NotImplementedException();

    public GroupSummary SetGroupPinned(long groupId, bool pinned) => throw new NotImplementedException();

    public BarSnapshot GetBarSnapshot() => throw new NotImplementedException();

    public IReadOnlyList<NoteSummary> ListGroupNotes(long groupId) => throw new NotImplementedException();

    public IReadOnlyList<NoteSummary> ListAppendTargets(int limit) => throw new NotImplementedException();

    public Note QuickCapture(string text, QuickCaptureTarget target) => throw new NotImplementedException();

    /// <summary>Idempotent no-op; Task 3 closes the native handle exactly once.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
    }
}
