using System;
using Microsoft.Win32;

namespace StickyNotes.App.Interop;

/// <summary>
/// Per-user auto-start ("start with Windows") via the HKCU ...\CurrentVersion\Run value. No admin
/// rights and no Startup-folder shortcut: the Run key is the standard, user-scoped mechanism.
/// </summary>
internal static class StartupRegistration
{
    internal const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ValueName = "StickyNotes";

    /// <summary>The command string the Run key stores: the exe path, quoted so spaces are safe. Pure.</summary>
    internal static string BuildCommand(string exePath) => $"\"{exePath}\"";

    /// <summary>Enable/disable launch-on-logon by writing or removing the Run value.</summary>
    public static void SetEnabled(bool enabled, string exePath)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (enabled)
        {
            key.SetValue(ValueName, BuildCommand(exePath), RegistryValueKind.String);
        }
        else if (key.GetValue(ValueName) is not null)
        {
            key.DeleteValue(ValueName);
        }
    }

    /// <summary>True when the Run value is present (auto-start enabled).</summary>
    public static bool IsEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is not null;
    }
}