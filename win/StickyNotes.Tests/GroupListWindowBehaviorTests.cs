// F6 gate - GroupListWindow runtime behaviour: row context menus, the header close button,
// the rename/delete inline flows and placement. Private members are reached via reflection;
// nothing here edits or relaxes production code.
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using StickyNotes.App.Core;
using StickyNotes.App.Interop;
using StickyNotes.App.Windows;
using Xunit;

namespace StickyNotes.Tests;

[Trait("Task", "F6")]
[Collection("WpfUi")]
public class GroupListWindowBehaviorTests
{
    private static MonitorBounds Mon(int x, int y, int w, int h, int taskbar = 40) =>
        new(new PxRect(x, y, w, h), new PxRect(x, y, w, h - taskbar));

    private static PxRect RectOf(object list, FrameworkElement element)
    {
        MethodInfo method = list.GetType().GetMethod("RectOf", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("RectOf not found");
        return (PxRect)method.Invoke(list, new object[] { element })!;
    }

    private static StackPanel RowsOf(GroupListWindow win) => (StackPanel)WinWindowTestSupport.FindByName(win, "RowsPanel");

    private static void RightClick(UIElement element) =>
        element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
        {
            RoutedEvent = UIElement.MouseRightButtonUpEvent,
        });

    // ------------------------------------------------------------------ setup helpers

    private static (GroupSummary Group, IReadOnlyList<NoteSummary> Notes) MakeGroupWithNotes(TempCore t, int count)
    {
        var g = t.Client.CreateGroup("Work", pinned: true);
        for (int i = 0; i < count; i++)
        {
            t.Client.CreateNote(g.Id, $"note {i}");
            TempCore.Tick();
        }

        // Refresh the summary so NoteCount reflects the notes we just created (as the app does).
        var fresh = t.Client.GetBarSnapshot().AllGroups.Single(x => x.Id == g.Id);
        return (fresh, t.Client.ListGroupNotes(g.Id));
    }

    private static GroupListWindow ShowOpenGroup(TempCore t, int noteCount)
    {
        var win = new GroupListWindow();
        var (group, notes) = MakeGroupWithNotes(t, noteCount);
        win.ShowGroup(group, notes);
        win.PositionAndShow(new PxRect(500, 66, 120, 30), Mon(0, 0, 1920, 1080), WinWindowTestSupport.ActualDpi(win));
        WinWindowTestSupport.Pump();
        return win;
    }

    // ------------------------------------------------------------------ context menu on rows

    [Fact]
    public void RightClick_On_A_NoteRow_Raises_ContextMenuRequested_With_The_Row_Anchor()
    {
        WinWindowTestSupport.RunOnSta(() =>
        {
            using var t = new TempCore();
            var win = ShowOpenGroup(t, 2);
            var expectedNoteId = RowsOf(win).Children.Cast<Button>().Select(b => ((NoteSummary)b.Tag!).Id).ToList();
            GroupListContextMenuEventArgs? captured = null;
            bool leftClickFired = false;
            win.ContextMenuRequested += (_, e) => captured = e;
            win.NoteClicked += (_, _) => leftClickFired = true;
            try
            {
                var row = (Button)RowsOf(win).Children[0];
                RightClick(row);

                Assert.NotNull(captured);
                Assert.Equal(ContextMenuTargetKind.Note, captured.Kind);
                Assert.Equal(((NoteSummary)row.Tag!).Id, captured.Id);
                Assert.Same(row, captured.Target);
                Assert.Equal(RectOf(win, row), captured.Anchor);
                Assert.Contains(captured.Id, expectedNoteId);
                Assert.False(leftClickFired, "a right-click must not activate the row");
            }
            finally
            {
                WinWindowTestSupport.HideAllWindows();
            }
        });
    }

    [Fact]
    public void RightClick_On_A_GroupRow_Raises_ContextMenuRequested_With_Kind_Group()
    {
        WinWindowTestSupport.RunOnSta(() =>
        {
            using var t = new TempCore();
            var g = t.Client.CreateGroup("Alpha", pinned: true);
            var win = new GroupListWindow();
            try
            {
                win.ShowAllGroups(new[] { g });
                win.PositionAndShow(new PxRect(500, 66, 120, 30), Mon(0, 0, 1920, 1080), WinWindowTestSupport.ActualDpi(win));
                WinWindowTestSupport.Pump();

                GroupListContextMenuEventArgs? captured = null;
                bool rowActivated = false;
                win.ContextMenuRequested += (_, e) => captured = e;
                win.GroupRowActivated += _ => rowActivated = true;

                var row = Assert.Single(RowsOf(win).Children.Cast<Button>());
                RightClick(row);

                Assert.NotNull(captured);
                Assert.Equal(ContextMenuTargetKind.Group, captured.Kind);
                Assert.Equal(g.Id, captured.Id);
                Assert.Same(row, captured.Target);
                Assert.Equal(RectOf(win, row), captured.Anchor);
                Assert.False(rowActivated, "a right-click must not activate the row");
            }
            finally
            {
                WinWindowTestSupport.HideAllWindows();
            }
        });
    }

    // ------------------------------------------------------------------ close button

