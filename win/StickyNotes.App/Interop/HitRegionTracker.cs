using System;
using System.Collections.Generic;
using System.Threading;

namespace StickyNotes.App.Interop;

/// <summary>
/// Drives keep-open / auto-hide decisions for the bar + flyouts (blueprint §8.1).
/// A 50ms GetCursorPos poll runs ONLY while <see cref="IsVisible"/> is true — zero idle CPU.
/// Visible surfaces (Trigger, Bar, GroupList, NoteWindow, QuickNote) are treated as their rects
/// inflated by <see cref="HitInflateDip"/> DIPs. When the cursor leaves every surface, a
/// <see cref="ExitGraceMs"/> one-shot grace timer starts; re-entering cancels it; on elapse
/// <see cref="ExitCompleted"/> fires. Hiding stops BOTH timers completely.
///
/// Events fire on a threadpool timer thread — WPF consumers must marshal to the UI dispatcher.
/// </summary>
public sealed class HitRegionTracker : IDisposable
{
    public const int PollIntervalMs = 50;
    public const int HitInflateDip = 12;
    public const int ExitGraceMs = 200;

    private readonly Func<IReadOnlyList<PxRect>> _regionsProvider;
    private readonly Timer _pollTimer;
    private readonly Timer _graceTimer;
    private readonly object _sync = new();

    private bool _visible;
    private bool _outside; // in exit grace
    private double _dpi = 1.0;

    /// <summary>Raised when the cursor first leaves all visible surfaces (exit grace begins).</summary>
    public event EventHandler? ExitGraceStarted;

    /// <summary>Raised when the cursor re-enters a surface during the exit grace window.</summary>
    public event EventHandler? ExitGraceResolved;

    /// <summary>Raised after <see cref="ExitGraceMs"/> with the cursor outside every surface. Consumers must hide the bar.</summary>
    public event EventHandler? ExitCompleted;

    public HitRegionTracker(Func<IReadOnlyList<PxRect>> regionsProvider)
    {
        _regionsProvider = regionsProvider ?? throw new ArgumentNullException(nameof(regionsProvider));
        _pollTimer = new Timer(PollTick, null, Timeout.Infinite, Timeout.Infinite);
        _graceTimer = new Timer(GraceElapsed, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>True while the poll timer is active (i.e. some surface is meant to stay open).</summary>
    public bool IsVisible
    {
        get { lock (_sync) { return _visible; } }
    }

    /// <summary>Last probed cursor-inside state (updated on every poll tick).</summary>
    public bool IsCursorInside { get; private set; }

    /// <summary>DPI scale used to inflate the hit regions (12 DIPs → physical px).</summary>
    public double Dpi
    {
        get { lock (_sync) { return _dpi; } }
        set
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "DPI scale must be positive.");
            }

            lock (_sync)
            {
                _dpi = value;
            }
        }
    }

    /// <summary>Start or stop the poll + grace timers atomically. Idempotent.</summary>
    public void SetVisible(bool visible)
    {
        lock (_sync)
        {
            if (_visible == visible)
            {
                return;
            }

            _visible = visible;
            _outside = false;
            if (visible)
            {
                _graceTimer.Change(Timeout.Infinite, Timeout.Infinite);
                _pollTimer.Change(0, PollIntervalMs);
            }
            else
            {
                _pollTimer.Change(Timeout.Infinite, Timeout.Infinite);
                _graceTimer.Change(Timeout.Infinite, Timeout.Infinite);
            }
        }
    }

    /// <summary>Stop both timers and release resources. Safe to call more than once.</summary>
    public void Dispose()
    {
        _pollTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _graceTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _pollTimer.Dispose();
        _graceTimer.Dispose();
    }

    private void PollTick(object? state)
    {
        bool inside;
        try
        {
            inside = CursorInsideInflated();
        }
        catch
        {
            // Fail-safe: a transient probe failure must never close an open editor.
            inside = true;
        }

        EventHandler? started = null, resolved = null;
        lock (_sync)
        {
            if (!_visible)
            {
                return;
            }

            IsCursorInside = inside;
            if (inside)
            {
                if (_outside)
                {
                    _outside = false;
                    _graceTimer.Change(Timeout.Infinite, Timeout.Infinite);
                    resolved = ExitGraceResolved;
                }
            }
            else if (!_outside)
            {
                _outside = true;
                _graceTimer.Change(ExitGraceMs, Timeout.Infinite);
                started = ExitGraceStarted;
            }
        }

        started?.Invoke(this, EventArgs.Empty);
        resolved?.Invoke(this, EventArgs.Empty);
    }

    private void GraceElapsed(object? state)
    {
        EventHandler? completed = null;
        lock (_sync)
        {
            if (!_visible || !_outside)
            {
                return; // re-entered during the grace window
            }

            _outside = false;
            _visible = false;
            _pollTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _graceTimer.Change(Timeout.Infinite, Timeout.Infinite);
            completed = ExitCompleted;
        }

        completed?.Invoke(this, EventArgs.Empty);
    }

    private bool CursorInsideInflated()
    {
        if (!Win32.GetCursorPos(out POINT pt))
        {
            return true; // fail-safe
        }

        double dpi = Dpi;
        int inflate = (int)Math.Round(HitInflateDip * dpi);

        foreach (PxRect r in _regionsProvider())
        {
            int left = r.Left - inflate;
            int top = r.Top - inflate;
            int right = r.Right + inflate;
            int bottom = r.Bottom + inflate;
            if (pt.X >= left && pt.X < right && pt.Y >= top && pt.Y < bottom)
            {
                return true;
            }
        }

        return false;
    }
}
