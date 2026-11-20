using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using StickyNotes.App.Core;
using StickyNotes.App.Interop;
using StickyNotes.App.State;

namespace StickyNotes.App.Windows;

public enum GroupListMode
{
    OpenGroup,
    OpenAllGroups,

    /// <summary>Every note (pinned included), sortable Date / A→Z — the "old notes are gone" escape hatch.</summary>
    OpenAllNotes,
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

    // v0.2.2+ (F5): hovering a GROUP ROW for ~180ms raises GroupRowHover with DwellReady=true; the
    // coordinator shows that group's notes in a side list to the RIGHT of this dropdown. The dwell clock
    // lives HERE (a 50ms poll over the hovered row) because a pointer that simply SITS on a row raises no
    // further MouseEnter events, and the coordinator's tick cannot be assumed to run (the bar is hidden
    // until a trigger dwell completes; tests may show windows without starting it).
    private readonly HoverDwellTracker _rowDwell = new();
    private readonly DispatcherTimer _rowDwellTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private long _hoveredGroupId;
    private PxRect _hoveredRowRect;

    private void BeginRowHover(long groupId, PxRect rowRect)
    {
        if (groupId != _hoveredGroupId)
        {
            _hoveredGroupId = groupId;
            _rowDwell.Reset();
        }

        _hoveredRowRect = rowRect;
        _rowDwell.Poll(true, Environment.TickCount64); // seed the dwell clock NOW — a pointer that then just SITS raises no further events
        _rowDwellTimer.Start();
        GroupRowHover?.Invoke(this, new GroupListRowHoverEventArgs(groupId, inside: true, rowRect));
    }

    private void PollRowDwell()
    {
        if (!IsLoaded || _hoveredGroupId == 0)
        {
            _rowDwellTimer.Stop();
            return;
        }

        if (_rowDwell.Poll(true, Environment.TickCount64) == HoverDwellTracker.HoverStage.Ready)
        {
            _rowDwell.Reset(); // one DwellReady per uninterrupted dwell
            _rowDwellTimer.Stop(); // the dwell is DONE — a stationary pointer must not re-fire it every ~180ms
            GroupRowHover?.Invoke(this, new GroupListRowHoverEventArgs(_hoveredGroupId, inside: true, _hoveredRowRect, dwellReady: true));
        }
    }

    private void EndRowHover(long groupId)
    {
        if (groupId == _hoveredGroupId)
        {
            GroupRowHover?.Invoke(this, new GroupListRowHoverEventArgs(groupId, inside: false, default));
            _hoveredGroupId = 0;
        }

        _rowDwell.Reset();
        _rowDwellTimer.Stop();
    }

    /// <summary>Drop all row-hover state so a hidden/re-rendered list never fires a stale dwell.</summary>
    private void ResetRowHover()
    {
        _rowDwellTimer.Stop();
        _rowDwell.Reset();
        _hoveredGroupId = 0;
    }

    private GroupListMode _mode = GroupListMode.OpenAllGroups;
    private GroupSummary? _group;
    private InlineEditPurpose _editPurpose = InlineEditPurpose.None;
    private bool _editing;

    /// <summary>OpenAllNotes: the order currently rendered, and the one a sort toggle flips from.</summary>
    private NoteSort _sort = NoteSort.Recent;

    /// <summary>The '+ New Note' permanent row (OpenGroup) was clicked. Rect = screen rect of the row (note window anchor).</summary>
    public event Action<long, PxRect>? NewNoteRequested;

    /// <summary>A note row was clicked (Task 7 opens NoteWindow). Rect = screen rect of the row.</summary>
    public event Action<long, PxRect>? NoteClicked;

    /// <summary>A group row in OpenAllGroups was clicked — switch to that group's list.</summary>
    public event Action<long>? GroupRowActivated;

    /// <summary>F6: right-click on a note/group row — coordinator builds the context menu.</summary>
    public event EventHandler<GroupListContextMenuEventArgs>? ContextMenuRequested;

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

    /// <summary>F6: the header close button was pressed — coordinator clears the anchor and hides the list.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>OpenAllNotes: the user flipped Date ↔ A→Z. The coordinator re-queries and re-renders.</summary>
    public event Action<NoteSort>? SortToggleRequested;

    public GroupListWindow()
    {
        InitializeComponent();
        Visibility = Visibility.Hidden;
        _rowDwellTimer.Tick += (_, _) => PollRowDwell();
        ShowActivated = false;
        Deactivated += OnDeactivated;
    }

    public GroupListMode Mode => _mode;

    /// <summary>Group currently shown in OpenGroup mode (null in OpenAllGroups).</summary>
    public long? CurrentGroupId => _group?.Id;

    /// <summary>F4: resolve a note body for the read-only hover preview (must use markOpened:false).</summary>
    public Func<long, string?>? PreviewResolver { get; set; }

