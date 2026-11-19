// v0.2.2+ bug-fix gate (bug 3: QUICKNOTE '+'). Focus lands on the body box when the overlay opens;
// losing activation closes+commits it (empty → no write, non-empty → commit); the × button exists and
// discards+closes; and the Deactivated/Esc suppression that fires while WPF focus is still within the
// window must never strand it open. Real WPF Application on a dedicated STA thread + real
// StickyCoreClient (TempCore); focus-sensitive paths are asserted through Keyboard.FocusedElement,
// the QuickNoteClosed event and Visibility (a test host owns no foreground guarantee). Reviewer rule:
// every close-path test (Esc / × / deactivate) first focuses BodyBox + Pumps and asserts
// IsKeyboardFocusInside, so the suppression guard is exercised for real instead of passing vacuously
// with a null FocusedElement.
using System;
using System.Linq;
using System.Reflection;
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

[Trait("Task", "v0.2.2")]
[Collection("WpfUi")]
public class QuickNoteWindowBehaviorTests
{
    private static readonly MonitorBounds Bounds = new(new PxRect(0, 0, 1920, 1080), new PxRect(0, 0, 1920, 1040));
    private static readonly PxRect BarRegion = new(100, 100, 800, 44); // physical px; ShowQuickNote centers below it

    private static object? InvokePrivate(object target, string name, params object?[] args)
    {
        // Prefer the method THIS class declares (e.g. QuickNoteWindow.OnDeactivated), not the
        // same-named base Window one — a bare name lookup is ambiguous.
        MethodInfo[] candidates = target.GetType().GetMethods(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public | BindingFlags.FlattenHierarchy)
            .Where(m => m.Name == name)
            .ToArray();
        MethodInfo method = candidates.SingleOrDefault(m => m.DeclaringType == target.GetType())
            ?? candidates.SingleOrDefault()
            ?? throw new InvalidOperationException($"private method '{name}' not found on {target.GetType().Name}");
        return method.Invoke(target, args);
    }

    private static QuickNoteWindow Show(TempCore t, out TextBox body)
    {
        var win = new QuickNoteWindow(t.Client);
        win.ShowQuickNote(BarRegion, Bounds, 1.0);
        WinWindowTestSupport.Pump();
        body = (TextBox)WinWindowTestSupport.FindByName(win, "BodyBox");
        return win;
    }

    private static void Deactivate(QuickNoteWindow win) =>
        InvokePrivate(win, "OnDeactivated", win, EventArgs.Empty);

    /// <summary>
    /// Reviewer requirement: the Deactivated/Esc suppression only applies while WPF focus really sits
    /// inside the editor, so every close-path test establishes that explicitly (ShowQuickNote alone
    /// leaves Keyboard.FocusedElement null on this headless host) and proves it before driving the
    /// close. Without this the close tests pass vacuously — the guard never fires.
    /// </summary>
    private static void FocusBody(QuickNoteWindow win, TextBox body)
    {
        Assert.True(body.Focus());
        WinWindowTestSupport.Pump();
        Assert.True(FocusHelper.IsKeyboardFocusInside(win));
        Assert.Same(body, Keyboard.FocusedElement);
    }

    /// <summary>A genuine click-outside: the WPF focus scope is abandoned before Deactivated fires.</summary>
    private static void AbandonFocus()
    {
        Keyboard.ClearFocus();
        WinWindowTestSupport.Pump();
    }

