using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace StickyNotes.App;

/// <summary>
/// Application entry: single-instance gate → DB path init → tray icon with Exit →
/// ShellCoordinator (triggers/bar/state machine). Task 5.
/// Tray icon is a raw Shell_NotifyIcon (no WinForms/System.Drawing dependency).
/// </summary>
public partial class App : Application
{
    private const string MutexName = @"Local\StickyNotes.App.SingleInstance";
    private const uint TrayIconId = 1;
    private const uint WM_APP_TRAYICON = 0x0401; // WM_APP + 1
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIM_SETVERSION = 0x00000004;
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint NOTIFYICON_VERSION_3 = 3;
    private static readonly IntPtr IDI_APPLICATION = new(32512);

    // Full NOTIFYICONDATAW — cbSize must match the documented platform size (976 bytes on x64),
    // not the truncated V1 size. V3 keeps uCallbackMessage delivering the mouse message in lParam.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public uint uTimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    private Mutex? _singleInstanceMutex;
    private bool _ownsMutex;
    private HwndSource? _traySink;
    private IntPtr _trayIcon;
    private State.ShellCoordinator? _coordinator;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Single instance first: a second .exe exits cleanly without touching anything.
        _singleInstanceMutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        _ownsMutex = createdNew;
        base.OnStartup(e); // base first — exiting before base.OnStartup is not supported

        if (!createdNew)
        {
            Shutdown();
            return;
        }

        string dbDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StickyNotes");
        Directory.CreateDirectory(dbDir);
        string dbPath = Path.Combine(dbDir, "sticky.db");

        CreateTrayIcon();

        _coordinator = new State.ShellCoordinator(dbPath);
        _coordinator.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // App-exit flush happens inside coordinator.Shutdown() (closes quick note → commit-if-non-empty,
        // flushes the note window), BEFORE the coordinator is disposed and sn_core_close runs.
        // Cleanup is in finally blocks so tray/mutex can't be stranded by an exception.
        try
        {
            _coordinator?.Shutdown(); // flushes dirty text, then closes triggers/bar/list + sn_core_close
        }
        catch (Exception ex)
        {
            // Release builds strip Debug.WriteLine — keep the failure observable via Trace (R1: call site IS Trace).
            System.Diagnostics.Trace.WriteLine($"ShellCoordinator.Shutdown failed: {ex}");
#if DEBUG
            System.Diagnostics.Debug.Fail($"ShellCoordinator.Shutdown failed: {ex}");
#endif
        }

        _coordinator = null;

        try
        {
            DestroyTrayIcon();
        }
        finally
        {
            if (_ownsMutex)
            {
                try
                {
                    _singleInstanceMutex?.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    // Mutex not owned anymore (or already released) — nothing to do.
                }
            }

            _singleInstanceMutex?.Dispose();
            _singleInstanceMutex = null;
            base.OnExit(e);
        }
    }

    // ------------------------------------------------------------------ tray

    private void CreateTrayIcon()
    {
        // Message sink window; never shown (no WS_VISIBLE) — only receives WM_APP_TRAYICON.
        // Message-only window (HWND_MESSAGE): invisible, never in the taskbar or Alt+Tab,
        // but still receives the WM_APP_TRAYICON callback sent to its HWND.
        var parameters = new HwndSourceParameters("StickyNotesTraySink")
        {
            ParentWindow = (IntPtr)(-3), // HWND_MESSAGE
            WindowStyle = 0,
            Width = 0,
            Height = 0,
        };

        _traySink = new HwndSource(parameters);
        _traySink.AddHook(TraySinkHook);

        _trayIcon = LoadIconW(IntPtr.Zero, IDI_APPLICATION);

        var data = new NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _traySink.Handle,
            uID = TrayIconId,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_APP_TRAYICON,
            hIcon = _trayIcon,
            szTip = "StickyNotes",
            szInfo = "",
            szInfoTitle = "",
        };

        if (!Shell_NotifyIconW(NIM_ADD, ref data))
        {
            System.Diagnostics.Trace.WriteLine($"Shell_NotifyIconW(NIM_ADD) failed; error {Marshal.GetLastWin32Error()}.");
        }

        // V3 keeps lParam == the mouse message for WM_APP_TRAYICON callbacks.
        data.uFlags = 0;
        data.uTimeoutOrVersion = NOTIFYICON_VERSION_3;
        _ = Shell_NotifyIconW(NIM_SETVERSION, ref data);
    }

    private void DestroyTrayIcon()
    {
        if (_traySink is not null)
        {
            var data = new NOTIFYICONDATAW
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd = _traySink.Handle,
                uID = TrayIconId,
            };

            _ = Shell_NotifyIconW(NIM_DELETE, ref data);
        }

        _traySink?.Dispose();
        _traySink = null;
    }

    private IntPtr TraySinkHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_APP_TRAYICON && (uint)lParam.ToInt64() == WM_RBUTTONUP)
        {
            ShowTrayMenu();
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void ShowTrayMenu()
    {
        var menu = new ContextMenu();
        var exitItem = new MenuItem { Header = "Exit" };
        exitItem.Click += (_, _) => Shutdown();
        menu.Items.Add(exitItem);

        // WPF converts the mouse position itself — no physical/DIP math needed.
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

}
