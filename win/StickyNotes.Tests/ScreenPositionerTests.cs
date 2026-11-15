// TASK 4 gate - physical-pixel placement math (no UI needed).
//
// ASSUMED TYPES (adapt names/constructors ONLY to compile; never weaken assertions):
//   PxRect(int X, int Y, int Width, int Height) with Left/Top/Right/Bottom (Right = X + Width)
//   PxSize(int Width, int Height)
//   MonitorBounds(PxRect Monitor, PxRect WorkArea)
//   new ScreenPositioner() : IScreenPositioner
using StickyNotes.App.Interop;
using Xunit;

namespace StickyNotes.Tests;

[Trait("Task", "4")]
public class ScreenPositionerTests
{
    static readonly ScreenPositioner P = new();

    // 1920x1080 monitor with a 40px taskbar at the bottom.
    static MonitorBounds Mon(int x = 0, int y = 0, int w = 1920, int h = 1080, int taskbar = 40) =>
        new(new PxRect(x, y, w, h), new PxRect(x, y, w, h - taskbar));

    public static IEnumerable<object[]> Scales => new[]
    {
        new object[] { 1.0 }, new object[] { 1.25 }, new object[] { 1.5 }, new object[] { 2.0 },
    };

    static void Near(int expected, int actual, int tol = 1) =>
        Assert.InRange(actual, expected - tol, expected + tol);

    static void Inside(PxRect r, PxRect area)
    {
        Assert.True(r.Left >= area.Left, $"left {r.Left} < {area.Left}");
        Assert.True(r.Top >= area.Top, $"top {r.Top} < {area.Top}");
        Assert.True(r.Right <= area.Right, $"right {r.Right} > {area.Right}");
        Assert.True(r.Bottom <= area.Bottom, $"bottom {r.Bottom} > {area.Bottom}");
    }

    // ---------- trigger ----------
    [Theory, MemberData(nameof(Scales))]
    public void Trigger_is_100x4_dips_centered_at_top_of_work_area(double dpi)
    {
        var m = Mon();
        var r = P.GetTriggerRect(m, dpi);
        Near((int)Math.Round(100 * dpi), r.Width);
        Near((int)Math.Round(4 * dpi), r.Height);
        Assert.Equal(m.WorkArea.Top, r.Top);
        Near(m.WorkArea.Left + m.WorkArea.Width / 2, r.Left + r.Width / 2);
    }

    [Fact]
    public void Trigger_centers_correctly_on_a_secondary_monitor_with_negative_coordinates()
    {
        var m = Mon(x: -1920);
        var r = P.GetTriggerRect(m, 1.0);
        Near(-960, r.Left + r.Width / 2);
        Inside(r, m.WorkArea);
    }

    // ---------- bar ----------
    [Theory, MemberData(nameof(Scales))]
    public void Bar_is_44_dips_tall_capped_at_1100_dips_and_centered(double dpi)
    {
        var m = Mon(w: (int)(2560 * dpi));
        var r = P.GetBarRect(m, dpi);
        Assert.Equal(m.WorkArea.Top, r.Top);
        Near((int)Math.Round(44 * dpi), r.Height);
        Near((int)Math.Round(1100 * dpi), r.Width, 2);
        Near(m.WorkArea.Left + m.WorkArea.Width / 2, r.Left + r.Width / 2);
        Inside(r, m.WorkArea);
    }

    [Fact]
    public void Bar_on_a_narrow_monitor_uses_92_percent_of_work_width()
    {
        var m = Mon(w: 1024, h: 768);
        var r = P.GetBarRect(m, 1.0);
        Near((int)(1024 * 0.92), r.Width, 2);
        Inside(r, m.WorkArea);
    }

    // ---------- group list ----------
    [Fact]
    public void Group_list_opens_directly_below_its_chip()
    {
        var m = Mon();
        var chip = new PxRect(500, 0, 120, 44);
        var r = P.GetGroupListRect(chip, new PxSize(260, 420), m);
        Assert.Equal(chip.Left, r.Left);
        Assert.InRange(r.Top - chip.Bottom, 0, 8);
        Inside(r, m.WorkArea);
    }

