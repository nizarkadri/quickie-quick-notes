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
/// Pure HWND-free quick-note capture session (blueprint §8.4, contract pinned by QuickNoteSessionTests).
/// Ctrl+Enter / click-outside commits if text is non-whitespace; Escape always discards without writing.
/// </summary>
public sealed class QuickNoteSession
{
    private readonly IStickyCoreClient _core;

    public QuickNoteSession(IStickyCoreClient core) =>
        _core = core ?? throw new ArgumentNullException(nameof(core));

    public string Text { get; set; } = string.Empty;

    public QuickCaptureTarget Target { get; set; } = QuickCaptureTarget.Standalone();

    public QuickNoteOutcome Close(QuickNoteClose reason)
    {
        if (reason == QuickNoteClose.Escape)
        {
            return QuickNoteOutcome.Discarded; // panic-cancel: never writes
        }

        if (string.IsNullOrWhiteSpace(Text))
        {
            return QuickNoteOutcome.NothingToCommit;
        }

        _core.QuickCapture(Text, Target);
        return QuickNoteOutcome.Committed;
    }
}
