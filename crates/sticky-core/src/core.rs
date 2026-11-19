//! Thread-safe domain API: a single `Mutex<Connection>` behind arbitrary C# threads.

use std::cmp::Reverse;
use std::path::Path;
use std::sync::{Mutex, MutexGuard};
use std::time::{SystemTime, UNIX_EPOCH};

use rusqlite::{params, Connection, OptionalExtension};

use crate::db::{self, GROUP_COLUMNS, NOTE_COLUMNS, SUMMARY_COLUMNS};
use crate::errors::CoreError;
use crate::models::{
    BarSnapshot, GroupSummary, Note, NoteSort, NoteSummary, PinnedItem, QuickCaptureTarget,
    WindowContext,
};

/// Bar Recents: "Max 5" (blueprint §6).
const RECENTS_LIMIT: i64 = 5;

pub struct StickyCore {
    conn: Mutex<Connection>,
}

impl StickyCore {
    pub fn open(db_path: &Path) -> Result<Self, CoreError> {
        Ok(Self {
            conn: Mutex::new(db::open(db_path)?),
        })
    }

    fn lock(&self) -> Result<MutexGuard<'_, Connection>, CoreError> {
        self.conn
            .lock()
            .map_err(|_| CoreError::Internal("connection mutex poisoned".to_string()))
    }

    // -----------------------------------------------------------------------
    // Bar snapshot
    // -----------------------------------------------------------------------

    /// v1 ignores `_ctx` entirely; the parameter is the reserved v2 seam (§5).
    pub fn get_bar_snapshot(&self, _ctx: Option<&WindowContext>) -> Result<BarSnapshot, CoreError> {
        let conn = self.lock()?;

        // Pinned rail: groups + notes interleaved by pin time (oldest left, newest right).
        let mut stmt = conn.prepare(
            "SELECT kind, id, pinned_at_ms FROM (
                 SELECT 'group' AS kind, id AS id, pinned_at_ms AS pinned_at_ms
                   FROM groups WHERE is_pinned = 1
                 UNION ALL
                 SELECT 'note' AS kind, id AS id, pinned_at_ms AS pinned_at_ms
                   FROM notes WHERE is_pinned = 1
             )
             ORDER BY pinned_at_ms ASC, id ASC",
        )?;
        let rail: Vec<(String, i64)> = stmt
            .query_map([], |row| Ok((row.get(0)?, row.get(1)?)))?
            .collect::<rusqlite::Result<_>>()?;
        drop(stmt);

        let mut pinned_items = Vec::with_capacity(rail.len());
        for (kind, id) in rail {
            if kind == "group" {
                pinned_items.push(PinnedItem::Group(fetch_group(&conn, id)?));
            } else {
                pinned_items.push(PinnedItem::Note(fetch_summary(&conn, id)?));
            }
        }

        // Recents: unpinned notes only (pinned ones already live on the rail).
        let recents_sql = format!(
            "SELECT {SUMMARY_COLUMNS} FROM notes
              WHERE is_pinned = 0
              ORDER BY last_opened_at_ms DESC, id DESC
              LIMIT ?1"
        );
        let mut stmt = conn.prepare(&recents_sql)?;
        let recent_notes = stmt
            .query_map([RECENTS_LIMIT], db::summary_from_row)?
            .collect::<rusqlite::Result<Vec<_>>>()?;
        drop(stmt);

        // Every group, oldest first (powers `Groups ▾` and the QuickNote picker).
        let groups_sql = format!("SELECT {GROUP_COLUMNS} FROM groups ORDER BY created_at_ms ASC, id ASC");
        let mut stmt = conn.prepare(&groups_sql)?;
        let all_groups = stmt
            .query_map([], db::group_from_row)?
            .collect::<rusqlite::Result<Vec<_>>>()?;
        drop(stmt);

        Ok(BarSnapshot {
            pinned_items,
            recent_notes,
            all_groups,
        })
    }

    // -----------------------------------------------------------------------
    // Notes
    // -----------------------------------------------------------------------

    /// Notes of one group, most recently opened first.
    pub fn list_group_notes(&self, group_id: i64) -> Result<Vec<NoteSummary>, CoreError> {
        let conn = self.lock()?;
        require_group(&conn, group_id)?;

        let sql = format!(
            "SELECT {SUMMARY_COLUMNS} FROM notes
              WHERE group_id = ?1
              ORDER BY last_opened_at_ms DESC, id DESC"
        );
        let mut stmt = conn.prepare(&sql)?;
        let notes = stmt
            .query_map([group_id], db::summary_from_row)?
            .collect::<rusqlite::Result<Vec<_>>>()?;
        Ok(notes)
    }

    /// Append picker: every note (pinned included), most recently opened first.
    pub fn list_append_targets(&self, limit: usize) -> Result<Vec<NoteSummary>, CoreError> {
        let conn = self.lock()?;

        let sql = format!(
            "SELECT {SUMMARY_COLUMNS} FROM notes
              ORDER BY last_opened_at_ms DESC, id DESC
              LIMIT ?1"
        );
        let mut stmt = conn.prepare(&sql)?;
        let notes = stmt
            .query_map([limit as i64], db::summary_from_row)?
            .collect::<rusqlite::Result<Vec<_>>>()?;
        Ok(notes)
    }

    ///
    /// Every UNPINNED note for the "All notes" dropdown. Recents only shows five, so without this a
    /// note that falls out of the rail and off the recents is unreachable unless it is grouped.
    ///
    /// Pinned notes are excluded on purpose: they are already on the pinned rail, and the dropdown
    /// exists to reach the notes the bar is NOT showing.
    ///
    /// Sorted in Rust rather than SQL because the A→Z key is `title_preview` — the first non-empty line
    /// of the body — which is derived, not a column. Ties fall back to newest-first so the order is
    /// stable between refreshes.
    pub fn list_unpinned_notes(&self, sort: NoteSort) -> Result<Vec<NoteSummary>, CoreError> {
        let conn = self.lock()?;

        let sql = format!("SELECT {SUMMARY_COLUMNS} FROM notes WHERE is_pinned = 0");
        let mut stmt = conn.prepare(&sql)?;
        let mut notes = stmt
            .query_map([], db::summary_from_row)?
            .collect::<rusqlite::Result<Vec<_>>>()?;
        drop(stmt);

        match sort {
            NoteSort::Recent => {
                notes.sort_by_cached_key(|n| (Reverse(n.last_opened_at_ms), Reverse(n.id)))
            }
            NoteSort::Alphabetical => notes.sort_by_cached_key(|n| {
                (
                    n.title_preview.to_lowercase(),
                    Reverse(n.last_opened_at_ms),
                    Reverse(n.id),
                )
            }),
        }

        Ok(notes)
    }

    pub fn get_note(&self, note_id: i64, mark_opened: bool) -> Result<Note, CoreError> {
        let conn = self.lock()?;

        if mark_opened {
            let touched = conn.execute(
                "UPDATE notes SET last_opened_at_ms = ?2 WHERE id = ?1",
                params![note_id, now_ms()],
            )?;
            if touched == 0 {
                return Err(not_found_note(note_id));
            }
        }

        fetch_note(&conn, note_id)
    }

    /// Creates a note and counts it as opened (`last_opened_at_ms = now`).
    pub fn create_note(&self, group_id: Option<i64>, body: &str) -> Result<Note, CoreError> {
        let conn = self.lock()?;
        if let Some(gid) = group_id {
            require_group(&conn, gid)?;
        }
        insert_note(&conn, group_id, body)
    }

    /// Autosave: touches `updated_at_ms` only — editing is not opening.
    pub fn update_note_body(&self, note_id: i64, body: &str) -> Result<NoteSummary, CoreError> {
        let conn = self.lock()?;
        let updated = conn.execute(
            "UPDATE notes SET body = ?2, updated_at_ms = ?3 WHERE id = ?1",
            params![note_id, body, now_ms()],
        )?;
        if updated == 0 {
            return Err(not_found_note(note_id));
        }
        fetch_summary(&conn, note_id)
    }

    pub fn delete_note(&self, note_id: i64) -> Result<(), CoreError> {
        let conn = self.lock()?;
        let deleted = conn.execute("DELETE FROM notes WHERE id = ?1", [note_id])?;
        if deleted == 0 {
            return Err(not_found_note(note_id));
        }
        Ok(())
    }

    pub fn set_note_pinned(&self, note_id: i64, pinned: bool) -> Result<NoteSummary, CoreError> {
        let conn = self.lock()?;
        let updated = if pinned {
            conn.execute(
                "UPDATE notes SET is_pinned = 1, pinned_at_ms = ?2 WHERE id = ?1",
                params![note_id, now_ms()],
            )?
        } else {
            conn.execute(
                "UPDATE notes SET is_pinned = 0, pinned_at_ms = NULL WHERE id = ?1",
                [note_id],
            )?
        };
        if updated == 0 {
            return Err(not_found_note(note_id));
        }
        fetch_summary(&conn, note_id)
    }

    /// Moves a note into `group_id` (or `None` for standalone). Validates the target group
    /// exists; a move is not an open and does not touch `last_opened_at_ms`/`updated_at_ms`.
    pub fn set_note_group(&self, note_id: i64, group_id: Option<i64>) -> Result<Note, CoreError> {
        let conn = self.lock()?;
        if let Some(gid) = group_id {
            require_group(&conn, gid)?;
        }
        let updated = conn.execute(
            "UPDATE notes SET group_id = ?2 WHERE id = ?1",
            params![note_id, group_id],
        )?;
        if updated == 0 {
            return Err(not_found_note(note_id));
        }
        fetch_note(&conn, note_id)
    }

    // -----------------------------------------------------------------------
    // Groups
    // -----------------------------------------------------------------------

    pub fn create_group(&self, name: &str, pinned: bool) -> Result<GroupSummary, CoreError> {
        let name = validated_name(name)?;
        let conn = self.lock()?;
        let now = now_ms();
        let (flag, pinned_at) = if pinned { (1, Some(now)) } else { (0, None) };

        conn.execute(
            "INSERT INTO groups (name, is_pinned, pinned_at_ms, created_at_ms)
             VALUES (?1, ?2, ?3, ?4)",
            params![name, flag, pinned_at, now],
        )?;
        fetch_group(&conn, conn.last_insert_rowid())
    }

    pub fn rename_group(&self, group_id: i64, name: &str) -> Result<GroupSummary, CoreError> {
        let name = validated_name(name)?;
        let conn = self.lock()?;

        let renamed = conn.execute(
            "UPDATE groups SET name = ?2 WHERE id = ?1",
            params![group_id, name],
        )?;
        if renamed == 0 {
            return Err(not_found_group(group_id));
        }
        fetch_group(&conn, group_id)
    }

    /// `notes.group_id` is `ON DELETE CASCADE`, so member notes (and their pins) go with it.
    pub fn delete_group(&self, group_id: i64) -> Result<(), CoreError> {
        let conn = self.lock()?;
        let deleted = conn.execute("DELETE FROM groups WHERE id = ?1", [group_id])?;
        if deleted == 0 {
            return Err(not_found_group(group_id));
        }
        Ok(())
    }

    pub fn set_group_pinned(&self, group_id: i64, pinned: bool) -> Result<GroupSummary, CoreError> {
        let conn = self.lock()?;
        let updated = if pinned {
            conn.execute(
                "UPDATE groups SET is_pinned = 1, pinned_at_ms = ?2 WHERE id = ?1",
                params![group_id, now_ms()],
            )?
        } else {
            conn.execute(
                "UPDATE groups SET is_pinned = 0, pinned_at_ms = NULL WHERE id = ?1",
                [group_id],
            )?
        };
        if updated == 0 {
            return Err(not_found_group(group_id));
        }
        fetch_group(&conn, group_id)
    }

    // -----------------------------------------------------------------------
    // Quick capture — one transaction per call
    // -----------------------------------------------------------------------

    pub fn quick_capture(
        &self,
        text: &str,
        target: QuickCaptureTarget,
    ) -> Result<Note, CoreError> {
        let mut guard = self.lock()?;
        let tx = guard.transaction()?;

        let note = match target {
            QuickCaptureTarget::Standalone => insert_note(&tx, None, text)?,
            QuickCaptureTarget::Group { group_id } => {
                require_group(&tx, group_id)?;
                insert_note(&tx, Some(group_id), text)?
            }
            QuickCaptureTarget::Append { note_id } => append_to_note(&tx, note_id, text)?,
        };

        tx.commit()?;
        Ok(note)
    }
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

