// F4 gate - read-only hover preview for note chips/rows.
//
// MANUAL_ACCEPTANCE.md owns the actual hover/tooltip behaviour (placement, focus,
// dismissal on move-away). Everything that can crash or silently regress WITHOUT a
// mouse is pinned here:
//
//   1. NotePreview.Format          - pure text shaping (null/empty/whitespace, the
//                                    ~200 char cap, word-boundary preference, hard cut).
//   2. NotePreview.CreateToolTip   - the non-activating Popup contract (StaysOpen=false,
//                                    wrapping, width cap) probed on a real STA thread with a
//                                    real WPF Application (Application.Current.Resources is
//                                    REQUIRED by CreateToolTip - a missing brush Key or a
//                                    null Application.Current is a hover-time crash).
//   3. The FFI path the preview uses to resolve a body: GetNote(id, markOpened: false).
//      A hover must never bump last_opened_at_ms or reorder Recents, and the two exception
//      types the hover handlers swallow (StickyCoreException for a vanished note,
//      ObjectDisposedException for a shutdown race) must be exactly what the client throws.
//
// WPF objects are thread-affine, so every WPF assertion runs INSIDE a dedicated STA thread.
using System.Windows;
using StickyNotes.App.Core;
using StickyNotes.App.Windows;
using Xunit;

namespace StickyNotes.Tests;

[Trait("Task", "F4")]
public class NotePreviewFormatTests
{
    private const string Placeholder = "(empty note)";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   \t  ")]
    [InlineData("\r\n\r\n\r\n")]
    [InlineData(" \n\r\t ")]
    public void Null_empty_and_whitespace_bodies_show_the_placeholder(string? body)
    {
        Assert.Equal(Placeholder, NotePreview.Format(body));
    }

    [Fact]
    public void Short_body_is_returned_verbatim()
    {
        Assert.Equal("hello world", NotePreview.Format("hello world"));
    }

    [Fact]
    public void Whitespace_runs_collapse_to_single_spaces()
    {
        Assert.Equal("a b c d e", NotePreview.Format("a\n\nb\r\nc\td  e"));
    }

    [Fact]
    public void No_leading_or_trailing_space_survives_collapsing()
    {
        string formatted = NotePreview.Format("   leading and trailing   \n\n  ");
        Assert.Equal("leading and trailing", formatted);
    }

    [Fact]
    public void Body_of_exactly_MaxChars_is_not_truncated()
    {
        string body = new string('a', NotePreview.MaxChars);
        Assert.Equal(body, NotePreview.Format(body));
    }

    [Fact]
    public void Body_one_char_over_MaxChars_is_truncated_with_an_ellipsis()
    {
        string body = new string('a', NotePreview.MaxChars + 1);
        string formatted = NotePreview.Format(body);
        Assert.EndsWith("\u2026", formatted);
        Assert.Equal(NotePreview.MaxChars + 1, formatted.Length); // MaxChars chars + ellipsis, minus TrimEnd
    }

    [Fact]
    public void Hard_cut_is_used_when_the_body_has_no_break_point()
    {
        string body = new string('x', 250);
        string expected = string.Concat(new string('x', NotePreview.MaxChars), "\u2026");
        Assert.Equal(expected, NotePreview.Format(body));
    }

    [Fact]
    public void Truncation_prefers_the_last_space_before_the_limit()
    {
        // 190 chars of text, a space at index 190, then more text past the limit.
        const int breakAt = 190;
        string head = new string('a', breakAt);
        string body = head + " " + new string('b', 50);
        string formatted = NotePreview.Format(body);
        Assert.Equal(head + "\u2026", formatted);
    }

    [Fact]
    public void Space_exactly_at_the_limit_is_a_valid_break_point()
    {
        // Space sits at index MaxChars: span(0, MaxChars) excludes it, so no trailing space precedes the ellipsis.
        string body = new string('a', NotePreview.MaxChars) + " " + new string('b', 50);
        string formatted = NotePreview.Format(body);
        Assert.Equal(new string('a', NotePreview.MaxChars) + "\u2026", formatted);
    }

    [Fact]
    public void Break_point_before_half_the_limit_is_rejected_in_favour_of_a_hard_cut()
    {
        // Word break at index 50 (< MaxChars/2) would leave a sliver of a line: hard cut instead.
        const int earlyBreak = 50;
        string body = new string('a', earlyBreak) + " " + new string('b', 300);
        string formatted = NotePreview.Format(body);
        Assert.Equal(string.Concat(body.AsSpan(0, NotePreview.MaxChars), "\u2026"), formatted);
    }

    [Fact]
    public void Truncation_never_ends_with_a_space_before_the_ellipsis()
    {
        // Craft a body whose break-point cut lands right after spaces.
        string body = new string('a', 150) + "   " + new string('b', 100);
        string formatted = NotePreview.Format(body);
        Assert.EndsWith("a\u2026", formatted);
        Assert.DoesNotContain(" \u2026", formatted);
    }

