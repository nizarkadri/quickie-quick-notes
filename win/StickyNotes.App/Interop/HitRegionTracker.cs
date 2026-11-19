using System;
using System.Collections.Generic;
using System.Threading;

namespace StickyNotes.App.Interop;

/// <summary>
/// Drives keep-open / auto-hide decisions for the bar + flyouts (blueprint §8.1).
/// A 50ms GetCursorPos poll runs ONLY while <see cref="IsVisible"/> is true — zero idle CPU.
/// Visible surfaces (Trigger, Bar, GroupList, NoteWindow, QuickNote) are treated as their rects
/// inflated by <see cref="HitInflateDip"/> DIPs.
///
/// v0.2.2+: the coordinator's 2s bar fade (BarExitPolicy) owns the exit timing, so the legacy
/// exit-grace machinery is retired — <see cref="ExitGraceStarted"/> / <see cref="ExitGraceResolved"/>
/// are deleted and the grace timer is parked in <see cref="PollTick"/>. <see cref="ExitCompleted"/>
/// remains declared for contract compatibility but is INERT (never raised); Hiding stops the poll is
/// done by <see cref="SetVisible(false)"/> / <see cref="Dispose"/> only.
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

    /// <summary>Raised after <see cref="ExitGraceMs"/> with the cursor outside every surface. Consumers must hide the bar. INERT since v0.2.2 (the coordinator's 2s fade owns the exit); kept only for contract compatibility.</summary>
    public event EventHandler? ExitCompleted;

    /// <summary>Push model: no provider is ever invoked. Rects are snapshotted as-is (callers should hand over immutable/copy lists).</summary>
    public HitRegionTracker()
    {
        _pollTimer = new Timer(PollTick, null, Timeout.Infinite, Timeout.Infinite);
        _graceTimer = new Timer(GraceElapsed, null, Timeout.Infinite, Timeout.Infinite);
        _poseProvider = ProbePose;
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

    /// <summary>
    /// Pose used by <see cref="IsCursorInside"/>. Production keeps the OS-probed pose (push surfaces or
    /// the pull provider); callers that cannot probe the cursor (test hosts, UIPI-blocked reads) may
    /// inject a pure pose via <see cref="SetCursorPose"/>. Fail-safe default: true — a transient probe
    /// failure must never close an open editor.
    /// </summary>
    private Func<bool> _poseProvider = () => true; // replaced by ProbePose in the ctor; SetCursorPose may override it

    /// <summary>Inject the cursor pose (test hosts / UIPI-blocked probes). Thread-safe; takes effect on the next poll.</summary>
    internal void SetCursorPose(Func<bool> poseProvider) => _poseProvider = poseProvider ?? throw new ArgumentNullException(nameof(poseProvider));

    /// <summary>Last probed cursor-inside state (updated on every poll tick). Reads take <c>_sync</c>.</summary>
    public bool IsCursorInside
    {
        get { lock (_sync) { return _cursorInside; } }
    }

    /// <summary>Production pose: hit-test the published surfaces against the OS cursor (pull model supported). Fail-safe true on any probe failure.</summary>
    private bool ProbePose()
    {
        IReadOnlyList<PxRect>? surfaces;
        Func<IReadOnlyList<PxRect>>? pull;
        double dpi;
        lock (_sync)
        {
            if (_disposed)
            {
                return true;
            }

            dpi = _dpi;
            surfaces = _surfaces;
            pull = _regionsProvider;
        }

        try
        {
            if (surfaces is null && pull is not null)
            {
                surfaces = pull() ?? Array.Empty<PxRect>(); // pull model: invoked by the caller of PollTick
            }

            int inflate = (int)Math.Round(HitInflateDip * dpi);
            return CursorInsideInflated(surfaces ?? Array.Empty<PxRect>(), inflate);
        }
        catch
        {
            return true; // fail-safe: a transient probe failure must never close an open editor
        }
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
        lock (_sync)
        {
            if (_disposed || !_visible)
            {
                return; // disposed or explicitly hidden (SetVisible(false)) — never touch the timers
            }

            // v0.2.2+: the 2s bar fade (ShellCoordinator + BarExitPolicy) owns the exit timing now.
            // Keep polling while the caller keeps us visible so a re-entry during the fade is
            // observed; the legacy grace timer is parked (ExitCompleted stays inert). Stopping is
            // SetVisible(false)/Dispose only.
            _outside = false;

            _cursorInside = _poseProvider();
        }
    }

    /// <summary>
    /// Legacy exit path, kept inert: the coordinator's 2s bar fade owns the exit timing, so the
    /// grace window is parked in <see cref="PollTick"/> and this callback only ever fires if a caller
    /// manually arms <see cref="ExitGraceMs"/>. Never hides the tracker or stops the poll.
    /// </summary>
    private void GraceElapsed(object? state)
    {
        EventHandler? completed = null;
        lock (_sync)
        {
            if (_disposed || !_visible || !_outside)
            {
                return; // re-entered during the grace window, disposed, or already hidden
            }

            _outside = false;
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