    private static void PressEscape(QuickNoteWindow win, TextBox body) =>
        body.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(body)!, 0, Key.Escape)
        {
            RoutedEvent = UIElement.PreviewKeyDownEvent,
        });

    [Fact]
    public void ShowQuickNote_puts_keyboard_focus_on_the_body_box()
    {
        WinWindowTestSupport.RunOnSta(() =>
        {
            using var t = new TempCore();
            var win = Show(t, out var body);
            try
            {
                Assert.Equal(Visibility.Visible, win.Visibility);
                Assert.Same(body, Keyboard.FocusedElement); // focus on open — the whole point of '+'
            }
            finally
            {
                WinWindowTestSupport.HideAllWindows();
            }
        });
    }

    [Fact]
    public void Deactivated_with_an_empty_body_closes_without_writing()
    {
        WinWindowTestSupport.RunOnSta(() =>
        {
            using var t = new TempCore();
            var win = Show(t, out var body);
            bool closed = false;
            QuickNoteClose reason = default;
            QuickNoteOutcome outcome = default;
            win.QuickNoteClosed += (_, e) => { closed = true; reason = e.Reason; outcome = e.Outcome; };
            try
            {
                FocusBody(win, body); // focus lands, then leaves: the real click-outside sequence
                AbandonFocus();
                Assert.False(FocusHelper.IsKeyboardFocusInside(win));

                body.Text = "";
                Deactivate(win);

                Assert.True(closed, "deactivate must close the quick note");
                Assert.Equal(QuickNoteClose.ClickOutside, reason);
                Assert.Equal(QuickNoteOutcome.NothingToCommit, outcome);
                Assert.Equal(Visibility.Hidden, win.Visibility);
                Assert.Empty(t.Client.GetBarSnapshot().RecentNotes); // empty → no write
            }
            finally
            {
                WinWindowTestSupport.HideAllWindows();
            }
        });
    }

    [Fact]
    public void Deactivated_with_text_commits_the_note_and_closes()
    {
        WinWindowTestSupport.RunOnSta(() =>
        {
            using var t = new TempCore();
            var win = Show(t, out var body);
            bool closed = false;
            QuickNoteClose reason = default;
            QuickNoteOutcome outcome = default;
            win.QuickNoteClosed += (_, e) => { closed = true; reason = e.Reason; outcome = e.Outcome; };
            try
            {
                FocusBody(win, body);
                AbandonFocus();
                Assert.False(FocusHelper.IsKeyboardFocusInside(win));

                body.Text = "quick capture from deactivate";
                Deactivate(win);

                Assert.True(closed);
                Assert.Equal(QuickNoteClose.ClickOutside, reason);
                Assert.Equal(QuickNoteOutcome.Committed, outcome);
                Assert.Equal(Visibility.Hidden, win.Visibility);
                Note committed = t.Client.GetNote(t.Client.GetBarSnapshot().RecentNotes[0].Id, false);
                Assert.Equal("quick capture from deactivate", committed.Body);       // whole body committed
                Assert.Equal("quick capture from deactivat", committed.TitlePreview); // core pins preview to 28 chars
            }
            finally
            {
                WinWindowTestSupport.HideAllWindows();
            }
        });
    }

    [Fact]
    public void CloseButton_exists_and_discards_and_closes()
    {
        WinWindowTestSupport.RunOnSta(() =>
        {
            using var t = new TempCore();
            var win = Show(t, out var body);
            var close = (Button)WinWindowTestSupport.FindByName(win, "CloseButton");
            Assert.Equal("×", close.Content.ToString()); // the discard affordance exists
            Assert.Equal("Discard", close.ToolTip.ToString());

            bool closed = false;
            QuickNoteClose reason = default;
            QuickNoteOutcome outcome = default;
            win.QuickNoteClosed += (_, e) => { closed = true; reason = e.Reason; outcome = e.Outcome; };
            try
            {
                FocusBody(win, body); // the × is clicked while the editor owns the focus scope
                body.Text = "typed then discarded";
                LeftClick(close);

                Assert.True(closed, "× must close the quick note");
                Assert.Equal(QuickNoteClose.Escape, reason);      // × always discards — same contract as Esc
                Assert.Equal(QuickNoteOutcome.Discarded, outcome);
                Assert.Equal(Visibility.Hidden, win.Visibility);
                Assert.Empty(t.Client.GetBarSnapshot().RecentNotes); // discarded → no write
            }
            finally
            {
                WinWindowTestSupport.HideAllWindows();
            }
        });
    }

    [Fact]
    public void Escape_discards_and_closes_while_the_editor_still_owns_the_focus_scope()
    {
        WinWindowTestSupport.RunOnSta(() =>
        {
            using var t = new TempCore();
            var win = Show(t, out var body);
            FocusBody(win, body); // the suppression case: focus really is within

            bool closed = false;
            QuickNoteClose reason = default;
            QuickNoteOutcome outcome = default;
            win.QuickNoteClosed += (_, e) => { closed = true; reason = e.Reason; outcome = e.Outcome; };
            try
            {
                body.Text = "esc me";
                PressEscape(win, body);

                Assert.True(closed, "Esc must discard + close even while focus is within");
                Assert.Equal(QuickNoteClose.Escape, reason);
                Assert.Equal(QuickNoteOutcome.Discarded, outcome);
                Assert.Equal(Visibility.Hidden, win.Visibility); // not stranded open
                Assert.Equal(0, t.Client.GetBarSnapshot().RecentNotes.Count); // discarded → no write
            }
            finally
            {
                WinWindowTestSupport.HideAllWindows();
            }
        });
    }

    [Fact]
    public void Deactivate_suppression_while_focus_is_within_does_not_strand_the_editor_open()
    {
        WinWindowTestSupport.RunOnSta(() =>
        {
            using var t = new TempCore();
            var win = Show(t, out var body);
            bool closed = false;
            win.QuickNoteClosed += (_, _) => closed = true;
            try
            {
                FocusBody(win, body);
                body.Text = "kept through a false deactivation";

                Deactivate(win); // we lost the OS foreground but still own the WPF focus scope
                Assert.False(closed, "a deactivation with focus within must be suppressed");
                Assert.Equal(Visibility.Visible, win.Visibility);
                Assert.Empty(t.Client.GetBarSnapshot().RecentNotes); // and must not commit

                AbandonFocus(); // a genuine click-outside: the WPF focus scope is abandoned
                Deactivate(win);

                Assert.True(closed, "the suppressed editor must still close on the next real deactivation");
                Assert.Equal(Visibility.Hidden, win.Visibility);
                Note committed = t.Client.GetNote(t.Client.GetBarSnapshot().RecentNotes[0].Id, false);
                Assert.Equal("kept through a false deactivation", committed.Body);       // whole body committed
                Assert.Equal("kept through a false deactiv", committed.TitlePreview);     // core pins preview to 28 chars
            }
            finally
            {
                WinWindowTestSupport.HideAllWindows();
            }
        });
    }

    private static void LeftClick(Button button) =>
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
}
