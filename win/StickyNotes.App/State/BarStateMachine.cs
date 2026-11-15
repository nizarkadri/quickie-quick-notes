using System;
using System.Collections.Generic;

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
/// Task 1 stub. Pure HWND-free trigger/bar/focus-lock/grace logic lands in Task 5
/// (blueprint §8.1); no WPF <c>Window</c> may ever be referenced from here.
/// </summary>
public sealed class BarStateMachine
{
    public BarState State { get; private set; } = BarState.Hidden;

    public bool PollingActive => State != BarState.Hidden;

    public void TriggerEntered(nint monitor) => throw new NotImplementedException();

    public void TriggerLeft() => throw new NotImplementedException();

    public void EditorFocused() => throw new NotImplementedException();

    public void EditorDeactivated(bool toExternalWindow, bool cursorInSurfaces) => throw new NotImplementedException();

    public void DisplaySettingsChanged() => throw new NotImplementedException();

    public void AppExit() => throw new NotImplementedException();

    public void Advance(int ms, BarProbe probe) => throw new NotImplementedException();

    public IReadOnlyList<ShellAction> DrainActions() => throw new NotImplementedException();
}
