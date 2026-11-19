// Shared WPF-test scaffold for the F6 window-behaviour tests.
//
// ALL window tests run on ONE dedicated STA "UI thread" that lives for the whole test process.
// Rules this enforces:
//   * every window is created, laid out, hidden and observed on the same thread (WPF objects
//     are thread-affine; a window outliving its STA thread crashes WTS subclass teardown);
//   * the app-level brushes (App.xaml's BarBackground/PillIdle/PillHover/TextPrimary) exist
//     exactly once and are FROZEN, so any thread can resolve them;
//   * if another test class already created the Application on a different thread (it does —
//     the F4 tooltip tests do), we mutate its Resources through ITS dispatcher, with frozen
//     brushes, so nothing is ever touched across threads.
//
// The WpfUi collection disables parallelization so no two of these tests contend for the UI.
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Xunit;

namespace StickyNotes.Tests;

[CollectionDefinition("WpfUi", DisableParallelization = true)]
public sealed class WpfUiCollection
{
}

internal static class WinWindowTestSupport
{
    private static readonly object UiGate = new();
    private static Dispatcher? s_ui;

    private static Dispatcher EnsureUiThread()
    {
        if (s_ui is not null)
        {
            return s_ui;
        }

        lock (UiGate)
        {
            if (s_ui is not null)
            {
                return s_ui;
            }

            var ready = new ManualResetEventSlim(false);
            var thread = new Thread(() =>
            {
                s_ui = Dispatcher.CurrentDispatcher;
                EnsureResources(); // runs on the owning thread, whatever created the Application
                ready.Set();
                Dispatcher.PushFrame(new DispatcherFrame()); // pump so Invoke can return; thread is background
            })
            {
                IsBackground = true,
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            ready.Wait();
            return s_ui;
        }
    }

    public static void RunOnSta(Action body)
    {
        Dispatcher ui = EnsureUiThread();
        Exception? error = null;
        ui.Invoke(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });

        if (error is not null)
        {
            throw error;
        }
    }

    private static void EnsureResources()
    {
        var app = Application.Current;
        if (app is null)
        {
            app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            SetBrushes(app);
            return;
        }

        // Some other test created the Application on its own thread — marshal to its dispatcher.
        app.Dispatcher.Invoke(() => SetBrushes(app));
    }

    private static void SetBrushes(Application app)
    {
        (string Key, byte R, byte G, byte B, byte A)[] all =
        {
            ("BarBackground", 0x20, 0x20, 0x20, 0xE8),
            ("PillIdle", 0x88, 0x88, 0x88, 0x38),
            ("PillHover", 0x88, 0x88, 0x88, 0x80),
            ("TextPrimary", 0xDF, 0xFF, 0xFF, 0xFF),
        };

        foreach ((string key, byte r, byte g, byte b, byte a) in all)
        {
            var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            brush.Freeze(); // cross-thread safe, replaces whatever an earlier Application put there
            app.Resources[key] = brush;
        }
    }

    /// <summary>Let layout + bindings settle for windows owned by the calling (UI) thread.</summary>
    public static void Pump()
    {
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        dispatcher.Invoke(
            () =>
            {
                foreach (Window window in Application.Current.Windows.OfType<Window>())
                {
                    if (ReferenceEquals(window.Dispatcher, dispatcher))
                    {
                        window.UpdateLayout();
                    }
                }
            },
            DispatcherPriority.ContextIdle);
        dispatcher.Invoke(() => { }, DispatcherPriority.Background);
    }

    /// <summary>Hide every window owned by the calling (UI) thread (never close — shutdown-safe).</summary>
    public static void HideAllWindows()
    {
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        foreach (Window window in Application.Current.Windows.OfType<Window>().ToList())
        {
            if (ReferenceEquals(window.Dispatcher, dispatcher))
            {
                try
                {
                    window.Hide();
                }
                catch (InvalidOperationException)
                {
                    // already closed
                }
            }
        }
    }

    /// <summary>Real DPI scale of the monitor the window is currently on (tests anchor every window on the primary monitor).</summary>
    public static double ActualDpi(Window window) => VisualTreeHelper.GetDpi(window).DpiScaleX;

    public static FrameworkElement FindByName(FrameworkElement root, string name)
    {
        FrameworkElement? found = root.FindName(name) as FrameworkElement;
        Assert.True(found is not null, $"XAML element '{name}' not found");
        return found;
    }
}
