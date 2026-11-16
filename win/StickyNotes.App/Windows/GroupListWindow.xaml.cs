using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using StickyNotes.App.Core;
using StickyNotes.App.Interop;

namespace StickyNotes.App.Windows;

public enum GroupListMode
{
    OpenGroup,
    OpenAllGroups,
}

/// <summary>
/// Vertical group dropdown (blueprint §7/§8.2): `+ New Note` + note rows (OpenGroup) or all-groups
/// rows + `+ New Group` (OpenAllGroups). Permanently non-activating tool window EXCEPT during an
/// inline edit (BeginInlineEdit/EndInlineEdit) for Rename / New Group. Delete confirmation is an
/// inline header swap — never a modal dialog.
/// </summary>
public partial class GroupListWindow : Window
{
    private enum InlineEditPurpose
    {
        None,
        Rename,
        NewGroup,
    }

    private readonly ScreenPositioner _positioner = new();

    private GroupListMode _mode = GroupListMode.OpenAllGroups;
    private GroupSummary? _group;
    private InlineEditPurpose _editPurpose = InlineEditPurpose.None;
    private bool _editing;

    /// <summary>The '+ New Note' permanent row (OpenGroup) was clicked. Rect = screen rect of the row (note window anchor).</summary>
    public event Action<long, PxRect>? NewNoteRequested;

    /// <summary>A note row was clicked (Task 7 opens NoteWindow). Rect = screen rect of the row.</summary>
    public event Action<long, PxRect>? NoteClicked;

    /// <summary>A group row in OpenAllGroups was clicked — switch to that group's list.</summary>
    public event Action<long>? GroupRowActivated;

    /// <summary>Pin toggle pressed (OpenGroup header pin or a row pin). Coordinator flips the current state via the core.</summary>
    public event Action<long>? GroupPinToggled;

    /// <summary>Inline New Group edit committed with a non-empty name.</summary>
    public event Action<string>? GroupCreateCommitted;

    /// <summary>Inline Rename edit committed with a non-empty name.</summary>
    public event Action<long, string>? GroupRenameCommitted;

    /// <summary>Inline delete confirmation accepted — delete group + member notes.</summary>
    public event Action<long>? GroupDeleteConfirmed;

    /// <summary>An inline edit (Rename / New Group) began — coordinator enters the focus lock.</summary>
    public event EventHandler? InlineEditStarted;

    /// <summary>An inline edit ended — coordinator releases the focus lock.</summary>
    public event EventHandler? InlineEditEnded;

    public GroupListWindow()
    {
        InitializeComponent();
        Visibility = Visibility.Hidden;
        ShowActivated = false;
        Deactivated += OnDeactivated;
    }

    public GroupListMode Mode => _mode;

