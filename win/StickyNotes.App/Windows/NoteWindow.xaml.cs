using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
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
/// Click-outside: immediate flush + close (blueprint §1 dismissal).
/// </summary>
public partial class NoteWindow : Window
{
    public const int PassiveShow = 0;
    public const int FocusedShow = 1;

    private readonly IStickyCoreClient _core;
    private readonly NoteEditSession _session;
    private readonly string? _groupName;

    private readonly DispatcherTimer _autosaveTimer;
    private bool _closed;
    private bool _everActivated;             // the window really took activation at least once
    private bool _reassertedFocus;           // one bounce-filter re-activation per activation cycle
    private bool _reasserting;               // we are inside our own ForceActivateAndFocus right now
    private long _lastReassertTick;          // time cap: never re-assert more often than ReassertCooldownMs
    private const int ReassertCooldownMs = 2000; // ping-pong guard
    private DispatcherTimer? _deactivateSettleTimer; // one-shot: lets an activation bounce settle before closing
    private bool _pinned;
    private long? _groupId;
    private bool _suppressDeactivate; // the move menu is open; OnDeactivated must not close the note
    private const int DeactivateSettleMs = 120; // grace before a Deactivated is trusted (spurious-bounce filter)
    private bool _menuPicked;         // a move-menu item was clicked
    private bool _pendingNewGroup;    // the "New group…" item was clicked

    /// <summary>Raised when the window takes keyboard focus (focused open, or click-in from passive).</summary>
    public event EventHandler? EditorActivated;

    /// <summary>F1: the note moved to another group (or standalone). Coordinator refreshes bar + list.</summary>
    public event EventHandler<NoteMovedEventArgs>? NoteMoved;

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
        _groupId = note.GroupId;
        _pinned = note.IsPinned;

        InitializeComponent();
        Visibility = Visibility.Hidden; // create hidden; ShowNote positions before making it visible

        BodyBox.Text = note.Body ?? string.Empty;
        BodyBox.CaretIndex = BodyBox.Text.Length;

        TitleBox.Text = note.Title ?? string.Empty;
        UpdateTitlePlaceholder();

        UpdatePinGlyph();
        UpdateGroupBadge(_groupId, _groupName);

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

