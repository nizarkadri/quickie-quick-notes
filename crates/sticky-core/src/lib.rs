//! `sticky-core` — platform-neutral core for StickyNotes.
//!
//! Public paths (contract for `tests/*.rs` and the FFI layer):
//!
//! ```text
//! sticky_core::{Note, NoteSummary, GroupSummary, PinnedItem, BarSnapshot,
//!               QuickCaptureTarget, WindowContext}
//! sticky_core::{StickyCore, CoreError}
//! ```
//!
//! Layout (blueprint §4):
//! * `models.rs` — platform-neutral DTOs (serde, shared with C# as UTF-8 JSON)
//! * `errors.rs` — `CoreError` + FFI status codes
//! * `db.rs`     — PRAGMAs, `user_version` migrations and row mapping
//! * `core.rs`   — thread-safe `StickyCore` domain API
//! * `ffi.rs`    — `extern "C"` ABI (Task 3)

pub mod core;
pub mod db;
pub mod errors;
pub mod ffi;
pub mod models;

pub use crate::core::StickyCore;
pub use crate::errors::CoreError;
pub use crate::models::{
    BarSnapshot, GroupSummary, Note, NoteSort, NoteSummary, PinnedItem, QuickCaptureTarget,
    WindowContext,
};
