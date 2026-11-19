// F6 gate - NoteWindow runtime behaviour: the click-outside auto-close, the F1 inline
// new-group panel, the F1/F6 badge flow. Private members are reached via reflection; nothing
// here edits or relaxes production code.
//
// Focus-sensitive paths are intentionally asserted through EVENTS and Visibility, not through
// real foreground state: the test runner owns no foreground guarantee.
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using StickyNotes.App.Core;
using StickyNotes.App.Interop;
using StickyNotes.App.State;
using StickyNotes.App.Windows;
using Xunit;

namespace StickyNotes.Tests;

[Trait("Task", "F6")]
[Collection("WpfUi")]
public class NoteWindowBehaviorTests
{
    private static MonitorBounds Mon(int x, int y, int w, int h, int taskbar = 40) =>
        new(new PxRect(x, y, w, h), new PxRect(x, y, w, h - taskbar));

    private static object? InvokePrivate(object target, string name, params object?[] args)
    {
        MethodInfo[] candidates = target.GetType().GetMethods(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public | BindingFlags.FlattenHierarchy)
            .Where(m => m.Name == name)
            .ToArray();
        MethodInfo method = candidates.SingleOrDefault(m => m.DeclaringType == target.GetType())
            ?? candidates.SingleOrDefault()
            ?? throw new InvalidOperationException($"private method '{name}' not found on {target.GetType().Name}");
        return method.Invoke(target, args);
    }

