using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using StickyNotes.App.Core;
using StickyNotes.App.Interop;
using StickyNotes.App.Windows;

namespace StickyNotes.App.State;

/// <summary>
/// Thin UI orchestrator wiring the HWND-free <see cref="BarStateMachine"/> to the real windows
/// (blueprint §8.1). Owns the per-monitor TriggerWindows, the (pre-created, hidden) BarWindow,
/// the 50ms UI-thread tick, and the <see cref="HitRegionTracker"/> (push model — surfaces are
/// re-published on the dispatcher whenever the layout changes; tracker events are unused so
/// nothing crosses the thread boundary raw).
/// </summary>
public sealed class ShellCoordinator : IDisposable
{
    private const int TickIntervalMs = 50;

    /// <summary>Consecutive "cursor outside every surface" polls required before the exit cascade starts (pose hysteresis).</summary>
    private const int OutsidePollsBeforeExit = 2;

    /// <summary>v0.2.2+: grace before a hovered group side list closes once the cursor has left both the row and the list.</summary>
    private const int PreviewLeaveGraceMs = 200;

    /// <summary>Grace before an open dropdown closes once the cursor has left its whole keep-open cluster (chip+corridor, list, side preview, list-owned note).</summary>
    private const int ListViewLeaveGraceMs = 200;

    private readonly string _dbPath;
    private readonly ScreenPositioner _positioner = new();
    private readonly DispatcherTimer _tick;
    private readonly HitRegionTracker _tracker = new();

    private readonly List<TriggerWindow> _triggers = new();
    private readonly BarWindow _bar;
    private readonly GroupListWindow _list;

    private StickyCoreClient? _core;
    private BarSnapshot? _lastSnapshot;
    private string? _openListAnchorTag; // anchor tag of the currently-open GroupListWindow ("groups-menu" | "group:{id}")
    private bool _listOpenedByHover;    // the open dropdown appeared from a hover dwell (not a click): a click on the chip keeps it open
    private long? _listOwnedNoteId;      // note window spawned from the open dropdown: only it keeps the dropdown open (strict origin)
    private DateTime? _listLeaveGraceUtc; // cursor first seen outside the dropdown's keep-open cluster → grace countdown
    private NoteSort _allNotesSort = NoteSort.Recent; // the all-notes dropdown's order, remembered between opens
    private ContextMenu? _activeMenu;   // F6: the open note/group menu (keeps the bar alive; self-heals via IsOpen)

    private NoteWindow? _noteWindow;       // max 1 open — opening a second flushes + replaces the first
    private QuickNoteWindow? _quickNoteWindow;
    private DispatcherTimer? _idleTrimTimer;

    // v0.2.2+ (F5): hovering a GROUP ROW inside the "Groups ▾" dropdown opens this side list to the RIGHT
    // of the dropdown (aligned with the row). Additive — the dropdown stays open. The dwell is the pure
    // HoverDwellTracker (owned by GroupListWindow); the ~200ms leave grace runs off the existing 50ms tick.
    private GroupPreviewWindow? _groupPreview;
    private PxRect _previewRowRect;      // physical rect of that row (stale if the list scrolls mid-hover)
    private bool _previewRowInside;      // cursor over the hovered group row
    private bool _previewListInside;     // cursor over the side list itself
    private DateTime? _previewGraceUtc;  // cursor left both row + side list → grace countdown

    private nint _activeMonitor; // monitor of the pending/open reveal (last trigger entered)
    private DateTime _lastTickUtc;
    private bool _started;
    private bool _disposed;

    // v0.2.2 "consistent close": edge-detect the cursor pose, remember when it left every surface
    // (the 2s linear bar fade runs off that timestamp), and flag the close cascade so the editors'
    // own close handlers don't classify the cursor-out as "deactivated to an external window".
    private readonly ExitPoseTracker _exitPose = new();
    private DateTime? _outsideSinceUtc;
    private bool _exitClosing;
    private int _outsidePolls; // consecutive outside ticks (the pose is poll evidence, never a single-sample trigger)
    private bool _exitPending;   // a real cursor-out EDGE was seen and is still waiting for its hysteresis ticks

    /// <summary>
    /// Cursor counts as inside while an editor is being closed by <see cref="OnCursorLeftApp"/>, during a
    /// fade, on a live surface, or while an editor holds the focus lock. The last clause is what lets a
    /// focused editor close into OpenHover (so the tick starts the fade) instead of an immediate hide.
    /// </summary>
    private bool CursorInSurfaces =>
        _exitClosing ||
        _outsideSinceUtc is not null ||
        _tracker.IsCursorInside ||
        Machine.State == BarState.OpenFocusedLock;

    /// <summary>Stop the 50ms UI tick entirely whenever the machine is Hidden (0% idle CPU); restart on first activity.</summary>
    private void UpdateTickState()
    {
        if (_disposed)
        {
            if (_tick.IsEnabled)
            {
                _tick.Stop();
            }

            return;
        }

        if (Machine.PollingActive)
        {
            if (!_tick.IsEnabled)
            {
                _lastTickUtc = DateTime.UtcNow;
                _tick.Start();
            }
        }
        else if (_tick.IsEnabled)
        {
            _tick.Stop();
        }
    }

