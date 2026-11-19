// v0.2.2+ bug-fix gate - the group side list docks to the RIGHT of the hovered row's dropdown
// (human amendment to blueprint §1, recorded in docs/ROADMAP-v0.2.0.md F5). Pure positioner math:
// no WPF, no clock. This file recreates the coverage deleted with the retired API (orch-relayed
// restore of ScreenPositioner.GetGroupPreviewRect + PreviewGapPx + the IScreenPositioner member).
// The click path's below-chip geometry is asserted unchanged in ScreenPositionerTests (untouched,
// per the no-existing-test-edits rule).
using StickyNotes.App.Interop;
using Xunit;

namespace StickyNotes.Tests;

[Trait("Task", "v0.2.2")]
public class GroupPreviewPositionerTests
{
    private static readonly ScreenPositioner P = new();
    private const int RowHeight = 30;   // GroupListWindow group row height (DIP == physical px at 96 DPI)

    private static MonitorBounds Mon() =>
        new(new PxRect(0, 0, 1920, 1080), new PxRect(0, 0, 1920, 1040));

    private static PxRect Row(int x, int y) => new(x, y, 260, RowHeight);

    private static void Inside(PxRect r, PxRect wa)
    {
        Assert.InRange(r.Left, wa.Left, wa.Right);
        Assert.InRange(r.Right, wa.Left, wa.Right);
        Assert.InRange(r.Top, wa.Top, wa.Bottom);
        Assert.InRange(r.Bottom, wa.Top, wa.Bottom);
    }

    [Fact]
    public void Side_list_opens_to_the_right_of_the_dropdown_aligned_with_the_row()
    {
        PxRect row = Row(500, 100);
        PxRect r = P.GetGroupPreviewRect(row, new PxSize(240, 420), Mon());

        Assert.Equal(row.Right + ScreenPositioner.PreviewGapPx, r.Left); // right of the dropdown
        Assert.Equal(row.Top, r.Top);                                    // aligned with the hovered row
        Assert.Equal(240, r.Width);
        Assert.InRange(r.Top - row.Top, 0, 1);
        Inside(r, Mon().WorkArea);
    }

    [Fact]
    public void Side_list_flips_left_of_the_row_when_the_right_edge_has_no_room()
    {
        PxRect row = Row(1700, 100); // dropdown pinned near the right edge

        PxRect r = P.GetGroupPreviewRect(row, new PxSize(240, 420), Mon());

        Assert.True(r.Right <= Mon().WorkArea.Right);
        Assert.True(r.Right <= row.Left); // flipped to the LEFT of the row
        Inside(r, Mon().WorkArea);
    }

    [Fact]
    public void Side_list_is_clamped_inside_the_work_area_on_a_narrow_monitor()
    {
        var narrow = new MonitorBounds(new PxRect(0, 0, 640, 480), new PxRect(0, 0, 640, 440));
        PxRect row = Row(300, 40);

        PxRect r = P.GetGroupPreviewRect(row, new PxSize(240, 420), narrow);

        // Right-dock overflows (560 + 8 + 240 > 640), so it flips left of the row and stays on-screen.
        Assert.Equal(240, r.Width);
        Assert.True(r.Left >= narrow.WorkArea.Left);
        Assert.True(r.Right <= narrow.WorkArea.Right);
        Assert.True(r.Right <= row.Left);
        Assert.Equal(row.Top, r.Top);
        Assert.True(r.Bottom <= narrow.WorkArea.Bottom); // height clamped to the work area, never negative
        Inside(r, narrow.WorkArea);
    }
}
