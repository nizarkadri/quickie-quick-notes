using System.IO;
using StickyNotes.App.State;
using Xunit;

namespace StickyNotes.Tests;

/// <summary>
/// Settings/path/migration logic is pure or filesystem-only (no focus, z-order, or registry), so it
/// is fully unit-testable. These tests are the contract for the settings feature; the registry write
/// and the SettingsWindow UI are covered by manual acceptance instead (AGENTS.md rule 6).
/// </summary>
public class AppSettingsTests
{
    private static AppSettings Settings(string? folder, bool startup = false, string? previousDbPath = null) =>
        new() { NotesFolder = folder, StartWithWindows = startup, PreviousDbPath = previousDbPath };

    [Fact]
    public void Default_folder_is_used_when_none_chosen()
    {
        Assert.Equal(AppSettingsStore.DefaultNotesFolder, AppSettingsStore.ResolveNotesFolder(Settings(null)));
        Assert.Equal(AppSettingsStore.DefaultNotesFolder, AppSettingsStore.ResolveNotesFolder(Settings("   ")));
    }

    [Fact]
    public void Chosen_folder_is_used_verbatim()
    {
        const string chosen = @"D:\My Notes";
        Assert.Equal(chosen, AppSettingsStore.ResolveNotesFolder(Settings(chosen)));
    }

    [Fact]
    public void Db_path_is_sticky_db_inside_the_notes_folder()
    {
        string dbPath = AppSettingsStore.ResolveDbPath(Settings(@"D:\My Notes"));
        Assert.Equal(Path.Combine(@"D:\My Notes", "sticky.db"), dbPath);
    }

    [Fact]
    public void Save_then_load_round_trips_every_field()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sn-settings-" + Path.GetRandomFileName());
        string path = Path.Combine(dir, "settings.json");
        try
        {
            AppSettingsStore.Save(Settings(@"E:\Notes", startup: true, previousDbPath: @"E:\Notes\sticky.db"), path);
            AppSettings loaded = AppSettingsStore.Load(path);

            Assert.Equal(@"E:\Notes", loaded.NotesFolder);
            Assert.True(loaded.StartWithWindows);
            Assert.Equal(@"E:\Notes\sticky.db", loaded.PreviousDbPath);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Load_missing_file_returns_defaults()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sn-settings-" + Path.GetRandomFileName());
        AppSettings loaded = AppSettingsStore.Load(Path.Combine(dir, "does-not-exist.json"));

        Assert.Null(loaded.NotesFolder);
        Assert.False(loaded.StartWithWindows);
        Assert.Null(loaded.PreviousDbPath);
    }

    [Fact]
    public void Load_corrupt_file_returns_defaults_instead_of_throwing()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sn-settings-" + Path.GetRandomFileName());
        string path = Path.Combine(dir, "settings.json");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, "{ this is not valid json ");
            AppSettings loaded = AppSettingsStore.Load(path);

            Assert.Null(loaded.NotesFolder); // fell back to defaults, no throw
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}

/// <summary>Migration (folder change) must copy the old DB into a fresh folder without losing notes.</summary>
public class StorageMigrationTests
{
    [Fact]
    public void Seeds_missing_target_from_an_existing_source()
    {
        string root = NewTempDir();
        try
        {
            string source = Path.Combine(root, "old", "sticky.db");
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.WriteAllText(source, "OLD DATA");

            string target = Path.Combine(root, "new", "sticky.db");
            bool copied = StorageMigration.SeedDatabaseIfMissing(target, source, null);

            Assert.True(copied);
            Assert.True(File.Exists(target));
            Assert.Equal("OLD DATA", File.ReadAllText(target));
        }
        finally { Clean(root); }
    }

    [Fact]
    public void Never_overwrites_an_existing_target()
    {
        string root = NewTempDir();
        try
        {
            string source = Path.Combine(root, "old.db");
            File.WriteAllText(source, "OLD");
            string target = Path.Combine(root, "new", "sticky.db");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, "EXISTING");

            bool copied = StorageMigration.SeedDatabaseIfMissing(target, source);

            Assert.False(copied);
            Assert.Equal("EXISTING", File.ReadAllText(target)); // untouched
        }
        finally { Clean(root); }
    }

    [Fact]
    public void Copies_nothing_when_no_source_exists()
    {
        string root = NewTempDir();
        try
        {
            string target = Path.Combine(root, "new", "sticky.db");
            bool copied = StorageMigration.SeedDatabaseIfMissing(target, Path.Combine(root, "nope.db"), null);

            Assert.False(copied);
            Assert.False(File.Exists(target));
        }
        finally { Clean(root); }
    }

    [Fact]
    public void Target_equal_to_source_is_not_copied_onto_itself()
    {
        string root = NewTempDir();
        try
        {
            // Target doesn't exist yet, so seed runs; source == target would be a self-copy — skip it.
            string target = Path.Combine(root, "sticky.db");
            bool copied = StorageMigration.SeedDatabaseIfMissing(target, target);

            Assert.False(copied);
            Assert.False(File.Exists(target));
        }
        finally { Clean(root); }
    }

    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sn-migrate-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Clean(string dir)
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}

/// <summary>The Run-key command string is pure and must quote the exe path (spaces stay safe).</summary>
public class StartupRegistrationTests
{
    [Fact]
    public void Command_is_the_quoted_exe_path()
    {
        Assert.Equal("\"C:\\Program Files\\StickyNotes\\StickyNotes.App.exe\"",
            StickyNotes.App.Interop.StartupRegistration.BuildCommand(@"C:\Program Files\StickyNotes\StickyNotes.App.exe"));
    }

    [Fact]
    public void Run_key_path_and_value_name_are_pinned()
    {
        // Pinning these guards against an accidental registry-path change (which would orphan auto-start).
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Run",
            StickyNotes.App.Interop.StartupRegistration.RunKeyPath);
        Assert.Equal("StickyNotes", StickyNotes.App.Interop.StartupRegistration.ValueName);
    }
}