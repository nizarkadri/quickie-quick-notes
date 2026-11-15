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
        InvokeJson<BarSnapshot>(() => NativeCoreMethods.sn_get_bar_snapshot(_handle));

    public IReadOnlyList<NoteSummary> ListGroupNotes(long groupId) =>
        InvokeJson<List<NoteSummary>>(() => NativeCoreMethods.sn_list_group_notes(_handle, groupId));

    public IReadOnlyList<NoteSummary> ListAppendTargets(int limit = 30) =>
        InvokeJson<List<NoteSummary>>(() => NativeCoreMethods.sn_list_append_targets(_handle, checked((uint)limit)));

    public Note GetNote(long noteId, bool markOpened) =>
        InvokeJson<Note>(() => NativeCoreMethods.sn_get_note(_handle, noteId, markOpened));

    public Note CreateNote(long? groupId, string body = "") =>
        InvokeJson<Note>(() => NativeCoreMethods.sn_create_note(_handle, groupId.HasValue, groupId ?? 0L, body ?? ""));

    public NoteSummary UpdateNoteBody(long noteId, string body) =>
        InvokeJson<NoteSummary>(() => NativeCoreMethods.sn_update_note_body(_handle, noteId, body ?? ""));

    public void DeleteNote(long noteId) =>
        InvokeUnit(() => NativeCoreMethods.sn_delete_note(_handle, noteId));

    public NoteSummary SetNotePinned(long noteId, bool pinned) =>
        InvokeJson<NoteSummary>(() => NativeCoreMethods.sn_set_note_pinned(_handle, noteId, pinned));

    public GroupSummary CreateGroup(string name, bool pinned = true) =>
        InvokeJson<GroupSummary>(() => NativeCoreMethods.sn_create_group(_handle, name ?? "", pinned));

    public GroupSummary RenameGroup(long groupId, string name) =>
        InvokeJson<GroupSummary>(() => NativeCoreMethods.sn_rename_group(_handle, groupId, name ?? ""));

    public void DeleteGroup(long groupId) =>
        InvokeUnit(() => NativeCoreMethods.sn_delete_group(_handle, groupId));

    public GroupSummary SetGroupPinned(long groupId, bool pinned) =>
        InvokeJson<GroupSummary>(() => NativeCoreMethods.sn_set_group_pinned(_handle, groupId, pinned));

    public Note QuickCapture(string text, QuickCaptureTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return InvokeJson<Note>(() => NativeCoreMethods.sn_quick_capture(_handle, text ?? "", target.ToJson()));
    }

    /// <summary>Idempotent: closes the native handle exactly once.</summary>
    public void Dispose()
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

        GC.SuppressFinalize(this);
    }

    private T InvokeJson<T>(Func<FfiResult> call)
    {
        ThrowIfDisposed();
        FfiResult res = call();
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

    private void InvokeUnit(Func<FfiResult> call) =>
        InvokeJson<JsonElement>(call);

    private static string PayloadText(FfiResult res) =>
        res.Payload != IntPtr.Zero ? Marshal.PtrToStringUTF8(res.Payload) ?? string.Empty : string.Empty;

    private static void FreePayload(FfiResult res)
    {
        if (res.Payload != IntPtr.Zero)
        {
            NativeCoreMethods.sn_string_free(res.Payload);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(StickyCoreClient));
        }
    }
}