fn now_ms() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_millis() as i64)
        .unwrap_or(0)
}

fn validated_name(name: &str) -> Result<String, CoreError> {
    let trimmed = name.trim();
    if trimmed.is_empty() {
        return Err(CoreError::Validation("group name must not be blank".to_string()));
    }
    Ok(trimmed.to_string())
}

fn not_found_note(note_id: i64) -> CoreError {
    CoreError::NotFound(format!("note id {note_id}"))
}

fn not_found_group(group_id: i64) -> CoreError {
    CoreError::NotFound(format!("group id {group_id}"))
}

fn require_group(conn: &Connection, group_id: i64) -> Result<(), CoreError> {
    let found: Option<i64> = conn
        .query_row("SELECT id FROM groups WHERE id = ?1", [group_id], |r| r.get(0))
        .optional()?;
    match found {
        Some(_) => Ok(()),
        None => Err(not_found_group(group_id)),
    }
}

fn fetch_note(conn: &Connection, note_id: i64) -> Result<Note, CoreError> {
    let sql = format!("SELECT {NOTE_COLUMNS} FROM notes WHERE id = ?1");
    conn.query_row(&sql, [note_id], db::note_from_row)
        .optional()?
        .ok_or_else(|| not_found_note(note_id))
}

fn fetch_summary(conn: &Connection, note_id: i64) -> Result<NoteSummary, CoreError> {
    let sql = format!("SELECT {SUMMARY_COLUMNS} FROM notes WHERE id = ?1");
    conn.query_row(&sql, [note_id], db::summary_from_row)
        .optional()?
        .ok_or_else(|| not_found_note(note_id))
}

