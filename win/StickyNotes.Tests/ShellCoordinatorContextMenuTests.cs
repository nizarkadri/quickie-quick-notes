// F6 gate - ShellCoordinator wiring around the group list (responsible for the "list closed
// when I opened the note" bug class). Real ShellCoordinator, real StickyCoreClient, real
// monitors; private members are reached via reflection; nothing here edits production code.
using System.Reflection;
using System.Windows;
using StickyNotes.App.Core;
using StickyNotes.App.Interop;
using StickyNotes.App.State;
using StickyNotes.App.Windows;
using Xunit;

namespace StickyNotes.Tests;

[Trait("Task", "F6")]
[Collection("WpfUi")]
public class ShellCoordinatorContextMenuTests
{
    private static readonly MonitorBounds Bounds = new(new PxRect(0, 0, 1920, 1080), new PxRect(0, 0, 1920, 1040));

    private static object? PrivateField(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target);

    private static void InvokePrivate(object target, string name, params object?[] args) =>
        target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, args);

    [Fact]
    public void Opening_A_Note_From_The_List_Keeps_The_List_Visible()
    {
        WinWindowTestSupport.RunOnSta(() =>
        {
            using var t = new TempCore();
            var group = t.Client.CreateGroup("Work", pinned: true);
            var note = t.Client.CreateNote(group.Id, "body");

            using var coordinator = new ShellCoordinator(t.DbPath);
            coordinator.Start();
            try
            {
                var bar = (BarWindow)PrivateField(coordinator, "_bar")!;
                bar.ShowOnMonitor(Bounds, nint.Zero, WinWindowTestSupport.ActualDpi(bar));
                WinWindowTestSupport.Pump();

                // Open the group's list the way a 'group:{id}' chip click does.
                InvokePrivate(coordinator, "ToggleList", "group:" + group.Id, GroupListMode.OpenGroup, (long?)group.Id);
                WinWindowTestSupport.Pump();

                var list = (GroupListWindow)PrivateField(coordinator, "_list")!;
                Assert.Equal(Visibility.Visible, list.Visibility);

                // The bug: opening a note from the list must NOT hide the list.
                InvokePrivate(coordinator, "OnListNoteClicked", note.Id, new PxRect(500, 100, 200, 30));
                WinWindowTestSupport.Pump();

                Assert.Equal(Visibility.Visible, list.Visibility);
            }
            finally
            {
                coordinator.Shutdown();
            }
        });
    }

    [Fact]
    public void Closing_The_List_Clears_The_Anchor_Tag_So_The_Chip_Reopens_It()
    {
        WinWindowTestSupport.RunOnSta(() =>
        {
            using var t = new TempCore();
            var group = t.Client.CreateGroup("Work", pinned: true);
            t.Client.CreateNote(group.Id, "body");

            using var coordinator = new ShellCoordinator(t.DbPath);
            coordinator.Start();
            try
            {
                var bar = (BarWindow)PrivateField(coordinator, "_bar")!;
                bar.ShowOnMonitor(Bounds, nint.Zero, WinWindowTestSupport.ActualDpi(bar));
                WinWindowTestSupport.Pump();

                InvokePrivate(coordinator, "ToggleList", "group:" + group.Id, GroupListMode.OpenGroup, (long?)group.Id);
                WinWindowTestSupport.Pump();
                var list = (GroupListWindow)PrivateField(coordinator, "_list")!;
                Assert.Equal(Visibility.Visible, list.Visibility);
                Assert.Equal("group:" + group.Id, PrivateField(coordinator, "_openListAnchorTag"));

                // Press the list's close button — coordinator hides the list and drops the anchor.
                var close = (System.Windows.Controls.Button)WinWindowTestSupport.FindByName(list, "CloseButton");
                close.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, close));
                WinWindowTestSupport.Pump();

                Assert.Equal(Visibility.Hidden, list.Visibility);
                Assert.Null(PrivateField(coordinator, "_openListAnchorTag"));

                // Clicking the chip again re-opens the list instead of toggling it shut.
                InvokePrivate(coordinator, "ToggleList", "group:" + group.Id, GroupListMode.OpenGroup, (long?)group.Id);
                WinWindowTestSupport.Pump();
                Assert.Equal(Visibility.Visible, list.Visibility);
            }
            finally
            {
                coordinator.Shutdown();
            }
        });
    }
}
