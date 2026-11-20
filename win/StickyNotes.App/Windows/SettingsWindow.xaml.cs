using System;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using StickyNotes.App.Interop;
using StickyNotes.App.State;

namespace StickyNotes.App.Windows;

/// <summary>
/// User preferences dialog: where notes are saved (folder) and whether the app starts with Windows.
/// Saves <see cref="AppSettings"/>; the folder change is applied on next launch (the DB is migrated
/// by the startup path), while the auto-start toggle takes effect immediately via the HKCU Run key.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    // null = unchanged from the loaded settings; a chosen folder otherwise.
    private string? _pendingFolder;
    private bool _folderChanged;

    public SettingsWindow(AppSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        InitializeComponent();

        _pendingFolder = AppSettingsStore.ResolveNotesFolder(_settings);
        FolderBox.Text = _pendingFolder;
        StartupCheck.IsChecked = _settings.StartWithWindows;

        string dbPath = AppSettingsStore.ResolveDbPath(_settings);
        DbPathHint.Text = $"Database: {dbPath}";
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose a folder for your notes",
            Multiselect = false,
        };

        if (Directory.Exists(_pendingFolder))
        {
            dialog.InitialDirectory = _pendingFolder;
        }

        if (dialog.ShowDialog(this) == true)
        {
            _pendingFolder = dialog.FolderName;
            _folderChanged = !string.Equals(
                Path.GetFullPath(_pendingFolder),
                Path.GetFullPath(AppSettingsStore.ResolveNotesFolder(_settings)),
                StringComparison.OrdinalIgnoreCase);

            FolderBox.Text = _pendingFolder;
            DbPathHint.Text = $"Database (after restart): {Path.Combine(_pendingFolder, "sticky.db")}";
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        // Folder: persist only when it actually changed. A null/blank box means "keep current".
        if (_folderChanged && !string.IsNullOrWhiteSpace(_pendingFolder))
        {
            _settings.NotesFolder = _pendingFolder;
        }

        _settings.StartWithWindows = StartupCheck.IsChecked == true;

        // Auto-start applies now (independent of the restart needed for a folder change).
        try
        {
            string exePath = Environment.ProcessPath
                ?? Path.Combine(AppContext.BaseDirectory, "StickyNotes.App.exe");
            StartupRegistration.SetEnabled(_settings.StartWithWindows, exePath);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Registry write can fail under locked-down policy — keep the in-app preference, don't crash.
            System.Diagnostics.Trace.WriteLine($"StartupRegistration.SetEnabled failed: {ex}");
        }

        AppSettingsStore.Save(_settings);
        Close(); // modeless window: DialogResult is only valid for ShowDialog, so just close
    }
}