    /// <summary>Group currently shown in OpenGroup mode (null in OpenAllGroups).</summary>
    public long? CurrentGroupId => _group?.Id;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        nint hwnd = new WindowInteropHelper(this).Handle;
        FocusHelper.ApplyNonActivatingToolWindowStyles(hwnd);
        FocusHelper.InstallNonActivatingHook(hwnd);
        FocusHelper.ApplyRoundedCorners(hwnd);
    }

    // ------------------------------------------------------------------ show / refresh

    /// <summary>Position below <paramref name="anchorRect"/> (physical px) on <paramref name="bounds"/> and show without activating.</summary>
    public void PositionAndShow(PxRect anchorRect, MonitorBounds bounds, double dpi)
    {
        nint hwnd = new WindowInteropHelper(this).Handle;
        var size = new PxSize((int)Math.Round(260 * dpi), (int)Math.Round(420 * dpi));
        PxRect rect = _positioner.GetGroupListRect(anchorRect, size, bounds);
        Visibility = Visibility.Visible;
        _ = Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, rect.X, rect.Y, rect.Width, rect.Height,
            Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
    }

    public void ShowGroup(GroupSummary group, IReadOnlyList<NoteSummary> notes)
    {
        _mode = GroupListMode.OpenGroup;
        RenderGroupMode(group, notes);
    }

    public void ShowAllGroups(IReadOnlyList<GroupSummary> allGroups)
    {
        _mode = GroupListMode.OpenAllGroups;
        RenderAllGroupsMode(allGroups);
    }

    /// <summary>Re-render after data changed under us (list stays open).</summary>
    public void RefreshGroup(GroupSummary group, IReadOnlyList<NoteSummary> notes) => RenderGroupMode(group, notes);

    public void RefreshAllGroups(IReadOnlyList<GroupSummary> allGroups) => RenderAllGroupsMode(allGroups);

    /// <summary>Hide (never close — the window is reused).</summary>
    public void CloseList() => Visibility = Visibility.Hidden;

    /// <summary>Physical pixel rect for hit-testing; default when hidden.</summary>
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

    // ------------------------------------------------------------------ render

    private void RenderGroupMode(GroupSummary group, IReadOnlyList<NoteSummary> notes)
    {
        _group = group;

        HeaderPanel.Visibility = Visibility.Visible;
        ConfirmPanel.Visibility = Visibility.Collapsed;
        HeaderTitle.Text = group.Name;
        NewGroupButton.Visibility = Visibility.Collapsed;
        PinToggleButton.Visibility = Visibility.Visible;
        RenameButton.Visibility = Visibility.Visible;
        DeleteButton.Visibility = Visibility.Visible;
        PinToggleButton.Opacity = group.IsPinned ? 1.0 : 0.35;
        ToolTipService.SetToolTip(PinToggleButton, group.IsPinned ? "Unpin group" : "Pin group");
        NewNoteButton.Visibility = Visibility.Visible;

        RowsPanel.Children.Clear();
        foreach (NoteSummary note in notes.OrderByDescending(n => n.LastOpenedAtMs))
        {
            RowsPanel.Children.Add(BuildNoteRow(note));
        }
    }

    private void RenderAllGroupsMode(IReadOnlyList<GroupSummary> allGroups)
    {
        _group = null;

        HeaderPanel.Visibility = Visibility.Visible;
        ConfirmPanel.Visibility = Visibility.Collapsed;
        HeaderTitle.Text = "Groups";
        NewGroupButton.Visibility = Visibility.Visible;
        PinToggleButton.Visibility = Visibility.Collapsed;
        RenameButton.Visibility = Visibility.Collapsed;
        DeleteButton.Visibility = Visibility.Collapsed;
        NewNoteButton.Visibility = Visibility.Collapsed;

        RowsPanel.Children.Clear();
        foreach (GroupSummary g in allGroups.OrderBy(g => g.CreatedAtMs))
        {
            RowsPanel.Children.Add(BuildGroupRow(g));
        }
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
            Cursor = Cursors.Hand,
            Padding = new Thickness(10, 0, 10, 0),
        };
        row.Click += (_, _) => NoteClicked?.Invoke(note.Id, RectOf(row));

        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (note.IsPinned)
        {
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }

        var text = new TextBlock
        {
            Text = note.TitlePreview,
            Foreground = (Brush)Application.Current.Resources["TextPrimary"],
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(text, 1);
        content.Children.Add(text);

        if (note.IsPinned)
        {
            var pin = new TextBlock { Text = "📌", FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            Grid.SetColumn(pin, 2);
            content.Children.Add(pin);
        }

        row.Content = content;
        return row;
    }

    private Button BuildGroupRow(GroupSummary group)
    {
        var row = new Button
        {
            Tag = group,
            Height = 30,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Padding = new Thickness(10, 0, 6, 0),
        };
        row.Click += (_, _) => GroupRowActivated?.Invoke(group.Id);

        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock
        {
            Text = string.Format(CultureInfo.InvariantCulture, "{0}  ({1})", group.Name, group.NoteCount),
            Foreground = (Brush)Application.Current.Resources["TextPrimary"],
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(label, 0);
        content.Children.Add(label);

        var pin = new Button
        {
            Content = "📌",
            FontSize = 10,
            Width = 24,
            Height = 24,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Opacity = group.IsPinned ? 1.0 : 0.35,
            ToolTip = group.IsPinned ? "Unpin group" : "Pin group",
            Cursor = Cursors.Hand,
        };
        pin.Click += (_, e) =>
        {
            e.Handled = true; // don't activate the row
            GroupPinToggled?.Invoke(group.Id);
        };
        Grid.SetColumn(pin, 2);
        content.Children.Add(pin);

        row.Content = content;
        return row;
    }

    // ------------------------------------------------------------------ inline edit (rename / new group)

    private void OnRenameClick(object sender, RoutedEventArgs e)
    {
        if (_mode != GroupListMode.OpenGroup || _group is null)
        {
            return;
        }

        BeginInlineEdit(InlineEditPurpose.Rename, _group.Name);
    }

    private void OnNewGroupClick(object sender, RoutedEventArgs e)
    {
        if (_mode != GroupListMode.OpenAllGroups)
        {
            return;
        }

        BeginInlineEdit(InlineEditPurpose.NewGroup, string.Empty);
    }

    private void BeginInlineEdit(InlineEditPurpose purpose, string initialText)
    {
        _editPurpose = purpose;
        _editing = true;
        EditHost.Visibility = Visibility.Visible;
        EditBox.Text = initialText;
        EditBox.SelectAll();

        // Temporarily activate this window so the TextBox can take keyboard input.
        FocusHelper.BeginInlineEdit(this, EditBox);
        InlineEditStarted?.Invoke(this, EventArgs.Empty);
    }

    private void OnEditBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            FinishInlineEdit(commit: true, restoreForeground: true);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            FinishInlineEdit(commit: false, restoreForeground: true);
            e.Handled = true;
        }
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (_editing)
        {
            // Deactivated: commit only if the edit is non-empty; never steal focus back.
            FinishInlineEdit(commit: !string.IsNullOrWhiteSpace(EditBox.Text), restoreForeground: false);
        }
    }

    private void FinishInlineEdit(bool commit, bool restoreForeground)
    {
        if (!_editing)
        {
            return;
        }

        string text = EditBox.Text?.Trim() ?? string.Empty;
        InlineEditPurpose purpose = _editPurpose;
        long? renamingGroupId = _group?.Id;

        _editing = false;
        _editPurpose = InlineEditPurpose.None;

        EditHost.Visibility = Visibility.Collapsed;
        FocusHelper.EndInlineEdit(this, EditBox, restorePreviousForeground: restoreForeground);

        if (commit && text.Length > 0)
        {
            if (purpose == InlineEditPurpose.Rename && renamingGroupId.HasValue)
            {
                GroupRenameCommitted?.Invoke(renamingGroupId.Value, text);
            }
            else if (purpose == InlineEditPurpose.NewGroup)
            {
                GroupCreateCommitted?.Invoke(text);
            }
        }

        InlineEditEnded?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------ inline delete confirm

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_mode != GroupListMode.OpenGroup || _group is null)
        {
            return;
        }

        string noun = _group.NoteCount == 1 ? "note" : "notes";
        ConfirmText.Text = $"Delete '{_group.Name}' and {_group.NoteCount} {noun}?";
        HeaderPanel.Visibility = Visibility.Collapsed;
        ConfirmPanel.Visibility = Visibility.Visible;
    }

    private void OnConfirmDeleteClick(object sender, RoutedEventArgs e)
    {
        long? id = _group?.Id;
        ConfirmPanel.Visibility = Visibility.Collapsed;
        HeaderPanel.Visibility = Visibility.Visible;
        if (id.HasValue)
        {
            GroupDeleteConfirmed?.Invoke(id.Value);
        }
    }

    private void OnConfirmCancelClick(object sender, RoutedEventArgs e)
    {
        ConfirmPanel.Visibility = Visibility.Collapsed;
        HeaderPanel.Visibility = Visibility.Visible;
    }

    // ------------------------------------------------------------------ other actions

    private void OnPinToggleClick(object sender, RoutedEventArgs e)
    {
        if (_group is not null)
        {
            GroupPinToggled?.Invoke(_group.Id);
        }
    }

    private void OnNewNoteClick(object sender, RoutedEventArgs e)
    {
        if (_group is not null)
        {
            NewNoteRequested?.Invoke(_group.Id, RectOf(NewNoteButton));
        }
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
