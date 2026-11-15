using System;

namespace StickyNotes.App.Interop;

// ---------------------------------------------------------------------------
// Physical-pixel monitor positioning math (blueprint §7/§8.1, tests in
// ScreenPositionerTests). Never mixes WPF DIPs with Win32 pixels: callers pass a
// DPI scale and get physical-pixel rects back.
// ---------------------------------------------------------------------------

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

public sealed class ScreenPositioner : IScreenPositioner
{
    /// <summary>Gap between a group-list chip and its dropdown / a bar chip and its note window (physical px at 96 DPI; callers pass physical sizes).</summary>
    public const int AnchorGapPx = 6;

    /// <summary>Group list opens below its chip with this offset.</summary>
    public const int GroupListGapPx = 4;

    public const int TriggerDipWidth = 100;
    public const int TriggerDipHeight = 4;
    public const int BarDipHeight = 44;
    public const int BarDipMaxWidth = 1100;
    public const double BarMaxWorkWidthFraction = 0.92;

    public PxRect GetTriggerRect(MonitorBounds monitor, double dpi)
    {
        PxRect wa = monitor.WorkArea;
        int w = (int)Math.Round(TriggerDipWidth * dpi);
        int h = (int)Math.Round(TriggerDipHeight * dpi);
        int x = wa.Left + (wa.Width - w) / 2;
        return new PxRect(x, wa.Top, w, h);
    }

    public PxRect GetBarRect(MonitorBounds monitor, double dpi)
    {
        PxRect wa = monitor.WorkArea;
        int h = (int)Math.Round(BarDipHeight * dpi);
        int maxByDips = (int)Math.Round(BarDipMaxWidth * dpi);
        int maxByWork = (int)(wa.Width * BarMaxWorkWidthFraction);
        int w = Math.Min(maxByDips, maxByWork);
        w = Math.Min(w, wa.Width);
        int x = wa.Left + (wa.Width - w) / 2;
        return new PxRect(x, wa.Top, w, h);
    }

    public PxRect GetGroupListRect(PxRect anchor, PxSize size, MonitorBounds monitor)
    {
        PxRect wa = monitor.WorkArea;

        int w = Math.Min(size.Width, wa.Width);
        int left = anchor.Left;
        // Clamp horizontally into the work area.
        if (left + w > wa.Right)
        {
            left = wa.Right - w;
        }

        if (left < wa.Left)
        {
            left = wa.Left;
        }

        int top = anchor.Bottom + GroupListGapPx;
        if (top > wa.Bottom)
        {
            top = wa.Bottom;
        }

        // Clamp vertically: never extend past the work area bottom.
        int h = Math.Min(size.Height, wa.Bottom - top);
        if (h < 0)
        {
            h = 0;
        }

        return new PxRect(left, top, w, h);
    }

    public PxRect GetNoteWindowRect(PxRect anchor, PxSize size, MonitorBounds monitor, bool anchorIsHorizontalBarItem)
    {
        PxRect wa = monitor.WorkArea;

        int w = Math.Min(size.Width, wa.Width);
        int h = Math.Min(size.Height, wa.Height);

        int left;
        int top;
        if (anchorIsHorizontalBarItem)
        {
            // Opens directly below the bar chip; shifts left instead of overflowing the right edge.
            left = anchor.Left;
            top = anchor.Bottom + AnchorGapPx;
            if (left + w > wa.Right)
            {
                left = wa.Right - w;
            }
        }
        else
        {
            // Opens to the right of the list row; flips to the left side when there is no room.
            left = anchor.Right + AnchorGapPx;
            if (left + w > wa.Right)
            {
                left = anchor.Left - AnchorGapPx - w;
            }

            top = anchor.Top;
        }

        // Absolute horizontal safety clamp (property test: nothing may ever leave the work area).
        if (left < wa.Left)
        {
            left = wa.Left;
        }

        if (left + w > wa.Right)
        {
            left = wa.Right - w;
        }

        // Vertical clamp: pin to the work area bottom edge when near the bottom.
        if (top + h > wa.Bottom)
        {
            top = wa.Bottom - h;
        }

        if (top < wa.Top)
        {
            top = wa.Top;
        }

        return new PxRect(left, top, w, h);
    }
}
