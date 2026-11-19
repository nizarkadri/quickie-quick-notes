namespace StickyNotes.App.State;

/// <summary>Cursor-pose edge produced by the last <see cref="ExitPoseTracker.Update"/> call.</summary>
public enum ExitPoseTransition
{
    None,
    Entered,
    Left,
}

/// <summary>
/// Pure edge detector for "cursor inside any app surface" (v0.2.2). Starts outside (the app does
/// not know where the cursor is until the first tick); each <see cref="Update"/> reports only the
/// crossing, so per-tick side effects (close editors, start/cancel the exit fade) run once per edge.
/// </summary>
public sealed class ExitPoseTracker
{
    private bool _inside;

    /// <summary>Last observed pose. False until the first <see cref="Update"/> says otherwise.</summary>
    public bool IsInside => _inside;

    /// <summary>Feed the current pose; returns <see cref="ExitPoseTransition.Entered"/> / <see cref="ExitPoseTransition.Left"/> on a crossing, otherwise <see cref="ExitPoseTransition.None"/>.</summary>
    public ExitPoseTransition Update(bool inside)
    {
        if (inside == _inside)
        {
            return ExitPoseTransition.None;
        }

        _inside = inside;
        return inside ? ExitPoseTransition.Entered : ExitPoseTransition.Left;
    }

    /// <summary>Reset the pose to "outside" (e.g. after a full hide: the next tick must re-learn it, not replay a stale fade). Matches the initial state.</summary>
    public void Reset() => _inside = false;
}
