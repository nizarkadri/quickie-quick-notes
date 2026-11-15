using System;
using System.Runtime.InteropServices;
using System.Text;

namespace StickyNotes.App.Interop;

// ---------------------------------------------------------------------------
// Hand-written Win32 P/Invoke surface (blueprint §6/§7).
// All placement math is done in PHYSICAL pixels; WPF logical DIPs are converted
// at the window layer (VisualTreeHelper.GetDpi), never here.
// ---------------------------------------------------------------------------

[StructLayout(LayoutKind.Sequential)]
public struct POINT
{
    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Sequential)]
public struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

[StructLayout(LayoutKind.Sequential)]
public struct MONITORINFO
{
    public int cbSize;
    public RECT rcMonitor;
    public RECT rcWork;
    public uint dwFlags;
}

internal static class Win32
{
    // --- HWND_TOPMOST / SetWindowPos ---
    internal static readonly IntPtr HWND_TOPMOST = new(-1);
    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOZORDER = 0x0004;
    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint SWP_SHOWWINDOW = 0x0040;

    // --- extended styles ---
    internal const int GWL_STYLE = -16;
    internal const int GWL_EXSTYLE = -20;
    internal const int WS_VISIBLE = 0x10000000;
    internal const int WS_CAPTION = 0x00C00000;
    internal const int WS_EX_TOPMOST = 0x00000008;
    internal const int WS_EX_TOOLWINDOW = 0x00000080;
    internal const int WS_EX_NOACTIVATE = 0x08000000;

    // --- WM_MOUSEACTIVATE ---
    internal const int WM_MOUSEACTIVATE = 0x0021;
    internal const int MA_NOACTIVATE = 3;

    // --- MonitorFromWindow / MonitorFromPoint ---
    internal const uint MONITOR_DEFAULTTONULL = 0;
    internal const uint MONITOR_DEFAULTTONEAREST = 2;

    // --- SHQueryUserNotificationState ---
    internal const int QUNS_NOT_PRESENT = 1;
    internal const int QUNS_BUSY = 2; // intentionally excluded from fullscreen suppression (system-global)
    internal const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
    internal const int QUNS_PRESENTATION_MODE = 4;
    internal const int QUNS_ACCEPTS_NOTIFICATIONS = 5;
    internal const int QUNS_QUIET_TIME = 6;
    internal const int QUNS_APP = 7;

    // --- DWM (Win11 rounded corners; no-op safe on Win10) ---
    internal const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    internal const int DWMWCP_ROUND = 2;

    // --- input / activation ---
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    internal static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    internal static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    internal static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int X,
        int Y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("shell32.dll")]
    internal static extern int SHQueryUserNotificationState(out int pstate);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    /// <summary>Desktop class check for fullscreen suppression (Progman / WorkerW).</summary>
    internal static bool IsDesktopClass(IntPtr hwnd)
    {
        var sb = new StringBuilder(64);
        int len = GetClassName(hwnd, sb, sb.Capacity);
        if (len <= 0)
        {
            return false;
        }

        string cls = sb.ToString(0, len);
        return string.Equals(cls, "Progman", StringComparison.OrdinalIgnoreCase)
            || string.Equals(cls, "WorkerW", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when <paramref name="hwnd"/> belongs to this process.</summary>
    internal static bool IsOwnAppWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == (uint)Environment.ProcessId;
    }
}
