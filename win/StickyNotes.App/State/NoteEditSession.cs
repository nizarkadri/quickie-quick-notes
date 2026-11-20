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
/// Pure HWND-free note edit session (blueprint §8.3, contract pinned by NoteEditSessionTests).
/// 350ms trailing-debounce autosave, flush-on-close, and the empty-new-note delete rule.
/// No WPF types referenced.
/// </summary>
public sealed class NoteEditSession
{
    public const int AutosaveMs = 350;

    private readonly IStickyCoreClient _core;
    private readonly Note _note;
    private readonly bool _createdThisOpen;

    private string _savedBody;
    private string _body;
    private string _savedTitle;
    private string _title;
    private int _debounceElapsed;

    public NoteEditSession(IStickyCoreClient core, Note note, bool createdThisOpen)
    {
        _core = core ?? throw new ArgumentNullException(nameof(core));
        _note = note ?? throw new ArgumentNullException(nameof(note));
        _createdThisOpen = createdThisOpen;
        _savedBody = note.Body ?? string.Empty;
        _body = _savedBody;
        _savedTitle = note.Title ?? string.Empty;
        _title = _savedTitle;
        _debounceElapsed = 0;
    }

    public long NoteId => _note.Id;

    /// <summary>Whether the in-memory body or title differs from the last persisted values.</summary>
    public bool IsDirty => _body != _savedBody || _title != _savedTitle;

    /// <summary>Current (possibly unsaved) body text.</summary>
    public string CurrentText => _body;

    /// <summary>Current (possibly unsaved) title; empty means "no title".</summary>
    public string CurrentTitle => _title;

    /// <summary>Last persisted title; empty means "no title".</summary>
    public string SavedTitle => _savedTitle;

    /// <summary>Record a keystroke; restarts the trailing debounce.</summary>
    public void TextChanged(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (body == _body)
        {
            return;
        }

        _body = body;
        _debounceElapsed = 0;
    }

    /// <summary>Record a title edit; restarts the trailing debounce.</summary>
    public void TitleChanged(string? title)
    {
        var normalized = title ?? string.Empty;
        if (normalized == _title)
        {
            return;
        }

        _title = normalized;
        _debounceElapsed = 0;
    }

    /// <summary>Discard an unsaved title edit; the body debounce is untouched.</summary>
    public void RevertTitle() => _title = _savedTitle;

    /// <summary>Advance the virtual/debounce clock (windows call with elapsed ms).</summary>
    public void Advance(int ms)
    {
        if (ms < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ms), ms, "Advance requires a non-negative interval.");
        }

        if (!IsDirty)
        {
            _debounceElapsed = 0;
            return;
        }

        _debounceElapsed += ms;
        if (_debounceElapsed >= AutosaveMs)
        {
            SaveNow();
        }
    }

    /// <summary>Synchronous save when dirty; no-op otherwise (never bumps updated_at_ms spuriously).</summary>
    public void Flush()
    {
        _ = FlushIfDirty();
    }

    /// <summary>
    /// Flush, then apply the empty-new-note rule: a note created this open that is still
    /// whitespace-only and not pinned (pinned state read from the core at close time) is deleted.
    /// </summary>
    public NoteCloseOutcome Close()
    {
        bool saved = FlushIfDirty();

        if (_createdThisOpen
            && string.IsNullOrWhiteSpace(_savedBody)
            && string.IsNullOrWhiteSpace(_savedTitle))
        {
            bool pinned = false;
            try
            {
                pinned = _core.GetNote(_note.Id, markOpened: false).IsPinned;
            }
            catch (StickyCoreException)
            {
                // Note vanished underneath us (or core unreachable): nothing left to clean up, treat as empty-and-gone.
                pinned = false;
            }

            if (!pinned)
            {
                try
                {
                    _core.DeleteNote(_note.Id);
                }
                catch (StickyCoreException)
                {
                    // Best effort — an already-deleted row is fine.
                }

                return NoteCloseOutcome.DeletedEmptyNew;
            }
        }

        return saved ? NoteCloseOutcome.Saved : NoteCloseOutcome.Unchanged;
    }

    private bool FlushIfDirty()
    {
        if (!IsDirty)
        {
            return false;
        }

        SaveNow();
        return true;
    }

    private void SaveNow()
    {
        if (_body != _savedBody)
        {
            _core.UpdateNoteBody(_note.Id, _body);
            _savedBody = _body;
        }

        if (_title != _savedTitle)
        {
            var updated = _core.SetNoteTitle(_note.Id, _title);
            _savedTitle = updated.Title ?? string.Empty;
        }

        _debounceElapsed = 0;
    }
}
