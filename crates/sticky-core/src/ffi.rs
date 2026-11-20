//! Thin `extern "C"` ABI over [`StickyCore`] (blueprint §6).
//!
//! Contract enforced here:
//! * every entrypoint runs inside [`std::panic::catch_unwind`] — a panic becomes
//!   `FfiResult { status: 5 }`, never an unwind across the FFI boundary;
//! * `FfiResult.payload` is ALWAYS non-null (`"{}"` for unit results) and is
//!   allocated with `CString::into_raw`; the caller frees it exactly once via
//!   [`sn_string_free`];
//! * input `*const c_char` buffers are borrowed only for the duration of the call
//!   (caller keeps ownership, UTF-8, NUL-terminated);
//! * `bool` parameters cross as 1 byte (`_Bool`) — C# must marshal them as
//!   `UnmanagedType.U1`.

use std::ffi::{c_char, CStr, CString};
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::path::Path;
use std::ptr;

use serde::Serialize;

use crate::core::StickyCore;
use crate::errors::CoreError;
use crate::models::{NoteSort, QuickCaptureTarget};

// ---------------------------------------------------------------------------
// C-visible types
// ---------------------------------------------------------------------------

/// `[repr(C)]` mirror of the C# `FfiResult` struct (`int32 status; char* payload;`).
#[repr(C)]
pub struct FfiResult {
    /// 0 = OK (payload is UTF-8 JSON), >0 = `CoreError::code()` (payload is a message).
    pub status: i32,
    /// Always non-null UTF-8 CString; free with [`sn_string_free`].
    pub payload: *mut c_char,
}

