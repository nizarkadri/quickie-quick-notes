using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using StickyNotes.App.Core;
using StickyNotes.App.Interop;
using StickyNotes.App.State;

namespace StickyNotes.App.Windows;

/// <summary>
/// Floating note editor (blueprint §7/§8.3): 320×280 DIP, non-activating tool window unless the
/// user explicitly focuses it ('+ New Note' activates; clicking in activates naturally). Shows
/// passively without stealing focus (SWP_NOACTIVATE) otherwise. Backed by
/// <see cref="NoteEditSession"/>: 350ms trailing-debounce autosave; close always flushes;
/// delete of an empty just-created unpinned note happens inside the session.
/// Esc / close button / delete: immediate flush + close (Esc also restores the previous foreground).
/// Click-outside: 5s grace (§8.3) — clicking back in cancels the pending close.
/// </summary>
public partial class NoteWindow : Window
{
    public const int PassiveShow = 0;
    public const int FocusedShow = 1;

    private readonly IStickyCoreClient _core;
    private readonly NoteEditSession _session;
    private readonly string? _groupName;
    private const int ClickOutsideGraceMs = 5000;

    private readonly DispatcherTimer _autosaveTimer;
    private readonly DispatcherTimer _clickOutsideTimer = new();
    private bool _closed;
    private bool _pinned;

    /// <summary>Raised when the window takes keyboard focus (focused open, or click-in from passive).</summary>
    public event EventHandler? EditorActivated;

    public NoteWindow(IStickyCoreClient core, Note note, bool createdThisOpen)
    {
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(note);
        _core = core;
        _session = new NoteEditSession(core, note, createdThisOpen);

        string? groupName = null;
        if (note.GroupId.HasValue)
        {
            try
            {
                groupName = core.GetBarSnapshot().AllGroups.FirstOrDefault(g => g.Id == note.GroupId.Value)?.Name;
            }
            catch (StickyCoreException)
            {
                groupName = null; // badge optional; never block the open on a core read
            }
        }

        _groupName = groupName;
        _pinned = note.IsPinned;

        InitializeComponent();

        BodyBox.Text = note.Body ?? string.Empty;
        BodyBox.CaretIndex = BodyBox.Text.Length;

        UpdatePinGlyph();
        if (_groupName is { Length: > 0 })
        {
            GroupBadge.Text = "● " + _groupName;
            GroupBadge.Visibility = Visibility.Visible;
        }

        _autosaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _autosaveTimer.Tick += (_, _) =>
        {
            try
            {
                _session.Advance(50);
            }
            catch (StickyCoreException ex)
            {
                System.Diagnostics.Trace.WriteLine($"Autosave failed ({ex.Status}): {ex.Message}");
            }
        };
        _autosaveTimer.Start();

        _clickOutsideTimer.Interval = TimeSpan.FromMilliseconds(ClickOutsideGraceMs);
        _clickOutsideTimer.Tick += (_, _) =>
        {
            _clickOutsideTimer.Stop();
            if (!_closed)
            {
                CloseInternal(restoreForeground: false, raiseEvent: true);
            }
        };

        Deactivated += OnDeactivated;
        Activated += OnActivated;
    }

    private void OnActivated(object? sender, EventArgs e)
    {
        _clickOutsideTimer.Stop(); // a click back in cancels the pending close
        EditorActivated?.Invoke(this, EventArgs.Empty);
    }

    public long NoteId => _session.NoteId;

    /// <summary>True while the 5s click-outside grace timer is armed — coordinator keeps the bar but leaves this note open.</summary>
    internal bool ClickOutsideGracePending => _clickOutsideTimer.IsEnabled;

    /// <summary>Raised when the window closes itself (flush completed). Parameter: session outcome.</summary>
    public event EventHandler<NoteWindowClosedEventArgs>? ClosedByUser;

