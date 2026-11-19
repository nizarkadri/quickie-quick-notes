using System;

namespace StickyNotes.App.State;

/// <summary>
/// Pure hover-dwell detector (v0.2.2, "group list opens ON HOVER"). Callers tick
/// <see cref="Poll"/> with a monotonic millisecond timestamp (e.g. Environment.TickCount64) as the
/// pointer moves across UI elements; it reports <see cref="HoverStage.Ready"/> exactly once per
/// uninterrupted dwell of <see cref="DwellMs"/>. Re-entering / leaving the element cancels the
/// pending dwell; a fresh entry starts a new one.
/// </summary>
public sealed class HoverDwellTracker
{
    /// <summary>Hover time (ms) before a group chip's list opens — matches the bar's trigger dwell.</summary>
    public const int DwellMs = 180;

    public enum HoverStage
    {
        None,
        Started,
        Ready,
    }

    private long _enteredAt = -1;

    /// <summary>True while a dwell is counting toward <see cref="DwellMs"/>.</summary>
    public bool IsHovering => _enteredAt >= 0;

    /// <summary>
    /// Advance the dwell. <paramref name="inside"/> is true while the pointer is over the element;
    /// <paramref name="nowMs"/> is any monotonic clock. Returns:
    /// <see cref="HoverStage.Started"/> once when <paramref name="inside"/> turns true,
    /// <see cref="HoverStage.Ready"/> once when the dwell completes (inside stays true),
    /// <see cref="HoverStage.None"/> otherwise.
    /// </summary>
    public HoverStage Poll(bool inside, long nowMs)
    {
        if (!inside)
        {
            _enteredAt = -1;
            return HoverStage.None;
        }

        if (_enteredAt < 0)
        {
            _enteredAt = nowMs;
            return HoverStage.Started;
        }

        return nowMs - _enteredAt >= DwellMs ? HoverStage.Ready : HoverStage.None;
    }

    /// <summary>Cancel any in-flight dwell (pointer left the element or the element was rebuilt).</summary>
    public void Reset() => _enteredAt = -1;
}