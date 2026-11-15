using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using StickyNotes.App.Interop;

namespace StickyNotes.App.Windows;

/// <summary>
/// 100x4 DIP auto-hide notch pinned to the top-center of ONE monitor (blueprint §7/§8.1).
/// Borderless, topmost, tool-window, never activates. One instance per connected monitor.
/// Placement is done in PHYSICAL pixels via SetWindowPos at OnSourceInitialized; DPI is read
/// from the TARGET monitor (GetDpiForMonitor), not from the window.
/// </summary>
public partial class TriggerWindow : Window
{
    private const int WM_DPICHANGED = 0x02E0;

    private readonly MonitorBounds _bounds;
    private readonly nint _hMonitor;
    private readonly ScreenPositioner _positioner = new();

    public TriggerWindow(MonitorBounds bounds, nint hMonitor)
    {
        ArgumentNullException.ThrowIfNull(bounds);
        _bounds = bounds;
        _hMonitor = hMonitor; // authoritative — do NOT re-derive from the HWND after moving

        InitializeComponent();

        MouseEnter += OnMouseEnter;
        MouseLeave += OnMouseLeave;
        Pill.MouseEnter += (_, _) => Pill.Background = (Brush)Application.Current.Resources["PillHover"];
        Pill.MouseLeave += (_, _) => Pill.Background = (Brush)Application.Current.Resources["PillIdle"];
    }

    /// <summary>HMONITOR of the monitor this trigger lives on (supplied by the enumerator — never re-derived).</summary>
    public nint HMonitor => _hMonitor;

    /// <summary>Monitor bounds (physical pixels) this trigger represents.</summary>
    public MonitorBounds Bounds => _bounds;

    /// <summary>Raised when the cursor enters this trigger's notch.</summary>
    public event EventHandler<TriggerMonitorEventArgs>? TriggerEntered;

    /// <summary>Raised when the cursor leaves this trigger's notch.</summary>
    public event EventHandler<TriggerMonitorEventArgs>? TriggerLeft;

    public void ShowOnMonitor()
    {
        Show(); // OnSourceInitialized positions us; after the first call Position() repositions.
        Position();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        nint hwnd = new WindowInteropHelper(this).Handle;
        FocusHelper.ApplyNonActivatingToolWindowStyles(hwnd);
        FocusHelper.InstallNonActivatingHook(hwnd);

        HwndSource.FromHwnd(hwnd)?.AddHook((IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg == WM_DPICHANGED)
            {
                Position(); // re-place at the new scale; positioner math stays in physical px
            }

            return IntPtr.Zero;
        });

        Position();
    }

    private void Position()
    {
        nint hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == nint.Zero)
        {
            return;
        }

        double dpi = Win32.DpiScaleOf(_hMonitor) ?? VisualTreeHelper.GetDpi(this).DpiScaleX;
        PxRect rect = _positioner.GetTriggerRect(_bounds, dpi);
        _ = Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, rect.X, rect.Y, rect.Width, rect.Height,
            Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
    }

    /// <summary>Physical pixel rect of this trigger (from GetWindowRect), for cursor hit-testing.</summary>
    public bool TryGetPhysicalRect(out PxRect rect)
    {
        rect = default;
        nint hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == nint.Zero)
        {
            return false;
        }

        if (!Win32.GetWindowRect(hwnd, out RECT r))
        {
            return false;
        }

        rect = new PxRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        return true;
    }

    private void OnMouseEnter(object sender, MouseEventArgs e) =>
        TriggerEntered?.Invoke(this, new TriggerMonitorEventArgs(HMonitor));

    private void OnMouseLeave(object sender, MouseEventArgs e) =>
        TriggerLeft?.Invoke(this, new TriggerMonitorEventArgs(HMonitor));

    protected override void OnClosed(EventArgs e)
    {
        FocusHelper.ForgetWindow(new WindowInteropHelper(this).Handle);
        base.OnClosed(e);
    }
}

public sealed class TriggerMonitorEventArgs : EventArgs
{
    public TriggerMonitorEventArgs(nint monitor) => Monitor = monitor;

    public nint Monitor { get; }
}
