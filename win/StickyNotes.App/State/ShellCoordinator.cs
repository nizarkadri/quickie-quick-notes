using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
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

    private NoteWindow? _noteWindow;       // max 1 open — opening a second flushes + replaces the first
    private QuickNoteWindow? _quickNoteWindow;
    private nint _activeMonitor; // monitor of the pending/open reveal (last trigger entered)
    private DateTime _lastTickUtc;
    private bool _started;
    private bool _disposed;

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
        _bar.ChipPinToggleRequested += OnChipPinToggleRequested;

        _list = new GroupListWindow();
        _list.NewNoteRequested += OnListNewNoteRequested;
        _list.NoteClicked += OnListNoteClicked;
        _list.GroupRowActivated += OnListGroupRowActivated;
        _list.GroupPinToggled += OnListGroupPinToggled;
        _list.GroupCreateCommitted += OnListGroupCreateCommitted;
        _list.GroupRenameCommitted += OnListGroupRenameCommitted;
        _list.GroupDeleteConfirmed += OnListGroupDeleteConfirmed;
        _list.InlineEditStarted += (_, _) => Machine.EditorFocused();
        _list.InlineEditEnded += (_, _) =>
        {
            if (_disposed)
            {
                return; // shutdown already drained the queue
            }

            Machine.EditorDeactivated(
                toExternalWindow: !Win32.IsOwnAppWindow(Win32.GetForegroundWindow()),
                cursorInSurfaces: _tracker.IsCursorInside);
        };

        _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TickIntervalMs) };
        _tick.Tick += OnTick;
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
        _list.Close(); // triggers ForgetWindow via OnClosed
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
    }

    private void OnTriggerEntered(object? sender, TriggerMonitorEventArgs e)
    {
        _activeMonitor = e.Monitor;
        Machine.TriggerEntered(e.Monitor);
        UpdateTickState(); // wake the tick if the machine wants polling (DwellPending...)
    }

    private void OnTriggerLeft(object? sender, TriggerMonitorEventArgs e) => Machine.TriggerLeft();

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

        bool fullscreen = false;
        if (Machine.State == BarState.DwellPending)
        {
            // Spec: fullscreen is consulted ONLY at dwell completion (and not via zero-background hooks).
            fullscreen = _activeMonitor != nint.Zero && FocusHelper.IsFullscreenOnMonitor(_activeMonitor);
        }

        var probe = new BarProbe(cursorInTrigger, _tracker.IsCursorInside, fullscreen);
        Machine.Advance(elapsed, probe);

        foreach (ShellAction action in Machine.DrainActions())
        {
            ApplyAction(action);
        }

        UpdateTickState(); // stop the 50ms tick the moment the machine returns to Hidden
    }

    private bool CursorInTrigger()
    {
        if (!Win32.GetCursorPos(out POINT pt))
        {
            return false;
        }

        foreach (TriggerWindow trigger in _triggers)
        {
            if (trigger.TryGetPhysicalRect(out PxRect r)
                && pt.X >= r.Left && pt.X < r.Right && pt.Y >= r.Top && pt.Y < r.Bottom)
            {
                return true;
            }
        }

        return false;
    }

    // ------------------------------------------------------------------ actions

    private void ApplyAction(ShellAction action)
    {
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

        CloseNoteWindow(); // max 1 open — opening a second flushes + replaces the first

        Note note = existingNote ?? _core.GetNote(noteId, markOpened: true);

        (MonitorBounds bounds, double dpi, _) = BarContext();
        var win = new NoteWindow(_core, note, createdThisOpen);
        win.ClosedByUser += OnNoteWindowClosed;
        win.EditorActivated += (_, _) => Machine.EditorFocused(); // lock the bar ONLY when the note really takes focus
        _noteWindow = win;
        win.ShowNote(anchorRect, bounds, dpi, anchorIsHorizontalBarItem, focused);

        PublishSurfaces(dpi);
    }

    private void CloseNoteWindow()
    {
        NoteWindow? win = _noteWindow;
        if (win is null)
        {
            return;
        }

        _noteWindow = null; // before closing: the ClosedByUser handler would see the stale reference otherwise
        win.ClosedByUser -= OnNoteWindowClosed;
        win.FlushAndClose(); // raises nothing (unsubscribed); flush happens inside
    }

    private void OnNoteWindowClosed(object? sender, NoteWindowClosedEventArgs e)
    {
        if (sender is NoteWindow win && ReferenceEquals(win, _noteWindow))
        {
            _noteWindow = null;
        }

        Machine.EditorDeactivated(
            toExternalWindow: !Win32.IsOwnAppWindow(Win32.GetForegroundWindow()),
            cursorInSurfaces: _tracker.IsCursorInside);
        RefreshViews();

        double dpi = _bar.HMonitor != nint.Zero ? Win32.DpiScaleOf(_bar.HMonitor) ?? 1.0 : 1.0;
        PublishSurfaces(dpi);
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
        _list.CloseList();

        if (_quickNoteWindow is not null)
        {
            _quickNoteWindow.FocusBody(); // already open: just refocus, don't stack a second
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
            System.Diagnostics.Debug.WriteLine($"ListAppendTargets failed ({ex.Status}): {ex.Message}");
            appendTargets = new List<NoteSummary>();
        }

        win.LoadTargets(_lastSnapshot ?? _core.GetBarSnapshot(), appendTargets);
        win.QuickNoteClosed += OnQuickNoteClosed;
        _quickNoteWindow = win;

        if (!_bar.TryGetPhysicalRect(out PxRect barRect))
        {
            // Bar fell over between '+' click and open — bail out cleanly.
            _quickNoteWindow = null;
            return;
        }

        win.ShowQuickNote(barRect, bounds, dpi);

        Machine.EditorFocused();
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
            cursorInSurfaces: _tracker.IsCursorInside);
        RefreshViews(); // appended/committed note moves to #1 in Recents

        double dpi = _bar.HMonitor != nint.Zero ? Win32.DpiScaleOf(_bar.HMonitor) ?? 1.0 : 1.0;
        PublishSurfaces(dpi);
    }

    /// <summary>Refresh bar + (if open) sync the group list after data changed.</summary>
    private void RefreshViews()
    {
        RefreshBar();

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
    }

    // ------------------------------------------------------------------ bar & group list

    private void OnChipActivated(object? sender, Windows.BarChipEventArgs e)
    {
        string tag = e.Tag;
        if (tag == "groups-menu")
        {
            ToggleList("groups-menu", openAllGroups: true, groupId: null);
        }
        else if (tag.StartsWith("group:", StringComparison.Ordinal)
                 && long.TryParse(tag.AsSpan("group:".Length), out long groupId))
        {
            ToggleList(tag, openAllGroups: false, groupId);
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

    private void OnChipPinToggleRequested(object? sender, Windows.BarChipEventArgs e)
    {
        string tag = e.Tag;
        if (tag.StartsWith("group:", StringComparison.Ordinal)
            && long.TryParse(tag.AsSpan("group:".Length), out long groupId))
        {
            ToggleGroupPinned(groupId);
        }
        else if (tag.StartsWith("note:", StringComparison.Ordinal)
                 && long.TryParse(tag.AsSpan("note:".Length), out long noteId))
        {
            ToggleNotePinned(noteId);
        }
    }

    private void ToggleList(string anchorTag, bool openAllGroups, long? groupId)
    {
        if (_openListAnchorTag == anchorTag)
        {
            CloseList();
            return;
        }

        if (!TryGetAnchorAndBounds(anchorTag, out PxRect anchorRect, out MonitorBounds bounds, out double dpi))
        {
            return; // bar hidden — nothing to anchor to
        }

        if (openAllGroups)
        {
            _list.ShowAllGroups(GroupsSorted());
        }
        else if (groupId.HasValue)
        {
            GroupSummary? group = FindGroup(groupId.Value);
            if (group is null)
            {
                return;
            }

            _list.ShowGroup(group, GroupNotesSorted(groupId.Value));
        }

        _list.PositionAndShow(anchorRect, bounds, dpi);
        _openListAnchorTag = anchorTag;
        PublishSurfaces(dpi);
    }

    private void CloseList()
    {
        if (_openListAnchorTag is null)
        {
            return;
        }

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
        if (_core is null)
        {
            return;
        }

        Note created = _core.CreateNote(groupId, "");
        RefreshViews();
        OpenNote(created.Id, created, createdThisOpen: true, anchorRect, anchorIsHorizontalBarItem: false, focused: true);
    }

    private void OnListNoteClicked(long noteId, PxRect anchorRect) =>
        OpenNote(noteId, existingNote: null, createdThisOpen: false, anchorRect, anchorIsHorizontalBarItem: false, focused: false);

    private void OnListGroupRowActivated(long groupId)
    {
        // Stay anchored to the 'Groups ▾' chip; re-render the list in place to the group's notes.
        GroupSummary? group = FindGroup(groupId);
        if (group is null)
        {
            return;
        }

        _list.ShowGroup(group, GroupNotesSorted(groupId));
        _openListAnchorTag = "groups-menu"; // unchanged anchor
        PublishSurfaces(Win32.DpiScaleOf(_bar.HMonitor) ?? 1.0);
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
        RefreshBar();
    }

    private void RefreshBar()
    {
        if (_core is null)
        {
            return;
        }

        _lastSnapshot = _core.GetBarSnapshot();
        _bar.Rebuild(_lastSnapshot);
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
        CloseQuickNote(); // commit-if-non-empty, like click-outside
        CloseNoteWindow(); // flushes (empty new unpinned note is deleted)
        _openListAnchorTag = null;
        _list.CloseList();
        _tracker.SetVisible(false);
        _bar.HideBar();

        // Idle trim: once fully hidden, return memory to the OS (GC + working-set emptying).
        // Keeps the steady-state RSS under the <50 MB budget after big flushes/renders.
        GC.Collect();
        _ = EmptyWorkingSet(new IntPtr(-1)); // pseudo-handle to the current process
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
