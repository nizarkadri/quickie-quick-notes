// "All notes" dropdown gate — the bar recents show five, so a note that falls out of the rail and off
// the recents is unreachable unless it is grouped. This is the way back to it, sortable Date / A→Z.
// Real ShellCoordinator + real StickyCoreClient + real windows; private members are reached via
// reflection; nothing here edits production code.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using StickyNotes.App.Core;
using StickyNotes.App.Interop;
using StickyNotes.App.State;
using StickyNotes.App.Windows;
using Xunit;

namespace StickyNotes.Tests;

[Trait("Task", "v0.3")]
[Collection("WpfUi")]
public class AllNotesDropdownTests
{
    private static readonly MonitorBounds Bounds = new(new PxRect(0, 0, 1920, 1080), new PxRect(0, 0, 1920, 1040));

    private static object? PrivateField(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target);

    private static void InvokePrivate(object target, string name, params object?[] args) =>
        target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, args);

    /// <summary>The note titles in the order the rows render, top to bottom.</summary>
    private static List<string> RowTitles(GroupListWindow list)
    {
        var rows = (Panel)WinWindowTestSupport.FindByName(list, "RowsPanel");
        return rows.Children.OfType<Button>()
            .Select(row => ((Panel)row.Content).Children.OfType<TextBlock>().First().Text)
            .ToList();
    }

    private static Button SortButton(GroupListWindow list) =>
        (Button)WinWindowTestSupport.FindByName(list, "SortButton");

    private static void Click(Button button) =>
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));

    [Fact]
    public void The_Bar_Shows_An_All_Notes_Arrow()
    {
        WinWindowTestSupport.RunOnSta(() =>
        {
            using var t = new TempCore();
            t.Client.CreateNote(null, "one");

            using var coordinator = new ShellCoordinator(t.DbPath);
            coordinator.Start();
            try
            {
                var bar = (BarWindow)PrivateField(coordinator, "_bar")!;
                bar.ShowOnMonitor(Bounds, nint.Zero, WinWindowTestSupport.ActualDpi(bar));
                WinWindowTestSupport.Pump();

                var chips = (Dictionary<string, Button>)PrivateField(bar, "_chips")!;
                Assert.True(chips.ContainsKey("all-notes"), "the bar must carry an all-notes arrow");
            }
            finally
            {
                coordinator.Shutdown();
            }
        });
    }

    [Fact]
    public void Hovering_The_Arrow_Opens_The_List()
    {
        WinWindowTestSupport.RunOnSta(() =>
        {
            using var t = new TempCore();
            t.Client.CreateNote(null, "one");

            using var coordinator = new ShellCoordinator(t.DbPath);
            coordinator.Start();
            try
            {
                var bar = (BarWindow)PrivateField(coordinator, "_bar")!;
                bar.ShowOnMonitor(Bounds, nint.Zero, WinWindowTestSupport.ActualDpi(bar));
                WinWindowTestSupport.Pump();

                var chips = (Dictionary<string, Button>)PrivateField(bar, "_chips")!;
                Button arrow = chips["all-notes"];
                arrow.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, -1) { RoutedEvent = UIElement.MouseEnterEvent });

                // The dwell is 180ms on the bar's own 50ms timer; pump until it fires (bounded so a
                // regression fails the test instead of hanging the suite).
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (DateTime.UtcNow < deadline && PrivateField(coordinator, "_openListAnchorTag") is null)
                {
                    Thread.Sleep(25);
                    WinWindowTestSupport.Pump();
                }

                Assert.Equal("all-notes", PrivateField(coordinator, "_openListAnchorTag"));
                var list = (GroupListWindow)PrivateField(coordinator, "_list")!;
                Assert.Equal(GroupListMode.OpenAllNotes, list.Mode);
                Assert.Equal(Visibility.Visible, list.Visibility);
            }
            finally
            {
                coordinator.Shutdown();
            }
        });
    }

    [Fact]
    public void Opening_The_Arrow_Lists_Every_Note_Not_Just_The_Recents()
    {
        WinWindowTestSupport.RunOnSta(() =>
        {
            using var t = new TempCore();
            for (int i = 0; i < 8; i++)
            {
                t.Client.CreateNote(null, "note " + i);
                TempCore.Tick();
            }

            var pinned = t.Client.CreateNote(null, "pinned one");
            t.Client.SetNotePinned(pinned.Id, true); // already on the rail — must not be duplicated

            using var coordinator = new ShellCoordinator(t.DbPath);
            coordinator.Start();
            try
            {
                var bar = (BarWindow)PrivateField(coordinator, "_bar")!;
                bar.ShowOnMonitor(Bounds, nint.Zero, WinWindowTestSupport.ActualDpi(bar));
                WinWindowTestSupport.Pump();

                InvokePrivate(coordinator, "ToggleList", "all-notes", GroupListMode.OpenAllNotes, null);
                WinWindowTestSupport.Pump();

                var list = (GroupListWindow)PrivateField(coordinator, "_list")!;
                Assert.Equal(Visibility.Visible, list.Visibility);
                Assert.Equal(GroupListMode.OpenAllNotes, list.Mode);
                Assert.Equal(8, RowTitles(list).Count); // recents would have shown 5
                Assert.DoesNotContain("pinned one", RowTitles(list));
                Assert.Equal("All notes", ((TextBlock)WinWindowTestSupport.FindByName(list, "HeaderTitle")).Text);
            }
            finally
            {
                coordinator.Shutdown();
            }
        });
    }

    [Fact]
    public void Sort_Toggle_Flips_Between_Newest_First_And_AZ()
    {
        WinWindowTestSupport.RunOnSta(() =>
        {
            using var t = new TempCore();
            t.Client.CreateNote(null, "beta");
            TempCore.Tick();
            t.Client.CreateNote(null, "Alpha"); // capital A must not sort after lowercase
            TempCore.Tick();
            t.Client.CreateNote(null, "gamma"); // newest -> first under Date

            using var coordinator = new ShellCoordinator(t.DbPath);
            coordinator.Start();
            try
            {
                var bar = (BarWindow)PrivateField(coordinator, "_bar")!;
                bar.ShowOnMonitor(Bounds, nint.Zero, WinWindowTestSupport.ActualDpi(bar));
                WinWindowTestSupport.Pump();

                InvokePrivate(coordinator, "ToggleList", "all-notes", GroupListMode.OpenAllNotes, null);
                WinWindowTestSupport.Pump();

                var list = (GroupListWindow)PrivateField(coordinator, "_list")!;
                Button sort = SortButton(list);
                Assert.Equal("Date ↓", sort.Content);
                Assert.Equal(new[] { "gamma", "Alpha", "beta" }, RowTitles(list));

                Click(sort);
                WinWindowTestSupport.Pump();
                Assert.Equal("A→Z", sort.Content);
                Assert.Equal(new[] { "Alpha", "beta", "gamma" }, RowTitles(list));

                Click(sort);
                WinWindowTestSupport.Pump();
                Assert.Equal("Date ↓", sort.Content);
                Assert.Equal(new[] { "gamma", "Alpha", "beta" }, RowTitles(list));
            }
            finally
            {
                coordinator.Shutdown();
            }
        });
    }

    [Fact]
    public void Clicking_A_Row_Opens_That_Note()
    {
        WinWindowTestSupport.RunOnSta(() =>
        {
            using var t = new TempCore();
            var wanted = t.Client.CreateNote(null, "find me");
            t.Client.CreateNote(null, "other");

            using var coordinator = new ShellCoordinator(t.DbPath);
            coordinator.Start();
            try
            {
                var bar = (BarWindow)PrivateField(coordinator, "_bar")!;
                bar.ShowOnMonitor(Bounds, nint.Zero, WinWindowTestSupport.ActualDpi(bar));
                WinWindowTestSupport.Pump();

                InvokePrivate(coordinator, "ToggleList", "all-notes", GroupListMode.OpenAllNotes, null);
                WinWindowTestSupport.Pump();

                var list = (GroupListWindow)PrivateField(coordinator, "_list")!;
                var rows = (Panel)WinWindowTestSupport.FindByName(list, "RowsPanel");
                Button target = rows.Children.OfType<Button>()
                    .First(b => ((Panel)b.Content).Children.OfType<TextBlock>().First().Text == "find me");

                Click(target);
                WinWindowTestSupport.Pump();

                var noteWindow = (NoteWindow?)PrivateField(coordinator, "_noteWindow");
                Assert.NotNull(noteWindow);
                Assert.Equal(wanted.Id, noteWindow!.NoteId);
            }
            finally
            {
                coordinator.Shutdown();
            }
        });
    }
}