using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace StickyNotes.App.Core;

/// <summary>
/// Managed wrapper over the Rust <c>sticky_core.dll</c> C ABI (blueprint §6/§7).
/// Every call marshals through <see cref="InvokeJson{T}"/>, which always frees the
/// native payload exactly once (sn_string_free) in a finally block.
/// </summary>
public sealed class StickyCoreClient : IStickyCoreClient
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = false,
        AllowTrailingCommas = false,
    };

    // Serializes InvokeJson against Dispose: the disposed-check, the handle read, the
    // native call, and sn_core_close are all under this gate, so a Dispose can never
    // zero/close the handle between a caller's check and its DllImport.
    private readonly object _gate = new();
    private IntPtr _handle;
    private bool _disposed;

    public StickyCoreClient(string dbPath)
    {
        if (string.IsNullOrWhiteSpace(dbPath))
        {
            throw new ArgumentException("Database path must not be null or empty.", nameof(dbPath));
        }

        DbPath = dbPath;

        FfiResult res = NativeCoreMethods.sn_core_open(dbPath, out IntPtr handle);
        try
        {
            string text = PayloadText(res);
            if (res.Status != 0)
            {
                throw new StickyCoreException(res.Status, text);
            }

            _handle = handle;
        }
        finally
        {
            FreePayload(res);
        }
    }

    /// <summary>Path to the SQLite file passed to sn_core_open.</summary>
    public string DbPath { get; }

    public BarSnapshot GetBarSnapshot() =>
        InvokeJson<BarSnapshot>(h => NativeCoreMethods.sn_get_bar_snapshot(h));

    public IReadOnlyList<NoteSummary> ListGroupNotes(long groupId) =>
        InvokeJson<List<NoteSummary>>(h => NativeCoreMethods.sn_list_group_notes(h, groupId));

    public IReadOnlyList<NoteSummary> ListAppendTargets(int limit = 30)
    {
        if (limit < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "Limit must be non-negative.");
        }

        return InvokeJson<List<NoteSummary>>(h => NativeCoreMethods.sn_list_append_targets(h, (uint)limit));
    }

    public Note GetNote(long noteId, bool markOpened) =>
        InvokeJson<Note>(h => NativeCoreMethods.sn_get_note(h, noteId, markOpened));

    public Note CreateNote(long? groupId, string body = "") =>
        InvokeJson<Note>(h => NativeCoreMethods.sn_create_note(h, groupId.HasValue, groupId ?? 0L, body ?? ""));

    public NoteSummary UpdateNoteBody(long noteId, string body) =>
        InvokeJson<NoteSummary>(h => NativeCoreMethods.sn_update_note_body(h, noteId, body ?? ""));

    public void DeleteNote(long noteId) =>
        InvokeUnit(h => NativeCoreMethods.sn_delete_note(h, noteId));

    public NoteSummary SetNotePinned(long noteId, bool pinned) =>
        InvokeJson<NoteSummary>(h => NativeCoreMethods.sn_set_note_pinned(h, noteId, pinned));

    public GroupSummary CreateGroup(string name, bool pinned = true) =>
        InvokeJson<GroupSummary>(h => NativeCoreMethods.sn_create_group(h, name ?? "", pinned));

    public GroupSummary RenameGroup(long groupId, string name) =>
        InvokeJson<GroupSummary>(h => NativeCoreMethods.sn_rename_group(h, groupId, name ?? ""));

    public void DeleteGroup(long groupId) =>
        InvokeUnit(h => NativeCoreMethods.sn_delete_group(h, groupId));

    public GroupSummary SetGroupPinned(long groupId, bool pinned) =>
        InvokeJson<GroupSummary>(h => NativeCoreMethods.sn_set_group_pinned(h, groupId, pinned));

    public Note QuickCapture(string text, QuickCaptureTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return InvokeJson<Note>(h => NativeCoreMethods.sn_quick_capture(h, text ?? "", target.ToJson()));
    }

    /// <summary>Idempotent: closes the native handle exactly once.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_handle != IntPtr.Zero)
            {
                NativeCoreMethods.sn_core_close(_handle);
                _handle = IntPtr.Zero;
            }
        }

        GC.SuppressFinalize(this);
    }

#if DEBUG
    /// <summary>Debug aid: a forgotten <c>using</c> fails loudly here instead of leaking the native handle silently.</summary>
    ~StickyCoreClient()
    {
        System.Diagnostics.Debug.Fail("StickyCoreClient was not disposed; closing the native handle from the finalizer.");
        Dispose();
    }
#endif

    private T InvokeJson<T>(Func<IntPtr, FfiResult> call)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(StickyCoreClient));
            }

            IntPtr handle = _handle;
            if (handle == IntPtr.Zero)
            {
                throw new ObjectDisposedException(nameof(StickyCoreClient));
            }

            FfiResult res = call(handle);
            try
            {
                string text = PayloadText(res);
                if (res.Status != 0)
                {
                    throw new StickyCoreException(res.Status, text);
                }

                return JsonSerializer.Deserialize<T>(text, JsonOpts)
                    ?? throw new StickyCoreException(5, $"Native returned null JSON for {typeof(T).Name}.");
            }
            finally
            {
                FreePayload(res);
            }
        }
    }

    private void InvokeUnit(Func<IntPtr, FfiResult> call) =>
        InvokeJson<JsonElement>(call);

    private static string PayloadText(FfiResult res)
    {
        if (res.Payload == IntPtr.Zero)
        {
            // Rust contract: payload is ALWAYS non-null, even for unit results. Null means contract violation.
            System.Diagnostics.Debug.Fail("Native returned a null FfiResult.Payload (contract violation).");
            return string.Empty;
        }

        return Marshal.PtrToStringUTF8(res.Payload) ?? string.Empty;
    }

    private static void FreePayload(FfiResult res)
    {
        if (res.Payload != IntPtr.Zero)
        {
            NativeCoreMethods.sn_string_free(res.Payload);
        }
    }

}
