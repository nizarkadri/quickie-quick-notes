using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;

namespace StickyNotes.App.Interop;

/// <summary>
/// Focus capture/restore + non-activating window helpers (blueprint §7).
/// A WS_EX_NOACTIVATE window cannot receive keyboard input; inline-edit affordances
/// (TextBox inside GroupListWindow) therefore temporarily drop NOACTIVATE via
/// <see cref="BeginInlineEdit"/> and restore it via <see cref="EndInlineEdit"/>.
/// </summary>
public static class FocusHelper
{
    private sealed class WindowState
    {
        public bool AllowActivate;
        public IntPtr SavedForeground = IntPtr.Zero;
    }

    private static readonly Dictionary<IntPtr, WindowState> States = new();
    private static readonly HashSet<IntPtr> HookedHwnds = new();

    /// <summary>Foreground window captured before the last ForceActivateAndFocus / BeginInlineEdit, if it was external.</summary>
    public static IntPtr SavedForegroundHwnd { get; private set; }

    private static WindowState StateFor(IntPtr hwnd)
    {
        lock (States)
        {
            if (!States.TryGetValue(hwnd, out WindowState? state))
            {
                state = new WindowState();
                States.Add(hwnd, state);
            }

            return state;
        }
    }

    /// <summary>Forget the tracked state for a window (call after it closes to avoid a stale-hwnd dictionary leak).</summary>
    public static void ForgetWindow(IntPtr hwnd)
    {
        lock (States)
        {
            States.Remove(hwnd);
            HookedHwnds.Remove(hwnd);
        }
    }