    // F4: one non-activating ToolTip per note row, closed when the list re-renders or hides.
    private readonly List<(Button Row, TextBlock Text)> _tips = new();

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
        Left = rect.X / dpi;
        Top = rect.Y / dpi;
        Visibility = Visibility.Visible;
        _ = Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, rect.X, rect.Y, rect.Width, rect.Height,
            Win32.SWP_NOACTIVATE);
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

    /// <summary>OpenAllNotes: every note, pinned included, in <paramref name="sort"/> order.</summary>
    public void ShowAllNotes(IReadOnlyList<NoteSummary> notes, NoteSort sort)
    {
        _mode = GroupListMode.OpenAllNotes;
        RenderAllNotesMode(notes, sort);
    }

    /// <summary>Re-render after data changed under us (list stays open).</summary>
    public void RefreshGroup(GroupSummary group, IReadOnlyList<NoteSummary> notes) => RenderGroupMode(group, notes);

    public void RefreshAllGroups(IReadOnlyList<GroupSummary> allGroups) => RenderAllGroupsMode(allGroups);

    public void RefreshAllNotes(IReadOnlyList<NoteSummary> notes, NoteSort sort) => RenderAllNotesMode(notes, sort);

    /// <summary>v0.2.2+: the pointer entered/left a GROUP ROW in OpenAllGroups mode, or a row's 180ms dwell completed. Inside=false on leave; the row rect is physical px (valid while inside). DwellReady=true fires ONCE per uninterrupted dwell from the window's own 50ms poll (the coordinator tick cannot be relied on: a pointer that SITS on a row raises no further events).</summary>
    public event EventHandler<GroupListRowHoverEventArgs>? GroupRowHover;

    /// <summary>Hide (never close — the window is reused). Commits an in-flight inline edit first.</summary>
    public void CloseList()
    {
        ResetRowHover(); // a hidden list must not fire a stale DwellReady at the coordinator

        if (_editing)
        {
            FinishInlineEdit(commit: !string.IsNullOrWhiteSpace(EditBox.Text), restoreForeground: false);
        }

        CloseTips();
        Visibility = Visibility.Hidden;
    }

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
        SortButton.Visibility = Visibility.Collapsed;
        PinToggleButton.Visibility = Visibility.Visible;
        RenameButton.Visibility = Visibility.Visible;
        DeleteButton.Visibility = Visibility.Visible;
        PinToggleButton.Opacity = group.IsPinned ? 1.0 : 0.35;
        ToolTipService.SetToolTip(PinToggleButton, group.IsPinned ? "Unpin group" : "Pin group");
        NewNoteButton.Visibility = Visibility.Visible;

        CloseTips();
        ResetRowHover();
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
        SortButton.Visibility = Visibility.Collapsed;
        PinToggleButton.Visibility = Visibility.Collapsed;
        RenameButton.Visibility = Visibility.Collapsed;
        DeleteButton.Visibility = Visibility.Collapsed;
        NewNoteButton.Visibility = Visibility.Collapsed;

        CloseTips();
        ResetRowHover();
        RowsPanel.Children.Clear();
        foreach (GroupSummary g in allGroups.OrderBy(g => g.CreatedAtMs))
        {
            RowsPanel.Children.Add(BuildGroupRow(g));
        }
    }

    /// <summary>
    /// OpenAllNotes: one flat row per note. Rows are reused verbatim from OpenGroup, so the hover
    /// preview, the right-click context menu and the click-to-open wiring all behave identically.
    /// </summary>
    private void RenderAllNotesMode(IReadOnlyList<NoteSummary> notes, NoteSort sort)
    {
        _group = null;
        _sort = sort;

        HeaderPanel.Visibility = Visibility.Visible;
        ConfirmPanel.Visibility = Visibility.Collapsed;
        HeaderTitle.Text = "All notes";
        NewGroupButton.Visibility = Visibility.Collapsed;
        SortButton.Visibility = Visibility.Visible;
        SortButton.Content = sort == NoteSort.Alphabetical ? "A→Z" : "Date ↓";
        ToolTipService.SetToolTip(
            SortButton,
            sort == NoteSort.Alphabetical
                ? "Sorted A→Z — click for newest first"
                : "Sorted newest first — click for A→Z");
        PinToggleButton.Visibility = Visibility.Collapsed;
        RenameButton.Visibility = Visibility.Collapsed;
        DeleteButton.Visibility = Visibility.Collapsed;
        NewNoteButton.Visibility = Visibility.Collapsed;

        CloseTips();
        ResetRowHover();
        RowsPanel.Children.Clear();
        foreach (NoteSummary note in notes)
        {
            RowsPanel.Children.Add(BuildNoteRow(note));
        }
    }

    /// <summary>OpenAllNotes: flip the order and let the coordinator re-query and re-render.</summary>
    private void OnSortClick(object sender, RoutedEventArgs e)
    {
        _sort = _sort == NoteSort.Alphabetical ? NoteSort.Recent : NoteSort.Alphabetical;
        SortToggleRequested?.Invoke(_sort);
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
        row.Click += (_, _) =>
        {
            CloseTips();
            NoteClicked?.Invoke(note.Id, RectOf(row));
        };
        row.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            CloseTips();
            ContextMenuRequested?.Invoke(this, new GroupListContextMenuEventArgs(ContextMenuTargetKind.Note, note.Id, row, RectOf(row)));
        };

        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (note.IsPinned)
        {
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }

        var text = new TextBlock
        {
            Text = note.DisplayTitle,
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
        AttachNotePreview(row, note.Id);
        return row;
    }

    /// <summary>
    /// F4: read-only hover preview for a note row. Fetched on ToolTipOpening with
    /// markOpened:false, so hovering never reorders recents or marks the note opened.
    /// </summary>
    private void AttachNotePreview(Button row, long noteId)
    {
        // WPF forbids attaching a ToolTip with StaysOpen=false to an element, so the bubble is
        // shown as ToolTipService content; its default lifetime already dismisses on mouse-away.
        TextBlock text = NotePreview.CreateContent();
        ToolTipService.SetToolTip(row, text);
        ToolTipService.SetPlacement(row, PlacementMode.Bottom);
        ToolTipService.SetVerticalOffset(row, NotePreview.VerticalOffset);
        ToolTipService.SetInitialShowDelay(row, NotePreview.InitialShowDelayMs);
        _tips.Add((row, text));

        // A manual CloseTips() clears the tooltip; re-arm it on the next hover.
        row.MouseEnter += (_, _) => ToolTipService.SetToolTip(row, text);

        row.ToolTipOpening += (_, e) =>
        {
            string? body;
            try
            {
                body = PreviewResolver?.Invoke(noteId);
            }
            catch (StickyCoreException)
            {
                body = null;
            }
            catch (ObjectDisposedException)
            {
                body = null; // shutdown raced a hover
            }

            if (body is null)
            {
                e.Handled = true;
                return;
            }

            text.Text = NotePreview.Format(body);
        };
    }

    private void CloseTips()
    {
        foreach ((Button row, _) in _tips)
        {
            ToolTipService.SetToolTip(row, null); // re-armed by the row's MouseEnter handler
        }

        _tips.Clear();
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
        row.MouseEnter += (_, _) => BeginRowHover(group.Id, RectOf(row));
        row.MouseLeave += (_, _) => EndRowHover(group.Id);
        row.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            ContextMenuRequested?.Invoke(this, new GroupListContextMenuEventArgs(ContextMenuTargetKind.Group, group.Id, row, RectOf(row)));
        };

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

    private void OnCloseButtonClick(object sender, RoutedEventArgs e)
    {
        if (_editing)
        {
            FinishInlineEdit(commit: true, restoreForeground: false);
        }

        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnRenameClick(object sender, RoutedEventArgs e) => BeginRename();

    /// <summary>F6: start renaming the open group (coordinator switches to OpenGroup first).</summary>
    public void BeginRename()
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

    private void OnDeleteClick(object sender, RoutedEventArgs e) => BeginDelete();

    /// <summary>F6: show the inline delete confirmation for the open group.</summary>
    public void BeginDelete()
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
            _group = null; // group is being deleted — never keep a stale reference (double-delete guard)
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
        ResetRowHover(); // never expand a list that is already gone
        FocusHelper.ForgetWindow(new WindowInteropHelper(this).Handle);
        base.OnClosed(e);
    }
}

