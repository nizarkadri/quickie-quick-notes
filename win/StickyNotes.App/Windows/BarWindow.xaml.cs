using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Controls.Primitives;
using StickyNotes.App.Core;
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

    /// <summary>Physical bounds of the monitor this bar is shown on (set by ShowOnMonitor).</summary>
    public MonitorBounds? CurrentBounds { get; private set; }

    // ------------------------------------------------------------------ chips

    /// <summary>A left-rail or recents chip (or the trailing 'Groups ▾' button) was activated. Tag: "group:{id}" | "note:{id}" | "groups-menu".</summary>
    public event EventHandler<BarChipEventArgs>? ChipActivated;

    /// <summary>Right-click on a chip — toggle pin. Same tag grammar as <see cref="ChipActivated"/>.</summary>
    public event EventHandler<BarChipEventArgs>? ChipPinToggleRequested;

    private readonly Dictionary<string, Button> _chips = new();

    // F4: one non-activating ToolTip per note chip; closed when the bar hides or a chip is clicked.
    private readonly Dictionary<string, TextBlock> _tips = new();

    /// <summary>
    /// F4: resolve a note body for the read-only hover preview. Must use markOpened:false so
    /// hovering never bumps last_opened_at_ms / recents ordering. Returning null hides the bubble.
    /// </summary>
    public Func<long, string?>? PreviewResolver { get; set; }

    /// <summary>Raised when the user clicks the centered '+' (coordinator opens the real QuickNoteWindow).</summary>
    public event EventHandler? QuickCaptureRequested;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        nint hwnd = new WindowInteropHelper(this).Handle;
        FocusHelper.ApplyNonActivatingToolWindowStyles(hwnd);
        FocusHelper.InstallNonActivatingHook(hwnd);
        FocusHelper.ApplyRoundedCorners(hwnd);
        // NOTE: HMonitor is set by ShowOnMonitor(bounds, hMonitor, dpi) — never derive it from an unpositioned HWND (FS1 trap).
    }

    /// <summary>Rebuild the left rail (pinned rail: PinnedItems by pinned_at_ms ASC + trailing 'Groups ▾') and the recents panel (up to 5).</summary>
    public void Rebuild(BarSnapshot snapshot)
    {
        CloseTips();
        _chips.Clear();
        _tips.Clear();

        PinnedPanel.Children.Clear();
        foreach (PinnedItem item in snapshot.PinnedItems)
        {
            if (item.Group is not null)
            {
                PinnedPanel.Children.Add(BuildChip("group:" + item.Group.Id, item.Group.Name, strikePinned: false));
            }
            else if (item.Note is not null)
            {
                PinnedPanel.Children.Add(BuildChip("note:" + item.Note.Id, item.Note.TitlePreview, strikePinned: true));
            }
        }

        PinnedPanel.Children.Add(BuildChip("groups-menu", "Groups ▾", strikePinned: false, isMenu: true));

        RecentPanel.Children.Clear();
        foreach (NoteSummary note in snapshot.RecentNotes.Take(5))
        {
            if (note.IsPinned)
            {
                continue; // standalone pinned notes already live in the rail — never duplicate
            }

            RecentPanel.Children.Add(BuildChip("note:" + note.Id, note.TitlePreview, strikePinned: false));
        }
    }

    /// <summary>Physical-pixel rect of the chip with the given tag; default when the chip doesn't exist or the bar is hidden.</summary>
    public bool TryGetChipRect(string tag, out PxRect rect)
    {
        rect = default;
        if (Visibility != Visibility.Visible)
        {
            return false;
        }

        if (!_chips.TryGetValue(tag, out Button? chip))
        {
            return false;
        }

        var root = (FrameworkElement)chip;
        Point topLeft = root.PointToScreen(new Point(0, 0));
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int x = (int)Math.Round(topLeft.X);
        int y = (int)Math.Round(topLeft.Y);
        // PointToScreen already returns PHYSICAL pixels; rescale the size from DIPs by this window's DPI.
        int w = (int)Math.Round(root.RenderSize.Width * dpi);
        int h = (int)Math.Round(root.RenderSize.Height * dpi);
        rect = new PxRect(x, y, w, h);
        return true;
    }

    private Button BuildChip(string tag, string label, bool strikePinned, bool isMenu = false)
    {
        var chip = new Button
        {
            Tag = tag,
            Height = 30,
            Padding = new Thickness(10, 0, 10, 0),
            Margin = new Thickness(0, 0, 4, 0),
            Background = new SolidColorBrush(isMenu ? System.Windows.Media.Color.FromArgb(0x38, 0x60, 0xA0, 0xE0) : System.Windows.Media.Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = label,
        };

        var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = (Brush)Application.Current.Resources["TextPrimary"],
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 180,
        });

        if (strikePinned)
        {
            content.Children.Add(new TextBlock
            {
                Text = " 📌",
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        chip.Content = content;
        chip.Click += (_, _) =>
        {
            CloseTips();
            ChipActivated?.Invoke(this, new BarChipEventArgs(tag));
        };
        chip.MouseRightButtonUp += (_, _) =>
        {
            CloseTips();
            ChipPinToggleRequested?.Invoke(this, new BarChipEventArgs(tag));
        };

        if (tag.StartsWith("note:", StringComparison.Ordinal))
        {
            AttachNotePreview(chip, tag);
        }

        _chips[tag] = chip;
        return chip;
    }

    /// <summary>
    /// F4: attach a read-only hover preview to a note chip. The body is fetched on ToolTipOpening
    /// (not on hover-enter) so a quick pass over the bar costs nothing, and the tooltip is a
    /// non-activating popup.
    /// </summary>
    private void AttachNotePreview(Button chip, string tag)
    {
        if (!long.TryParse(tag.AsSpan("note:".Length), out long noteId))
        {
            return;
        }

        // WPF forbids attaching a ToolTip with StaysOpen=false to an element, so the bubble is
        // shown as ToolTipService content; its default lifetime already dismisses on mouse-away.
        TextBlock text = NotePreview.CreateContent();
        ToolTipService.SetToolTip(chip, text);
        ToolTipService.SetPlacement(chip, PlacementMode.Bottom);
        ToolTipService.SetVerticalOffset(chip, NotePreview.VerticalOffset);
        ToolTipService.SetInitialShowDelay(chip, NotePreview.InitialShowDelayMs);
        _tips[tag] = text;

        // A manual CloseTips() clears the tooltip; re-arm it on the next hover.
        chip.MouseEnter += (_, _) => ToolTipService.SetToolTip(chip, text);

        chip.ToolTipOpening += (_, e) =>
        {
            string? body = ResolvePreview(noteId);
            if (body is null)
            {
                e.Handled = true; // note vanished / core error — no misleading bubble
                return;
            }

            text.Text = NotePreview.Format(body);
        };
    }

    private string? ResolvePreview(long noteId)
    {
        try
        {
            return PreviewResolver?.Invoke(noteId);
        }
        catch (StickyCoreException)
        {
            return null;
        }
        catch (ObjectDisposedException)
        {
            return null; // shutdown raced a hover
        }
    }

    private void CloseTips()
    {
        foreach (string tag in _tips.Keys)
        {
            if (_chips.TryGetValue(tag, out Button? chip))
            {
                ToolTipService.SetToolTip(chip, null); // re-armed by the chip's MouseEnter handler
            }
        }
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
        CurrentBounds = bounds;
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
        CloseTips(); // a tooltip must not linger after the bar slides away

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

    private void OnPlusClick(object sender, RoutedEventArgs e) =>
        QuickCaptureRequested?.Invoke(this, EventArgs.Empty);

    protected override void OnClosed(EventArgs e)
    {
        FocusHelper.ForgetWindow(new WindowInteropHelper(this).Handle);
        base.OnClosed(e);
    }
}

public sealed class BarChipEventArgs : EventArgs
{
    public BarChipEventArgs(string tag) => Tag = tag;

    /// <summary>"group:{id}" | "note:{id}" | "groups-menu".</summary>
    public string Tag { get; }
}