fn fetch_group(conn: &Connection, group_id: i64) -> Result<GroupSummary, CoreError> {
    let sql = format!("SELECT {GROUP_COLUMNS} FROM groups WHERE id = ?1");
    conn.query_row(&sql, [group_id], db::group_from_row)
        .optional()?
        .ok_or_else(|| not_found_group(group_id))
}

fn insert_note(conn: &Connection, group_id: Option<i64>, body: &str) -> Result<Note, CoreError> {
    let now = now_ms();
    conn.execute(
        "INSERT INTO notes (group_id, body, is_pinned, pinned_at_ms,
                            created_at_ms, updated_at_ms, last_opened_at_ms)
         VALUES (?1, ?2, 0, NULL, ?3, ?3, ?3)",
        params![group_id, body, now],
    )?;
    fetch_note(conn, conn.last_insert_rowid())
}

/// Append rule (§6): no leading newline when the existing body is empty.
fn append_to_note(conn: &Connection, note_id: i64, text: &str) -> Result<Note, CoreError> {
    let existing: String = conn
        .query_row("SELECT body FROM notes WHERE id = ?1", [note_id], |r| r.get(0))
        .optional()?
        .ok_or_else(|| not_found_note(note_id))?;

    let new_body = if existing.is_empty() {
        text.to_string()
    } else {
        format!("{existing}\n{text}")
    };

    // Appending counts as opening, so the target jumps to the top of Recents.
    let now = now_ms();
    conn.execute(
        "UPDATE notes SET body = ?2, updated_at_ms = ?3, last_opened_at_ms = ?3 WHERE id = ?1",
        params![note_id, new_body, now],
    )?;

    fetch_note(conn, note_id)
}