    public ShellCoordinator(string dbPath)
    {
        _dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));
        _bar = new BarWindow();
        _bar.QuickCaptureRequested += (_, _) => OpenQuickNote();
        _bar.ChipActivated += OnChipActivated;
        _bar.ChipHovered += OnChipHovered;
        _bar.ContextMenuRequested += OnBarContextMenuRequested;

        _list = new GroupListWindow();
        _list.NewNoteRequested += OnListNewNoteRequested;
        _list.NoteClicked += OnListNoteClicked;
        _list.GroupRowActivated += OnListGroupRowActivated;
        _list.GroupRowHover += OnListGroupRowHovered;
        _list.GroupPinToggled += OnListGroupPinToggled;
        _list.GroupCreateCommitted += OnListGroupCreateCommitted;
        _list.GroupRenameCommitted += OnListGroupRenameCommitted;
        _list.GroupDeleteConfirmed += OnListGroupDeleteConfirmed;
        _list.ContextMenuRequested += OnListContextMenuRequested;
        _list.CloseRequested += (_, _) => CloseList();
        _list.SortToggleRequested += OnAllNotesSortToggled;
        _list.InlineEditStarted += (_, _) => Machine.EditorFocused();
        _list.InlineEditEnded += (_, _) =>
        {
            if (_disposed)
            {
                return; // shutdown already drained the queue
            }

            Machine.EditorDeactivated(
                toExternalWindow: !Win32.IsOwnAppWindow(Win32.GetForegroundWindow()),
                cursorInSurfaces: CursorInSurfaces);

            if (EditorClosedOutside)
            {
                StartExitFade(); // cursor was outside the whole time: start the 2s fade now (N3)
            }
        };

        _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TickIntervalMs) };
        _tick.Tick += OnTick;

        _groupPreview = new GroupPreviewWindow();
        _groupPreview.NoteClicked += OnPreviewNoteClicked;
        _groupPreview.PointerEnteredLeft += (_, inside) =>
        {
            _previewListInside = inside;
            if (inside)
            {
                _previewGraceUtc = null; // cursor stepped from the row into the side list
            }
            else if (!_previewRowInside)
            {
                _previewGraceUtc = DateTime.UtcNow;
            }
        };

        // F4: read-only hover previews for note chips/rows (never mark the note opened).
        _bar.PreviewResolver = ResolveNotePreview;
        _list.PreviewResolver = ResolveNotePreview;
        _groupPreview.PreviewResolver = ResolveNotePreview; // Bug 2: side-list note rows get the same bubble
    }

    /// <summary>30s-delayed one-shot idle trim: hides memory pressure without stuttering the 100ms hide animation or evicting the pre-created windows.</summary>
    private void ScheduleIdleTrim()
    {
        if (_idleTrimTimer is null)
        {
            _idleTrimTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _idleTrimTimer.Tick += (_, _) =>
            {
                _idleTrimTimer!.Stop();
                if (_disposed)
                {
                    return;
                }

                GC.Collect();
                GC.WaitForPendingFinalizers();
                _ = EmptyWorkingSet(new IntPtr(-1)); // pseudo-handle to the current process
            };
        }

        _idleTrimTimer.Stop();
        _idleTrimTimer.Start();
    }

    public BarStateMachine Machine { get; } = new();

    /// <summary>Open the database, create one TriggerWindow per monitor, and start the tick.</summary>
    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _core = new StickyCoreClient(_dbPath);

        // Pre-create the HWNDs hidden (Visibility.Hidden) without ever showing the bar.
        _ = new WindowInteropHelper(_bar).EnsureHandle();
        _ = new WindowInteropHelper(_list).EnsureHandle();

        // Prime the bar with real data so the reveal path never renders empty chips.
        RefreshBar();

        RebuildTriggers();

        _lastTickUtc = DateTime.UtcNow;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        UpdateTickState(); // Hidden at startup: tick stays stopped; TriggerEntered restarts it
    }

    /// <summary>Flush + hide everything and release native resources. Idempotent.</summary>
    public void Shutdown()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _tick.Stop();
        _idleTrimTimer?.Stop();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;

        Machine.AppExit();
        foreach (ShellAction action in Machine.DrainActions())
        {
            ApplyAction(action);
        }

        foreach (TriggerWindow trigger in _triggers)
        {
            trigger.Close();
        }

        _triggers.Clear();
        CloseQuickNote();
        CloseNoteWindow();
        HideGroupPreview();
        _list.Close(); // triggers ForgetWindow via OnClosed
        _groupPreview?.Close(); // the side list is coordinator-owned — close it, don't leak the HWND
        _bar.Close();
        _tracker.Dispose();
        _core?.Dispose();
        _core = null;
    }

    public void Dispose() => Shutdown();

    // ------------------------------------------------------------------ triggers

    private void RebuildTriggers()
    {
        foreach (TriggerWindow trigger in _triggers)
        {
            trigger.TriggerEntered -= OnTriggerEntered;
            trigger.TriggerLeft -= OnTriggerLeft;
            trigger.Close();
        }

        _triggers.Clear();

        foreach ((MonitorBounds bounds, nint hMonitor) in EnumerateMonitors())
        {
            var trigger = new TriggerWindow(bounds, hMonitor);
            trigger.TriggerEntered += OnTriggerEntered;
            trigger.TriggerLeft += OnTriggerLeft;
            _triggers.Add(trigger);
            trigger.ShowOnMonitor();
        }

        // F3: rebuilt triggers are placed HWND_TOPMOST and can land above a visible bar. Raise the
        // bar again without replaying the reveal, so the taller hit strip never covers its chips.
        if (_bar.Visibility == Visibility.Visible)
        {
            _bar.BringToFront();
        }
    }

    private void OnTriggerEntered(object? sender, TriggerMonitorEventArgs e)
    {
        _activeMonitor = e.Monitor;
        Machine.TriggerEntered(e.Monitor);
        UpdateTickState(); // wake the tick if the machine wants polling (DwellPending...)
    }

    private void OnTriggerLeft(object? sender, TriggerMonitorEventArgs e)
    {
        Machine.TriggerLeft(); // Dwell may be cancelled → machine may go Hidden
        UpdateTickState();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        // SystemEvents fires on a threadpool thread — all WPF work must hop to the dispatcher.
        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed)
            {
                return; // shutdown already ran — a queued event must not resurrect the shell
            }

            Machine.DisplaySettingsChanged();
            foreach (ShellAction action in Machine.DrainActions())
            {
                ApplyAction(action);
            }

            RebuildTriggers();
            UpdateTickState();
        }));
    }

    // ------------------------------------------------------------------ tick

    private void OnTick(object? sender, EventArgs e)
    {
        DateTime now = DateTime.UtcNow;
        int elapsed = Math.Max(1, (int)(now - _lastTickUtc).TotalMilliseconds);
        _lastTickUtc = now;

        bool cursorInTrigger = CursorInTrigger();

        // v0.2.2 "consistent close": inside = any live app surface (incl. an open context menu).
        bool inside = _tracker.IsCursorInside || (_activeMenu is { IsOpen: true });
        ExitPoseTransition transition = _exitPose.Update(inside);

        // The pose comes from a 50 ms background poll, and the cursor can cross the gap between two surfaces
        // (bar -> note/quick note, bar -> dropdown) in between samples. Requiring two consecutive outside
        // polls means one stale or edge sample can never start the exit cascade on a cursor that is actually
        // moving onto an editor.
        // The Left EDGE is latched: it can only ever be seen on the first outside tick, so the 2-poll
        // hysteresis must not lose it (and the open dropdown must not veto an edge that already happened —
        // that swallowed the fade and let the machine hide everything after its 200ms grace instead).
        if (transition == ExitPoseTransition.Left)
        {
            _exitPending = true;
        }

        if (inside)
        {
            _exitPending = false;
            _outsidePolls = 0;
        }
        else
        {
            _outsidePolls++;
        }

        if (transition == ExitPoseTransition.Entered)
        {
            OnCursorEnteredApp(); // cancel the fade
        }
        else if (_bar.Visibility == Visibility.Visible && _outsideSinceUtc is null && !inside
                 && _outsidePolls >= OutsidePollsBeforeExit
                 && (_exitPending || _list.Visibility != Visibility.Visible))
        {
            _exitPending = false; // consumed
            // Either a latched cursor-out EDGE just completed its 2-poll hysteresis, or (no edge: e.g. the bar
            // was revealed while the cursor was already outside) the pose has stayed outside for it and no
            // dropdown is open. An open dropdown never vetoes a latched edge — vetoing it swallowed the 2 s
            // fade entirely and let the machine hide everything after its 200ms grace instead.
            OnCursorLeftApp();
        }

        if (_groupPreview is { } preview && preview.Visibility == Visibility.Visible && _previewGraceUtc is { } grace)
        {
            // ~200ms leave grace for the side list: cursor left the group row AND the side list. A note opened
            // from that side list counts as part of the tree — without this the tree closed 200ms after the
            // cursor moved onto its own note ("the note alone in mid air").
            if ((long)(now - grace).TotalMilliseconds >= PreviewLeaveGraceMs && !CursorOverOpenNote())
            {
                HideGroupPreview();
            }
        }

        // Bug 1: an open dropdown stays open ONLY while the cursor is inside its keep-open cluster (anchor
        // chip + chip→list corridor, the list itself, the side preview, and the note it spawned). The bar or
        // an unrelated note no longer holds it open. This is fully INDEPENDENT of the bar's 2 s fade below:
        // it only ever closes the dropdown, never starts the fade. Suppressed while a focused inline edit or
        // an originating context menu owns the interaction (so the field/menu isn't yanked out from under the
        // user); the grace timer is held reset in that window.
        if (_openListAnchorTag is not null)
        {
            bool suppress = _list.IsEditing || _activeMenu is { IsOpen: true };
            if (suppress)
            {
                _listLeaveGraceUtc = null;
            }
            else if (CursorInListKeepOpenRegion())
            {
                _listLeaveGraceUtc = null;
            }
            else if (_listLeaveGraceUtc is null)
            {
                _listLeaveGraceUtc = now;
            }
            else if (_listLeaveGraceUtc is { } left && (long)(now - left).TotalMilliseconds >= ListViewLeaveGraceMs)
            {
                CloseList();
            }
        }

        if (_outsideSinceUtc is { } since)
        {
            long ms = (long)(now - since).TotalMilliseconds;
            _bar.SetOpacity(BarExitPolicy.OpacityAt(ms));
            if (BarExitPolicy.ShouldHide(ms))
            {
                _outsideSinceUtc = null;
                Machine.HideNow();
            }
        }

        bool fullscreen = false;
        if (Machine.State == BarState.DwellPending)
        {
            // Spec: fullscreen is consulted ONLY at dwell completion (and not via zero-background hooks).
            fullscreen = _activeMonitor != nint.Zero && FocusHelper.IsFullscreenOnMonitor(_activeMonitor);
        }

        // The 2s fade below is the ONLY exit policy. Until v0.2.2 the coordinator also had to park the
        // machine's own 200ms ExitGrace by passing a half-true CursorInSurfaces; that second policy could
        // hide everything behind the coordinator's back (a new note vanished the moment the cursor left the
        // bar for it). The machine no longer times an exit, so it needs to know nothing about the cursor.
        Machine.Advance(elapsed, new BarProbe(cursorInTrigger, fullscreen));

        foreach (ShellAction action in Machine.DrainActions())
        {
            ApplyAction(action);
        }

        UpdateTickState(); // stop the 50ms tick the moment the machine returns to Hidden
    }

    /// <summary>
    /// The cursor left every app surface. Only while the bar is actually visible and no editor holds
    /// the focus lock (F1-rev: never yank a focused editor on cursor-out): close every flyout/editor
    /// and start the 2s linear bar fade.
    /// </summary>
    private void OnCursorLeftApp()
    {
        if (_bar.Visibility != Visibility.Visible)
        {
            Diag.Log("OnCursorLeftApp: bar hidden -> ignore");
            return;
        }

        if (_outsideSinceUtc is not null)
        {
            return; // fade already running — re-entering cancels it via OnCursorEnteredApp, not here
        }

        if (Machine.State == BarState.OpenFocusedLock)
        {
            Diag.Log("OnCursorLeftApp: OpenFocusedLock -> ignore");
            return; // F1-rev: the editor keeps keyboard focus; the bar stays opaque
        }

        if (FocusedEditorOwnsForeground())
        {
            Diag.Log($"OnCursorLeftApp: editor owns foreground -> ignore (state={Machine.State})");
            // The editor really holds the OS foreground (a note opened focused, or a fresh note whose
            // EditorFocused() landed after the machine left the lock): fading would call CloseNoteWindow
            // and kill a note the user is typing in. The machine's own EditorDeactivated path closes it
            // when the editor actually loses focus.
            return;
        }

        StartExitFade();
    }

    /// <summary>
    /// True while an editor window (note / quick note) is the one the user is typing into. Checked two ways
    /// because a forced activation can be only half-successful: the HWND may own neither the OS foreground
    /// nor a WPF-activation flag, and then a cursor-out tick would fade and CloseNoteWindow a note that just
    /// appeared (the "new note closes instantly" bug). WPF's <c>IsActive</c> covers the WM_ACTIVATE that
    /// arrives without the foreground being granted.
    /// </summary>
    private bool FocusedEditorOwnsForeground()
    {
        nint fg = Win32.GetForegroundWindow();

        if (_noteWindow is { } note)
        {
            if (note.IsActive || (fg != nint.Zero && new WindowInteropHelper(note).Handle == fg))
            {
                return true;
            }
        }

        if (_quickNoteWindow is { } quick)
        {
            if (quick.IsActive || (fg != nint.Zero && new WindowInteropHelper(quick).Handle == fg))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Close the dropdown and start the 2s linear bar fade. Called on the cursor-out edge (and when an
    /// editor closes while the cursor is already outside). The cursor has left EVERY app surface, so the
    /// editors go now rather than at fade end: the fade is about the bar, and keeping an editor alive for
    /// 2s after the cursor abandoned it made the note outlive the cursor.
    /// </summary>
    private void StartExitFade()
    {
        Diag.Log($"StartExitFade state={Machine.State} cursorInside={_tracker.IsCursorInside} noteOpen={_noteWindow is not null} quickOpen={_quickNoteWindow is not null}");

        // Claim the exit BEFORE closing anything: CloseNoteWindow / CloseQuickNote raise their closed
        // events, which reach this method again through EditorClosedOutside. Setting the timestamp first
        // turns that re-entry into a no-op instead of a second fade.
        _outsideSinceUtc = DateTime.UtcNow;

        _exitClosing = true;
        CloseList();        // commits an inline edit, then hides
        CloseNoteWindow();  // cursor left every surface: the note goes NOW (an empty new note is deleted)
        CloseQuickNote();   // ... and the quick capture commits-if-non-empty, exactly like click-outside
        _exitClosing = false;
        RefreshViews();     // a deleted empty note / a committed quick capture must not leave a stale chip

        // Re-publish NOW so the tracker holds only surfaces that really exist during the fade: CloseList()
        // above just dropped the dropdown (and the editors are gone), and a stale rect would make a cursor
        // over empty space read as "inside", cancelling the fade and popping the bar back to opaque with
        // nothing under it. Idempotent.
        double dpi = _bar.CurrentBounds is not null && _bar.HMonitor != nint.Zero
            ? Win32.DpiScaleOf(_bar.HMonitor) ?? 1.0
            : 1.0;
        PublishSurfaces(dpi);
    }

    /// <summary>True when an editor just fell out of the focus lock with the cursor outside: fade must start now.</summary>
    private bool EditorClosedOutside =>
        Machine.State == BarState.OpenHover && _outsideSinceUtc is null && _bar.Visibility == Visibility.Visible && !_exitPose.IsInside;

    /// <summary>The cursor came back onto an app surface during the fade: cancel it and restore opacity.</summary>
    private void OnCursorEnteredApp()
    {
        _outsideSinceUtc = null;
        _bar.SetOpacity(1.0);
    }

    private bool CursorInTrigger()
    {
        foreach (TriggerWindow trigger in _triggers)
        {
            if (trigger.TryGetPhysicalRect(out PxRect r) && CursorIn(r))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Hit-test one published surface (physical pixels) against the OS cursor.</summary>
    private static bool CursorIn(PxRect r) =>
        Win32.GetCursorPos(out POINT pt)
        && pt.X >= r.Left && pt.X < r.Right
        && pt.Y >= r.Top && pt.Y < r.Bottom;

    /// <summary>
    /// True while the cursor is over the open note window. A note opened from the group side list counts
    /// as part of that tree: the side list's leave grace is suppressed while the cursor is on its note, so
    /// the tree stays open until the cursor actually leaves (issue 3).
    /// </summary>
    private bool CursorOverOpenNote() =>
        _noteWindow is { } note && note.TryGetPhysicalRect(out PxRect r) && CursorIn(r);

    /// <summary>
    /// Bug 1: the dropdown's keep-open cluster — the anchor chip (+ the chip→list corridor that the bar
    /// already publishes), the list window, the side preview (all-groups only), and the note the dropdown
    /// spawned (strict origin: a bar-opened note is NOT in the cluster). Cursor anywhere here keeps the
    /// dropdown open; anywhere else (bar, unrelated note, empty space) does not.
    /// </summary>
    private bool CursorInListKeepOpenRegion()
    {
        if (_openListAnchorTag is null)
        {
            return false;
        }

        // Anchor chip + the few-px corridor down to the list (mirrors PublishSurfaces' bar extension), so a
        // cursor resting on the chip that opened the dropdown — or crossing into the list — reads as inside.
        if (_bar.TryGetChipRect(_openListAnchorTag, out PxRect chip))
        {
            chip = chip with { Height = chip.Height + ScreenPositioner.GroupListGapPx };
            if (CursorIn(chip))
            {
                return true;
            }
        }

        if (_list.TryGetPhysicalRect(out PxRect listRect) && CursorIn(listRect))
        {
            return true;
        }

        if (_groupPreview is not null && _groupPreview.TryGetPhysicalRect(out PxRect previewRect) && CursorIn(previewRect))
        {
            return true;
        }

        // Only the note this dropdown opened (strict origin). A bar-opened note must not hold it open.
        if (_noteWindow is not null && _noteWindow.NoteId == _listOwnedNoteId
            && _noteWindow.TryGetPhysicalRect(out PxRect noteRect) && CursorIn(noteRect))
        {
            return true;
        }

        return false;
    }

    // ------------------------------------------------------------------ actions

    private void ApplyAction(ShellAction action)
    {
        Diag.Log($"ApplyAction {action.Kind} state={Machine.State} noteOpen={_noteWindow is not null}");
        switch (action.Kind)
        {
            case ShellActionKind.ShowBar:
                ShowBar(action.Monitor);
                break;
            case ShellActionKind.FlushAndHideAll:
                HideAll();
                break;
        }
    }

    private void ShowBar(nint monitor)
    {
        _idleTrimTimer?.Stop(); // a reveal in flight must not be interrupted by the deferred trim

        MonitorBounds? bounds = FindBounds(monitor) ?? FindBounds(_activeMonitor) ?? (_triggers.Count > 0 ? _triggers[0].Bounds : null);
        if (bounds is null)
        {
            return;
        }

        double dpi = Win32.DpiScaleOf(monitor) ?? VisualTreeHelper.GetDpi(_bar).DpiScaleX;
        _bar.ShowOnMonitor(bounds, monitor, dpi);

        PublishSurfaces(dpi);
        _tracker.SetVisible(true);
    }

    /// <summary>Re-publish trigger + bar + (if open) flyout rects to the hit tracker.</summary>
    private void PublishSurfaces(double dpi)
    {
        var surfaces = new List<PxRect>();
        if (_bar.TryGetPhysicalRect(out PxRect barRect))
        {
            if (_openListAnchorTag is not null)
            {
                // While a dropdown is open, the chip→dropdown corridor (GroupListGapPx) counts as inside, so
                // a cursor sample landing in that 4px gap can never close the dropdown. Published ONLY while
                // the list is open — CloseList re-publishes, so no phantom surface outlives it.
                barRect = barRect with { Height = barRect.Height + ScreenPositioner.GroupListGapPx };
            }

            surfaces.Add(barRect);
        }

        foreach (TriggerWindow trigger in _triggers)
        {
            if (trigger.TryGetPhysicalRect(out PxRect triggerRect))
            {
                surfaces.Add(triggerRect);
            }
        }

        if (_list.TryGetPhysicalRect(out PxRect listRect))
        {
            surfaces.Add(listRect);
        }

        if (_noteWindow is not null && _noteWindow.TryGetPhysicalRect(out PxRect noteRect))
        {
            surfaces.Add(noteRect);
        }

        if (_quickNoteWindow is not null && _quickNoteWindow.TryGetPhysicalRect(out PxRect quickRect))
        {
            surfaces.Add(quickRect);
        }

        if (_groupPreview is not null && _groupPreview.TryGetPhysicalRect(out PxRect previewRect))
        {
            surfaces.Add(previewRect); // the side list is an app surface too: cursor inside it keeps everything open
        }

        _tracker.Dpi = dpi;
        _tracker.SetSurfaces(surfaces);
    }

    private (MonitorBounds Bounds, double Dpi, nint HMonitor) BarContext()
    {
        MonitorBounds bounds = _bar.CurrentBounds
            ?? (_triggers.Count > 0
                ? _triggers[0].Bounds
                : new MonitorBounds(new PxRect(0, 0, 1920, 1080), new PxRect(0, 0, 1920, 1040)));
        nint hMonitor = _bar.HMonitor != nint.Zero ? _bar.HMonitor : (_triggers.Count > 0 ? _triggers[0].HMonitor : nint.Zero);
        double dpi = Win32.DpiScaleOf(hMonitor) ?? VisualTreeHelper.GetDpi(_bar).DpiScaleX;
        return (bounds, dpi, hMonitor);
    }

    // ------------------------------------------------------------------ note window (max 1 open)

    private void OpenNote(long noteId, Note? existingNote, bool createdThisOpen, PxRect anchorRect, bool anchorIsHorizontalBarItem, bool focused)
    {
        if (_core is null)
        {
            return;
        }

        Diag.Log($"OpenNote id={noteId} createdThisOpen={createdThisOpen} focused={focused} state={Machine.State} fgOwn={Win32.IsOwnAppWindow(Win32.GetForegroundWindow())}");
        CloseNoteWindow(); // max 1 open — opening a second flushes + replaces the first
        _listOwnedNoteId = null; // default: not owned by the dropdown; list-origin callers claim it after this returns

        Note note = existingNote ?? _core.GetNote(noteId, markOpened: true);

        (MonitorBounds bounds, double dpi, _) = BarContext();
        var win = new NoteWindow(_core, note, createdThisOpen);
        win.ClosedByUser += OnNoteWindowClosed;
        win.NoteMoved += OnNoteMoved;
        win.EditorActivated += (_, _) =>
        {
            Diag.Log($"EditorActivated -> EditorFocused (state was {Machine.State})");
            Machine.EditorFocused(); // lock the bar ONLY when the note really takes focus
            Diag.Log($"EditorFocused -> state={Machine.State}");
        };
        _noteWindow = win;
        win.ShowNote(anchorRect, bounds, dpi, anchorIsHorizontalBarItem, focused);

        Diag.Log($"OpenNote shown: fgIsNote={Win32.GetForegroundWindow() == new WindowInteropHelper(win).Handle} state={Machine.State}");
        PublishSurfaces(dpi);
    }

    private void CloseNoteWindow()
    {
        NoteWindow? win = _noteWindow;
        if (win is null)
        {
            return;
        }

        string caller = new System.Diagnostics.StackTrace(1, false).GetFrame(0)?.GetMethod()?.Name ?? "?";
        Diag.Log($"CloseNoteWindow (reason={caller}) state={Machine.State} fgOwn={Win32.IsOwnAppWindow(Win32.GetForegroundWindow())}");
        _noteWindow = null; // before closing: the ClosedByUser handler would see the stale reference otherwise
        win.ClosedByUser -= OnNoteWindowClosed;
        win.FlushAndClose(); // raises nothing (unsubscribed); flush happens inside
    }

    private void OnNoteWindowClosed(object? sender, NoteWindowClosedEventArgs e)
    {
        Diag.Log($"OnNoteWindowClosed outcome={e.Outcome} noteId={e.NoteId} state={Machine.State} fgOwn={Win32.IsOwnAppWindow(Win32.GetForegroundWindow())} cursorInSurfaces={CursorInSurfaces}");
        if (sender is NoteWindow win && ReferenceEquals(win, _noteWindow))
        {
            _noteWindow = null;
            if (_listOwnedNoteId == e.NoteId)
            {
                _listOwnedNoteId = null; // the dropdown's owned note is gone — nothing left to hold the list open
            }
        }

        Machine.EditorDeactivated(
            toExternalWindow: !Win32.IsOwnAppWindow(Win32.GetForegroundWindow()),
            cursorInSurfaces: CursorInSurfaces);
        if (EditorClosedOutside)
        {
            StartExitFade(); // cursor was outside the whole time: start the 2s fade now (N3)
        }
        RefreshViews();

        double dpi = _bar.HMonitor != nint.Zero ? Win32.DpiScaleOf(_bar.HMonitor) ?? 1.0 : 1.0;
        PublishSurfaces(dpi);
    }

    /// <summary>F1: a note was moved into a group / standalone — re-render bar + open group list now.</summary>
    private void OnNoteMoved(object? sender, NoteMovedEventArgs e)
    {
        RefreshViews();

        double dpi = _bar.HMonitor != nint.Zero ? Win32.DpiScaleOf(_bar.HMonitor) ?? 1.0 : 1.0;
        PublishSurfaces(dpi);
    }

    // ------------------------------------------------------------------ F6: context menus

    private void OnBarContextMenuRequested(object? sender, BarContextMenuEventArgs e)
    {
        if (e.Tag.StartsWith("note:", StringComparison.Ordinal)
            && long.TryParse(e.Tag.AsSpan("note:".Length), out long noteId))
        {
            ShowNoteContextMenu(noteId, e.Target, e.Anchor, fromBar: true);
        }
        else if (e.Tag.StartsWith("group:", StringComparison.Ordinal)
                 && long.TryParse(e.Tag.AsSpan("group:".Length), out long groupId))
        {
            ShowGroupContextMenu(groupId, e.Target, e.Anchor, fromBar: true);
        }
    }

    private void OnListContextMenuRequested(object? sender, GroupListContextMenuEventArgs e)
    {
        if (e.Kind == ContextMenuTargetKind.Note)
        {
            ShowNoteContextMenu(e.Id, e.Target, e.Anchor, fromBar: false);
        }
        else
        {
            ShowGroupContextMenu(e.Id, e.Target, e.Anchor, fromBar: false);
        }
    }

    /// <summary>One code path for every F6 menu: anchor it, arm the bar-hide suppression, clear it on close.</summary>
    private void OpenContextMenu(ContextMenu menu, Button target)
    {
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.PlacementTarget = target;
        _activeMenu = menu;
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_activeMenu, menu))
            {
                _activeMenu = null;
            }
        };
        menu.IsOpen = true;
    }

    /// <summary>
    /// Run a UI refresh after the current ContextMenu has closed. A menu item's Click fires
    /// BEFORE the menu closes; rebuilding the bar/list there removes the menu's PlacementTarget
    /// and the menu never closes (and the bar stays pinned open).
    /// </summary>
    private void AfterMenu(Action action) =>
        _bar.Dispatcher.BeginInvoke(new Action(action), DispatcherPriority.ContextIdle);

    private void ShowNoteContextMenu(long noteId, Button target, PxRect anchor, bool fromBar)
    {
        if (_core is null)
        {
            return;
        }

        Note? note = TryGetNote(noteId);
        var menu = new ContextMenu();

        var open = new MenuItem { Header = "Open" };
        open.Click += (_, _) =>
            AfterMenu(() => OpenNote(noteId, existingNote: null, createdThisOpen: false, anchor, anchorIsHorizontalBarItem: fromBar, focused: false));
        menu.Items.Add(open);

        var pin = new MenuItem { Header = note?.IsPinned == true ? "Unpin" : "Pin" };
        pin.Click += (_, _) => ToggleNotePinned(noteId);
        menu.Items.Add(pin);

        var move = new MenuItem { Header = "Move to group" };
        var standalone = new MenuItem { Header = "Standalone" };
        standalone.Click += (_, _) => MoveNoteToGroup(noteId, null, null);
        move.Items.Add(standalone);
        move.Items.Add(new Separator());

        foreach (GroupSummary group in GroupsSorted())
        {
            var item = new MenuItem { Header = group.Name };
            long gid = group.Id;
            string name = group.Name;
            item.Click += (_, _) => MoveNoteToGroup(noteId, gid, name);
            move.Items.Add(item);
        }

        move.Items.Add(new Separator());
        var newGroup = new MenuItem { Header = "＋ New group…" };
        newGroup.Click += (_, _) => BeginNewGroupForNote(noteId, target, anchor, fromBar);
        move.Items.Add(newGroup);
        menu.Items.Add(move);

        var delete = new MenuItem { Header = "Delete" };
        delete.Click += (_, _) => ConfirmDeleteNote(noteId, target, note);
        menu.Items.Add(delete);

        OpenContextMenu(menu, target);
    }

    private void ShowGroupContextMenu(long groupId, Button target, PxRect anchor, bool fromBar)
    {
        if (_core is null)
        {
            return;
        }

        GroupSummary? group = FindGroup(groupId);
        if (group is null)
        {
            return;
        }

        var menu = new ContextMenu();

        var open = new MenuItem { Header = "Open" };
        open.Click += (_, _) =>
        {
            if (fromBar)
            {
                ToggleList($"group:{groupId}", GroupListMode.OpenGroup, groupId);
            }
            else if (_list.Mode == GroupListMode.OpenAllGroups)
            {
                AfterMenu(() =>
                {
                    _list.ShowGroup(group, GroupNotesSorted(groupId)); // switch in place, keep anchor
                    PublishSurfaces(Win32.DpiScaleOf(_bar.HMonitor) ?? 1.0);
                });
            }
        };
        menu.Items.Add(open);

        var newNote = new MenuItem { Header = "New note" };
        newNote.Click += (_, _) =>
        {
            Note created = _core.CreateNote(groupId, "");
            AfterMenu(() =>
            {
                RefreshViews();
                OpenNote(created.Id, created, createdThisOpen: true, anchor, anchorIsHorizontalBarItem: fromBar, focused: true);
            });
        };
        menu.Items.Add(newNote);

        var add = new MenuItem { Header = "Add note…" };
        List<NoteSummary> candidates;
        try
        {
            candidates = _core.ListAppendTargets(15)
                .Where(n => n.GroupId != groupId) // include standalone, exclude notes already in this group
                .ToList();
        }
        catch (StickyCoreException ex)
        {
            System.Diagnostics.Trace.WriteLine($"F6 ListAppendTargets failed ({ex.Status}): {ex.Message}");
            candidates = new List<NoteSummary>();
        }

        if (candidates.Count == 0)
        {
            add.Items.Add(new MenuItem { Header = "(no notes to add)", IsEnabled = false });
        }
        else
        {
            foreach (NoteSummary candidate in candidates)
            {
                var item = new MenuItem { Header = candidate.DisplayTitle };
                long noteId = candidate.Id;
                item.Click += (_, _) => MoveNoteToGroup(noteId, groupId, group.Name);
                add.Items.Add(item);
            }
        }
        menu.Items.Add(add);

        var rename = new MenuItem { Header = "Rename" };
        rename.Click += (_, _) => AfterMenu(() =>
        {
            OpenGroupInListForMenu(group, anchor, fromBar);
            _list.BeginRename();
        });
        menu.Items.Add(rename);

        var pin = new MenuItem { Header = group.IsPinned ? "Unpin" : "Pin" };
        pin.Click += (_, _) => ToggleGroupPinned(groupId);
        menu.Items.Add(pin);

        var delete = new MenuItem { Header = "Delete" };
        delete.Click += (_, _) => AfterMenu(() =>
        {
            OpenGroupInListForMenu(group, anchor, fromBar);
            _list.BeginDelete();
        });
        menu.Items.Add(delete);

        OpenContextMenu(menu, target);
    }

    /// <summary>
    /// F6: switch the list to OpenGroup mode for Rename/Delete. Pinned bar chips anchor the list;
    /// group-list rows re-render in place (never ToggleList — unpinned groups have no chip).
    /// </summary>
    private void OpenGroupInListForMenu(GroupSummary group, PxRect anchor, bool fromBar)
    {
        _list.ShowGroup(group, GroupNotesSorted(group.Id));

        if (fromBar)
        {
            (MonitorBounds bounds, double dpi, _) = BarContext();
            _list.PositionAndShow(anchor, bounds, dpi);
            _openListAnchorTag = $"group:{group.Id}";
        }

        PublishSurfaces(Win32.DpiScaleOf(_bar.HMonitor) ?? 1.0);
    }

    private void MoveNoteToGroup(long noteId, long? groupId, string? groupName)
    {
        if (_core is null)
        {
            return;
        }

        try
        {
            _core.SetNoteGroup(noteId, groupId);
        }
        catch (StickyCoreException ex)
        {
            System.Diagnostics.Trace.WriteLine($"F6 SetNoteGroup failed ({ex.Status}): {ex.Message}");
            return;
        }

        if (_noteWindow is not null && _noteWindow.NoteId == noteId)
        {
            _noteWindow.RefreshGroupBadge(groupId, groupName);
        }

        AfterMenu(() =>
        {
            RefreshListView(); // a group change doesn't alter the bar chips — avoid the flicker
            double dpi = _bar.HMonitor != nint.Zero ? Win32.DpiScaleOf(_bar.HMonitor) ?? 1.0 : 1.0;
            PublishSurfaces(dpi);
        });
    }

    private void BeginNewGroupForNote(long noteId, Button target, PxRect anchor, bool fromBar)
    {
        if (_noteWindow is not null && _noteWindow.NoteId == noteId)
        {
            _noteWindow.BeginMoveToNewGroup(); // reuse the open note — no recents bump
            return;
        }

        // Option A (approved): open the note focused so the user can type the new group name.
        // Known cost: opening marks it opened (recents bump) and closes any other open note.
        OpenNote(noteId, existingNote: null, createdThisOpen: false, anchor, anchorIsHorizontalBarItem: fromBar, focused: true);
        _noteWindow?.BeginMoveToNewGroup();
    }

    private void ConfirmDeleteNote(long noteId, Button target, Note? note)
    {
        if (_core is null)
        {
            return;
        }

        if (note is null || string.IsNullOrWhiteSpace(note.Body))
        {
            try
            {
                _core.DeleteNote(noteId);
            }
            catch (StickyCoreException)
            {
                // already gone
            }

            AfterMenu(RefreshViews);
            return;
        }

        var confirm = new ContextMenu();
        confirm.Items.Add(new MenuItem { Header = $"Delete '{Truncate(note.DisplayTitle, 40)}'?", IsEnabled = false });

        var yes = new MenuItem { Header = "Delete" };
        yes.Click += (_, _) =>
        {
            try
            {
                _core.DeleteNote(noteId);
            }
            catch (StickyCoreException)
            {
                // already gone
            }

            AfterMenu(RefreshViews);
        };
        confirm.Items.Add(yes);
        confirm.Items.Add(new MenuItem { Header = "Cancel" });

        // Let the first menu's Closed handler run before opening the confirm, otherwise it would
        // clear _contextMenuOpen for the menu that is about to open.
        _bar.Dispatcher.BeginInvoke(new Action(() => OpenContextMenu(confirm, target)), DispatcherPriority.ContextIdle);
    }

    private Note? TryGetNote(long noteId)
    {
        if (_core is null)
        {
            return null;
        }

        try
        {
            return _core.GetNote(noteId, markOpened: false);
        }
        catch (StickyCoreException)
        {
            return null;
        }
    }

    private static string Truncate(string text, int max)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return text.Length <= max ? text : text[..max] + "…";
    }

    // ------------------------------------------------------------------ quick note window

    private void OpenQuickNote()
    {
        if (_core is null)
        {
            return;
        }

        // §8.4: on '+', close any open NoteWindow / GroupList first.
        CloseNoteWindow();
        _openListAnchorTag = null;
        HideGroupPreview();
        _list.CloseList();

        if (_quickNoteWindow is not null)
        {
            _quickNoteWindow.FocusBody(); // already open: just refocus, don't stack a second
            return;
        }

        if (!_bar.TryGetPhysicalRect(out PxRect barRect))
        {
            // Bar fell over between '+' click and open — bail out cleanly (before creating the window).
            return;
        }

        (MonitorBounds bounds, double dpi, _) = BarContext();
        var win = new QuickNoteWindow(_core);
        List<NoteSummary> appendTargets;
        try
        {
            appendTargets = _core.ListAppendTargets(30).ToList();
        }
        catch (StickyCoreException ex)
        {
            System.Diagnostics.Trace.WriteLine($"ListAppendTargets failed ({ex.Status}): {ex.Message}");
            appendTargets = new List<NoteSummary>();
        }

        win.LoadTargets(_lastSnapshot ?? _core.GetBarSnapshot(), appendTargets);
        win.QuickNoteClosed += OnQuickNoteClosed;
        _quickNoteWindow = win;

        // Only take the focus lock when the OS foreground really landed on the editor (P2-7): a failed
        // activation (elevated foreground / UIPI) must not park the machine in OpenFocusedLock forever.
        if (win.ShowQuickNote(barRect, bounds, dpi))
        {
            Machine.EditorFocused();
        }

        PublishSurfaces(dpi);
    }

    private void CloseQuickNote()
    {
        QuickNoteWindow? win = _quickNoteWindow;
        if (win is null)
        {
            return;
        }

        _quickNoteWindow = null;
        win.QuickNoteClosed -= OnQuickNoteClosed;
        win.CloseProgrammatically(QuickNoteClose.ClickOutside); // commit-if-non-empty semantics for bar-hide / exit
    }

    private void OnQuickNoteClosed(object? sender, QuickNoteClosedEventArgs e)
    {
        if (sender is QuickNoteWindow win && ReferenceEquals(win, _quickNoteWindow))
        {
            _quickNoteWindow = null;
        }

        Machine.EditorDeactivated(
            toExternalWindow: !Win32.IsOwnAppWindow(Win32.GetForegroundWindow()),
            cursorInSurfaces: CursorInSurfaces);
        if (EditorClosedOutside)
        {
            StartExitFade(); // cursor was outside the whole time: start the 2s fade now (N3)
        }
        RefreshViews(); // appended/committed note moves to #1 in Recents

        double dpi = _bar.HMonitor != nint.Zero ? Win32.DpiScaleOf(_bar.HMonitor) ?? 1.0 : 1.0;
        PublishSurfaces(dpi);
    }

    /// <summary>Refresh bar + (if open) sync the group list after data changed.</summary>
    private void RefreshViews()
    {
        RefreshBar();
        RefreshListView();
    }

    /// <summary>Refresh only the open group list (used when a move doesn't change the bar chips).</summary>
    private void RefreshListView()
    {
        if (_list.Mode == GroupListMode.OpenGroup && _list.CurrentGroupId is long groupId)
        {
            GroupSummary? group = FindGroup(groupId);
            if (group is not null)
            {
                _list.RefreshGroup(group, GroupNotesSorted(groupId));
            }
        }
        else if (_list.Mode == GroupListMode.OpenAllGroups)
        {
            _list.RefreshAllGroups(GroupsSorted());
        }
        else if (_list.Mode == GroupListMode.OpenAllNotes)
        {
            _list.RefreshAllNotes(LoadAllNotes(_allNotesSort), _allNotesSort);
        }
    }

    /// <summary>Every note (pinned included) for the all-notes dropdown. A core failure yields an empty list rather than a crash.</summary>
    private IReadOnlyList<NoteSummary> LoadAllNotes(NoteSort sort)
    {
        if (_core is null)
        {
            return Array.Empty<NoteSummary>();
        }

        try
        {
            return _core.ListUnpinnedNotes(sort);
        }
        catch (StickyCoreException ex)
        {
            System.Diagnostics.Trace.WriteLine($"ListAllNotes failed ({ex.Status}): {ex.Message}");
            return Array.Empty<NoteSummary>();
        }
        catch (ObjectDisposedException)
        {
            return Array.Empty<NoteSummary>(); // shutdown raced the open
        }
    }

    /// <summary>The all-notes dropdown flipped Date ↔ A→Z: re-query in place, list stays open.</summary>
    private void OnAllNotesSortToggled(NoteSort sort)
    {
        _allNotesSort = sort;
        _list.RefreshAllNotes(LoadAllNotes(_allNotesSort), _allNotesSort);
        PublishSurfaces(Win32.DpiScaleOf(_bar.HMonitor) ?? 1.0);
    }

    // ------------------------------------------------------------------ bar & group list

    /// <summary>
    /// F4: body for the read-only hover preview. markOpened:false keeps recents/ordering untouched.
    /// </summary>
    private string? ResolveNotePreview(long noteId)
    {
        if (_core is null)
        {
            return null;
        }

        try
        {
            return _core.GetNote(noteId, markOpened: false).Body;
        }
        catch (StickyCoreException)
        {
            return null; // deleted / db error — no bubble
        }
        catch (ObjectDisposedException)
        {
            return null; // shutdown raced a hover
        }
    }


    private void OnChipActivated(object? sender, Windows.BarChipEventArgs e)
    {
        OnCursorEnteredApp(); // a click on the bar is inside: cancel any in-flight fade now, not on the next poll
        string tag = e.Tag;
        if (tag == "all-notes")
        {
            ToggleListOrKeepOpen("all-notes", GroupListMode.OpenAllNotes);
        }
        else if (tag == "groups-menu")
        {
            ToggleListOrKeepOpen("groups-menu", GroupListMode.OpenAllGroups);
        }
        else if (tag.StartsWith("group:", StringComparison.Ordinal)
                 && long.TryParse(tag.AsSpan("group:".Length), out long groupId))
        {
            ToggleList(tag, GroupListMode.OpenGroup, groupId);
        }
        else if (tag.StartsWith("note:", StringComparison.Ordinal)
                 && long.TryParse(tag.AsSpan("note:".Length), out long noteId))
        {
            if (_bar.TryGetChipRect(tag, out PxRect anchor))
            {
                OpenNote(noteId, existingNote: null, createdThisOpen: false, anchor, anchorIsHorizontalBarItem: true, focused: false);
            }
        }
    }

    private void ToggleList(string anchorTag, GroupListMode mode, long? groupId)
    {
        if (_openListAnchorTag == anchorTag)
        {
            CloseList();
            return;
        }

        ShowList(anchorTag, mode, groupId);
    }

    /// <summary>
    /// A click on a chip that owns a hover-opened dropdown. If the list is already there because the
    /// cursor DWELLED on this chip, the click keeps it open — closing it under the same pointer would
    /// feel like a double-action. The flag is consumed, so the NEXT click toggles it shut as usual.
    /// </summary>
    private void ToggleListOrKeepOpen(string anchorTag, GroupListMode mode)
    {
        if (_openListAnchorTag == anchorTag && _list.Mode == mode && _listOpenedByHover)
        {
            _listOpenedByHover = false;
            return;
        }

        ToggleList(anchorTag, mode, null);
    }

    /// <summary>Open the dropdown on <paramref name="anchorTag"/> (no toggle semantics — hover-open uses this).</summary>
    private void ShowList(string anchorTag, GroupListMode mode, long? groupId)
    {
        if (!TryGetAnchorAndBounds(anchorTag, out PxRect anchorRect, out MonitorBounds bounds, out double dpi))
        {
            return; // bar hidden — nothing to anchor to
        }

        _listOpenedByHover = false; // a fresh, click-anchored open always starts non-hover

        switch (mode)
        {
            case GroupListMode.OpenAllGroups:
                _list.ShowAllGroups(GroupsSorted());
                break;

            case GroupListMode.OpenAllNotes:
                _list.ShowAllNotes(LoadAllNotes(_allNotesSort), _allNotesSort);
                break;

            default:
                if (!groupId.HasValue)
                {
                    return;
                }

                GroupSummary? group = FindGroup(groupId.Value);
                if (group is null)
                {
                    return;
                }

                _list.ShowGroup(group, GroupNotesSorted(groupId.Value));
                break;
        }

        _list.PositionAndShow(anchorRect, bounds, dpi);
        _openListAnchorTag = anchorTag;
        PublishSurfaces(dpi);
    }

    private void CloseList()
    {
        _listOpenedByHover = false; // never leave a stale keep-open flag behind (the corridor dies with the list)
        _listOwnedNoteId = null;    // the tree's owned note no longer has a list to belong to
        _listLeaveGraceUtc = null;
        if (_openListAnchorTag is null)
        {
            return;
        }

        HideGroupPreview();
        _list.CloseList();
        _openListAnchorTag = null;
        double dpi = _bar.CurrentBounds is not null && _bar.HMonitor != nint.Zero
            ? Win32.DpiScaleOf(_bar.HMonitor) ?? 1.0
            : 1.0;
        PublishSurfaces(dpi);
    }

    private bool TryGetAnchorAndBounds(string anchorTag, out PxRect anchorRect, out MonitorBounds bounds, out double dpi)
    {
        anchorRect = default;
        bounds = _bar.CurrentBounds ?? (_triggers.Count > 0 ? _triggers[0].Bounds : new MonitorBounds(new PxRect(0, 0, 1920, 1080), new PxRect(0, 0, 1920, 1040)));
        dpi = Win32.DpiScaleOf(_bar.HMonitor) ?? 1.0;
        return _bar.TryGetChipRect(anchorTag, out anchorRect);
    }

    private void OnListNewNoteRequested(long groupId, PxRect anchorRect)
    {
        OnCursorEnteredApp(); // inside the app: cancel any in-flight fade now, not on the next poll
        if (_core is null)
        {
            return;
        }

        Note created = _core.CreateNote(groupId, "");
        RefreshViews();
        OpenNote(created.Id, created, createdThisOpen: true, anchorRect, anchorIsHorizontalBarItem: false, focused: true);
        _listOwnedNoteId = created.Id; // spawned from this dropdown: it keeps the dropdown open while the cursor is on it
    }

    private void OnListNoteClicked(long noteId, PxRect anchorRect)
    {
        OnCursorEnteredApp(); // inside the app: cancel any in-flight fade now, not on the next poll
        OpenNote(noteId, existingNote: null, createdThisOpen: false, anchorRect, anchorIsHorizontalBarItem: false, focused: false);
        _listOwnedNoteId = noteId; // spawned from this dropdown: it keeps the dropdown open while the cursor is on it
    }

    private void OnListGroupRowActivated(long groupId)
    {
        OnCursorEnteredApp(); // inside the app: cancel any in-flight fade now, not on the next poll
        // Stay anchored to the 'Groups ▾' chip; re-render the list in place to the group's notes.
        HideGroupPreview(); // the side list belongs to the all-groups view only
        GroupSummary? group = FindGroup(groupId);
        if (group is null)
        {
            return;
        }

        _list.ShowGroup(group, GroupNotesSorted(groupId));
        _openListAnchorTag = "groups-menu"; // unchanged anchor
        PublishSurfaces(Win32.DpiScaleOf(_bar.HMonitor) ?? 1.0);
    }

    // ------------------------------------------------------------------ group row hover → side list (v0.2.2+ F5)

    /// <summary>
    /// A GROUP ROW inside the all-groups dropdown was hovered/left. A ~180ms dwell on one row shows the
    /// side list of that group's notes to the RIGHT of the dropdown; leaving the row (and the side list)
    /// starts a ~200ms grace before it closes. Clicking a row still switches the main dropdown (and
    /// closes the side list via <see cref="OnListGroupRowActivated"/>).
    /// </summary>
    private void OnListGroupRowHovered(object? sender, GroupListRowHoverEventArgs e)
    {
        if (_disposed || _openListAnchorTag != "groups-menu" || _list.Mode != GroupListMode.OpenAllGroups)
        {
            return; // the side list only exists alongside the all-groups dropdown (or we already shut down)
        }

        if (e.Inside)
        {
            _previewRowInside = true;
            _previewRowRect = e.RowRect; // refresh on EVERY entry — the list may have scrolled since the last hover
            _previewGraceUtc = null; // cursor stepped onto the row: cancel any pending close

            if (e.DwellReady && _groupPreview?.ShownForGroupId != e.GroupId) // de-dup: a re-fired Ready for the group already shown must not rebuild rows
            {
                ShowGroupPreview(e.GroupId); // the row wiring owns the dwell clock; it fires Ready once per hover
            }
        }
        else
        {
            _previewRowInside = false;
            if (CursorOverOpenNote())
            {
                _previewGraceUtc = null; // the cursor moved onto a note opened from the side list: the tree stays
            }
            else if (!_previewListInside)
            {
                _previewGraceUtc = DateTime.UtcNow; // both sides abandoned → start the grace
            }
        }
    }

    /// <summary>
    /// Populate + show the side list for <paramref name="groupId"/>, right of the hovered row.
    /// Reached from the ROW DWELL, i.e. asynchronously to the render that drew the row: the group may have
    /// been deleted in between and the core may be disposed during shutdown, and this runs on the UI thread
    /// with no unhandled-exception handler above it — so every core call here must be guarded.
    /// </summary>
    private void ShowGroupPreview(long groupId)
    {
        GroupPreviewWindow? preview = _groupPreview;
        if (preview is null || _disposed)
        {
            return;
        }

        GroupSummary? group;
        IReadOnlyList<NoteSummary> notes;
        try
        {
            group = FindGroup(groupId);
            notes = group is null ? Array.Empty<NoteSummary>() : GroupNotesSorted(groupId);
        }
        catch (StickyCoreException)
        {
            return; // group deleted / db error between the row render and the dwell: nothing to preview
        }
        catch (ObjectDisposedException)
        {
            return; // shutdown raced the dwell
        }

        if (group is null)
        {
            return; // stale row: the group is gone, so is the preview
        }

        (MonitorBounds bounds, double dpi, _) = BarContext();
        preview.ShowForGroup(group, notes, _previewRowRect, bounds, dpi);
        PublishSurfaces(dpi); // the side list is a surface: cursor inside it keeps everything open
    }

    /// <summary>Hide the side list (never close the window) and clear its dwell/grace state.</summary>
    private void HideGroupPreview()
    {
        _groupPreview?.HidePreview();
        _previewRowInside = false;
        _previewListInside = false;
        _previewGraceUtc = null;
        PublishSurfaces(Win32.DpiScaleOf(_bar.HMonitor) ?? 1.0); // drop the stale hit surface or a cursor resting there never fades
    }

    /// <summary>A note row inside the side list was clicked — open the note via the normal path.</summary>
    private void OnPreviewNoteClicked(long noteId, PxRect anchor)
    {
        OnCursorEnteredApp(); // inside the app: cancel any in-flight fade now, not on the next poll
        OpenNote(noteId, existingNote: null, createdThisOpen: false, anchor, anchorIsHorizontalBarItem: false, focused: false);
        _listOwnedNoteId = noteId; // spawned from the side preview (part of the Groups ▾ tree)
        // ISSUE-2: the side list STAYS open on purpose. HideGroupPreview() here made the cursor (still on
        // the row) instantly outside every surface, and the tick then closed the fresh note along with the
        // group list. The normal row+list leave grace (~200ms) hides the side list once the cursor moves.
    }

    // ------------------------------------------------------------------ 'Groups ▾' hover-open (F5)

    /// <summary>
    /// The pointer dwelt ~180ms on the 'Groups ▾' chip: open the all-groups dropdown without a click.
    /// One-shot per hover (the chip's own 50ms poll fires DwellReady once), and a no-op when that list
    /// is already open. Clicking the chip still toggles it immediately (<see cref="OnChipActivated"/>).
    /// </summary>
    private void OnChipHovered(object? sender, Windows.BarChipHoverEventArgs e)
    {
        if (_disposed || !e.DwellReady)
        {
            return;
        }

        // Only chips that own a dropdown: 'Groups ▾' and the all-notes arrow. A note or a group chip
        // ignores the dwell.
        (string AnchorTag, GroupListMode Mode)? target = e.Tag switch
        {
            "groups-menu" => ("groups-menu", GroupListMode.OpenAllGroups),
            "all-notes" => ("all-notes", GroupListMode.OpenAllNotes),
            _ => null,
        };

        if (target is not { } t)
        {
            return;
        }

        if (_openListAnchorTag == t.AnchorTag && _list.Mode == t.Mode)
        {
            return; // already showing exactly this dropdown — never rebuild it under the cursor
        }

        OnCursorEnteredApp();
        ShowList(t.AnchorTag, t.Mode, null);
        _listOpenedByHover = _openListAnchorTag == t.AnchorTag; // keep-open-on-click applies only to this hover-opened list
    }

    private void OnListGroupPinToggled(long groupId) => ToggleGroupPinned(groupId);

    private void OnListGroupCreateCommitted(string name)
    {
        if (_core is null)
        {
            return;
        }

        _core.CreateGroup(name, pinned: true); // §10: new groups are pinned by default
        RefreshViews();
    }

    private void OnListGroupRenameCommitted(long groupId, string name)
    {
        if (_core is null)
        {
            return;
        }

        _core.RenameGroup(groupId, name);
        RefreshViews();
    }

    private void OnListGroupDeleteConfirmed(long groupId)
    {
        if (_core is null)
        {
            return;
        }

        try
        {
            _core.DeleteGroup(groupId); // cascade note delete happens inside the core
        }
        catch (StickyCoreException ex) when (ex.Status == 1)
        {
            // Already gone (e.g. a fast double-click on Delete) — nothing to do.
        }

        CloseList();
        RefreshBar();
    }

    private void ToggleGroupPinned(long groupId)
    {
        GroupSummary? group = FindGroup(groupId);
        if (group is null || _core is null)
        {
            return;
        }

        bool nowPinned = !group.IsPinned; // group is the pre-toggle snapshot
        _core.SetGroupPinned(groupId, nowPinned);
        AfterMenu(() =>
        {
            RefreshBar();

            // If the open list is anchored to this group's chip and the chip just disappeared
            // (group became unpinned), close it; otherwise re-render in place.
            if (_openListAnchorTag == $"group:{groupId}" && !nowPinned)
            {
                CloseList();
            }
            else if (_list.Mode == GroupListMode.OpenGroup && _list.CurrentGroupId == groupId)
            {
                GroupSummary? updated = FindGroup(groupId);
                if (updated is not null)
                {
                    _list.RefreshGroup(updated, GroupNotesSorted(groupId));
                }
            }
            else if (_list.Mode == GroupListMode.OpenAllGroups)
            {
                _list.RefreshAllGroups(GroupsSorted());
            }
        });
    }

    private void ToggleNotePinned(long noteId)
    {
        if (_core is null || _lastSnapshot is null)
        {
            return;
        }

        bool currentlyPinned = _lastSnapshot.PinnedItems
            .Select(p => p.Note)
            .OfType<NoteSummary>()
            .Any(n => n.Id == noteId);

        _core.SetNotePinned(noteId, !currentlyPinned);
        AfterMenu(RefreshBar);
    }

    private void RefreshBar()
    {
        if (_core is null)
        {
            return;
        }

        _lastSnapshot = _core.GetBarSnapshot();
        _bar.Rebuild(_lastSnapshot);
        if (_openListAnchorTag is null)
        {
            _listOpenedByHover = false; // a rebuild with no open dropdown must not carry a keep-open flag
        }
    }

    private IReadOnlyList<GroupSummary> GroupsSorted() =>
        _lastSnapshot?.AllGroups.OrderBy(g => g.CreatedAtMs).ToList() ?? new List<GroupSummary>();

    private IReadOnlyList<NoteSummary> GroupNotesSorted(long groupId) =>
        _core?.ListGroupNotes(groupId).OrderByDescending(n => n.LastOpenedAtMs).ToList() ?? new List<NoteSummary>();

    private GroupSummary? FindGroup(long groupId) =>
        _lastSnapshot?.AllGroups.FirstOrDefault(g => g.Id == groupId)
        ?? _core?.GetBarSnapshot().AllGroups.FirstOrDefault(g => g.Id == groupId);

    private void HideAll()
    {
        Diag.Log($"HideAll state={Machine.State} noteOpen={_noteWindow is not null} quickOpen={_quickNoteWindow is not null}");
        CloseQuickNote(); // commit-if-non-empty, like click-outside
        CloseNoteWindow(); // click-outside closes immediately (flushes; empty new unpinned note is deleted)

        _openListAnchorTag = null;
        _listOpenedByHover = false;
        _exitPending = false; // the pose state restarts with the next reveal
        HideGroupPreview();
        _list.CloseList();
        _tracker.SetVisible(false);
        _bar.HideBar();

        // A mid-fade hide must not leak its state into the next reveal: the bar comes back opaque and
        // the exit pose re-learns from scratch (otherwise the next show is hidden by a stale 2s timer).
        _outsideSinceUtc = null;
        _bar.SetOpacity(1.0);
        _exitPose.Reset();

        ScheduleIdleTrim();
    }

    [System.Runtime.InteropServices.DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    // ------------------------------------------------------------------ monitors

    private MonitorBounds? FindBounds(nint monitor)
    {
        foreach (TriggerWindow trigger in _triggers)
        {
            if (trigger.HMonitor == monitor)
            {
                return trigger.Bounds;
            }
        }

        return null;
    }

    private static List<(MonitorBounds Bounds, nint HMonitor)> EnumerateMonitors()
    {
        var results = new List<(MonitorBounds, nint)>();
        EnumDisplayMonitors(nint.Zero, nint.Zero, (nint hMonitor, nint hdc, ref RECT rc, nint data) =>
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (Win32.GetMonitorInfo(hMonitor, ref info))
            {
                results.Add((new MonitorBounds(
                    new PxRect(info.rcMonitor.Left, info.rcMonitor.Top,
                        info.rcMonitor.Right - info.rcMonitor.Left,
                        info.rcMonitor.Bottom - info.rcMonitor.Top),
                    new PxRect(info.rcWork.Left, info.rcWork.Top,
                        info.rcWork.Right - info.rcWork.Left,
                        info.rcWork.Bottom - info.rcWork.Top)),
                    hMonitor));
            }

            return true;
        }, nint.Zero);
        return results;
    }

    private delegate bool MonitorEnumProc(nint hMonitor, nint hdc, ref RECT rc, nint dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(nint hdc, nint lprcClip, MonitorEnumProc lpfnEnum, nint dwData);
}
