// v0.2.2 gate - the pure 2s linear bar exit-fade curve (no WPF, no clock).
using StickyNotes.App.State;
using Xunit;

namespace StickyNotes.Tests;

[Trait("Task", "v0.2.2")]
public class BarExitPolicyTests
{
    private const int Fade = BarExitPolicy.FadeDurationMs;

    [Fact]
    public void Fade_duration_is_2000ms()
    {
        Assert.Equal(2000, Fade);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void OpacityAt_or_before_zero_is_fully_opaque(long elapsedMs)
    {
        Assert.Equal(1.0, BarExitPolicy.OpacityAt(elapsedMs));
    }

    [Fact]
    public void OpacityAt_is_linear_through_the_fade()
    {
        Assert.Equal(0.5, BarExitPolicy.OpacityAt(999), 2);   // 1 - 999/2000 = 0.5005
        Assert.Equal(0.5, BarExitPolicy.OpacityAt(1000), 10); // exact midpoint
        Assert.True(BarExitPolicy.OpacityAt(1999) > 0.0);     // never hits 0 early
        Assert.Equal(0.25, BarExitPolicy.OpacityAt(1500), 10);
    }

    [Theory]
    [InlineData(2000)]
    [InlineData(long.MaxValue)]
    public void OpacityAt_at_or_after_the_duration_is_transparent(long elapsedMs)
    {
        Assert.Equal(0.0, BarExitPolicy.OpacityAt(elapsedMs));
    }

    [Fact]
    public void OpacityAt_is_monotone_non_increasing_across_the_fade()
    {
        double previous = BarExitPolicy.OpacityAt(-100);
        for (long elapsed = -100; elapsed <= 3000; elapsed += 7)
        {
            double current = BarExitPolicy.OpacityAt(elapsed);
            Assert.True(current <= previous, $"opacity rose at {elapsed}ms: {previous} -> {current}");
            previous = current;
        }

        Assert.Equal(0.0, previous);
    }

    [Fact]
    public void ShouldHide_only_once_the_full_duration_elapsed()
    {
        Assert.False(BarExitPolicy.ShouldHide(0));
        Assert.False(BarExitPolicy.ShouldHide(1999));
        Assert.True(BarExitPolicy.ShouldHide(2000));
        Assert.True(BarExitPolicy.ShouldHide(long.MaxValue));
    }
}
