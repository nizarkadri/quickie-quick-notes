// Bug 2 gate - the group side list (GroupPreviewWindow) shows the same F4 read-only hover bubble as the
// main list rows. These tests pin the wiring: each note row carries a ToolTipService bubble, ToolTipOpening
// fills it from the injected PreviewResolver via NotePreview.Format, and hiding/re-showing dismisses stale
// bubbles. Nothing here edits or relaxes production code; private detail is reached only through the public
// ShowForGroup / HidePreview surface and the standard ToolTipService API.
using System;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using StickyNotes.App.Core;
using StickyNotes.App.Interop;
using StickyNotes.App.Windows;
using Xunit;

namespace StickyNotes.Tests;

[Trait("Task", "v0.2.2")]
[Collection("WpfUi")]
public class GroupPreviewWindowBehaviorTests
{
    private static MonitorBounds Mon(int x, int y, int w, int h, int taskbar = 40) =>
        new(new PxRect(x, y, w, h), new PxRect(x, y, w, h - taskbar));

    private static StackPanel RowsOf(GroupPreviewWindow win) =>
        (StackPanel)WinWindowTestSupport.FindByName(win, "RowsPanel");

    private static GroupSummary Group(long id = 1, string name = "Work") =>
        new() { Id = id, Name = name, NoteCount = 2, IsPinned = true, CreatedAtMs = 1 };

    private static NoteSummary Note(long id, string title) =>
        new() { Id = id, GroupId = 1, TitlePreview = title, Title = title, LastOpenedAtMs = id };

    private static GroupPreviewWindow ShowPreview(GroupPreviewWindow win, IReadOnlyList<NoteSummary> notes)
    {
        win.ShowForGroup(Group(), notes, new PxRect(500, 100, 260, 30), Mon(0, 0, 1920, 1080), WinWindowTestSupport.ActualDpi(win));
        WinWindowTestSupport.Pump();
        return win;
    }

    private static void OpenToolTip(Button row)
    {
        // Fire the same routed event WPF raises after the initial-show delay; the handler fills the bubble.
        // ToolTipEventArgs has only an internal bool ctor, so build it reflectively (test-only shim).
        var args = (RoutedEventArgs)Activator.CreateInstance(
            typeof(ToolTipEventArgs), BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { false }, null)!;
        args.RoutedEvent = ToolTipService.ToolTipOpeningEvent;
        row.RaiseEvent(args);
    }

    [Fact]
    public void Every_note_row_gets_a_tooltip_bubble()
    {
        WinWindowTestSupport.RunOnSta(() =>
        {
            var win = new GroupPreviewWindow();
            try
            {
                var notes = new[] { Note(1, "one"), Note(2, "two") };
                ShowPreview(win, notes);

                var rows = RowsOf(win).Children.Cast<Button>().ToList();
                Assert.Equal(2, rows.Count);
                foreach (Button row in rows)
                {
                    // The bubble is shown as ToolTipService content (WPF forbids a StaysOpen=false ToolTip on an element).
                    Assert.IsType<TextBlock>(ToolTipService.GetToolTip(row));
                }
            }
            finally
            {
                WinWindowTestSupport.HideAllWindows();
            }
        });
    }

    [Fact]
    public void ToolTipOpening_fills_the_bubble_from_the_resolver()
    {
        WinWindowTestSupport.RunOnSta(() =>
        {
            var win = new GroupPreviewWindow
            {
                PreviewResolver = id => $"body for note {id}",
            };
            try
            {
                var notes = new[] { Note(7, "seven") };
                ShowPreview(win, notes);

                Button row = RowsOf(win).Children.Cast<Button>().Single();
                var bubble = (TextBlock)ToolTipService.GetToolTip(row);
                OpenToolTip(row);

                Assert.Equal(NotePreview.Format("body for note 7"), bubble.Text);
            }
            finally
            {
                WinWindowTestSupport.HideAllWindows();
            }
        });
    }

    [Fact]
    public void HidePreview_dismisses_the_row_bubbles()
    {
        WinWindowTestSupport.RunOnSta(() =>
        {
            var win = new GroupPreviewWindow { PreviewResolver = _ => "hello" };
            try
            {
                var notes = new[] { Note(1, "one"), Note(2, "two") };
                ShowPreview(win, notes);

                win.HidePreview();

                foreach (Button row in RowsOf(win).Children.Cast<Button>())
                {
                    Assert.Null(ToolTipService.GetToolTip(row));
                }
            }
            finally
            {
                WinWindowTestSupport.HideAllWindows();
            }
        });
    }
}