    [Fact]
    public void CloseButton_Raises_CloseRequested()
    {
        WinWindowTestSupport.RunOnSta(() =>
        {
            using var t = new TempCore();
            var win = ShowOpenGroup(t, 1);
            bool fired = false;
            win.CloseRequested += (_, _) => fired = true;
            try
            {
                var close = (Button)WinWindowTestSupport.FindByName(win, "CloseButton");
                close.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, close));

                Assert.True(fired);
            }
            finally
            {
                WinWindowTestSupport.HideAllWindows();
            }
        });
    }

    // ------------------------------------------------------------------ BeginRename / BeginDelete

    [Fact]
    public void BeginRename_And_BeginDelete_Are_No_Ops_In_OpenAllGroups_Mode()
    {
        WinWindowTestSupport.RunOnSta(() =>
        {
            var g = new GroupSummary { Id = 1, Name = "Alpha", NoteCount = 3, IsPinned = true, CreatedAtMs = 1 };
            var win = new GroupListWindow();
            try
            {
                win.ShowAllGroups(new[] { g });
                var editHost = (Border)WinWindowTestSupport.FindByName(win, "EditHost");
                var confirm = (Grid)WinWindowTestSupport.FindByName(win, "ConfirmPanel");

                win.BeginRename();
                win.BeginDelete();

                Assert.Equal(Visibility.Collapsed, editHost.Visibility);
                Assert.Equal(Visibility.Collapsed, confirm.Visibility);
            }
            finally
            {
                WinWindowTestSupport.HideAllWindows();
            }
        });
    }

    [Fact]
    public void BeginRename_Shows_Inline_Edit_With_The_Group_Name_Selected()
    {
        WinWindowTestSupport.RunOnSta(() =>
        {
            using var t = new TempCore();
            var win = ShowOpenGroup(t, 2);
            try
            {
                var editHost = (Border)WinWindowTestSupport.FindByName(win, "EditHost");
                var editBox = (TextBox)WinWindowTestSupport.FindByName(win, "EditBox");

                win.BeginRename();

                Assert.Equal(Visibility.Visible, editHost.Visibility);
                Assert.Equal("Work", editBox.Text);
            }
            finally
            {
                WinWindowTestSupport.HideAllWindows();
            }
        });
    }

    [Fact]
    public void BeginDelete_Shows_The_Inline_Confirmation_With_Plural_Count()
    {
        WinWindowTestSupport.RunOnSta(() =>
        {
            using var t = new TempCore();
            var win = ShowOpenGroup(t, 2);
            try
            {
                var header = (Grid)WinWindowTestSupport.FindByName(win, "HeaderPanel");
                var confirm = (Grid)WinWindowTestSupport.FindByName(win, "ConfirmPanel");
                var confirmText = (TextBlock)WinWindowTestSupport.FindByName(win, "ConfirmText");

                win.BeginDelete();

                Assert.Equal(Visibility.Collapsed, header.Visibility);
                Assert.Equal(Visibility.Visible, confirm.Visibility);
                Assert.Equal("Delete 'Work' and 2 notes?", confirmText.Text);
            }
            finally
            {
                WinWindowTestSupport.HideAllWindows();
            }
        });
    }

    [Fact]
    public void BeginDelete_Uses_Singular_Note_For_A_Single_Member_Group()
    {
        WinWindowTestSupport.RunOnSta(() =>
        {
            using var t = new TempCore();
            var win = ShowOpenGroup(t, 1);
            try
            {
                var confirmText = (TextBlock)WinWindowTestSupport.FindByName(win, "ConfirmText");
                win.BeginDelete();
                Assert.Equal("Delete 'Work' and 1 note?", confirmText.Text);
            }
            finally
            {
                WinWindowTestSupport.HideAllWindows();
            }
        });
    }

    // ------------------------------------------------------------------ placement + hit-test rect

    [Fact]
    public void PositionAndShow_Places_The_List_And_TryGetPhysicalRect_Matches_The_Left_Top()
    {
        WinWindowTestSupport.RunOnSta(() =>
        {
            var win = new GroupListWindow();
            try
            {
                var bounds = Mon(0, 0, 1920, 1080);
                double dpi = WinWindowTestSupport.ActualDpi(win);
                var anchor = new PxRect(600, 55, 120, 40);
                var size = new PxSize((int)Math.Round(260 * dpi), (int)Math.Round(420 * dpi));
                PxRect expected = new ScreenPositioner().GetGroupListRect(anchor, size, bounds);

                win.PositionAndShow(anchor, bounds, dpi);
                WinWindowTestSupport.Pump();

                Assert.InRange(win.Left, expected.X / dpi - 1, expected.X / dpi + 1);
                Assert.InRange(win.Top, expected.Y / dpi - 1, expected.Y / dpi + 1);
                Assert.Equal(260, win.Width);
                Assert.True(win.TryGetPhysicalRect(out PxRect rect));
                Assert.InRange(rect.X, expected.X - 2, expected.X + 2);
                Assert.InRange(rect.Y, expected.Y - 2, expected.Y + 2);
                Assert.InRange(rect.Width, expected.Width - 2, expected.Width + 2);
                Assert.True(rect.Height > 0);
            }
            finally
            {
                WinWindowTestSupport.HideAllWindows();
            }
        });
    }
}
