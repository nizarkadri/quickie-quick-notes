//! Platform-neutral DTOs shared with C# via UTF-8 JSON (blueprint §6).

use serde::{Deserialize, Serialize};

/// Longest `title_preview` the UI shows in a chip / list row.
pub const TITLE_PREVIEW_MAX_CHARS: usize = 28;

/// Fallback preview for an empty / whitespace-only note.
pub const TITLE_PREVIEW_FALLBACK: &str = "New note";

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

/// Reserved v2 seam: v1 ignores it entirely (blueprint §5).
#[derive(Debug, Default, Clone)]
pub struct WindowContext {
    pub exe_name: Option<String>,
    pub cwd: Option<String>,
    pub window_title: Option<String>,
}

/// First non-empty trimmed line, capped at [`TITLE_PREVIEW_MAX_CHARS`] chars.
pub fn title_preview(body: &str) -> String {
    match body.lines().map(str::trim).find(|line| !line.is_empty()) {
        Some(line) => line.chars().take(TITLE_PREVIEW_MAX_CHARS).collect(),
        None => TITLE_PREVIEW_FALLBACK.to_string(),
    }
}
