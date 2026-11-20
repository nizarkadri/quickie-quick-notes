using System;
using System.IO;

namespace StickyNotes.App.State;

/// <summary>
/// When the configured database path has no database yet, seed it by copying the most recent
/// existing database (the previously-used path first, then the default location). An existing
/// database at the target is never overwritten — a folder that already holds notes is respected.
/// </summary>
internal static class StorageMigration
{
    /// <summary>
    /// Copy the first existing <paramref name="candidateSources"/> entry into
    /// <paramref name="targetDbPath"/> when the target does not yet exist. Returns true if a copy
    /// happened.
    /// </summary>
    internal static bool SeedDatabaseIfMissing(string targetDbPath, params string?[] candidateSources)
    {
        if (File.Exists(targetDbPath))
        {
            return false; // respect an existing database at the target
        }

        foreach (string? source in candidateSources)
        {
            if (string.IsNullOrWhiteSpace(source) || !File.Exists(source) || PathsEqual(source!, targetDbPath))
            {
                continue;
            }

            string? dir = Path.GetDirectoryName(targetDbPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.Copy(source!, targetDbPath, overwrite: false);
            return true;
        }

        return false;
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}