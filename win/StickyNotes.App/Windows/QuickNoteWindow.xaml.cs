using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using StickyNotes.App.Core;
using StickyNotes.App.Interop;
using StickyNotes.App.State;

namespace StickyNotes.App.Windows;

/// <summary>
/// Quick capture overlay (blueprint §7/§8.4, contract pinned by QuickNoteSessionTests). Activated
/// on open (FocusHelper.ForceActivateAndFocus — no NOACTIVATE), no Save button: Ctrl+Enter or
/// click-outside commits if the text is non-whitespace; Esc ALWAYS discards without writing.
/// Previous foreground is restored on Ctrl+Enter and Esc (never on click-outside).
/// </summary>
public partial class QuickNoteWindow : Window
{
    private readonly IStickyCoreClient _core;
    private readonly QuickNoteSession _session;
    private bool _closed;
    private bool _suppressDeactivate; // true while a target-picker ContextMenu owns the screen
    private bool _menuPicked;         // true when a menu item was clicked (re-focus instead of committing closed)

    private BarSnapshot? _targetSnapshot;
    private IReadOnlyList<NoteSummary>? _appendTargets;

    public QuickNoteWindow(IStickyCoreClient core)
    {
        ArgumentNullException.ThrowIfNull(core);
        _core = core;
        _session = new QuickNoteSession(core);

        InitializeComponent();
        Deactivated += OnDeactivated;
    }

    /// <summary>Raised when the window closes itself. Parameter: close reason + outcome.</summary>
    public event EventHandler<QuickNoteClosedEventArgs>? QuickNoteClosed;

    public void ShowQuickNote(PxRect barRect, MonitorBounds bounds, double dpi)
    {
        nint hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == nint.Zero)
        {
            Show();
            hwnd = new WindowInteropHelper(this).Handle;
        }

        int wPhys = (int)Math.Round(380 * dpi);
        int hPhys = (int)Math.Round(220 * dpi);
        Width = 380;
        Height = 220;
        int x = barRect.X + (barRect.Width - wPhys) / 2; // centered below '+'
        int y = barRect.Bottom + (int)(6 * dpi);
        if (x + wPhys > bounds.WorkArea.Right) x = bounds.WorkArea.Right - wPhys;
        if (x < bounds.WorkArea.Left) x = bounds.WorkArea.Left;
        if (y + hPhys > bounds.WorkArea.Bottom) y = bounds.WorkArea.Bottom - hPhys;
        if (y < bounds.WorkArea.Top) y = bounds.WorkArea.Top;

        Visibility = Visibility.Visible;
        var helper = new WindowInteropHelper(this);
        _ = Win32.SetWindowPos(helper.Handle, Win32.HWND_TOPMOST, x, y, wPhys, hPhys, Win32.SWP_SHOWWINDOW);

