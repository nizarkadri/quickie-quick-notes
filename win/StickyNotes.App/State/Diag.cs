using System;
using System.IO;

namespace StickyNotes.App.State;

/// <summary>
/// Opt-in diagnostic trace (set STICKY_TRACE=1): appends one line per interesting lifecycle decision to
/// <c>%TEMP%\sticky-notes-trace.log</c>. Used to pin down "the new note closes instantly" without a
/// debugger attached. Zero cost when the env var is absent (one static bool check); never throws.
/// </summary>
public static class Diag
{
    private static readonly bool Enabled =
        Environment.GetEnvironmentVariable("STICKY_TRACE") is { Length: > 0 } v && v != "0";

    public static string LogPath => Path.Combine(Path.GetTempPath(), "sticky-notes-trace.log");

    public static bool IsEnabled => Enabled;

    public static void Log(string message)
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // tracing must never break the app: IO, permissions, invalid path (ArgumentException /
            // NotSupportedException), encoding — swallow them all.
        }
    }
}
