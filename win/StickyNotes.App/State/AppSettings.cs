using System;
using System.IO;
using System.Text.Json;

namespace StickyNotes.App.State;

/// <summary>
/// Persistent user preferences. <see cref="NotesFolder"/> null means the default
/// (%LOCALAPPDATA%\StickyNotes). <see cref="PreviousDbPath"/> records the database path that was
/// actually in use last run, so a later folder change can migrate from it.
/// </summary>
public sealed class AppSettings
{
    public string? NotesFolder { get; set; }
    public bool StartWithWindows { get; set; }
    public string? PreviousDbPath { get; set; }
}

/// <summary>Loads/saves <see cref="AppSettings"/> as JSON and resolves the database path from it.</summary>
public static class AppSettingsStore
{
    /// <summary>%LOCALAPPDATA%\StickyNotes — holds both settings.json and the default sticky.db.</summary>
    public static string SettingsDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "StickyNotes");

    public static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    /// <summary>The folder used when the user has not chosen one.</summary>
    public static string DefaultNotesFolder => SettingsDirectory;

    private const string DbFileName = "sticky.db";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettings Load() => Load(SettingsPath);

    /// <summary>Reads settings from <paramref name="path"/>. A missing or unreadable/corrupt file yields defaults — settings must never block startup.</summary>
    internal static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Corrupt or locked settings fall back to defaults rather than crashing the app.
        }

        return new AppSettings();
    }

    public static void Save(AppSettings settings) => Save(settings, SettingsPath);

    internal static void Save(AppSettings settings, string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(settings, JsonOptions));
    }

    /// <summary>Effective notes folder: the user's choice, or the default when unset/blank. Pure path math.</summary>
    public static string ResolveNotesFolder(AppSettings settings)
    {
        return string.IsNullOrWhiteSpace(settings.NotesFolder) ? DefaultNotesFolder : settings.NotesFolder!;
    }

    /// <summary>Effective database file path (notes folder + sticky.db). Pure path math.</summary>
    public static string ResolveDbPath(AppSettings settings) =>
        Path.Combine(ResolveNotesFolder(settings), DbFileName);
}