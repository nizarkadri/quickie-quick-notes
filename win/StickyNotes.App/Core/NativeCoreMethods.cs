using System;
using System.Runtime.InteropServices;

namespace StickyNotes.App.Core;

/// <summary>
/// Hand-written P/Invoke surface for the 16-function `sticky_core.dll` C ABI
/// (blueprint §6). Memory ownership contract: Rust allocates FfiResult.Payload via
/// CString::into_raw; the caller MUST free it exactly once with sn_string_free.
/// Rust `bool` is 1 byte — all bool parameters use UnmanagedType.U1.
/// All `const char*` parameters are UTF-8 — LPUTF8Str, not ANSI/UTF-16.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FfiResult
{
    /// <summary>0 = OK, 1 = NotFound, 2 = Validation, 3 = Sqlite, 4 = UnsupportedSchema, 5 = Internal/panic.</summary>
    public int Status;

    /// <summary>Always non-null UTF-8 CString ("{}" for unit results). Free with sn_string_free.</summary>
    public IntPtr Payload;
}

internal static class NativeCoreMethods
{
    private const string Dll = "sticky_core";
    private const CallingConvention Cdecl = CallingConvention.Cdecl;

    // ------------------------- lifecycle & memory -------------------------

    [DllImport(Dll, CallingConvention = Cdecl)]
    internal static extern FfiResult sn_core_open(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string dbPathUtf8,
        out IntPtr outHandle);

    [DllImport(Dll, CallingConvention = Cdecl)]
    internal static extern void sn_core_close(IntPtr handle);

    [DllImport(Dll, CallingConvention = Cdecl)]
    internal static extern void sn_string_free(IntPtr ptr);

    // ------------------------- queries & mutations -------------------------

    [DllImport(Dll, CallingConvention = Cdecl)]
    internal static extern FfiResult sn_get_bar_snapshot(IntPtr handle);

    [DllImport(Dll, CallingConvention = Cdecl)]
    internal static extern FfiResult sn_list_group_notes(IntPtr handle, long groupId);

    [DllImport(Dll, CallingConvention = Cdecl)]
    internal static extern FfiResult sn_list_append_targets(IntPtr handle, uint limit);

    [DllImport(Dll, CallingConvention = Cdecl)]
    internal static extern FfiResult sn_get_note(
        IntPtr handle,
        long noteId,
        [MarshalAs(UnmanagedType.U1)] bool markOpened);

    [DllImport(Dll, CallingConvention = Cdecl)]
    internal static extern FfiResult sn_create_note(
        IntPtr handle,
        [MarshalAs(UnmanagedType.U1)] bool hasGroup,
        long groupId,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string bodyUtf8);

    [DllImport(Dll, CallingConvention = Cdecl)]
    internal static extern FfiResult sn_update_note_body(
        IntPtr handle,
        long noteId,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string bodyUtf8);

    [DllImport(Dll, CallingConvention = Cdecl)]
    internal static extern FfiResult sn_delete_note(IntPtr handle, long noteId);

    [DllImport(Dll, CallingConvention = Cdecl)]
    internal static extern FfiResult sn_set_note_pinned(
        IntPtr handle,
        long noteId,
        [MarshalAs(UnmanagedType.U1)] bool pinned);

    [DllImport(Dll, CallingConvention = Cdecl)]
    internal static extern FfiResult sn_set_note_group(
        IntPtr handle,
        long noteId,
        [MarshalAs(UnmanagedType.U1)] bool hasGroup,
        long groupId);

    [DllImport(Dll, CallingConvention = Cdecl)]
    internal static extern FfiResult sn_create_group(
        IntPtr handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string nameUtf8,
        [MarshalAs(UnmanagedType.U1)] bool pinned);

    [DllImport(Dll, CallingConvention = Cdecl)]
    internal static extern FfiResult sn_rename_group(
        IntPtr handle,
        long groupId,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string nameUtf8);

    [DllImport(Dll, CallingConvention = Cdecl)]
    internal static extern FfiResult sn_delete_group(IntPtr handle, long groupId);

    [DllImport(Dll, CallingConvention = Cdecl)]
    internal static extern FfiResult sn_set_group_pinned(
        IntPtr handle,
        long groupId,
        [MarshalAs(UnmanagedType.U1)] bool pinned);

    [DllImport(Dll, CallingConvention = Cdecl)]
    internal static extern FfiResult sn_quick_capture(
        IntPtr handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string textUtf8,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string targetJsonUtf8);
}