    private static T Field<T>(object target, string name)
    {
        FieldInfo field = target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"private field '{name}' not found on {target.GetType().Name}");
        return (T)field.GetValue(target)!;
    }

    private static void SetField(object target, string name, object? value) =>
        target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);

    // ------------------------------------------------------------------ click-outside auto-close

    // v0.2.2+: a Deactivated no longer closes the note inline. It arms a 120ms settle timer and the
    // decision moves to CloseIfDeactivationSettled -> NoteClosePolicy.ShouldCloseOnDeactivate, because a
    // force-activation, a topmost re-show and an inline-edit hand-off all deliver a SPURIOUS Deactivated
    // while the editor still holds the keyboard. This asserts the window-side half of that contract: the
    // deactivation is deferred, not acted on. The decision itself — all six inputs, including "the
    // foreground is one of our own windows, i.e. a bounce" — is pinned by NoteClosePolicyTests, and it
    // deliberately cannot be driven from here: the test host owns no foreground guarantee, which is
    // precisely why the policy was extracted.
    [Fact]
    public void ClickOutside_Does_Not_Close_Until_The_Deactivation_Settles()
    {
        WinWindowTestSupport.RunOnSta(() =>
        {
            using var t = new TempCore();
            var n = t.Client.CreateNote(null, "body");
            var win = new NoteWindow(t.Client, n, createdThisOpen: false);
            bool closed = false;
            win.ClosedByUser += (_, _) => closed = true;
            try
            {
                SetField(win, "_everActivated", true); // the note really took focus at least once
                InvokePrivate(win, "OnDeactivated", win, EventArgs.Empty);

                Assert.False(closed, "a deactivation must settle before it is allowed to close the note");
                Assert.NotNull(Field<DispatcherTimer?>(win, "_deactivateSettleTimer"));

                // Nothing was flushed and nothing was deleted: the note is still exactly as it was.
                Assert.Equal("body", t.Client.GetNote(n.Id, markOpened: false).Body);
            }
            finally
            {
                WinWindowTestSupport.HideAllWindows();
            }
        });
    }

    [Fact]
    public void Deactivation_Is_Suppressed_While_The_MoveMenu_Is_Open()
    {
        WinWindowTestSupport.RunOnSta(() =>
        {
            using var t = new TempCore();
            var n = t.Client.CreateNote(null, "body");
            var win = new NoteWindow(t.Client, n, createdThisOpen: false);
            bool closed = false;
            win.ClosedByUser += (_, _) => closed = true;
            try
            {
                SetField(win, "_suppressDeactivate", true); // what OpenMoveMenu sets while the menu is up
                InvokePrivate(win, "OnDeactivated", win, EventArgs.Empty);

                Assert.False(closed, "a deactivation that is really a menu-open must not close the note");
                Assert.NotEqual(Visibility.Visible, win.Visibility); // ctor keeps it Hidden; close must not have happened
                SetField(win, "_suppressDeactivate", false);
            }
            finally
            {
                WinWindowTestSupport.HideAllWindows();
            }
        });
    }

    [Fact]
    public void Real_MoveMenu_Click_Opens_The_Menu_And_Suppresses_Deactivation()
    {
        WinWindowTestSupport.RunOnSta(() =>
        {
            using var t = new TempCore();
            var n = t.Client.CreateNote(null, "body");
            var win = new NoteWindow(t.Client, n, createdThisOpen: false);
            bool closed = false;
            win.ClosedByUser += (_, _) => closed = true;
            try
            {
                InvokePrivate(win, "OnMoveToGroupClick", win, new RoutedEventArgs());

                Assert.True(Field<bool>(win, "_suppressDeactivate"), "opening the move menu must suppress the click-outside close");
                InvokePrivate(win, "OnDeactivated", win, EventArgs.Empty);
                Assert.False(closed);
            }
            finally
            {
                WinWindowTestSupport.HideAllWindows();
            }
        });
    }

    // ------------------------------------------------------------------ BeginMoveToNewGroup / badge

    [Fact]
    public void BeginMoveToNewGroup_Shows_The_Inline_Panel()
    {
        WinWindowTestSupport.RunOnSta(() =>
        {
            using var t = new TempCore();
            var n = t.Client.CreateNote(null, "body");
            var win = new NoteWindow(t.Client, n, createdThisOpen: false);
            try
            {
                var panel = (Grid)WinWindowTestSupport.FindByName(win, "NewGroupPanel");
                var box = (TextBox)WinWindowTestSupport.FindByName(win, "NewGroupBox");

                win.BeginMoveToNewGroup();
                WinWindowTestSupport.Pump();

                Assert.Equal(Visibility.Visible, panel.Visibility);
                Assert.Equal(string.Empty, box.Text);
            }
            finally
            {
                WinWindowTestSupport.HideAllWindows();
            }
        });
    }

    [Fact]
    public void RefreshGroupBadge_Updates_The_Badge_Text()
    {
        WinWindowTestSupport.RunOnSta(() =>
        {
            using var t = new TempCore();
            var n = t.Client.CreateNote(null, "body");
            var win = new NoteWindow(t.Client, n, createdThisOpen: false);
            try
            {
                var badge = (Button)WinWindowTestSupport.FindByName(win, "GroupBadge");
                Assert.Equal("Move to group ▾", badge.Content); // standalone: always visible for discoverability

                win.RefreshGroupBadge(42, "Work");
                Assert.Equal("● Work ▾", badge.Content);

                win.RefreshGroupBadge(null, null);
                Assert.Equal("Move to group ▾", badge.Content);
            }
            finally
            {
                WinWindowTestSupport.HideAllWindows();
            }
        });
    }

    // ------------------------------------------------------------------ move menu / create-and-move

    [Fact]
    public void MoveToGroup_Raises_NoteMoved_Updates_The_Core_And_The_Badge()
    {
        WinWindowTestSupport.RunOnSta(() =>
        {
            using var t = new TempCore();
            var group = t.Client.CreateGroup("Work", pinned: true);
            var n = t.Client.CreateNote(null, "body");
            var win = new NoteWindow(t.Client, n, createdThisOpen: false);
            long? movedTo = -1;
            bool fired = false;
            win.NoteMoved += (_, e) => { movedTo = e.GroupId; fired = true; };
            try
            {
                InvokePrivate(win, "MoveToGroup", (object?)group.Id, "Work");

                Assert.True(fired);
                Assert.Equal(group.Id, movedTo);
                Assert.Equal(group.Id, t.Client.GetNote(n.Id, markOpened: false).GroupId);
                Assert.Equal("● Work ▾", ((Button)WinWindowTestSupport.FindByName(win, "GroupBadge")).Content);
            }
            finally
            {
                WinWindowTestSupport.HideAllWindows();
            }
        });
    }

    [Fact]
    public void MoveToGroup_To_Standalone_Raises_NoteMoved_With_Null()
    {
        WinWindowTestSupport.RunOnSta(() =>
        {
            using var t = new TempCore();
            var group = t.Client.CreateGroup("Work", pinned: true);
            var n = t.Client.CreateNote(group.Id, "body");
            var win = new NoteWindow(t.Client, n, createdThisOpen: false);
            long? movedTo = 123;
            win.NoteMoved += (_, e) => movedTo = e.GroupId;
            try
            {
                InvokePrivate(win, "MoveToGroup", (object?)null, (object?)null);

                Assert.Null(movedTo);
                Assert.Null(t.Client.GetNote(n.Id, markOpened: false).GroupId);
            }
            finally
            {
                WinWindowTestSupport.HideAllWindows();
            }
        });
    }

    [Fact]
    public void MoveToGroup_To_Missing_Group_Does_Not_Move_Or_Raise()
    {
        WinWindowTestSupport.RunOnSta(() =>
        {
            using var t = new TempCore();
            var n = t.Client.CreateNote(null, "body");
            var win = new NoteWindow(t.Client, n, createdThisOpen: false);
            bool fired = false;
            win.NoteMoved += (_, _) => fired = true;
            try
            {
                InvokePrivate(win, "MoveToGroup", 999_999L, "Ghost");

                Assert.False(fired, "a failed move must not report success");
                Assert.Null(t.Client.GetNote(n.Id, markOpened: false).GroupId);
                Assert.Equal("Move to group ▾", ((Button)WinWindowTestSupport.FindByName(win, "GroupBadge")).Content);
            }
            finally
            {
                WinWindowTestSupport.HideAllWindows();
            }
        });
    }

    [Fact]
    public void MoveToGroup_To_The_Current_Group_Is_A_Silent_No_Op()
    {
        WinWindowTestSupport.RunOnSta(() =>
        {
            using var t = new TempCore();
            var group = t.Client.CreateGroup("Work", pinned: true);
            var n = t.Client.CreateNote(group.Id, "body");
            var win = new NoteWindow(t.Client, n, createdThisOpen: false);
            bool fired = false;
            win.NoteMoved += (_, _) => fired = true;
            try
            {
                InvokePrivate(win, "MoveToGroup", (object?)group.Id, "Work");
                Assert.False(fired, "moving to the group the note is already in must not re-announce");
            }
            finally
            {
                WinWindowTestSupport.HideAllWindows();
            }
        });
    }

    [Fact]
    public void Blank_NewGroup_Name_Creates_Nothing_And_Keeps_The_Panel_Open()
    {
        WinWindowTestSupport.RunOnSta(() =>
        {
            using var t = new TempCore();
            var n = t.Client.CreateNote(null, "body");
            var win = new NoteWindow(t.Client, n, createdThisOpen: false);
            bool fired = false;
            win.NoteMoved += (_, _) => fired = true;
            try
            {
                win.BeginMoveToNewGroup();
                var panel = (Grid)WinWindowTestSupport.FindByName(win, "NewGroupPanel");
                var box = (TextBox)WinWindowTestSupport.FindByName(win, "NewGroupBox");
                box.Text = "   ";

                InvokePrivate(win, "CreateGroupAndMove");

                Assert.Empty(t.Client.GetBarSnapshot().AllGroups);
                Assert.False(fired);
                Assert.Equal(Visibility.Visible, panel.Visibility); // still open, waiting for a name
            }
            finally
            {
                WinWindowTestSupport.HideAllWindows();
            }
        });
    }

    [Fact]
    public void Valid_NewGroup_Name_Creates_Group_Moves_Note_And_Closes_The_Panel()
    {
        WinWindowTestSupport.RunOnSta(() =>
        {
            using var t = new TempCore();
            var n = t.Client.CreateNote(null, "body");
            var win = new NoteWindow(t.Client, n, createdThisOpen: false);
            long? movedTo = -1;
            win.NoteMoved += (_, e) => movedTo = e.GroupId;
            try
            {
                win.BeginMoveToNewGroup();
                var panel = (Grid)WinWindowTestSupport.FindByName(win, "NewGroupPanel");
                var box = (TextBox)WinWindowTestSupport.FindByName(win, "NewGroupBox");
                box.Text = "Inbox";

                InvokePrivate(win, "CreateGroupAndMove");

                var groups = t.Client.GetBarSnapshot().AllGroups;
                Assert.Single(groups);
                Assert.Equal("Inbox", groups[0].Name);
                Assert.Equal(groups[0].Id, movedTo);
                Assert.Equal(groups[0].Id, t.Client.GetNote(n.Id, markOpened: false).GroupId);
                Assert.Equal(Visibility.Collapsed, panel.Visibility);
                Assert.Equal("● Inbox ▾", ((Button)WinWindowTestSupport.FindByName(win, "GroupBadge")).Content);
            }
            finally
            {
                WinWindowTestSupport.HideAllWindows();
            }
        });
    }

    [Fact]
    public void Esc_Closes_Immediately_And_Flushes()
    {
        WinWindowTestSupport.RunOnSta(() =>
        {
            using var t = new TempCore();
            var n = t.Client.CreateNote(null, "original");
            var win = new NoteWindow(t.Client, n, createdThisOpen: false);
            bool closed = false;
            win.ClosedByUser += (_, _) => closed = true;
            try
            {
                win.ShowNote(new PxRect(500, 0, 120, 44), Mon(0, 0, 1920, 1080), 1.0, anchorIsHorizontalBarItem: true, focused: false);
                WinWindowTestSupport.Pump();
                var box = (TextBox)WinWindowTestSupport.FindByName(win, "BodyBox");
                box.Text = "edited"; // user typed something; Esc must not lose it
                InvokePrivate(win, "OnBodyKeyDown", box, new System.Windows.Input.KeyEventArgs(
                    System.Windows.Input.Keyboard.PrimaryDevice,
                    PresentationSource.FromVisual(box)!,
                    0,
                    System.Windows.Input.Key.Escape)
                {
                    RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent,
                });

                Assert.True(closed);
                Assert.Equal("edited", t.Client.GetNote(n.Id, markOpened: false).Body);
            }
            finally
            {
                WinWindowTestSupport.HideAllWindows();
            }
        });
    }
}
