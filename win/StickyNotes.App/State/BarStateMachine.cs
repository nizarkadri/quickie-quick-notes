using System;
using System.Collections.Generic;
using System.Linq;

namespace StickyNotes.App.State;

public enum BarState
{
    Hidden,
    DwellPending,
    OpenHover,
    ExitGrace,
    OpenFocusedLock,
}

public enum ShellActionKind
{
    ShowBar,
    FlushAndHideAll,
}

public readonly record struct ShellAction(ShellActionKind Kind, nint Monitor);

/// <summary>Per-tick observation supplied by the shell (cursor + fullscreen).</summary>
public readonly record struct BarProbe(bool CursorInTrigger, bool CursorInSurfaces, bool FullscreenOnMonitor);

/// <summary>
/// Pure trigger/bar/focus-lock/grace state machine (blueprint §8.1, contract pinned by BarStateMachineTests).
/// Zero WPF Window / DispatcherTimer / HWND references — unit-testable with a virtual clock.
/// Timings: dwell 180ms, exit grace 200ms. PollingActive is false only in Hidden (0% idle CPU).
/// </summary>
public sealed class BarStateMachine
{
    public const int DwellMs = 180;
    public const int ExitGraceMs = 200;

    private readonly List<ShellAction> _actions = new();

    private BarState _state = BarState.Hidden;
    private nint _monitor;
    private int _dwellElapsed;
    private int _graceElapsed;

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
        if (_state is BarState.OpenHover or BarState.ExitGrace)
        {
            _state = BarState.OpenFocusedLock;
            _graceElapsed = 0;
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
                if (!probe.CursorInSurfaces)
                {
                    _state = BarState.ExitGrace;
                    _graceElapsed = 0;
                }

                break;

            case BarState.ExitGrace:
                if (probe.CursorInSurfaces)
                {
                    _state = BarState.OpenHover;
                    _graceElapsed = 0;
                    break;
                }

                _graceElapsed += ms;
                if (_graceElapsed >= ExitGraceMs)
                {
                    Hide();
                    _actions.Add(new ShellAction(ShellActionKind.FlushAndHideAll, _monitor));
                }

                break;

            case BarState.OpenFocusedLock:
            case BarState.Hidden:
                break;
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
        _graceElapsed = 0;
    }
}
