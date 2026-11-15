using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using StickyNotes.App.Interop;

namespace StickyNotes.App.Windows;

/// <summary>
/// The auto-hiding top bar (blueprint §8.1). Permanently non-activating tool window with a
/// centered 3-column layout: [pinned rail] | [+] | [recents]. Pre-created and hidden (never
/// closed except at app exit); shown via SetWindowPos(SWP_NOACTIVATE | SWP_SHOWWINDOW) after a
/// 180ms trigger dwell, slides in over 140ms, slides out over 100ms on hide.
/// Task 5 wires the shell + focus spike only; item population lands in Task 6.
/// </summary>
public partial class BarWindow : Window
{
    public const int RevealAnimMs = 140;
    public const int HideAnimMs = 100;

    private readonly ScreenPositioner _positioner = new();
    private int _visibilityToken; // guards against a stale hide-completion hiding a re-shown bar

    public BarWindow()
    {
        InitializeComponent();

        Visibility = Visibility.Hidden;
        ShowActivated = false;
    }

    public nint HMonitor { get; private set; }

    /// <summary>Raised when the user clicks the centered '+' (Task 8 wires quick capture; Task 5 spike only).</summary>
    public event EventHandler? QuickCaptureRequested;

    /// <summary>Raised when an editor (the spike TextBox) takes keyboard focus.</summary>
    public event EventHandler? EditorFocused;

    /// <summary>Raised when an editor loses keyboard focus (deactivation).</summary>
    public event EventHandler? EditorDeactivated;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        nint hwnd = new WindowInteropHelper(this).Handle;
        FocusHelper.ApplyNonActivatingToolWindowStyles(hwnd);
        FocusHelper.InstallNonActivatingHook(hwnd);
        FocusHelper.ApplyRoundedCorners(hwnd);
        // NOTE: HMonitor is set by ShowOnMonitor(bounds, hMonitor, dpi) — never derive it from an unpositioned HWND (FS1 trap).

        Deactivated += OnDeactivated;
    }

    /// <summary>Position the bar on the given monitor and play the 140ms slide-in (GPU TranslateTransform).</summary>
    public void ShowOnMonitor(MonitorBounds bounds, nint hMonitor, double dpi)
    {
        // HWND is pre-created via EnsureHandle() in the coordinator — never flash an unpositioned bar.
        nint hwnd = new WindowInteropHelper(this).Handle;

        PxRect rect = _positioner.GetBarRect(bounds, dpi);
        Width = rect.Width / dpi;
        Height = rect.Height / dpi;

        HMonitor = hMonitor;
        int token = ++_visibilityToken; // invalidate any pending hide-completion

        double startDipY = -(rect.Height / dpi); // DIP transform — never physical px
        var transform = new TranslateTransform(0, startDipY);
        Root.RenderTransform = transform;

        Visibility = Visibility.Visible;
        _ = Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, rect.X, rect.Y, rect.Width, rect.Height,
            Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);

        var slide = new DoubleAnimation(startDipY, 0, new Duration(TimeSpan.FromMilliseconds(RevealAnimMs)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        transform.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    /// <summary>Slide the bar up (100ms) then hide it. Mid-animation re-show wins via the visibility token.</summary>
    public void HideBar()
    {
        if (Visibility != Visibility.Visible)
        {
            return;
        }

        int token = ++_visibilityToken;

        double currentY = Root.RenderTransform is TranslateTransform t
            ? (double)t.GetValue(TranslateTransform.YProperty) // effective (animated) value, not the base value
            : 0;

        var transform = new TranslateTransform(0, currentY);
        Root.RenderTransform = transform;

        var slide = new DoubleAnimation(currentY, -Height, new Duration(TimeSpan.FromMilliseconds(HideAnimMs)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        slide.Completed += (_, _) =>
        {
            if (token == _visibilityToken) // not re-shown mid-hide
            {
                Visibility = Visibility.Hidden;
            }
        };
        transform.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    /// <summary>Physical pixel rect of the bar for hit-testing; default when not visible.</summary>
    public bool TryGetPhysicalRect(out PxRect rect)
    {
        rect = default;
        nint hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == nint.Zero || Visibility != Visibility.Visible)
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

    private void OnPlusClick(object sender, RoutedEventArgs e)
    {
        // --- Task 5 focus-spike: hand focus to a temporary activating TextBox. ---
        // Task 8 replaces SpikeHost with the real QuickNoteWindow.
        SpikeHost.Visibility = Visibility.Visible;
        FocusHelper.BeginInlineEdit(this, SpikeBox);
        EditorFocused?.Invoke(this, EventArgs.Empty);
        QuickCaptureRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnSpikeBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape || e.Key == Key.Enter)
        {
            EndSpike(commit: e.Key == Key.Enter);
            e.Handled = true;
        }
    }

    private void EndSpike(bool commit)
    {
        if (commit)
        {
            // Spike only: no persistence. Task 8 commits via StickyCoreClient.QuickCapture.
            SpikeBox.Clear();
        }

        SpikeHost.Visibility = Visibility.Collapsed;
        FocusHelper.EndInlineEdit(this, SpikeBox, restorePreviousForeground: true);
        EditorDeactivated?.Invoke(this, EventArgs.Empty);
    }

    private void OnDeactivated(object? sender, EventArgs e) =>
        EditorDeactivated?.Invoke(this, EventArgs.Empty);

    protected override void OnClosed(EventArgs e)
    {
        FocusHelper.ForgetWindow(new WindowInteropHelper(this).Handle);
        base.OnClosed(e);
    }
}