    /// <summary>Raised when the window loses keyboard focus (external or internal click-outside).</summary>
    public event EventHandler? EditorDeactivated;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        nint hwnd = new WindowInteropHelper(this).Handle;
        // NO WS_EX_NOACTIVATE: clicking inside activates naturally (full IME/clipboard).
        _ = Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        FocusHelper.ApplyRoundedCorners(hwnd);
    }

    /// <summary>Position + show. <paramref name="focused"/>: activate and focus the text box (for '+ New Note').</summary>
    public void ShowNote(PxRect anchorRect, MonitorBounds bounds, double dpi, bool anchorIsHorizontalBarItem, bool focused)
    {
        nint hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == nint.Zero)
        {
            Show();
            hwnd = new WindowInteropHelper(this).Handle;
        }

        var size = new PxSize((int)Math.Round(320 * dpi), (int)Math.Round(280 * dpi));
        PxRect rect = new ScreenPositioner().GetNoteWindowRect(anchorRect, size, bounds, anchorIsHorizontalBarItem);
        Width = rect.Width / dpi;
        Height = rect.Height / dpi;

        Visibility = Visibility.Visible;
        if (focused)
        {
            // Force-activate with AttachThreadInput fallback — the foreground lock may be held by a foreign app.
            _ = Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, rect.X, rect.Y, rect.Width, rect.Height,
                Win32.SWP_SHOWWINDOW);
            FocusHelper.ForceActivateAndFocus(this, hwnd, BodyBox);
            BodyBox.CaretIndex = BodyBox.Text.Length;
        }
        else
        {
            // Passive: read while the terminal keeps focus.
            _ = Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, rect.X, rect.Y, rect.Width, rect.Height,
                Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
        }
    }

    /// <summary>Physical pixel rect for hit-testing; default when hidden.</summary>
    public bool TryGetPhysicalRect(out PxRect rect)
    {
        rect = default;
        nint hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == nint.Zero || Visibility != Visibility.Visible)
        {
            return false;
        }

        if (!Win32.GetWindowRect(hwnd, out RECT r))
        {
            return false;
        }

        rect = new PxRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        return true;
    }

    /// <summary>Flush pending text and close. Idempotent; safe from any path. Returns the close outcome.</summary>
    public NoteCloseOutcome FlushAndClose() => CloseInternal(restoreForeground: false, raiseEvent: true);

    private void OnPinToggleClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _pinned = !_pinned;
            _core.SetNotePinned(_session.NoteId, _pinned);
            UpdatePinGlyph();
        }
        catch (StickyCoreException)
        {
            _pinned = !_pinned; // roll back the optimistic flip
            UpdatePinGlyph();
        }
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(BodyBox.Text))
        {
            DeleteConfirmed();
            return;
        }

        HeaderPanel.Visibility = Visibility.Collapsed;
        ConfirmPanel.Visibility = Visibility.Visible;
    }

    private void OnConfirmDeleteClick(object sender, RoutedEventArgs e) => DeleteConfirmed();

    private void OnConfirmCancelClick(object sender, RoutedEventArgs e)
    {
        ConfirmPanel.Visibility = Visibility.Collapsed;
        HeaderPanel.Visibility = Visibility.Visible;
    }

    private void DeleteConfirmed()
    {
        long id = _session.NoteId;
        try
        {
            _core.DeleteNote(id);
        }
        catch (StickyCoreException)
        {
            // Already gone — the user's intent is satisfied either way.
        }

        CloseInternal(restoreForeground: false, raiseEvent: true, alreadyDeleted: true);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseInternal(restoreForeground: false, raiseEvent: true);

    private void OnBodyKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            // Esc closes + flushes and hands focus back to the previous app.
            CloseInternal(restoreForeground: true, raiseEvent: true);
            e.Handled = true;
        }
    }

    private void OnBodyTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        _session.TextChanged(BodyBox.Text);

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (_closed)
        {
            return;
        }

        // Click-outside (or hand-off to another app window): 5s grace — the user may click straight back in.
        EditorDeactivated?.Invoke(this, EventArgs.Empty);
        _clickOutsideTimer.Stop();
        _clickOutsideTimer.Start();
    }

    private NoteCloseOutcome CloseInternal(bool restoreForeground, bool raiseEvent, bool alreadyDeleted = false)
    {
        if (_closed)
        {
            return NoteCloseOutcome.Unchanged;
        }

        _closed = true;
        _clickOutsideTimer.Stop();
        _autosaveTimer.Stop();

        NoteCloseOutcome outcome = NoteCloseOutcome.Unchanged;
        if (alreadyDeleted)
        {
            outcome = NoteCloseOutcome.Saved;
        }
        else
        {
            try
            {
                outcome = _session.Close();
            }
            catch (StickyCoreException ex)
            {
                System.Diagnostics.Trace.WriteLine($"Note flush-on-close failed ({ex.Status}): {ex.Message}");
                outcome = NoteCloseOutcome.Saved; // text may be lost — treat as saved-dirty rather than crash
            }
        }

        if (restoreForeground)
        {
            FocusHelper.RestorePreviousForeground();
        }

        Visibility = Visibility.Hidden;
        if (raiseEvent)
        {
            ClosedByUser?.Invoke(this, new NoteWindowClosedEventArgs(outcome, _session.NoteId));
        }

        Close(); // dispose the HWND; OnClosed removes focus-tracking state
        return outcome;
    }

    private void UpdatePinGlyph() => PinToggleButton.Opacity = _pinned ? 1.0 : 0.35;

    protected override void OnClosed(EventArgs e)
    {
        _clickOutsideTimer.Stop();
        _autosaveTimer.Stop();
        FocusHelper.ForgetWindow(new WindowInteropHelper(this).Handle);
        base.OnClosed(e);
    }
}

public sealed class NoteWindowClosedEventArgs : EventArgs
{
    public NoteWindowClosedEventArgs(NoteCloseOutcome outcome, long noteId)
    {
        Outcome = outcome;
        NoteId = noteId;
    }

    public NoteCloseOutcome Outcome { get; }
    public long NoteId { get; }
}
