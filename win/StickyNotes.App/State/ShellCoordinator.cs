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
    private StickyCoreClient? _core;

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

        _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TickIntervalMs) };
        _tick.Tick += OnTick;
    }

    public BarStateMachine Machine { get; } = new();

    /// <summary>Raised when the user clicks '+' (Task 8 opens QuickNoteWindow; unused in Task 5).</summary>
    public event EventHandler? QuickCaptureRequested;

    /// <summary>Open the database, create one TriggerWindow per monitor, and start the tick.</summary>
    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _core = new StickyCoreClient(_dbPath);

        // Pre-create the HWND hidden (Visibility.Hidden) without ever showing the bar.
        _ = new WindowInteropHelper(_bar).EnsureHandle();

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

        _tracker.Dpi = dpi;
        _tracker.SetSurfaces(surfaces);
        _tracker.SetVisible(true);
    }

    private void HideAll()
    {
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