    /// <summary>
    /// Registers (if not already registered) the WM_MOUSEACTIVATE → MA_NOACTIVATE gate for a
    /// non-activating tool window. While <see cref="BeginInlineEdit"/> is active the gate is paused.
    /// Call from OnSourceInitialized after ShowActivated = false.
    /// </summary>
    public static void InstallNonActivatingHook(IntPtr hwnd)
    {
        lock (States)
        {
            if (!HookedHwnds.Add(hwnd))
            {
                return; // already hooked — HwndSource.AddHook is not idempotent
            }
        }

        StateFor(hwnd);
        if (HwndSource.FromHwnd(hwnd) is not { } source)
        {
            lock (States)
            {
                HookedHwnds.Remove(hwnd);
            }

            return;
        }

        source.AddHook((IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg == Win32.WM_MOUSEACTIVATE && !StateFor(h).AllowActivate)
            {
                handled = true;
                return new IntPtr(Win32.MA_NOACTIVATE);
            }

            return IntPtr.Zero;
        });
    }

    /// <summary>Apply WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE to an HWND.</summary>
    public static void ApplyNonActivatingToolWindowStyles(IntPtr hwnd)
    {
        int exStyle = Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE);
        Win32.SetWindowLong(hwnd, Win32.GWL_EXSTYLE, exStyle | Win32.WS_EX_TOPMOST | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE);
    }

    /// <summary>Optional Win11 hardware rounded corners; safe no-op on Win10.</summary>
    public static void ApplyRoundedCorners(IntPtr hwnd)
    {
        int pref = Win32.DWMWCP_ROUND;
        // PreserveSig = true → returns HRESULT, ignored on failure (Win10).
        _ = Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
    }

    private static IntPtr CaptureExternalForeground()
    {
        IntPtr fg = Win32.GetForegroundWindow();
        if (fg != IntPtr.Zero && !Win32.IsOwnAppWindow(fg))
        {
            SavedForegroundHwnd = fg;
            return fg;
        }

        return IntPtr.Zero;
    }

    /// <summary>Capture the foreground window if it belongs to another app (used before activating an editor).</summary>
    public static void CaptureExternalForegroundHwnd() => CaptureExternalForeground();

    /// <summary>
    /// Begin inline editing inside a WS_EX_NOACTIVATE window: captures the external foreground
    /// window, pauses the MA_NOACTIVATE gate, clears WS_EX_NOACTIVATE, activates the window and
    /// focuses the text box. NEVER call from TextBox.GotFocus — call from Button.Click.
    /// </summary>
    public static void BeginInlineEdit(Window window, TextBox box)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(box);

        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        WindowState state = StateFor(hwnd);

        state.SavedForeground = CaptureExternalForeground();

        state.AllowActivate = true;
        int exStyle = Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE);
        Win32.SetWindowLong(hwnd, Win32.GWL_EXSTYLE, exStyle & ~Win32.WS_EX_NOACTIVATE);

        box.Visibility = Visibility.Visible;
        ForceActivateAndFocus(window, hwnd, box);
        box.SelectAll();
    }

    /// <summary>
    /// End inline editing: hides the box, restores the MA_NOACTIVATE gate and WS_EX_NOACTIVATE,
    /// and optionally restores the previously captured foreground window.
    /// </summary>
    public static void EndInlineEdit(Window window, TextBox box, bool restorePreviousForeground)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(box);

        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        WindowState state = StateFor(hwnd);

        box.Visibility = Visibility.Collapsed;
        state.AllowActivate = false;

        int exStyle = Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE);
        Win32.SetWindowLong(hwnd, Win32.GWL_EXSTYLE, exStyle | Win32.WS_EX_NOACTIVATE);

        if (restorePreviousForeground)
        {
            // Per-window restore: each inline editor returns focus to ITS OWN captured app.
            IntPtr target = state.SavedForeground;
            if (target != IntPtr.Zero && Win32.IsWindow(target))
            {
                _ = Win32.SetForegroundWindow(target);
            }

            if (SavedForegroundHwnd == target)
            {
                SavedForegroundHwnd = IntPtr.Zero;
            }

            state.SavedForeground = IntPtr.Zero;
        }
    }

    /// <summary>
    /// Active show with focus capture + AttachThreadInput fallback (QuickNote "+" or "+ New Note").
    /// If the foreground process belongs to another app, tries to steal foreground rights via
    /// AttachThreadInput; UIPI blocks this against elevated (Admin) windows — clicking the editor
    /// then activates it natively.
    /// </summary>
    public static void ForceActivateAndFocus(Window editorWindow, IntPtr editorHwnd, IInputElement focusTarget)
    {
        ArgumentNullException.ThrowIfNull(editorWindow);
        ArgumentNullException.ThrowIfNull(focusTarget);

        IntPtr prevFg = Win32.GetForegroundWindow();
        if (prevFg != IntPtr.Zero && !Win32.IsOwnAppWindow(prevFg))
        {
            SavedForegroundHwnd = prevFg;
        }

        _ = Win32.SetWindowPos(editorHwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_SHOWWINDOW);

        if (!Win32.SetForegroundWindow(editorHwnd) || Win32.GetForegroundWindow() != editorHwnd)
        {
            uint fgThread = prevFg != IntPtr.Zero ? Win32.GetWindowThreadProcessId(prevFg, out _) : 0;
            uint curThread = Win32.GetCurrentThreadId();
            if (fgThread != 0 && fgThread != curThread && Win32.AttachThreadInput(curThread, fgThread, true))
            {
                try
                {
                    _ = Win32.BringWindowToTop(editorHwnd);
                    _ = Win32.SetForegroundWindow(editorHwnd);
                }
                finally
                {
                    _ = Win32.AttachThreadInput(curThread, fgThread, false);
                }
            }
        }

        editorWindow.Activate();
        Keyboard.Focus(focusTarget);
    }

    /// <summary>Passive show: keep the window TOPMOST without activating it (NoteWindow reference reading).</summary>
    public static void ShowNonActivating(IntPtr hwnd, int x, int y, int width, int height)
    {
        _ = Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, x, y, width, height,
            Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
    }

    /// <summary>Restore the previously captured external foreground window, if any. Cleared after a successful restore.</summary>
    public static void RestorePreviousForeground()
    {
        IntPtr saved = SavedForegroundHwnd;
        if (saved != IntPtr.Zero && Win32.IsWindow(saved))
        {
            _ = Win32.SetForegroundWindow(saved);
            SavedForegroundHwnd = IntPtr.Zero;
        }
    }

    /// <summary>
    /// Cheap fullscreen check for ONE monitor: true when the foreground window is on the target
    /// monitor AND (a D3D fullscreen/presentation session is running, or the window is caption-less,
    /// visible and covers the monitor's full rcMonitor). QUNS is system-global, so it is honoured
    /// only after the monitor gate — a game on monitor 1 must not suppress monitor 2.
    /// QUNS_BUSY is intentionally NOT treated as fullscreen (DND/screen-share false positives).
    /// </summary>
    public static bool IsFullscreenOnMonitor(IntPtr targetMonitor)
    {
        IntPtr fg = Win32.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == Win32.GetShellWindow() || Win32.IsDesktopClass(fg))
        {
            return false;
        }

        if (Win32.MonitorFromWindow(fg, Win32.MONITOR_DEFAULTTONULL) != targetMonitor)
        {
            return false;
        }

        if (Win32.SHQueryUserNotificationState(out int quns) == 0
            && (quns == Win32.QUNS_RUNNING_D3D_FULL_SCREEN || quns == Win32.QUNS_PRESENTATION_MODE))
        {
            return true;
        }

        int style = Win32.GetWindowLong(fg, Win32.GWL_STYLE);
        if ((style & Win32.WS_VISIBLE) == 0 || (style & Win32.WS_CAPTION) != 0)
        {
            return false; // Normal maximized windows keep WS_CAPTION; caption-less borders are fullscreen games/F11 apps.
        }

        if (!Win32.GetWindowRect(fg, out RECT win))
        {
            return false;
        }

        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!Win32.GetMonitorInfo(targetMonitor, ref mi))
        {
            return false;
        }

        return win.Left <= mi.rcMonitor.Left && win.Top <= mi.rcMonitor.Top
            && win.Right >= mi.rcMonitor.Right && win.Bottom >= mi.rcMonitor.Bottom;
    }
}
