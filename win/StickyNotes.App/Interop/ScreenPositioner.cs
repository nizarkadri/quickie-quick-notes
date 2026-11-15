using System;

namespace StickyNotes.App.Interop;

/// <summary>Physical pixel rectangle (never DIPs).</summary>
public readonly record struct PxRect(int X, int Y, int Width, int Height)
{
    public int Left => X;
    public int Top => Y;
    public int Right => X + Width;
    public int Bottom => Y + Height;
}

public readonly record struct PxSize(int Width, int Height);

public sealed class MonitorBounds
{
    public MonitorBounds(PxRect monitor, PxRect workArea)
    {
        Monitor = monitor;
        WorkArea = workArea;
    }

    /// <summary>Full monitor bounds including the taskbar area.</summary>
    public PxRect Monitor { get; }

    /// <summary>Monitor bounds excluding the taskbar.</summary>
    public PxRect WorkArea { get; }
}

public interface IScreenPositioner
{
    PxRect GetTriggerRect(MonitorBounds monitor, double dpi);

    PxRect GetBarRect(MonitorBounds monitor, double dpi);

    PxRect GetGroupListRect(PxRect anchor, PxSize size, MonitorBounds monitor);

    PxRect GetNoteWindowRect(PxRect anchor, PxSize size, MonitorBounds monitor, bool anchorIsHorizontalBarItem);
}

/// <summary>
/// Task 1 stub. Task 4 implements physical-pixel monitor math, DPI conversion and
/// right-edge flip (blueprint §7).
/// </summary>
public sealed class ScreenPositioner : IScreenPositioner
{
    public PxRect GetTriggerRect(MonitorBounds monitor, double dpi) => throw new NotImplementedException();

    public PxRect GetBarRect(MonitorBounds monitor, double dpi) => throw new NotImplementedException();

    public PxRect GetGroupListRect(PxRect anchor, PxSize size, MonitorBounds monitor) => throw new NotImplementedException();

    public PxRect GetNoteWindowRect(PxRect anchor, PxSize size, MonitorBounds monitor, bool anchorIsHorizontalBarItem) =>
        throw new NotImplementedException();
}
