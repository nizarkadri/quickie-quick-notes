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
///
/// Prefer <see cref="SetSurfaces"/> (push model, called on the UI thread whenever the layout
/// changes) over the <c>regionsProvider</c> ctor overload (pull model, invoked on a threadpool
/// thread — the provider MUST NOT touch WPF-owned objects).
/// </summary>
public sealed class HitRegionTracker : IDisposable
{
    public const int PollIntervalMs = 50;
    public const int HitInflateDip = 12;
    public const int ExitGraceMs = 200;

    private readonly Func<IReadOnlyList<PxRect>>? _regionsProvider;
    private readonly Timer _pollTimer;
    private readonly Timer _graceTimer;
    private readonly object _sync = new();

    private bool _visible;
    private bool _outside;   // in exit grace
    private bool _disposed;  // guarded by _sync
    private double _dpi = 1.0;
    private IReadOnlyList<PxRect>? _surfaces; // non-null ⇒ push mode (SetSurfaces was used)

    /// <summary>Raised when the cursor first leaves all visible surfaces (exit grace begins).</summary>
    public event EventHandler? ExitGraceStarted;

    /// <summary>Raised when the cursor re-enters a surface during the exit grace window.</summary>
    public event EventHandler? ExitGraceResolved;

    /// <summary>Raised after <see cref="ExitGraceMs"/> with the cursor outside every surface. Consumers must hide the bar.</summary>
    public event EventHandler? ExitCompleted;

    /// <summary>Push model: no provider is ever invoked. Rects are snapshotted as-is (callers should hand over immutable/copy lists).</summary>
    public HitRegionTracker()
    {
        _pollTimer = new Timer(PollTick, null, Timeout.Infinite, Timeout.Infinite);
        _graceTimer = new Timer(GraceElapsed, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Pull model: the provider is invoked on a threadpool thread — it must not touch WPF-owned objects. Prefer <see cref="SetSurfaces"/>.</summary>
    public HitRegionTracker(Func<IReadOnlyList<PxRect>> regionsProvider) : this()
    {
        _regionsProvider = regionsProvider ?? throw new ArgumentNullException(nameof(regionsProvider));
    }

    /// <summary>Push the current visible surface rects (physical pixels). Switches the tracker to push mode permanently.</summary>
    public void SetSurfaces(IReadOnlyList<PxRect> rects)
    {
        ArgumentNullException.ThrowIfNull(rects);
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _surfaces = rects;
        }
    }

    /// <summary>True while the poll timer is active (i.e. some surface is meant to stay open). False after disposal.</summary>
    public bool IsVisible
    {
        get { lock (_sync) { return _visible; } }
    }

    private bool _cursorInside;

    /// <summary>Last probed cursor-inside state (updated on every poll tick). Reads take <c>_sync</c>.</summary>
    public bool IsCursorInside
    {
        get { lock (_sync) { return _cursorInside; } }
    }

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
                if (!_disposed)
                {
                    _dpi = value;
                }
            }
        }
    }

    /// <summary>Start or stop the poll + grace timers atomically. Idempotent. No-op after disposal.</summary>
    public void SetVisible(bool visible)
    {
        lock (_sync)
        {
            if (_disposed || _visible == visible)
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

    /// <summary>Stop both timers and release resources. Safe to call more than once; callbacks bail out before touching the timers.</summary>
    public void Dispose()
    {
        // Everything happens under _sync: callbacks also do all timer ops under _sync and
        // early-return when _disposed is set — so no callback can observe a half-disposed tracker.
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _visible = false;
            _outside = false;
            _pollTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _graceTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _pollTimer.Dispose();
            _graceTimer.Dispose();
        }
    }

    private void PollTick(object? state)
    {
        double dpi;
        lock (_sync)
        {
            if (_disposed || !_visible)
            {
                return; // disposed or already hidden — never touch the timers
            }

            dpi = _dpi;
        }

        IReadOnlyList<PxRect>? surfaces;
        lock (_sync)
        {
            surfaces = _surfaces;
        }

        bool inside;
        try
        {
            if (surfaces is null)
            {
                if (_regionsProvider is null)
                {
                    surfaces = Array.Empty<PxRect>();
                }
                else
                {
                    // Pull model: invoked on the threadpool thread — the provider must not
                    // touch WPF-owned objects. Prefer SetSurfaces (push model).
                    surfaces = _regionsProvider() ?? Array.Empty<PxRect>();
                }
            }

            int inflate = (int)Math.Round(HitInflateDip * dpi);
            inside = CursorInsideInflated(surfaces, inflate);
        }
        catch
        {
            // Fail-safe: a transient probe failure must never close an open editor.
            inside = true;
        }

        EventHandler? started = null, resolved = null;
        lock (_sync)
        {
            if (_disposed || !_visible)
            {
                return;
            }

            _cursorInside = inside;
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
            if (_disposed || !_visible || !_outside)
            {
                return; // re-entered during the grace window, or disposed
            }

            _outside = false;
            _visible = false;
            _pollTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _graceTimer.Change(Timeout.Infinite, Timeout.Infinite);
            completed = ExitCompleted;
        }

        completed?.Invoke(this, EventArgs.Empty);
    }

    private static bool CursorInsideInflated(IReadOnlyList<PxRect> surfaces, int inflate)
    {
        if (!Win32.GetCursorPos(out POINT pt))
        {
            return true; // fail-safe
        }

        foreach (PxRect r in surfaces)
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
