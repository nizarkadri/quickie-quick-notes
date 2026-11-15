using StickyNotes.App.Core;

namespace StickyNotes.Tests;

/// <summary>Creates a throwaway database + real StickyCoreClient (uses the real sticky_core.dll).</summary>
public sealed class TempCore : IDisposable
{
    public string Dir { get; }
    public string DbPath { get; }
    public StickyCoreClient Client { get; private set; }

    public TempCore()
    {
        Dir = Path.Combine(Path.GetTempPath(), "sn-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Dir);
        DbPath = Path.Combine(Dir, "sticky.db");
        Client = new StickyCoreClient(DbPath);
    }

    /// <summary>Close and reopen the same database file (simulates an app restart).</summary>
    public void Reopen()
    {
        Client.Dispose();
        Client = new StickyCoreClient(DbPath);
    }

    /// <summary>Timestamps are in milliseconds; make sure consecutive operations differ.</summary>
    public static void Tick() => Thread.Sleep(5);

    public void Dispose()
    {
        Client.Dispose();
        try { Directory.Delete(Dir, recursive: true); } catch { /* best effort */ }
    }
}
