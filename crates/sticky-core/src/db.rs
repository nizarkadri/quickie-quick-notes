//! Connection setup, PRAGMAs, `user_version` migrations and row mapping (blueprint §5).

use std::path::Path;
use std::time::Duration;

use rusqlite::{Connection, Row};

use crate::errors::CoreError;
use crate::models::{title_preview, GroupSummary, Note, NoteSummary};

/// Bump together with a new `MIGRATIONS` entry.
pub const CURRENT_SCHEMA_VERSION: i32 = 1;

/// `busy_timeout` per blueprint §5.
const BUSY_TIMEOUT_MS: u64 = 5000;

/// Column order expected by [`note_from_row`].
pub const NOTE_COLUMNS: &str =
    "id, group_id, body, is_pinned, pinned_at_ms, created_at_ms, updated_at_ms, last_opened_at_ms, title";

/// Column order expected by [`summary_from_row`].
pub const SUMMARY_COLUMNS: &str =
    "id, group_id, body, is_pinned, pinned_at_ms, last_opened_at_ms, title";

/// Column order expected by [`group_from_row`].
pub const GROUP_COLUMNS: &str = "id, name, is_pinned, pinned_at_ms, created_at_ms, \
                                (SELECT COUNT(*) FROM notes WHERE notes.group_id = groups.id)";

const V1_SQL: &str = r#"
CREATE TABLE groups (
    id              INTEGER PRIMARY KEY,
    name            TEXT    NOT NULL CHECK (length(trim(name)) > 0),
    is_pinned       INTEGER NOT NULL DEFAULT 1 CHECK (is_pinned IN (0, 1)),
    pinned_at_ms    INTEGER NULL,
    created_at_ms   INTEGER NOT NULL,
    CHECK ((is_pinned = 1 AND pinned_at_ms IS NOT NULL) OR (is_pinned = 0 AND pinned_at_ms IS NULL))
);

CREATE TABLE notes (
    id                  INTEGER PRIMARY KEY,
    group_id            INTEGER NULL REFERENCES groups(id) ON DELETE CASCADE,
    body                TEXT    NOT NULL DEFAULT '',
    is_pinned           INTEGER NOT NULL DEFAULT 0 CHECK (is_pinned IN (0, 1)),
    pinned_at_ms        INTEGER NULL,
    created_at_ms       INTEGER NOT NULL,
    updated_at_ms       INTEGER NOT NULL,
    last_opened_at_ms   INTEGER NOT NULL,
    CHECK ((is_pinned = 1 AND pinned_at_ms IS NOT NULL) OR (is_pinned = 0 AND pinned_at_ms IS NULL))
);

-- Pinned rail: groups + notes ordered by pinned_at_ms ASC (newest on right)
CREATE INDEX idx_groups_pinned ON groups(pinned_at_ms ASC, id ASC) WHERE is_pinned = 1;
CREATE INDEX idx_notes_pinned  ON notes(pinned_at_ms ASC, id ASC)  WHERE is_pinned = 1;

-- Bar Recents: top 5 unpinned notes ordered by last_opened_at_ms DESC
CREATE INDEX idx_notes_recents ON notes(is_pinned, last_opened_at_ms DESC, id DESC);

-- Group vertical list: notes in group ordered by last_opened_at_ms DESC
CREATE INDEX idx_notes_group_opened ON notes(group_id, last_opened_at_ms DESC, id DESC);
"#;

/// `(version, sql)` steps applied in ascending order. `context_rules` is v2 — do NOT add here.
/// Additive columns are applied by [`ensure_additive_columns`] instead, without bumping `user_version`.
const MIGRATIONS: &[(i32, &str)] = &[(1, V1_SQL)];

/// Open the database, apply PRAGMAs and run pending migrations.
pub fn open(path: &Path) -> Result<Connection, CoreError> {
    let conn = Connection::open(path)?;
    conn.busy_timeout(Duration::from_millis(BUSY_TIMEOUT_MS))?;

    // `journal_mode = WAL` is persistent in the file and cannot run inside a transaction.
    conn.execute_batch(
        "PRAGMA journal_mode = WAL;
         PRAGMA synchronous = NORMAL;
         PRAGMA foreign_keys = ON;",
    )?;

    migrate(&conn)?;
    ensure_additive_columns(&conn)?;
    Ok(conn)
}

