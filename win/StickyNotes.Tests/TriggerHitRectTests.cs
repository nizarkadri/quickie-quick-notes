using System;
using StickyNotes.App.Interop;
using Xunit;

namespace StickyNotes.Tests;

[Trait("Feature", "F3")]
public class TriggerHitRectTests
{
    static readonly ScreenPositioner P = new();

    static MonitorBounds Mon(int x = 0, int y = 0, int w = 1920, int h = 1080, int taskbar = 40) =>
        new(new PxRect(x, y, w, h), new PxRect(x, y, w, h - taskbar));

    static void Near(int expected, int actual, int tol = 1) =>
        Assert.InRange(actual, expected - tol, expected + tol);

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void Hit_rect_is_200x4_centered_at_top(double dpi)
    {
        var m = Mon();
        var r = P.GetTriggerHitRect(m, dpi);

        Near((int)Math.Round(200 * dpi), r.Width);
        Near((int)Math.Round(4 * dpi), r.Height);
        Assert.Equal(m.WorkArea.Top, r.Top);
        Near(m.WorkArea.Left + m.WorkArea.Width / 2, r.Left + r.Width / 2);
    }

    [Fact]
    public void Hit_rect_centers_on_negative_secondary_monitor()
    {
        var m = Mon(x: -1920);
        var r = P.GetTriggerHitRect(m, 1.0);

        Near(-960, r.Left + r.Width / 2);
    }

    [Fact]
    public void Hit_rect_stays_inside_narrow_work_area()
    {
        var m = Mon(w: 1024, h: 768);
        var r = P.GetTriggerHitRect(m, 1.0);

        Assert.True(r.Left >= m.WorkArea.Left);
        Assert.True(r.Right <= m.WorkArea.Right);
        Assert.True(r.Top >= m.WorkArea.Top);
    }

    [Fact]
    public void Visible_trigger_rect_is_still_100x4()
    {
        var m = Mon();
        var r = P.GetTriggerRect(m, 1.0);

        Near(100, r.Width);
        Near(4, r.Height);
    }
}