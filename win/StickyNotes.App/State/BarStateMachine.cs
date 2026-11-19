using System;
using System.Collections.Generic;
using System.Linq;

namespace StickyNotes.App.State;

public enum BarState
{
    Hidden,
    DwellPending,
    OpenHover,
    OpenFocusedLock,
}

public enum ShellActionKind
{
    ShowBar,
    FlushAndHideAll,
}

public readonly record struct ShellAction(ShellActionKind Kind, nint Monitor);

/// <summary>
/// Per-tick observation supplied by the shell. The machine needs only the two facts the DWELL depends on:
/// where the trigger is, and whether that monitor went fullscreen. Whether the cursor is over an app
/// surface is the fade's business (ShellCoordinator + BarExitPolicy), not the machine's.
/// </summary>
public readonly record struct BarProbe(bool CursorInTrigger, bool FullscreenOnMonitor);

/// <summary>
/// Pure trigger/bar/focus-lock state machine (blueprint §8.1, contract pinned by BarStateMachineTests).
/// Zero WPF Window / DispatcherTimer / HWND references — unit-testable with a virtual clock.
/// Timings: dwell 180ms. PollingActive is false only in Hidden (0% idle CPU).
///
/// <para>
/// The machine deliberately owns NO exit timing. Since v0.2.2 the exit is the coordinator's 2s linear
/// bar fade (BarExitPolicy), which is the single exit policy. Until then the machine timed a 200ms
/// <c>ExitGrace</c> of its own and the coordinator had to park it by passing a half-true
/// <see cref="BarProbe.CursorInTrigger"/> slot — a second policy firing behind its back, and the cause
/// of "a new note vanishes the moment I move the cursor toward it" (the machine hid everything 200ms
/// after the cursor left the bar while the coordinator believed it had declined to exit). The state, the
/// timer and the probe flag are deleted; leaving every surface is not the machine's business.
/// </para>
/// </summary>
public sealed class BarStateMachine
{
    public const int DwellMs = 180;

    private readonly List<ShellAction> _actions = new();

    private BarState _state = BarState.Hidden;
    private nint _monitor;
    private int _dwellElapsed;

    public BarState State => _state;

    /// <summary>False only when Hidden — drives the 0% idle CPU polling loop.</summary>
    public bool PollingActive => _state != BarState.Hidden;

    /// <summary>Begin (or restart) the 180ms dwell for <paramref name="monitor"/>.</summary>
    public void TriggerEntered(nint monitor)
    {
        switch (_state)
        {
            case BarState.Hidden:
                _monitor = monitor;
                _dwellElapsed = 0;
                _state = BarState.DwellPending;
                break;
            case BarState.DwellPending:
                // Re-hover on another monitor's trigger restarts the dwell there.
                _monitor = monitor;
                _dwellElapsed = 0;
                break;
            default:
                break; // bar already open/locked — a trigger re-enter means nothing
        }
    }

    public void TriggerLeft()
    {
        if (_state == BarState.DwellPending)
        {
            _state = BarState.Hidden;
            _dwellElapsed = 0;
        }
    }

    /// <summary>An editor window took keyboard focus — pause exit checks while it holds focus.</summary>
    public void EditorFocused()
    {
        if (_state is BarState.OpenHover)
        {
            _state = BarState.OpenFocusedLock;
        }
    }

    /// <summary>
    /// An editor lost focus. Deactivation to an external window with the cursor off our surfaces
    /// flushes and hides everything; deactivation while the cursor is still on a surface (or to
    /// another app-internal window, i.e. toExternalWindow == false) keeps the bar open.
    /// </summary>
    public void EditorDeactivated(bool toExternalWindow, bool cursorInSurfaces)
    {
        if (_state != BarState.OpenFocusedLock)
        {
            return;
        }

        if (toExternalWindow && !cursorInSurfaces)
        {
            Hide();
            _actions.Add(new ShellAction(ShellActionKind.FlushAndHideAll, _monitor));
        }
        else
        {
            _state = BarState.OpenHover;
        }
    }

    /// <summary>Monitor unplug/resolution/DPI change — flush and hide from any state.</summary>
    public void DisplaySettingsChanged()
    {
        if (_state == BarState.Hidden)
        {
            return;
        }

        Hide();
        _actions.Add(new ShellAction(ShellActionKind.FlushAndHideAll, _monitor));
    }

    /// <summary>
    /// v0.2.2: the 2s exit fade elapsed (or any other immediate teardown) — go straight to Hidden and
    /// flush. Additive; existing transitions are untouched.
    /// </summary>
    public void HideNow()
    {
        if (_state == BarState.Hidden)
        {
            return;
        }

        Hide();
        _actions.Add(new ShellAction(ShellActionKind.FlushAndHideAll, _monitor));
    }

    /// <summary>App is exiting — flush pending work (caller drains actions, then closes).</summary>
    public void AppExit()
    {
        if (_state == BarState.Hidden)
        {
            return;
        }

        Hide();
        _actions.Add(new ShellAction(ShellActionKind.FlushAndHideAll, _monitor));
    }

    /// <summary>Advance virtual time by <paramref name="ms"/>; the probe is constant over the interval.</summary>
    public void Advance(int ms, BarProbe probe)
    {
        if (ms < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ms), ms, "Advance requires a non-negative interval.");
        }

        switch (_state)
        {
            case BarState.DwellPending:
                _dwellElapsed += ms;
                if (_dwellElapsed >= DwellMs)
                {
                    if (probe.CursorInTrigger && !probe.FullscreenOnMonitor)
                    {
                        _state = BarState.OpenHover;
                        _actions.Add(new ShellAction(ShellActionKind.ShowBar, _monitor));
                    }
                    else
                    {
                        // Cursor left (or monitor went fullscreen) before the dwell completed: no reveal.
                        _state = BarState.Hidden;
                        _dwellElapsed = 0;
                    }
                }

                break;

            case BarState.OpenHover:
            case BarState.OpenFocusedLock:
            case BarState.Hidden:
                break; // the coordinator's fade owns the exit; the machine never hides on its own
        }
    }

    public IReadOnlyList<ShellAction> DrainActions()
    {
        if (_actions.Count == 0)
        {
            return Array.Empty<ShellAction>();
        }

        var drained = _actions.ToList();
        _actions.Clear();
        return drained;
    }

    private void Hide()
    {
        _state = BarState.Hidden;
        _dwellElapsed = 0;
    }
}