/// <summary>F6: which row was right-clicked.</summary>
public enum ContextMenuTargetKind
{
    Note,
    Group,
}

/// <summary>F6: right-click on a group-list row — coordinator builds and opens the context menu.</summary>
    public sealed class GroupListRowHoverEventArgs : EventArgs
    {
        public GroupListRowHoverEventArgs(long groupId, bool inside, PxRect rowRect, bool dwellReady = false)
        {
            GroupId = groupId;
            Inside = inside;
            RowRect = rowRect;
            DwellReady = dwellReady;
        }

        public long GroupId { get; }

        /// <summary>True while the pointer is over the row; false on leave.</summary>
        public bool Inside { get; }

        /// <summary>Physical-pixel rect of the row (valid while <see cref="Inside"/>).</summary>
        public PxRect RowRect { get; }

        /// <summary>True on the single event that fires when an uninterrupted ~180ms dwell completes.</summary>
        public bool DwellReady { get; }
    }
public sealed class GroupListContextMenuEventArgs : EventArgs
{
    public GroupListContextMenuEventArgs(ContextMenuTargetKind kind, long id, Button target, PxRect anchor)
    {
        Kind = kind;
        Id = id;
        Target = target;
        Anchor = anchor;
    }

    public ContextMenuTargetKind Kind { get; }

    public long Id { get; }

    /// <summary>The row to anchor the ContextMenu to.</summary>
    public Button Target { get; }

    /// <summary>Screen rect of the row (used as the note-window anchor).</summary>
    public PxRect Anchor { get; }
}