    [Fact]
    public void Truncation_never_splits_a_word_when_a_better_break_exists()
    {
        string body = string.Concat(Enumerable.Repeat("word ", 60)).TrimEnd(); // 299 chars
        string formatted = NotePreview.Format(body);
        Assert.EndsWith("\u2026", formatted);
        string kept = formatted[..^1];
        Assert.True(body.StartsWith(kept, StringComparison.Ordinal), "kept text must be a prefix of the normalized body");
        Assert.False(kept.EndsWith(' '), "kept text must not end with a space");
    }

    [Fact]
    public void Result_is_never_longer_than_MaxChars_plus_the_ellipsis()
    {
        var random = new Random(20261116);
        const string alphabet = "abc \n\t\r\n日本語🌍";
        for (int i = 0; i < 200; i++)
        {
            int len = random.Next(0, 600);
            var sb = new System.Text.StringBuilder(len);
            for (int j = 0; j < len; j++)
            {
                sb.Append(alphabet[random.Next(alphabet.Length)]);
            }

            string formatted = NotePreview.Format(sb.ToString());
            Assert.True(formatted.Length <= NotePreview.MaxChars + 1,
                $"formatted length {formatted.Length} exceeds {NotePreview.MaxChars + 1} for input length {len}");
        }
    }

    [Theory]
    [InlineData("日本語のメモです。これはとても長い日本語のテキストで、ホバープレビューの表示をテストするために書かれています。漢字かな交じりのテキストを十分な量だけ用意して、二百文字を超えるようにしています。")]
    [InlineData("emoji 🌍🔥✅ plus text to push this well beyond the two hundred character ceiling so truncation kicks in and the ellipsis path is exercised with multi-byte content present 🌍🔥✅🌍🔥✅🌍🔥✅🌍🔥✅🌍🔥✅🌍🔥✅🌍🔥✅🌍🔥✅🌍🔥✅🌍🔥✅")]
    public void Multibyte_and_emoji_bodies_truncate_without_crashing(string body)
    {
        string formatted = NotePreview.Format(body);
        Assert.NotNull(formatted);
        Assert.True(formatted.Length > 0);
    }

    [Fact]
    public void Surrogate_pair_straddling_the_cut_point_does_not_crash()
    {
        // U+1F30D is one scalar but two UTF-16 code units; pin it to straddle index MaxChars.
        string body = new string('a', NotePreview.MaxChars - 1) + "🌍" + new string('b', 50);
        var exception = Record.Exception(() => NotePreview.Format(body));
        Assert.Null(exception);
    }

    [Fact]
    public void CrLf_and_cr_bodies_round_trip_as_single_spaces()
    {
        Assert.Equal("a b c", NotePreview.Format("a\r\nb\rc"));
    }
}

[Trait("Task", "F4")]
[Collection("WpfUi")]
public class NotePreviewToolTipTests
{
    // CreateToolTip reads Application.Current.Resources["TextPrimary"]: a null Application.Current
    // (NullReferenceException) or a missing key (KeyNotFoundException) is a crash ON HOVER. The
    // real app supplies the key via App.xaml; this test supplies the same resource on a bare
    // Application so the contract CreateToolTip relies on is exercised, not re-created by accident.
    // Runs on the shared WPF UI thread (see WinWindowTestSupport) — one Application for the whole
    // suite, with the app-level brushes frozen. All assertions are unchanged from the original.
    private static void RunOnStaThread(Action body) => WinWindowTestSupport.RunOnSta(body);

    [Fact]
    public void Tooltip_is_a_non_activating_popup_that_stays_open_false()
    {
        RunOnStaThread(() =>
        {
            (System.Windows.Controls.ToolTip tip, _) = NotePreview.CreateToolTip();
            Assert.False(tip.StaysOpen);           // must dismiss when the cursor moves away
            Assert.False(tip.IsOpen);              // never starts open
            Assert.Equal(System.Windows.Controls.Primitives.PlacementMode.Bottom, tip.Placement);
            Assert.IsNotType<System.Windows.Window>(tip); // a Popup: no taskbar button, no activation
        });
    }

    [Fact]
    public void Tooltip_content_wraps_at_a_capped_width_so_it_cannot_overflow_the_screen()
    {
        RunOnStaThread(() =>
        {
            (System.Windows.Controls.ToolTip tip, System.Windows.Controls.TextBlock text) = NotePreview.CreateToolTip();
            Assert.Same(text, tip.Content);
            Assert.Equal(NotePreview.MaxWidth, text.MaxWidth);
            Assert.Equal(System.Windows.TextWrapping.Wrap, text.TextWrapping);
            Assert.Equal(string.Empty, text.Text); // body is resolved lazily on ToolTipOpening, never ahead of time
        });
    }

