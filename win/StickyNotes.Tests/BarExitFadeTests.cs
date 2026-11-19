// v0.2.2 gate (STA) - BarWindow exposes the fade opacity and a re-show comes back opaque.
using System.Windows.Controls;
using StickyNotes.App.Interop;
using StickyNotes.App.Windows;
using Xunit;

namespace StickyNotes.Tests;

[Trait("Task", "v0.2.2")]
[Collection("WpfUi")]
public class BarExitFadeTests
{
    private static readonly MonitorBounds Bounds = new(new PxRect(0, 0, 1920, 1080), new PxRect(0, 0, 1920, 1040));

    [Fact]
    public void SetOpacity_clamps_and_ShowOnMonitor_resets_to_opaque()
    {
        WinWindowTestSupport.RunOnSta(() =>
        {
            var win = new BarWindow();
            try
            {
                var root = (Border)WinWindowTestSupport.FindByName(win, "Root");

                win.SetOpacity(0.25);
                Assert.Equal(0.25, root.Opacity, 10);

                win.SetOpacity(-3.0);
                Assert.Equal(0.0, root.Opacity, 10);

                win.SetOpacity(9.0);
                Assert.Equal(1.0, root.Opacity, 10);

                win.SetOpacity(0.1); // mid-fade
                win.ShowOnMonitor(Bounds, nint.Zero, 1.0);
                WinWindowTestSupport.Pump();
                Assert.Equal(1.0, root.Opacity, 10); // re-show after a fade is fully opaque
            }
            finally
            {
                WinWindowTestSupport.HideAllWindows();
            }
        });
    }
}
