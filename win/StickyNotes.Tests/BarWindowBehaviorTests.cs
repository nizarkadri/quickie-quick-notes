// F6 gate - BarWindow runtime behaviour: the chip rebuild, chip hit-testing and the
// right-click context-menu request. Private members are reached via reflection; nothing here
// edits or relaxes production code.
using System.Collections;
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
public class BarWindowBehaviorTests
{
    private static readonly MonitorBounds Bounds = new(new PxRect(0, 0, 1920, 1080), new PxRect(0, 0, 1920, 1040));

    private static NoteSummary Note(long id, bool pinned = false) =>
        new() { Id = id, TitlePreview = "n" + id, IsPinned = pinned, LastOpenedAtMs = 1000 + id, PinnedAtMs = pinned ? 500 + id : null };

    private static GroupSummary Group(long id) =>
        new() { Id = id, Name = "g" + id, NoteCount = 0, IsPinned = true, CreatedAtMs = id };

    private static Dictionary<string, Button> Chips(BarWindow win) =>
        (Dictionary<string, Button>)win.GetType().GetField("_chips", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(win)!;

    private static void RightClick(UIElement element) =>
        element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
        {
            RoutedEvent = UIElement.MouseRightButtonUpEvent,
        });

    private static void LeftClick(Button button) =>
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));

    [Fact]
    public void Rebuild_Creates_PinnedRail_And_Recents_Chips()
    {
        WinWindowTestSupport.RunOnSta(() =>
        {
            var win = new BarWindow();
            try
            {
                var snapshot = new BarSnapshot
                {
                    PinnedItems = new List<PinnedItem> { PinnedItem.FromNote(Note(1, pinned: true)), PinnedItem.FromGroup(Group(9)) },
                    RecentNotes = new List<NoteSummary> { Note(2), Note(3, pinned: true), Note(4) },
                };

                win.Rebuild(snapshot);
                WinWindowTestSupport.Pump();

                var pinnedPanel = (StackPanel)WinWindowTestSupport.FindByName(win, "PinnedPanel");
                var recentPanel = (StackPanel)WinWindowTestSupport.FindByName(win, "RecentPanel");

                Assert.Equal(3, pinnedPanel.Children.Count); // pinned note + group + trailing 'Groups ▾'
                Assert.Equal(3, recentPanel.Children.Count); // two note chips (pinned recents are not duplicated) + the all-notes arrow
                var tags = Chips(win).Keys;
                Assert.Contains("note:1", tags);
                Assert.Contains("group:9", tags);
                Assert.Contains("groups-menu", tags);
                Assert.Contains("all-notes", tags); // the way back to notes the recents dropped
                Assert.Contains("note:2", tags);
                Assert.Contains("note:4", tags);
                Assert.DoesNotContain("note:3", tags); // pinned: lives in the rail only
            }
            finally
            {
                WinWindowTestSupport.HideAllWindows();
            }
        });
    }

    [Fact]
    public void TryGetChipRect_False_While_Hidden_True_After_ShowOnMonitor()
    {
        WinWindowTestSupport.RunOnSta(() =>
        {
            var win = new BarWindow();
            try
            {
                var snapshot = new BarSnapshot
                {
                    PinnedItems = new List<PinnedItem> { PinnedItem.FromNote(Note(1, pinned: true)) },
                    RecentNotes = new List<NoteSummary>(),
                };
                win.Rebuild(snapshot);

                Assert.False(win.TryGetChipRect("note:1", out _));

                win.ShowOnMonitor(Bounds, nint.Zero, 1.0);
                WinWindowTestSupport.Pump();

                Assert.True(win.TryGetChipRect("note:1", out PxRect rect));
                Assert.True(rect.Width > 0 && rect.Height > 0);
            }
            finally
            {
                WinWindowTestSupport.HideAllWindows();
            }
        });
    }

    [Fact]
    public void RightClick_On_A_Chip_Raises_ContextMenuRequested_With_Tag_Target_And_Anchor()
    {
        WinWindowTestSupport.RunOnSta(() =>
        {
            var win = new BarWindow();
            try
            {
                var snapshot = new BarSnapshot
                {
                    PinnedItems = new List<PinnedItem> { PinnedItem.FromGroup(Group(9)) },
                    RecentNotes = new List<NoteSummary> { Note(2) },
                };
                win.Rebuild(snapshot);
                win.ShowOnMonitor(Bounds, nint.Zero, 1.0);
                WinWindowTestSupport.Pump();

                BarContextMenuEventArgs? captured = null;
                win.ContextMenuRequested += (_, e) => captured = e;

                var chip = Chips(win)["note:2"];
                RightClick(chip);

                Assert.NotNull(captured);
                Assert.Equal("note:2", captured.Tag);
                Assert.Same(chip, captured.Target);
                Assert.True(captured.Anchor.Width > 0 && captured.Anchor.Height > 0);
                Assert.True(win.TryGetChipRect("note:2", out PxRect chipRect));
                Assert.Equal(chipRect, captured.Anchor);
            }
            finally
            {
                WinWindowTestSupport.HideAllWindows();
            }
        });
    }

    [Fact]
    public void RightClick_While_The_Bar_Is_Hidden_Raises_Nothing()
    {
        WinWindowTestSupport.RunOnSta(() =>
        {
            var win = new BarWindow();
            try
            {
                var snapshot = new BarSnapshot
                {
                    PinnedItems = new List<PinnedItem>(),
                    RecentNotes = new List<NoteSummary> { Note(2) },
                };
                win.Rebuild(snapshot);

                bool fired = false;
                win.ContextMenuRequested += (_, _) => fired = true;

                RightClick(Chips(win)["note:2"]);

                Assert.False(fired);
            }
            finally
            {
                WinWindowTestSupport.HideAllWindows();
            }
        });
    }

    [Fact]
    public void LeftClick_On_A_Chip_Raises_ChipActivated_Not_ContextMenuRequested()
    {
        WinWindowTestSupport.RunOnSta(() =>
        {
            var win = new BarWindow();
            try
            {
                var snapshot = new BarSnapshot
                {
                    PinnedItems = new List<PinnedItem>(),
                    RecentNotes = new List<NoteSummary> { Note(2) },
                };
                win.Rebuild(snapshot);
                win.ShowOnMonitor(Bounds, nint.Zero, 1.0);
                WinWindowTestSupport.Pump();

                string? activated = null;
                bool contextMenu = false;
                win.ChipActivated += (_, e) => activated = e.Tag;
                win.ContextMenuRequested += (_, _) => contextMenu = true;

                LeftClick(Chips(win)["note:2"]);

                Assert.Equal("note:2", activated);
                Assert.False(contextMenu);
            }
            finally
            {
                WinWindowTestSupport.HideAllWindows();
            }
        });
    }
}
