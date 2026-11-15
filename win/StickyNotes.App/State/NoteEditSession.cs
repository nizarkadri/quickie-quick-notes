using System;
using StickyNotes.App.Core;

namespace StickyNotes.App.State;

public enum NoteCloseOutcome
{
    Saved,
    Unchanged,
    DeletedEmptyNew,
}

/// <summary>
/// Task 1 stub. Pure HWND-free note edit session — dirty tracking, 350ms debounce
/// save, flush-on-close empty-new delete and conditional delete confirm land in
/// Task 7 (blueprint §8.3).
/// </summary>
public sealed class NoteEditSession
{
    public const int AutosaveMs = 350;

    private readonly IStickyCoreClient _core;
    private readonly Note _note;
    private readonly bool _createdThisOpen;

    public NoteEditSession(IStickyCoreClient core, Note note, bool createdThisOpen)
    {
        _core = core;
        _note = note;
        _createdThisOpen = createdThisOpen;
    }

    public void TextChanged(string text) => throw new NotImplementedException();

    public void Advance(int ms) => throw new NotImplementedException();

    public void Flush() => throw new NotImplementedException();

    public NoteCloseOutcome Close() => throw new NotImplementedException();
}
