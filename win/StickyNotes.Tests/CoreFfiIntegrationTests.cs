// TASK 3 gate - Rust <-> C# boundary, tested through the real sticky_core.dll.
//
// ASSUMED NAMES (adapt to the real code ONLY to make this compile; never weaken an assertion):
//   new StickyCoreClient(string dbPath)       StickyCoreException.Status (int)
//   QuickCaptureTarget.Standalone() / .Group(long) / .Append(long)
//   Note { Id, GroupId (long?), Body, TitlePreview, IsPinned }
//   GroupSummary { Id, Name, NoteCount, IsPinned }
//   BarSnapshot { RecentNotes, AllGroups }
using System.Diagnostics;
using StickyNotes.App.Core;
using Xunit;

namespace StickyNotes.Tests;

[Trait("Task", "3")]
public class CoreFfiIntegrationTests
{
    [Fact]
    public void Group_and_note_round_trip()
    {
        using var t = new TempCore();
        var g = t.Client.CreateGroup("Work", pinned: true);
        var n = t.Client.CreateNote(g.Id, "hello");

        var back = t.Client.GetNote(n.Id, markOpened: false);
        Assert.Equal("hello", back.Body);
        Assert.Equal(g.Id, back.GroupId);
        Assert.Equal("Work", t.Client.GetBarSnapshot().AllGroups.Single().Name);
    }

    [Fact]
    public void Standalone_note_has_no_group()
    {
        using var t = new TempCore();
        var n = t.Client.CreateNote(null, "solo");
        Assert.Null(t.Client.GetNote(n.Id, false).GroupId);
    }

    [Theory]
    [InlineData("héllo wörld")]
    [InlineData("日本語のメモ")]
    [InlineData("emoji 🌍🔥✅ done")]
    [InlineData("line1\nline2\r\nline3\ttabbed")]
    public void Utf8_bodies_round_trip(string body)
    {
        using var t = new TempCore();
        var n = t.Client.CreateNote(null, body);
        Assert.Equal(body, t.Client.GetNote(n.Id, false).Body);
        t.Client.UpdateNoteBody(n.Id, body + "!");
        Assert.Equal(body + "!", t.Client.GetNote(n.Id, false).Body);
    }

    [Fact]
    public void Large_body_round_trips()
    {
        using var t = new TempCore();
        var body = string.Concat(Enumerable.Repeat("abc\n", 125_000)); // ~500 KB
        var n = t.Client.CreateNote(null, body);
        Assert.Equal(body, t.Client.GetNote(n.Id, false).Body);
    }

    [Fact]
    public void Missing_note_maps_to_status_1()
    {
        using var t = new TempCore();
        var ex = Assert.Throws<StickyCoreException>(() => t.Client.GetNote(999_999, false));
        Assert.Equal(1, ex.Status);
    }

    [Fact]
    public void Blank_group_name_maps_to_status_2()
    {
        using var t = new TempCore();
        var ex = Assert.Throws<StickyCoreException>(() => t.Client.CreateGroup("   ", true));
        Assert.Equal(2, ex.Status);
    }

    [Fact]
    public void Pin_and_unpin_note_and_group()
    {
        using var t = new TempCore();
        var n = t.Client.CreateNote(null, "p");
        Assert.True(t.Client.SetNotePinned(n.Id, true).IsPinned);
        Assert.False(t.Client.SetNotePinned(n.Id, false).IsPinned);

        var g = t.Client.CreateGroup("G", pinned: false);
        Assert.True(t.Client.SetGroupPinned(g.Id, true).IsPinned);
        Assert.False(t.Client.SetGroupPinned(g.Id, false).IsPinned);
    }

    [Fact]
    public void Rename_group_works()
    {
        using var t = new TempCore();
        var g = t.Client.CreateGroup("Old", true);
        Assert.Equal("New", t.Client.RenameGroup(g.Id, "New").Name);
    }

    [Fact]
    public void QuickCapture_append_uses_a_single_newline()
    {
        using var t = new TempCore();
        var target = t.Client.CreateNote(null, "line1");
        t.Client.QuickCapture("line2", QuickCaptureTarget.Append(target.Id));
        Assert.Equal("line1\nline2", t.Client.GetNote(target.Id, false).Body);
    }

    [Fact]
    public void QuickCapture_standalone_and_group_targets()
    {
        using var t = new TempCore();
        var g = t.Client.CreateGroup("G", true);
        var solo = t.Client.QuickCapture("s", QuickCaptureTarget.Standalone());
        var inGroup = t.Client.QuickCapture("g", QuickCaptureTarget.Group(g.Id));
        Assert.Null(solo.GroupId);
        Assert.Equal(g.Id, inGroup.GroupId);
    }

    [Fact]
    public void DeleteGroup_cascades_to_member_notes()
    {
        using var t = new TempCore();
        var g = t.Client.CreateGroup("G", true);
        var n = t.Client.CreateNote(g.Id, "member");
        t.Client.DeleteGroup(g.Id);
        Assert.Throws<StickyCoreException>(() => t.Client.GetNote(n.Id, false));
        Assert.Empty(t.Client.GetBarSnapshot().AllGroups);
    }

    [Fact]
    public void Data_survives_restart()
    {
        using var t = new TempCore();
        var n = t.Client.CreateNote(null, "persist");
        t.Reopen();
        Assert.Equal("persist", t.Client.GetNote(n.Id, false).Body);
    }

    [Fact]
    public void Eight_threads_reading_and_writing_do_not_fail_or_lose_data()
    {
        using var t = new TempCore();
        var c = t.Client;
        var tasks = Enumerable.Range(0, 8).Select(w => Task.Run(() =>
        {
            for (int i = 0; i < 20; i++)
            {
                c.CreateNote(null, $"t{w}-{i}");
                c.GetBarSnapshot();
            }
        })).ToArray();

        Task.WaitAll(tasks);
        Assert.Equal(160, c.ListAppendTargets(500).Count);
    }

    [Fact]
    public void Twenty_thousand_calls_do_not_leak_native_memory()
    {
        using var t = new TempCore();
        for (int i = 0; i < 50; i++) t.Client.CreateNote(null, new string('x', 200));
        for (int i = 0; i < 500; i++) t.Client.GetBarSnapshot(); // warm-up

        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long before = Process.GetCurrentProcess().WorkingSet64;

        for (int i = 0; i < 20_000; i++) t.Client.GetBarSnapshot();

        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long growth = Process.GetCurrentProcess().WorkingSet64 - before;
        Assert.True(growth < 40L * 1024 * 1024,
            $"working set grew {growth / 1024 / 1024} MB over 20k snapshots - FfiResult.payload is probably not freed");
    }

    [Fact]
    public void Dispose_is_idempotent_and_use_after_dispose_throws()
    {
        var t = new TempCore();
        var client = t.Client;
        client.Dispose();
        client.Dispose(); // must not throw or double-free
        Assert.Throws<ObjectDisposedException>(() => client.GetBarSnapshot());
        t.Dispose();
    }
}