/// Opaque pointer type handed back to C# as `*mut StickyCoreHandle`.
#[repr(C)]
pub struct StickyCoreHandle {
    _private: [u8; 0],
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/// Run `body`, converting any panic into `status = 5` instead of unwinding.
fn guarded<F: FnOnce() -> FfiResult>(body: F) -> FfiResult {
    match catch_unwind(AssertUnwindSafe(body)) {
        Ok(result) => result,
        Err(payload) => {
            let message = panic_message(&payload);
            FfiResult {
                status: 5,
                payload: into_c_string(format!("panic: {message}")),
            }
        }
    }
}

fn panic_message(payload: &Box<dyn std::any::Any + Send>) -> String {
    if let Some(s) = payload.downcast_ref::<&str>() {
        (*s).to_string()
    } else if let Some(s) = payload.downcast_ref::<String>() {
        s.clone()
    } else {
        "unknown panic".to_string()
    }
}

/// Serialize a success payload as UTF-8 JSON.
fn ok_json<T: Serialize>(value: &T) -> FfiResult {
    match serde_json::to_string(value) {
        Ok(json) => FfiResult {
            status: 0,
            payload: into_c_string(json),
        },
        Err(err) => FfiResult {
            status: 5,
            payload: into_c_string(format!("internal: serialize failed: {err}")),
        },
    }
}

/// Success for unit-returning operations — payload is `"{}"`, never null.
fn ok_unit() -> FfiResult {
    FfiResult {
        status: 0,
        payload: into_c_string("{}".to_string()),
    }
}

fn err(error: CoreError) -> FfiResult {
    FfiResult {
        status: error.code(),
        payload: into_c_string(error.to_string()),
    }
}

/// `CString::into_raw` — ownership transfers to the caller.
fn into_c_string(value: String) -> *mut c_char {
    match CString::new(value) {
        Ok(cstring) => cstring.into_raw(),
        // Interior NUL cannot be represented; degrade instead of returning null
        // (the contract requires payload != null).
        Err(_) => CString::new("internal: payload contained a NUL byte")
            .expect("static fallback has no interior NUL")
            .into_raw(),
    }
}

/// Borrow an input string for the duration of the call.
unsafe fn read_str(ptr: *const c_char) -> Result<String, CoreError> {
    if ptr.is_null() {
        return Err(CoreError::Validation("null string pointer".to_string()));
    }
    CStr::from_ptr(ptr)
        .to_str()
        .map(str::to_owned)
        .map_err(|err| CoreError::Validation(format!("invalid UTF-8: {err}")))
}

/// Borrow an optional input string; a null pointer means "absent".
unsafe fn read_opt_str(ptr: *const c_char) -> Result<Option<String>, CoreError> {
    if ptr.is_null() {
        Ok(None)
    } else {
        read_str(ptr).map(Some)
    }
}

unsafe fn core_ref<'a>(handle: *mut StickyCoreHandle) -> Result<&'a StickyCore, CoreError> {
    if handle.is_null() {
        return Err(CoreError::Validation("null StickyCore handle".to_string()));
    }
    Ok(&*(handle as *const StickyCore))
}

// ---------------------------------------------------------------------------
// Lifecycle & memory (3)
// ---------------------------------------------------------------------------

/// Opens (and migrates) the database. On success `*out_handle` receives a handle
/// that must be released with [`sn_core_close`]; on failure it is set to null.
#[no_mangle]
pub extern "C" fn sn_core_open(
    db_path_utf8: *const c_char,
    out_handle: *mut *mut StickyCoreHandle,
) -> FfiResult {
    guarded(|| {
        if out_handle.is_null() {
            return err(CoreError::Validation("null out_handle".to_string()));
        }
        // Never leave a stale/garbage handle behind if we fail below.
        unsafe { *out_handle = ptr::null_mut() };

        let path = match unsafe { read_str(db_path_utf8) } {
            Ok(path) => path,
            Err(error) => return err(error),
        };

        match StickyCore::open(Path::new(&path)) {
            Ok(core) => {
                let handle = Box::into_raw(Box::new(core)) as *mut StickyCoreHandle;
                unsafe { *out_handle = handle };
                ok_unit()
            }
            Err(error) => err(error),
        }
    })
}

/// Releases a handle obtained from [`sn_core_open`]. Call exactly once at shutdown,
/// after every other caller has stopped. Null is ignored.
#[no_mangle]
pub extern "C" fn sn_core_close(handle: *mut StickyCoreHandle) {
    if handle.is_null() {
        return;
    }
    // Never let a destructor panic escape into C#.
    let _ = catch_unwind(AssertUnwindSafe(|| unsafe {
        drop(Box::from_raw(handle as *mut StickyCore));
    }));
}

/// Frees a payload returned in [`FfiResult`]. Every non-null payload is freed
/// exactly once; null is ignored (idempotent against accidental double-free).
#[no_mangle]
pub extern "C" fn sn_string_free(payload: *mut c_char) {
    if payload.is_null() {
        return;
    }
    let _ = catch_unwind(AssertUnwindSafe(|| unsafe {
        drop(CString::from_raw(payload));
    }));
}

// ---------------------------------------------------------------------------
// Queries & mutations (13) — each returns JSON in `payload`
// ---------------------------------------------------------------------------

#[no_mangle]
pub extern "C" fn sn_get_bar_snapshot(handle: *mut StickyCoreHandle) -> FfiResult {
    guarded(|| {
        let core = match unsafe { core_ref(handle) } {
            Ok(core) => core,
            Err(error) => return err(error),
        };
        match core.get_bar_snapshot(None) {
            Ok(snapshot) => ok_json(&snapshot),
            Err(error) => err(error),
        }
    })
}

#[no_mangle]
pub extern "C" fn sn_list_group_notes(handle: *mut StickyCoreHandle, group_id: i64) -> FfiResult {
    guarded(|| {
        let core = match unsafe { core_ref(handle) } {
            Ok(core) => core,
            Err(error) => return err(error),
        };
        match core.list_group_notes(group_id) {
            Ok(notes) => ok_json(&notes),
            Err(error) => err(error),
        }
    })
}

#[no_mangle]
pub extern "C" fn sn_list_append_targets(handle: *mut StickyCoreHandle, limit: u32) -> FfiResult {
    guarded(|| {
        let core = match unsafe { core_ref(handle) } {
            Ok(core) => core,
            Err(error) => return err(error),
        };
        match core.list_append_targets(limit as usize) {
            Ok(notes) => ok_json(&notes),
            Err(error) => err(error),
        }
    })
}

#[no_mangle]
pub extern "C" fn sn_list_unpinned_notes(
    handle: *mut StickyCoreHandle,
    sort_mode: u32,
) -> FfiResult {
    guarded(|| {
        let core = match unsafe { core_ref(handle) } {
            Ok(core) => core,
            Err(error) => return err(error),
        };
        let sort = match NoteSort::from_code(sort_mode) {
            Some(sort) => sort,
            None => {
                return err(CoreError::Validation(format!(
                    "unknown sort mode {sort_mode}"
                )))
            }
        };
        match core.list_unpinned_notes(sort) {
            Ok(notes) => ok_json(&notes),
            Err(error) => err(error),
        }
    })
}

#[no_mangle]
pub extern "C" fn sn_get_note(
    handle: *mut StickyCoreHandle,
    note_id: i64,
    mark_opened: bool,
) -> FfiResult {
    guarded(|| {
        let core = match unsafe { core_ref(handle) } {
            Ok(core) => core,
            Err(error) => return err(error),
        };
        match core.get_note(note_id, mark_opened) {
            Ok(note) => ok_json(&note),
            Err(error) => err(error),
        }
    })
}

#[no_mangle]
pub extern "C" fn sn_create_note(
    handle: *mut StickyCoreHandle,
    has_group: bool,
    group_id: i64,
    body_utf8: *const c_char,
) -> FfiResult {
    guarded(|| {
        let core = match unsafe { core_ref(handle) } {
            Ok(core) => core,
            Err(error) => return err(error),
        };
        let body = match unsafe { read_str(body_utf8) } {
            Ok(body) => body,
            Err(error) => return err(error),
        };
        let group = if has_group { Some(group_id) } else { None };
        match core.create_note(group, &body) {
            Ok(note) => ok_json(&note),
            Err(error) => err(error),
        }
    })
}

#[no_mangle]
pub extern "C" fn sn_update_note_body(
    handle: *mut StickyCoreHandle,
    note_id: i64,
    body_utf8: *const c_char,
) -> FfiResult {
    guarded(|| {
        let core = match unsafe { core_ref(handle) } {
            Ok(core) => core,
            Err(error) => return err(error),
        };
        let body = match unsafe { read_str(body_utf8) } {
            Ok(body) => body,
            Err(error) => return err(error),
        };
        match core.update_note_body(note_id, &body) {
            Ok(summary) => ok_json(&summary),
            Err(error) => err(error),
        }
    })
}

/// Sets or clears a note's title. `title_utf8 == NULL` clears it; the core normalizes
/// (trim / collapse / cap 64 / empty → clear). Returns the updated `Note`.
#[no_mangle]
pub extern "C" fn sn_set_note_title(
    handle: *mut StickyCoreHandle,
    note_id: i64,
    title_utf8: *const c_char,
) -> FfiResult {
    guarded(|| {
        let core = match unsafe { core_ref(handle) } {
            Ok(core) => core,
            Err(error) => return err(error),
        };
        let title = match unsafe { read_opt_str(title_utf8) } {
            Ok(title) => title,
            Err(error) => return err(error),
        };
        match core.set_note_title(note_id, title.as_deref()) {
            Ok(note) => ok_json(&note),
            Err(error) => err(error),
        }
    })
}

#[no_mangle]
pub extern "C" fn sn_delete_note(handle: *mut StickyCoreHandle, note_id: i64) -> FfiResult {
    guarded(|| {
        let core = match unsafe { core_ref(handle) } {
            Ok(core) => core,
            Err(error) => return err(error),
        };
        match core.delete_note(note_id) {
            Ok(()) => ok_unit(),
            Err(error) => err(error),
        }
    })
}

#[no_mangle]
pub extern "C" fn sn_set_note_pinned(
    handle: *mut StickyCoreHandle,
    note_id: i64,
    pinned: bool,
) -> FfiResult {
    guarded(|| {
        let core = match unsafe { core_ref(handle) } {
            Ok(core) => core,
            Err(error) => return err(error),
        };
        match core.set_note_pinned(note_id, pinned) {
            Ok(summary) => ok_json(&summary),
            Err(error) => err(error),
        }
    })
}

#[no_mangle]
pub extern "C" fn sn_set_note_group(
    handle: *mut StickyCoreHandle,
    note_id: i64,
    has_group: bool,
    group_id: i64,
) -> FfiResult {
    guarded(|| {
        let core = match unsafe { core_ref(handle) } {
            Ok(core) => core,
            Err(error) => return err(error),
        };
        let group = if has_group { Some(group_id) } else { None };
        match core.set_note_group(note_id, group) {
            Ok(note) => ok_json(&note),
            Err(error) => err(error),
        }
    })
}

#[no_mangle]
pub extern "C" fn sn_create_group(
    handle: *mut StickyCoreHandle,
    name_utf8: *const c_char,
    pinned: bool,
) -> FfiResult {
    guarded(|| {
        let core = match unsafe { core_ref(handle) } {
            Ok(core) => core,
            Err(error) => return err(error),
        };
        let name = match unsafe { read_str(name_utf8) } {
            Ok(name) => name,
            Err(error) => return err(error),
        };
        match core.create_group(&name, pinned) {
            Ok(group) => ok_json(&group),
            Err(error) => err(error),
        }
    })
}

#[no_mangle]
pub extern "C" fn sn_rename_group(
    handle: *mut StickyCoreHandle,
    group_id: i64,
    name_utf8: *const c_char,
) -> FfiResult {
    guarded(|| {
        let core = match unsafe { core_ref(handle) } {
            Ok(core) => core,
            Err(error) => return err(error),
        };
        let name = match unsafe { read_str(name_utf8) } {
            Ok(name) => name,
            Err(error) => return err(error),
        };
        match core.rename_group(group_id, &name) {
            Ok(group) => ok_json(&group),
            Err(error) => err(error),
        }
    })
}

#[no_mangle]
pub extern "C" fn sn_delete_group(handle: *mut StickyCoreHandle, group_id: i64) -> FfiResult {
    guarded(|| {
        let core = match unsafe { core_ref(handle) } {
            Ok(core) => core,
            Err(error) => return err(error),
        };
        match core.delete_group(group_id) {
            Ok(()) => ok_unit(),
            Err(error) => err(error),
        }
    })
}

#[no_mangle]
pub extern "C" fn sn_set_group_pinned(
    handle: *mut StickyCoreHandle,
    group_id: i64,
    pinned: bool,
) -> FfiResult {
    guarded(|| {
        let core = match unsafe { core_ref(handle) } {
            Ok(core) => core,
            Err(error) => return err(error),
        };
        match core.set_group_pinned(group_id, pinned) {
            Ok(group) => ok_json(&group),
            Err(error) => err(error),
        }
    })
}

/// `target_json_utf8` is the serde form of [`QuickCaptureTarget`]:
/// `{"mode":"standalone"}`, `{"mode":"group","group_id":N}` or
/// `{"mode":"append","note_id":N}`.
#[no_mangle]
pub extern "C" fn sn_quick_capture(
    handle: *mut StickyCoreHandle,
    text_utf8: *const c_char,
    target_json_utf8: *const c_char,
) -> FfiResult {
    guarded(|| {
        let core = match unsafe { core_ref(handle) } {
            Ok(core) => core,
            Err(error) => return err(error),
        };
        let text = match unsafe { read_str(text_utf8) } {
            Ok(text) => text,
            Err(error) => return err(error),
        };
        let target_json = match unsafe { read_str(target_json_utf8) } {
            Ok(json) => json,
            Err(error) => return err(error),
        };
        let target: QuickCaptureTarget = match serde_json::from_str(&target_json) {
            Ok(target) => target,
            Err(error) => {
                return err(CoreError::Validation(format!("invalid quick capture target: {error}")))
            }
        };
        match core.quick_capture(&text, target) {
            Ok(note) => ok_json(&note),
            Err(error) => err(error),
        }
    })
}