        FocusHelper.ForceActivateAndFocus(this, helper.Handle, BodyBox);
        BodyBox.SelectAll();
    }

    /// <summary>Force position for persistent hit-testing consumers.</summary>
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

    /// <summary>Populate the target selector at open time. <paramref name="appendTargets"/> should be ListAppendTargets(30) (falls back to RecentNotes).</summary>
    public void LoadTargets(BarSnapshot snapshot, IReadOnlyList<NoteSummary>? appendTargets = null)
    {
        _targetSnapshot = snapshot;
        _appendTargets = appendTargets;
        UpdateButtonStates();
    }

    /// <summary>Re-focus the text box (the bar called us while already open).</summary>
    public void FocusBody()
    {
        FocusHelper.ForceActivateAndFocus(this, new WindowInteropHelper(this).Handle, BodyBox);
    }

    /// <summary>Programmatic close from the coordinator (bar hide / app exit) — commit-if-non-empty semantics.</summary>
    public void CloseProgrammatically(QuickNoteClose reason) => CloseInternal(reason);

    private void OnStandaloneClick(object sender, RoutedEventArgs e)
    {
        _session.Target = QuickCaptureTarget.Standalone();
        UpdateButtonStates();
    }

    private void OnGroupClick(object sender, RoutedEventArgs e)
    {
        if (_targetSnapshot is null)
        {
            return;
        }

        // A ContextMenu is its own activated popup HWND — it fires our Deactivated, which would
        // commit+close the editor. Suppress deactivation handling while the menu is open.
        var menu = new ContextMenu();
        foreach (GroupSummary g in _targetSnapshot.AllGroups) // already created_at_ms ASC
        {
            long id = g.Id;
            bool selected = _session.Target.Mode == QuickCaptureTarget.GroupMode && _session.Target.GroupId == id;
            var item = new MenuItem { Header = (selected ? "✓ " : "") + g.Name };
            item.Click += (_, _) =>
            {
                _menuPicked = true;
                _session.Target = QuickCaptureTarget.Group(id);
                UpdateButtonStates();
            };
            menu.Items.Add(item);
        }

        if (menu.Items.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "(no groups yet)", IsEnabled = false });
        }

        OpenMenu(menu, GroupButton);
    }

    private void OnAppendClick(object sender, RoutedEventArgs e)
    {
        if (_targetSnapshot is null)
        {
            return;
        }

        IReadOnlyList<NoteSummary> appendList = _appendTargets ?? _targetSnapshot.RecentNotes;

        var menu = new ContextMenu();
        foreach (NoteSummary n in appendList.Take(30))
        {
            long id = n.Id;
            bool selected = _session.Target.Mode == QuickCaptureTarget.AppendMode && _session.Target.NoteId == id;
            var item = new MenuItem { Header = (selected ? "✓ " : "") + n.TitlePreview };
            item.Click += (_, _) =>
            {
                _menuPicked = true;
                _session.Target = QuickCaptureTarget.Append(id);
                UpdateButtonStates();
            };
            menu.Items.Add(item);
        }

        if (menu.Items.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "(no appendable notes)", IsEnabled = false });
        }

        OpenMenu(menu, AppendButton);
    }

    private void OpenMenu(ContextMenu menu, UIElement target)
    {
        _suppressDeactivate = true;
        menu.Closed += (_, _) =>
        {
            _suppressDeactivate = false;
            if (IsActive)
            {
                _menuPicked = false;
                return;
            }

            if (_menuPicked)
            {
                // The user picked a target — keep editing, return focus to the text box.
                _menuPicked = false;
                FocusHelper.ForceActivateAndFocus(this, new WindowInteropHelper(this).Handle, BodyBox);
            }
            else
            {
                // The suppression ate the click-away: it's a click-outside commit.
                CloseInternal(QuickNoteClose.ClickOutside);
            }
        };
        menu.PlacementTarget = target;
        menu.IsOpen = true;
    }

    private void UpdateButtonStates()
    {
        StandaloneButton.Background = _session.Target.Mode == QuickCaptureTarget.StandaloneMode
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x60, 0x60, 0xA0, 0xE0))
            : System.Windows.Media.Brushes.Transparent;
        GroupButton.Background = _session.Target.Mode == QuickCaptureTarget.GroupMode
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x60, 0x60, 0xA0, 0xE0))
            : System.Windows.Media.Brushes.Transparent;
        AppendButton.Background = _session.Target.Mode == QuickCaptureTarget.AppendMode
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x60, 0x60, 0xA0, 0xE0))
            : System.Windows.Media.Brushes.Transparent;
    }

    private void OnBodyPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control && e.Key == Key.Enter)
        {
            CloseInternal(QuickNoteClose.CtrlEnter);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseInternal(QuickNoteClose.Escape);
            e.Handled = true;
        }
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (_suppressDeactivate)
        {
            return; // the user is picking from a target ContextMenu, not clicking outside
        }

        // Click-outside commits non-empty text (§8.4); empty text commits nothing.
        CloseInternal(QuickNoteClose.ClickOutside);
    }

    private void CloseInternal(QuickNoteClose reason)
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _session.Text = BodyBox.Text;

        QuickNoteOutcome outcome;
        try
        {
            outcome = _session.Close(reason); // quick capture can throw if the append target was cascade-deleted / group deleted
        }
        catch (StickyCoreException ex)
        {
            System.Diagnostics.Trace.WriteLine($"QuickNote commit failed ({ex.Status}): {ex.Message}");
            outcome = QuickNoteOutcome.NothingToCommit;
        }

        if (reason != QuickNoteClose.ClickOutside)
        {
            FocusHelper.RestorePreviousForeground(); // Ctrl+Enter / Esc: hand focus back
        }

        Visibility = Visibility.Hidden;
        QuickNoteClosed?.Invoke(this, new QuickNoteClosedEventArgs(reason, outcome));
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        FocusHelper.ForgetWindow(new WindowInteropHelper(this).Handle);
        base.OnClosed(e);
    }
}

public sealed class QuickNoteClosedEventArgs : EventArgs
{
    public QuickNoteClosedEventArgs(QuickNoteClose reason, QuickNoteOutcome outcome)
    {
        Reason = reason;
        Outcome = outcome;
    }

    public QuickNoteClose Reason { get; }
    public QuickNoteOutcome Outcome { get; }
}
