using System;

namespace StickyNotes.App.State;

/// <summary>
/// v0.2.2+ fix: decides whether a note window's <c>Deactivated</c> event really means "the user clicked
/// away, close me". Windows can deliver a spurious Deactivated while the editor still holds the keyboard
/// (force-activation via AttachThreadInput, a topmost re-show, a context-menu/popup activation hand-off),
/// and closing on that killed freshly opened notes. The window lets the deactivation SETTLE first (a short
/// one-shot timer) and then asks this policy whether the focus really went somewhere else.
/// Pure (no WPF types) so the decision is unit-testable without windows.
/// </summary>
public static class NoteClosePolicy
{
    /// <summary>
    /// True when a settled deactivation should flush + close the note.
    /// </summary>
    /// <param name="closed">The window is already closing/closed. <remarks>Never close twice.</remarks></param>
    /// <param name="suppressDeactivate">A modal in-window UI (the move menu) owns the click-away decision.</param>
    /// <param name="everActivated">The window was really activated at least once; an editor that never took focus was never "clicked away from".</param>
    /// <param name="isActive">WPF still reports the window as active (focus came back after a bounce).</param>
    /// <param name="ownsForeground">The window is the OS foreground window right now.</param>
    /// <param name="externalForeground">
    /// The OS foreground is a window of ANOTHER process (a real app / the desktop). When it is not — the
    /// foreground is one of our own non-activating windows or nothing at all — the note lost focus to our
    /// own machinery, i.e. a bounce, and the user did not click away. Defaults to true ("the caller did not
    /// look"), which keeps the plain rule: a settled, inactive, unfocused note closes.
    /// </param>
    public static bool ShouldCloseOnDeactivate(
        bool closed,
        bool suppressDeactivate,
        bool everActivated,
        bool isActive,
        bool ownsForeground,
        bool externalForeground = true)
    {
        if (closed || suppressDeactivate)
        {
            return false;
        }

        if (!everActivated)
        {
            return false; // shown but never focused: an activation bounce, not a click-outside
        }

        if (isActive || ownsForeground)
        {
            return false; // focus came back
        }

        return externalForeground;
    }
}