    [Fact]
    public void Group_list_is_clamped_inside_the_work_area_near_the_right_edge()
    {
        var m = Mon();
        var chip = new PxRect(1850, 0, 120, 44);
        var r = P.GetGroupListRect(chip, new PxSize(260, 420), m);
        Inside(r, m.WorkArea);
    }

    [Fact]
    public void Group_list_taller_than_available_space_is_clamped_vertically()
    {
        var m = Mon(h: 500);
        var r = P.GetGroupListRect(new PxRect(500, 0, 120, 44), new PxSize(260, 420), m);
        Assert.True(r.Bottom <= m.WorkArea.Bottom);
    }

    // ---------- note window beside a group-list row ----------
    [Fact]
    public void Note_opens_to_the_right_of_a_list_row_with_a_6px_gap()
    {
        var m = Mon();
        var row = new PxRect(500, 100, 260, 32);
        var r = P.GetNoteWindowRect(row, new PxSize(320, 280), m, anchorIsHorizontalBarItem: false);
        Assert.Equal(row.Right + 6, r.Left);
        Assert.Equal(row.Top, r.Top);
    }

    [Fact]
    public void Note_flips_left_when_there_is_no_room_on_the_right()
    {
        var m = Mon();
        var row = new PxRect(1500, 100, 260, 32);
        var r = P.GetNoteWindowRect(row, new PxSize(320, 280), m, false);
        Assert.Equal(row.Left - 320 - 6, r.Left);
        Inside(r, m.WorkArea);
    }

    [Fact]
    public void Note_flip_works_on_a_monitor_left_of_the_primary()
    {
        var m = Mon(x: -1920);
        var row = new PxRect(-400, 100, 260, 32);
        var r = P.GetNoteWindowRect(row, new PxSize(320, 280), m, false);
        Assert.Equal(row.Left - 320 - 6, r.Left);
        Inside(r, m.WorkArea);
    }

    [Fact]
    public void Note_is_clamped_vertically_near_the_bottom()
    {
        var m = Mon();
        var row = new PxRect(500, 900, 260, 32);
        var r = P.GetNoteWindowRect(row, new PxSize(320, 280), m, false);
        Assert.Equal(m.WorkArea.Bottom - 280, r.Top);
    }

    // ---------- note window opened from a horizontal bar chip ----------
    [Fact]
    public void Note_from_a_bar_chip_opens_below_it()
    {
        var m = Mon();
        var chip = new PxRect(800, 0, 120, 44);
        var r = P.GetNoteWindowRect(chip, new PxSize(320, 280), m, anchorIsHorizontalBarItem: true);
        Assert.Equal(chip.Left, r.Left);
        Assert.Equal(chip.Bottom + 6, r.Top);
    }

    [Fact]
    public void Note_from_a_bar_chip_shifts_left_instead_of_overflowing()
    {
        var m = Mon();
        var chip = new PxRect(1850, 0, 120, 44);
        var r = P.GetNoteWindowRect(chip, new PxSize(320, 280), m, true);
        Assert.True(r.Right <= m.WorkArea.Right);
    }

    // ---------- property sweep: nothing may ever leave the screen ----------
    [Theory, MemberData(nameof(Scales))]
    public void No_placement_ever_leaves_the_work_area(double dpi)
    {
        foreach (var m in new[] { Mon(), Mon(x: -1920), Mon(x: 1920, w: 1600, h: 1000) })
        {
            for (int ax = m.WorkArea.Left; ax < m.WorkArea.Right; ax += 97)
            for (int ay = m.WorkArea.Top; ay < m.WorkArea.Bottom; ay += 89)
            {
                var anchor = new PxRect(ax, ay, 120, 32);
                var size = new PxSize((int)(320 * dpi), (int)(280 * dpi));
                Inside(P.GetNoteWindowRect(anchor, size, m, false), m.WorkArea);
                Inside(P.GetNoteWindowRect(anchor, size, m, true), m.WorkArea);
                Inside(P.GetGroupListRect(anchor, new PxSize((int)(260 * dpi), (int)(420 * dpi)), m), m.WorkArea);
            }
        }
    }
}
