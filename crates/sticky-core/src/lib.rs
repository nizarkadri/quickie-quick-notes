//! `sticky-core` — platform-neutral core for StickyNotes.
//!
//! Task 1 (workspace skeleton) keeps every platform-neutral type in this single
//! file so the crate compiles standalone. Task 2 splits the definitions out into
//! `models.rs`, `errors.rs`, `db.rs` and `core.rs` and re-exports them from here;
//! the public paths in the list below are the contract used by both the Rust
//! integration tests (`crates/sticky-core/tests/*.rs`) and the FFI layer.
//!
//!   sticky_core::{Note, NoteSummary, GroupSummary, PinnedItem, BarSnapshot,
//!                 QuickCaptureTarget, WindowContext}
//!   sticky_core::{StickyCore, CoreError}

use serde::{Deserialize, Serialize};
use std::sync::Mutex;

// ---------------------------------------------------------------------------
// Models (blueprint §6)
// ---------------------------------------------------------------------------

#[derive(Debug, Serialize, Deserialize, Clone)]
pub struct Note {
    pub id: i64,
    pub group_id: Option<i64>,
    pub body: String,
    /// Derived: first non-empty line trimmed to <=28 chars, fallback "New note".
    pub title_preview: String,
    pub is_pinned: bool,
    pub pinned_at_ms: Option<i64>,
    pub created_at_ms: i64,
    pub updated_at_ms: i64,
    pub last_opened_at_ms: i64,
}

#[derive(Debug, Serialize, Deserialize, Clone)]
pub struct NoteSummary {
    pub id: i64,
    pub group_id: Option<i64>,
    pub title_preview: String,
    /// Used by the UI to decide whether delete needs confirmation.
    pub is_empty: bool,
    pub is_pinned: bool,
    pub pinned_at_ms: Option<i64>,
    pub last_opened_at_ms: i64,
}

#[derive(Debug, Serialize, Deserialize, Clone)]
pub struct GroupSummary {
    pub id: i64,
    pub name: String,
    /// Used by the UI delete confirmation ("Delete 'Work' and 3 notes?").
    pub note_count: usize,
    pub is_pinned: bool,
    pub pinned_at_ms: Option<i64>,
    pub created_at_ms: i64,
}

#[derive(Debug, Serialize, Deserialize, Clone)]
#[serde(tag = "kind", rename_all = "snake_case")]
pub enum PinnedItem {
    Group(GroupSummary),
    Note(NoteSummary),
}

#[derive(Debug, Serialize, Deserialize, Clone)]
pub struct BarSnapshot {
    /// Interleaved by `pinned_at_ms` ASC (newest on the right).
    pub pinned_items: Vec<PinnedItem>,
    /// Max 5, `is_pinned = false`, `last_opened_at_ms` DESC.
    pub recent_notes: Vec<NoteSummary>,
    /// Ordered by `created_at_ms` ASC (powers `Groups ▾` and the QuickNote picker).
    pub all_groups: Vec<GroupSummary>,
}

#[derive(Debug, Serialize, Deserialize, Clone)]
#[serde(tag = "mode", rename_all = "snake_case")]
pub enum QuickCaptureTarget {
    Standalone,
    Group { group_id: i64 },
    Append { note_id: i64 },
}

#[derive(Debug, Default, Clone)]
pub struct WindowContext {
    pub exe_name: Option<String>,
    pub cwd: Option<String>,
    pub window_title: Option<String>,
}

// ---------------------------------------------------------------------------
// Errors (blueprint §6) — status codes used by ffi.rs
// ---------------------------------------------------------------------------

#[derive(Debug, thiserror::Error)]
pub enum CoreError {
    /// status = 1
    #[error("not found: {0}")]
    NotFound(String),
    /// status = 2
    #[error("validation: {0}")]
    Validation(String),
    /// status = 3
    #[error("sqlite error: {0}")]
    Sqlite(#[from] rusqlite::Error),
    /// status = 4
    #[error("unsupported schema")]
    UnsupportedSchema,
    /// status = 5
    #[error("internal panic: {0}")]
    Internal(String),
}

// ---------------------------------------------------------------------------
// Thread-safe API surface (blueprint §6).
// Task 1 only wires the type + constructor so the workspace compiles; Task 2
// implements schema v1, CRUD, pin ordering, recents and quick-capture.
// ---------------------------------------------------------------------------

pub struct StickyCore {
    #[allow(dead_code)] // read by db.rs/core.rs in Task 2
    conn: Mutex<rusqlite::Connection>,
}

impl StickyCore {
    pub fn open(_db_path: &std::path::Path) -> Result<Self, CoreError> {
        // TODO(Task 2): open the SQLite file, apply pragmas, migrate to user_version = 1.
        Err(CoreError::Internal(
            "sticky-core: implementation lands in Task 2".to_string(),
        ))
    }
}