    [Fact]
    public void Tooltip_text_uses_the_TextPrimary_resource_from_the_application()
    {
        RunOnStaThread(() =>
        {
            (_, System.Windows.Controls.TextBlock text) = NotePreview.CreateToolTip();
            var expected = (System.Windows.Media.Brush)Application.Current!.Resources["TextPrimary"];
            Assert.Same(expected, text.Foreground);
        });
    }
}

[Trait("Task", "F4")]
public class NotePreviewHoverCoreTests
{
    // The preview resolves bodies via GetNote(id, markOpened: false). These tests run through the
    // real sticky_core.dll, exactly like CoreFfiIntegrationTests, pinning the two guarantees a
    // hover must not violate and the exact exception types the F4 handlers catch.

    [Fact]
    public void Hover_does_not_bump_last_opened_at_ms()
    {
        using var t = new TempCore();
        var n = t.Client.CreateNote(null, "hover target");
        TempCore.Tick();

        long baseline = t.Client.GetNote(n.Id, markOpened: false).LastOpenedAtMs;
        for (int i = 0; i < 3; i++) // three hovers
        {
            TempCore.Tick();
            Assert.Equal(baseline, t.Client.GetNote(n.Id, markOpened: false).LastOpenedAtMs);
        }

        Assert.Equal(baseline, n.LastOpenedAtMs);
    }

    [Fact]
    public void Hover_does_not_reorder_recents()
    {
        using var t = new TempCore();
        var a = t.Client.CreateNote(null, "note A"); // unpinned
        TempCore.Tick();
        var b = t.Client.CreateNote(null, "note B"); // unpinned, newer

        Assert.Equal(new long[] { b.Id, a.Id }, t.Client.GetBarSnapshot().RecentNotes.Select(x => x.Id).ToArray());

        TempCore.Tick();
        _ = t.Client.GetNote(a.Id, markOpened: false); // hover over A

        Assert.Equal(new long[] { b.Id, a.Id }, t.Client.GetBarSnapshot().RecentNotes.Select(x => x.Id).ToArray());
    }

    [Fact]
    public void Real_open_after_hovers_still_bumps_last_opened_and_jumps_recents()
    {
        using var t = new TempCore();
        var a = t.Client.CreateNote(null, "note A");
        TempCore.Tick();
        var b = t.Client.CreateNote(null, "note B");

        TempCore.Tick();
        _ = t.Client.GetNote(a.Id, markOpened: false); // hovers
        _ = t.Client.GetNote(a.Id, markOpened: false);
        long beforeRealOpen = t.Client.GetNote(a.Id, markOpened: false).LastOpenedAtMs;

        TempCore.Tick();
        t.Client.GetNote(a.Id, markOpened: true); // actual click

        Assert.True(t.Client.GetNote(a.Id, markOpened: false).LastOpenedAtMs > beforeRealOpen);
        Assert.Equal(a.Id, t.Client.GetBarSnapshot().RecentNotes.First().Id);
    }

    [Fact]
    public void Preview_resolves_the_full_body_not_the_truncated_one()
    {
        using var t = new TempCore();
        string body = string.Concat(Enumerable.Repeat("line of text\n", 200)); // ~2600 chars, newlines included
        var n = t.Client.CreateNote(null, body);

        string resolved = t.Client.GetNote(n.Id, markOpened: false).Body;
        Assert.Equal(body, resolved); // truncation is the Format layer's job, not the core's
        Assert.True(NotePreview.Format(resolved).Length <= NotePreview.MaxChars + 1);
    }

    [Fact]
    public void Empty_note_resolves_to_a_body_the_placeholder_covers()
    {
        using var t = new TempCore();
        var n = t.Client.CreateNote(null, "");
        Assert.Equal("(empty note)", NotePreview.Format(t.Client.GetNote(n.Id, markOpened: false).Body));
    }

    [Fact]
    public void Hovering_a_deleted_note_throws_StickyCoreException_which_the_hover_handlers_swallow()
    {
        using var t = new TempCore();
        var n = t.Client.CreateNote(null, "doomed");
        t.Client.DeleteNote(n.Id);

        // BarWindow / GroupListWindow / ShellCoordinator catch exactly this type on hover -> null -> no bubble.
        var ex = Assert.Throws<StickyCoreException>(() => t.Client.GetNote(n.Id, markOpened: false));
        Assert.NotEqual(0, ex.Status);
    }

    [Fact]
    public void Hovering_after_shutdown_throws_ObjectDisposedException_which_the_hover_handlers_swallow()
    {
        var t = new TempCore();
        var n = t.Client.CreateNote(null, "race me");
        t.Dispose();

        // ShellCoordinator/BarWindow/GroupListWindow catch exactly this type on a shutdown race -> null -> no bubble.
        Assert.Throws<ObjectDisposedException>(() => t.Client.GetNote(n.Id, markOpened: false));
    }
}