        Deactivated += OnDeactivated;
        Activated += OnActivated;
    }

    private void OnActivated(object? sender, EventArgs e)
    {
        Diag.Log($"Note.Activated noteId={_session.NoteId} everActivated={_everActivated} isActive={IsActive}");
        _everActivated = true;
        if (!_reasserting)
        {
            _reassertedFocus = false; // a fresh USER activation earns one new re-assert; our own re-assert must not
        }
        _deactivateSettleTimer?.Stop(); // focus came back → a pending settle-close was a bounce
        EditorActivated?.Invoke(this, EventArgs.Empty);
    }

    public long NoteId => _session.NoteId;

    /// <summary>Raised when the window closes itself (flush completed). Parameter: session outcome.</summary>
    public event EventHandler<NoteWindowClosedEventArgs>? ClosedByUser;

    /// <summary>Raised when the window loses keyboard focus (external or internal click-outside).
    /// RESERVED/EXTENSION POINT (R2): kept for future shells that want raw deactivation.</summary>
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
        nint hwnd = new WindowInteropHelper(this).EnsureHandle(); // hidden; positioned + shown below

        var size = new PxSize((int)Math.Round(320 * dpi), (int)Math.Round(280 * dpi));
        PxRect rect = new ScreenPositioner().GetNoteWindowRect(anchorRect, size, bounds, anchorIsHorizontalBarItem);
        Width = rect.Width / dpi;
        Height = rect.Height / dpi;
        Left = rect.X / dpi;
        Top = rect.Y / dpi;

        // Left/Top first so Show() (triggered by Visibility=Visible) lands at the target,
        // not the default screen center. SetWindowPos then refines the physical rect.
        Visibility = Visibility.Visible;
        _ = Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, rect.X, rect.Y, rect.Width, rect.Height,
            Win32.SWP_NOACTIVATE);

        Diag.Log($"Note.ShowNote noteId={_session.NoteId} focused={focused} showActivated={ShowActivated} visible={Visibility}");

        if (focused)
        {
            // Force-activate with AttachThreadInput fallback — the foreground lock may be held by a foreign app.
            bool gotFg = FocusHelper.ForceActivateAndFocus(this, hwnd, BodyBox);
            BodyBox.CaretIndex = BodyBox.Text.Length;
            Diag.Log($"Note.ShowNote forceActivated={gotFg} isActive={IsActive} fgIsSelf={Win32.GetForegroundWindow() == hwnd}");
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

    // ------------------------------------------------------------------ F1: move to group

    private void OnMoveToGroupClick(object sender, RoutedEventArgs e) => OpenMoveMenu();

    private void OpenMoveMenu()
    {
        var menu = new ContextMenu();

        var standalone = new MenuItem
        {
            Header = _groupId is null ? "✓ Standalone" : "Standalone",
        };
        standalone.Click += (_, _) =>
        {
            _menuPicked = true;
            MoveToGroup(null, null);
        };
        menu.Items.Add(standalone);

        menu.Items.Add(new Separator());

        GroupSummary[] groups;
        try
        {
            groups = _core.GetBarSnapshot().AllGroups.ToArray();
        }
        catch (StickyCoreException ex)
        {
            System.Diagnostics.Trace.WriteLine($"F1 move menu groups failed ({ex.Status}): {ex.Message}");
            groups = Array.Empty<GroupSummary>();
        }

        foreach (GroupSummary group in groups)
        {
            var item = new MenuItem
            {
                Header = (group.Id == _groupId ? "✓ " : "") + group.Name,
            };
            long gid = group.Id;
            string name = group.Name;
            item.Click += (_, _) =>
            {
                _menuPicked = true;
                MoveToGroup(gid, name);
            };
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());

        var newGroup = new MenuItem { Header = "＋ New group…" };
        newGroup.Click += (_, _) =>
        {
            _menuPicked = true;
            _pendingNewGroup = true;
        };
        menu.Items.Add(newGroup);

        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.PlacementTarget = GroupBadge;

        _menuPicked = false;
        _pendingNewGroup = false;
        _suppressDeactivate = true;
        menu.Closed += OnMoveMenuClosed;
        menu.IsOpen = true;
    }

    private void OnMoveMenuClosed(object? sender, RoutedEventArgs e)
    {
        _suppressDeactivate = false;

        if (_menuPicked)
        {
            _menuPicked = false;
            if (_pendingNewGroup)
            {
                _pendingNewGroup = false;
                BeginNewGroup();
            }
            else
            {
                FocusHelper.ForceActivateAndFocus(this, new WindowInteropHelper(this).Handle, BodyBox);
            }

            return;
        }

        if (!IsActive)
        {
            // The suppression ate the click-away; click-outside closes immediately.
            CloseInternal(restoreForeground: false, raiseEvent: true);
        }
    }

    private void MoveToGroup(long? groupId, string? groupName)
    {
        if (groupId == _groupId)
        {
            return; // already there
        }

        try
        {
            Note note = _core.SetNoteGroup(_session.NoteId, groupId);
            UpdateGroupBadge(note.GroupId, groupName);
            NoteMoved?.Invoke(this, new NoteMovedEventArgs(note.GroupId));
        }
        catch (StickyCoreException ex)
        {
            System.Diagnostics.Trace.WriteLine($"F1 SetNoteGroup failed ({ex.Status}): {ex.Message}");
        }
    }

    private void BeginNewGroup()
    {
        NewGroupPanel.Visibility = Visibility.Visible;
        NewGroupBox.Text = string.Empty;
        FocusHelper.ForceActivateAndFocus(this, new WindowInteropHelper(this).Handle, NewGroupBox);
        NewGroupBox.SelectAll();
    }

    private void CreateGroupAndMove()
    {
        string name = NewGroupBox.Text.Trim();
        if (name.Length == 0)
        {
            NewGroupBox.Focus(); // blank name: stay in the inline box, create nothing
            return;
        }

        try
        {
            GroupSummary group = _core.CreateGroup(name, pinned: true); // §10: new groups are pinned by default
            Note note = _core.SetNoteGroup(_session.NoteId, group.Id);
            UpdateGroupBadge(note.GroupId, group.Name);
            NoteMoved?.Invoke(this, new NoteMovedEventArgs(note.GroupId));
            NewGroupPanel.Visibility = Visibility.Collapsed;
        }
        catch (StickyCoreException ex)
        {
            System.Diagnostics.Trace.WriteLine($"F1 CreateGroup/SetNoteGroup failed ({ex.Status}): {ex.Message}");
        }
    }

    private void OnNewGroupKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CreateGroupAndMove();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            NewGroupPanel.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }
    }

    private void OnCreateGroupClick(object sender, RoutedEventArgs e) => CreateGroupAndMove();

    private void OnCancelNewGroupClick(object sender, RoutedEventArgs e)
    {
        NewGroupPanel.Visibility = Visibility.Collapsed;
        BodyBox.Focus();
    }

    private void UpdateGroupBadge(long? groupId, string? groupName)
    {
        _groupId = groupId;
        GroupBadge.Content = groupId.HasValue && groupName is { Length: > 0 }
            ? "● " + groupName + " ▾"
            : "Move to group ▾";
        GroupBadge.Visibility = Visibility.Visible;
    }

    /// <summary>F6: context-menu "＋ New group…" — reuse the F1 inline new-group panel.</summary>
    public void BeginMoveToNewGroup() => BeginNewGroup();

    /// <summary>F6: the note was moved from a context menu while this window was open — refresh the header.</summary>
    public void RefreshGroupBadge(long? groupId, string? groupName) => UpdateGroupBadge(groupId, groupName);

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(BodyBox.Text) && string.IsNullOrWhiteSpace(TitleBox.Text))
        {
            DeleteConfirmed();
            return;
        }

        HeaderPanel.Visibility = Visibility.Collapsed;
        ConfirmPanel.Visibility = Visibility.Visible;
        NewGroupPanel.Visibility = Visibility.Collapsed;
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

    private void OnTitleTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _session.TitleChanged(TitleBox.Text);
        UpdateTitlePlaceholder();
    }

    private void UpdateTitlePlaceholder()
    {
        TitlePlaceholder.Visibility =
            string.IsNullOrEmpty(TitleBox.Text) && !TitleBox.IsKeyboardFocused
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void OnTitleGotFocus(object sender, RoutedEventArgs e) =>
        TitlePlaceholder.Visibility = Visibility.Collapsed;

    private void OnTitleLostFocus(object sender, RoutedEventArgs e) =>
        TitlePlaceholder.Visibility = string.IsNullOrEmpty(TitleBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void OnTitleKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            // Single-line title: Enter commits and moves to the body.
            _session.Flush();
            BodyBox.Focus();
            BodyBox.CaretIndex = BodyBox.Text.Length;
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            // Esc cancels the title edit; it does NOT close the note (the body's Esc still closes).
            _session.RevertTitle();
            TitleBox.Text = _session.CurrentTitle;
            UpdateTitlePlaceholder();
            BodyBox.Focus();
            e.Handled = true;
        }
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (_closed || _suppressDeactivate)
        {
            return; // closed, or the move menu owns the click-away decision
        }

        Diag.Log($"Note.Deactivated noteId={_session.NoteId} everActivated={_everActivated} isActive={IsActive} fgOwn={Win32.IsOwnAppWindow(Win32.GetForegroundWindow())} fgIsSelf={Win32.GetForegroundWindow() == new WindowInteropHelper(this).Handle}");

        // Click-outside (or hand-off to another app window) closes the note (blueprint §1), but a
        // Deactivated can also be SPURIOUS: force-activation (AttachThreadInput), a topmost re-show, or
        // an inline-edit hand-off deliver one while the editor still keeps the keyboard — closing on it
        // killed freshly opened notes. Let the deactivation settle one tick, then decide with the policy.
        _deactivateSettleTimer ??= CreateDeactivateSettleTimer();
        _deactivateSettleTimer.Stop();
        _deactivateSettleTimer.Start();
    }

    private DispatcherTimer CreateDeactivateSettleTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DeactivateSettleMs) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            CloseIfDeactivationSettled();
        };
        return timer;
    }

    /// <summary>Settled deactivation: close only if the note is genuinely no longer active.</summary>
    private void CloseIfDeactivationSettled()
    {
        if (_closed || _suppressDeactivate)
        {
            return;
        }

        nint hwnd = new WindowInteropHelper(this).Handle;
        nint fg = Win32.GetForegroundWindow();
        bool ownsForeground = hwnd != nint.Zero && fg == hwnd;
        bool externalForeground = fg != nint.Zero && !Win32.IsOwnAppWindow(fg);
        bool close = NoteClosePolicy.ShouldCloseOnDeactivate(
            closed: false,
            suppressDeactivate: false,
            everActivated: _everActivated,
            isActive: IsActive,
            ownsForeground: ownsForeground,
            externalForeground: externalForeground);
        Diag.Log($"Note.SETTLE noteId={_session.NoteId} close={close} isActive={IsActive} ownsFg={ownsForeground} externalFg={externalForeground} reasserted={_reassertedFocus}");

        if (!close)
        {
            // The focus did not land on another app: one of our non-activating surfaces (bar, dropdown,
            // side list, trigger) handed it back mid-open. Re-assert ONCE so the user keeps typing AND so a
            // later genuine click-away still delivers a Deactivated to close on.
            // Stand down when the foreground is one of OUR ACTIVATABLE windows: that is an inline editor
            // (group Rename / + New Group) which took focus on purpose — pulling it back would deactivate
            // the inline box and commit/cancel it a settle-timer later.
            bool userStartedInlineEdit = FocusHelper.IsOwnActivatableForeground();
            long sinceReassert = Environment.TickCount64 - _lastReassertTick;
            if (!_reassertedFocus && !_reasserting && _everActivated && hwnd != nint.Zero
                && sinceReassert >= ReassertCooldownMs && !userStartedInlineEdit)
            {
                _reassertedFocus = true;
                _reasserting = true;
                _lastReassertTick = Environment.TickCount64;
                Diag.Log("Note.SETTLE re-asserting focus (bounce filter)");
                try
                {
                    FocusHelper.ForceActivateAndFocus(this, hwnd, BodyBox);
                }
                finally
                {
                    _reasserting = false; // our own activation must not re-arm the one-shot
                }
            }
            else
            {
                Diag.Log($"Note.SETTLE stand-down reassert (inlineEdit={userStartedInlineEdit} reasserting={_reasserting} sinceReassert={sinceReassert}ms)");
            }

            return;
        }

        EditorDeactivated?.Invoke(this, EventArgs.Empty);
        CloseInternal(restoreForeground: false, raiseEvent: true);
    }

    private NoteCloseOutcome CloseInternal(bool restoreForeground, bool raiseEvent, bool alreadyDeleted = false)
    {
        if (_closed)
        {
            return NoteCloseOutcome.Unchanged;
        }

        string caller = new System.Diagnostics.StackTrace(1, false).GetFrame(0)?.GetMethod()?.Name ?? "?";
        Diag.Log($"Note.CloseInternal noteId={_session.NoteId} reason={caller} restoreForeground={restoreForeground} raiseEvent={raiseEvent}");
        _closed = true;
        _autosaveTimer.Stop();
        _deactivateSettleTimer?.Stop(); // a queued settle-close must not fire on an already-closed note

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

/// <summary>F1: raised after a note is moved into a group or back to standalone.</summary>
public sealed class NoteMovedEventArgs : EventArgs
{
    public NoteMovedEventArgs(long? groupId) => GroupId = groupId;

    /// <summary>New group id, or null when the note is now standalone.</summary>
    public long? GroupId { get; }
}
