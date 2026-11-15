using System;
using StickyNotes.App.Core;

namespace StickyNotes.App.State;

public enum QuickNoteClose
{
    CtrlEnter,
    ClickOutside,
    Escape,
}

public enum QuickNoteOutcome
{
    Committed,
    Discarded,
    NothingToCommit,
}

/// <summary>
/// Task 1 stub. Pure HWND-free quick-capture session — Standalone/Group/Append
/// commit, empty no-op and Esc discard land in Task 8 (blueprint §8.4).
/// </summary>
public sealed class QuickNoteSession
{
    private readonly IStickyCoreClient _core;

    public QuickNoteSession(IStickyCoreClient core) => _core = core;

    public string Text { get; set; } = "";

    public QuickCaptureTarget Target { get; set; } = QuickCaptureTarget.Standalone();

    public QuickNoteOutcome Close(QuickNoteClose reason) => throw new NotImplementedException();
}
