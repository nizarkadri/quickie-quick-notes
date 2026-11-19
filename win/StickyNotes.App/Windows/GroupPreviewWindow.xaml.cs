using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using StickyNotes.App.Core;
using StickyNotes.App.Interop;

namespace StickyNotes.App.Windows;

/// <summary>
/// v0.2.2+ side list: hovering a GROUP ROW inside the "Groups ▾" dropdown shows this window to the RIGHT
/// of the dropdown (aligned with the hovered row), listing that group's note titles. ADDITIVE — the
/// all-groups dropdown stays open underneath. Non-activating (never steals focus); rows open the note
/// via the coordinator's existing OpenNote path. Hidden, never closed, when the cursor leaves the row +
/// this window (~200ms grace) or the main dropdown closes.
/// </summary>
public partial class GroupPreviewWindow : Window
{
    private readonly ScreenPositioner _positioner = new();

    /// <summary>A note row was clicked — coordinator opens the note (same path as a group-list note row).</summary>
    public event Action<long, PxRect>? NoteClicked;

    /// <summary>The pointer entered/left THIS window (coordinator uses it to keep the side list open).</summary>
    public event EventHandler<bool>? PointerEnteredLeft;

    public GroupPreviewWindow()
    {
        InitializeComponent();
        Visibility = Visibility.Hidden;
        ShowActivated = false;
        MouseEnter += (_, _) => PointerEnteredLeft?.Invoke(this, true);
        MouseLeave += (_, _) => PointerEnteredLeft?.Invoke(this, false);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        nint hwnd = new WindowInteropHelper(this).Handle;
        FocusHelper.ApplyNonActivatingToolWindowStyles(hwnd);
        FocusHelper.InstallNonActivatingHook(hwnd);
        FocusHelper.ApplyRoundedCorners(hwnd);
    }

    /// <summary>Group whose notes are currently shown; null while hidden. Lets the coordinator skip re-shows.</summary>
    public long? ShownForGroupId { get; private set; }

    /// <summary>Populate with <paramref name="group"/>'s notes and show it right of the hovered row. Idempotent-safe: re-calling re-populates + re-positions.</summary>
    public void ShowForGroup(GroupSummary group, IReadOnlyList<NoteSummary> notes, PxRect rowRect, MonitorBounds bounds, double dpi)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(notes);

        ShownForGroupId = group.Id;
        GroupTitle.Text = group.Name;
        RowsPanel.Children.Clear();
        foreach (NoteSummary note in notes.OrderByDescending(n => n.LastOpenedAtMs))
        {
            RowsPanel.Children.Add(BuildNoteRow(note));
        }

        nint hwnd = new WindowInteropHelper(this).Handle;
        var size = new PxSize((int)Math.Round(240 * dpi), (int)Math.Round(420 * dpi));
        PxRect rect = _positioner.GetGroupPreviewRect(rowRect, size, bounds);

        // SizeToContent is OFF: the window height is exactly the clamped rect height (DIPs), so the
        // SetWindowPos height below can never fight the layout system (bottom clamp stays reliable).
        Height = Math.Max(1, rect.Height / dpi);
        Left = rect.X / dpi;
        Top = rect.Y / dpi;
        Visibility = Visibility.Visible;
        _ = Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, rect.X, rect.Y, rect.Width, rect.Height,
            Win32.SWP_NOACTIVATE);
    }

    /// <summary>Hide (never close — the window is reused).</summary>
    public void HidePreview()
    {
        if (Visibility != Visibility.Visible)
        {
            return;
        }

        ShownForGroupId = null;
        Visibility = Visibility.Hidden;
    }

    /// <summary>Physical-pixel rect of the side list for hit-testing; default when hidden.</summary>
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

    private Button BuildNoteRow(NoteSummary note)
    {
        var row = new Button
        {
            Tag = note,
            Height = 30,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            Padding = new Thickness(10, 0, 6, 0),
        };
        row.Click += (_, _) => NoteClicked?.Invoke(note.Id, RectOf(row));

        var label = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(note.TitlePreview) ? "(empty note)" : note.TitlePreview,
            Foreground = Application.Current?.Resources["TextPrimary"] as Brush ?? System.Windows.Media.Brushes.White,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        row.Content = label;
        return row;
    }

    private PxRect RectOf(FrameworkElement element)
    {
        Point p = element.PointToScreen(new Point(0, 0));
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        return new PxRect(
            (int)Math.Round(p.X),
            (int)Math.Round(p.Y),
            (int)Math.Round(element.RenderSize.Width * dpi),
            (int)Math.Round(element.RenderSize.Height * dpi));
    }

    protected override void OnClosed(EventArgs e)
    {
        FocusHelper.ForgetWindow(new WindowInteropHelper(this).Handle);
        base.OnClosed(e);
    }
}
