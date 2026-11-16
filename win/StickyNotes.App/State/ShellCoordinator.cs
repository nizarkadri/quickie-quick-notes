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

    private nint _activeMonitor; // monitor of the pending/open reveal (last trigger entered)
    private DateTime _lastTickUtc;
    private bool _started;
    private bool _disposed;

    public ShellCoordinator(string dbPath)
    {
        _dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));
        _bar = new BarWindow();
        _bar.QuickCaptureRequested += (_, _) => QuickCaptureRequested?.Invoke(this, EventArgs.Empty);
        _bar.EditorFocused += (_, _) => Machine.EditorFocused();
        _bar.EditorDeactivated += (_, _) => Machine.EditorDeactivated(
            toExternalWindow: !Win32.IsOwnAppWindow(Win32.GetForegroundWindow()),
            cursorInSurfaces: _tracker.IsCursorInside);
        _bar.ChipActivated += OnChipActivated;
        _bar.ChipPinToggleRequested += OnChipPinToggleRequested;

        _list = new GroupListWindow();
        _list.NewNoteRequested += OnListNewNoteRequested;
        _list.NoteClicked += id => OpenNoteRequested?.Invoke(this, id);
        _list.GroupRowActivated += OnListGroupRowActivated;
        _list.GroupPinToggled += OnListGroupPinToggled;
        _list.GroupCreateCommitted += OnListGroupCreateCommitted;
        _list.GroupRenameCommitted += OnListGroupRenameCommitted;
        _list.GroupDeleteConfirmed += OnListGroupDeleteConfirmed;
        _list.InlineEditStarted += (_, _) => Machine.EditorFocused();
        _list.InlineEditEnded += (_, _) => Machine.EditorDeactivated(
            toExternalWindow: !Win32.IsOwnAppWindow(Win32.GetForegroundWindow()),
            cursorInSurfaces: _tracker.IsCursorInside);

        _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TickIntervalMs) };
        _tick.Tick += OnTick;
    }

    public BarStateMachine Machine { get; } = new();

    /// <summary>Raised when the user clicks '+' (Task 8 opens QuickNoteWindow; unused in Task 5).</summary>
    public event EventHandler? QuickCaptureRequested;

    /// <summary>Raised when a note chip/row is activated (Task 7 opens NoteWindow; unused in Task 6 — note is created/left alone).</summary>
    public event EventHandler<long>? OpenNoteRequested;

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

        _tick.Start();
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

    /// <summary>Re-publish trigger + bar + (if open) group-list rects to the hit tracker.</summary>
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

        _tracker.Dpi = dpi;
        _tracker.SetSurfaces(surfaces);
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
            OpenNoteRequested?.Invoke(this, noteId); // Task 7 opens NoteWindow
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

    private void OnListNewNoteRequested(long groupId)
    {
        if (_core is null)
        {
            return;
        }

        // Task 6: create + refresh. Opening the NoteWindow is Task 7 (raise-only stub follows).
        Note created = _core.CreateNote(groupId, "");
        _lastSnapshot = _core.GetBarSnapshot();
        _bar.Rebuild(_lastSnapshot);
        if (_list.Mode == GroupListMode.OpenGroup && _list.CurrentGroupId == groupId)
        {
            GroupSummary? group = FindGroup(groupId);
            if (group is not null)
            {
                _list.RefreshGroup(group, GroupNotesSorted(groupId));
            }
        }

        OpenNoteRequested?.Invoke(this, created.Id);
    }

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
        RefreshBar();
        if (_list.Mode == GroupListMode.OpenAllGroups)
        {
            _list.RefreshAllGroups(GroupsSorted());
        }
    }

    private void OnListGroupRenameCommitted(long groupId, string name)
    {
        if (_core is null)
        {
            return;
        }

        _core.RenameGroup(groupId, name);
        RefreshBar();
        if (_list.Mode == GroupListMode.OpenGroup && _list.CurrentGroupId == groupId)
        {
            GroupSummary? group = FindGroup(groupId);
            if (group is not null)
            {
                _list.RefreshGroup(group, GroupNotesSorted(groupId));
            }
        }
    }

    private void OnListGroupDeleteConfirmed(long groupId)
    {
        if (_core is null)
        {
            return;
        }

        _core.DeleteGroup(groupId); // cascade note delete happens inside the core
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
        _openListAnchorTag = null;
        _list.CloseList();
        _tracker.SetVisible(false);
        _bar.HideBar();
        // NoteWindow/QuickNoteWindow are closed/flushed here in Tasks 7/8.
    }

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