/// Bring `user_version` up to [`CURRENT_SCHEMA_VERSION`], idempotently.
pub fn migrate(conn: &Connection) -> Result<(), CoreError> {
    let current: i32 = conn.query_row("PRAGMA user_version", [], |r| r.get(0))?;

    if current > CURRENT_SCHEMA_VERSION {
        return Err(CoreError::UnsupportedSchema);
    }

    for (version, sql) in MIGRATIONS {
        if *version > current {
            apply_migration(conn, *version, sql)?;
        }
    }

    Ok(())
}

fn apply_migration(conn: &Connection, version: i32, sql: &str) -> Result<(), CoreError> {
    conn.execute_batch("BEGIN IMMEDIATE;")?;

    let applied = conn
        .execute_batch(sql)
        .and_then(|()| conn.execute_batch(&format!("PRAGMA user_version = {version};")));

    match applied {
        Ok(()) => {
            conn.execute_batch("COMMIT;")?;
            Ok(())
        }
        Err(err) => {
            let _ = conn.execute_batch("ROLLBACK;");
            Err(CoreError::Sqlite(err))
        }
    }
}

/// Additive columns that are applied idempotently without bumping `user_version`.
/// See `docs/adr/0001-additive-columns-without-version-bump.md`.
fn ensure_additive_columns(conn: &Connection) -> Result<(), CoreError> {
    if !has_column(conn, "notes", "title")? {
        conn.execute_batch("ALTER TABLE notes ADD COLUMN title TEXT NULL;")?;
    }
    Ok(())
}

fn has_column(conn: &Connection, table: &str, column: &str) -> Result<bool, CoreError> {
    let mut stmt = conn.prepare(&format!("PRAGMA table_info({table})"))?;
    let names = stmt
        .query_map([], |row| row.get::<_, String>(1))?
        .collect::<rusqlite::Result<Vec<_>>>()?;
    Ok(names.iter().any(|name| name == column))
}

// ---------------------------------------------------------------------------
// Row mapping — column order is fixed by the `*_COLUMNS` constants above.
// ---------------------------------------------------------------------------

pub fn note_from_row(row: &Row<'_>) -> rusqlite::Result<Note> {
    let body: String = row.get(2)?;
    Ok(Note {
        id: row.get(0)?,
        group_id: row.get(1)?,
        title_preview: title_preview(&body),
        title: row.get(8)?,
        body,
        is_pinned: row.get::<_, i64>(3)? != 0,
        pinned_at_ms: row.get(4)?,
        created_at_ms: row.get(5)?,
        updated_at_ms: row.get(6)?,
        last_opened_at_ms: row.get(7)?,
    })
}

pub fn summary_from_row(row: &Row<'_>) -> rusqlite::Result<NoteSummary> {
    let body: String = row.get(2)?;
    let title: Option<String> = row.get(6)?;
    let has_title = title.as_deref().is_some_and(|t| !t.trim().is_empty());
    Ok(NoteSummary {
        id: row.get(0)?,
        group_id: row.get(1)?,
        title_preview: title_preview(&body),
        title,
        is_empty: body.trim().is_empty() && !has_title,
        is_pinned: row.get::<_, i64>(3)? != 0,
        pinned_at_ms: row.get(4)?,
        last_opened_at_ms: row.get(5)?,
    })
}

pub fn group_from_row(row: &Row<'_>) -> rusqlite::Result<GroupSummary> {
    Ok(GroupSummary {
        id: row.get(0)?,
        name: row.get(1)?,
        is_pinned: row.get::<_, i64>(2)? != 0,
        pinned_at_ms: row.get(3)?,
        created_at_ms: row.get(4)?,
        note_count: row.get::<_, i64>(5)? as usize,
    })
}
