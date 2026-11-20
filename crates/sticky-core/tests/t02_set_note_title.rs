//! F2 gate — explicit note titles: additive column, normalization, display fallback, sort.
mod common;
use common::*;
use rusqlite::Connection;
use sticky_core::{CoreError, NoteSort, StickyCore};

#[test]
fn set_note_title_returns_note_with_title_and_unchanged_preview() {
    let db = new_db();
    let n = db.core.create_note(None, "first line\nsecond").unwrap();

    let updated = db.core.set_note_title(n.id, Some("Shopping")).unwrap();

    assert_eq!(updated.title.as_deref(), Some("Shopping"));
    assert_eq!(updated.title_preview, "first line");
    assert_eq!(updated.body, "first line\nsecond");
}

#[test]
fn clearing_title_falls_back_to_preview() {
    let db = new_db();
    let n = db.core.create_note(None, "first line").unwrap();
    db.core.set_note_title(n.id, Some("Shopping")).unwrap();

    let cleared = db.core.set_note_title(n.id, None).unwrap();

    assert_eq!(cleared.title, None);
    assert_eq!(cleared.title_preview, "first line");
}

#[test]
fn empty_and_whitespace_titles_normalize_to_null() {
    let db = new_db();
    let n = db.core.create_note(None, "body").unwrap();

    for raw in ["", "   ", "\n\t "] {
        let updated = db.core.set_note_title(n.id, Some(raw)).unwrap();
        assert_eq!(updated.title, None, "raw={raw:?}");
    }
}

#[test]
fn title_is_trimmed_and_collapsed() {
    let db = new_db();
    let n = db.core.create_note(None, "body").unwrap();

    let updated = db.core.set_note_title(n.id, Some("  Shopping   list \n now ")).unwrap();

    assert_eq!(updated.title.as_deref(), Some("Shopping list now"));
}

#[test]
fn title_is_capped_at_64_chars() {
    let db = new_db();
    let n = db.core.create_note(None, "body").unwrap();

    let updated = db.core.set_note_title(n.id, Some(&"x".repeat(100))).unwrap();

    assert_eq!(updated.title.as_deref().unwrap().chars().count(), 64);
}

#[test]
fn title_bumps_updated_at_but_not_last_opened() {
    let db = new_db();
    let n = db.core.create_note(None, "body").unwrap();
    tick();
    let before = db.core.get_note(n.id, false).unwrap();
    tick();

    let after = db.core.set_note_title(n.id, Some("T")).unwrap();

    assert!(after.updated_at_ms > before.updated_at_ms);
    assert_eq!(after.last_opened_at_ms, before.last_opened_at_ms);
}

#[test]
fn set_title_on_missing_note_is_not_found() {
    let db = new_db();
    assert!(matches!(
        db.core.set_note_title(999_999, Some("x")),
        Err(CoreError::NotFound(_))
    ));
}

#[test]
fn summary_includes_title() {
    let db = new_db();
    let n = db.core.create_note(None, "body").unwrap();
    db.core.set_note_title(n.id, Some("T")).unwrap();

    let s = db.core.get_bar_snapshot(None).unwrap();

    assert_eq!(s.recent_notes[0].title.as_deref(), Some("T"));
}

#[test]
fn alphabetical_sort_uses_title_when_present() {
    let db = new_db();
    let _apple = db.core.create_note(None, "apple").unwrap();
    tick();
    let zebra = db.core.create_note(None, "apple").unwrap();
    db.core.set_note_title(zebra.id, Some("Zebra")).unwrap();
    tick();
    let _mango = db.core.create_note(None, "mango").unwrap();

    let labels: Vec<String> = db
        .core
        .list_unpinned_notes(NoteSort::Alphabetical)
        .unwrap()
        .into_iter()
        .map(|n| n.title.clone().unwrap_or(n.title_preview))
        .collect();

    assert_eq!(labels, vec!["apple", "mango", "Zebra"]);
}

#[test]
fn title_only_note_is_not_empty() {
    let db = new_db();
    let n = db.core.create_note(None, "").unwrap();
    db.core.set_note_title(n.id, Some("Only title")).unwrap();

    let s = db.core.get_bar_snapshot(None).unwrap();

    assert!(!s.recent_notes[0].is_empty);
}

#[test]
fn old_db_gets_title_without_version_bump() {
    let db = new_db();
    let n = db.core.create_note(None, "legacy").unwrap();
    drop(db.core);

    {
        let conn = Connection::open(&db.path).unwrap();
        conn.execute_batch("ALTER TABLE notes DROP COLUMN title;").unwrap();
        let v: i64 = conn.query_row("PRAGMA user_version", [], |r| r.get(0)).unwrap();
        assert_eq!(v, 1);
        let count: i64 = conn
            .query_row(
                "SELECT COUNT(*) FROM pragma_table_info('notes') WHERE name = 'title'",
                [],
                |r| r.get(0),
            )
            .unwrap();
        assert_eq!(count, 0, "the simulated v1 DB must not have title yet");
    }

    let core2 = StickyCore::open(&db.path).expect("reopen old db");

    let conn = Connection::open(&db.path).unwrap();
    let v: i64 = conn.query_row("PRAGMA user_version", [], |r| r.get(0)).unwrap();
    assert_eq!(v, 1, "adding title must not bump user_version");
    let count: i64 = conn
        .query_row(
            "SELECT COUNT(*) FROM pragma_table_info('notes') WHERE name = 'title'",
            [],
            |r| r.get(0),
        )
        .unwrap();
    assert_eq!(count, 1, "reopen must add the title column");
    drop(conn);

    let note = core2.get_note(n.id, false).unwrap();
    assert_eq!(note.title, None, "the legacy row gets NULL");
    let updated = core2.set_note_title(n.id, Some("New")).unwrap();
    assert_eq!(updated.title.as_deref(), Some("New"));
}

#[test]
fn reopen_preserves_title() {
    let db = new_db();
    let n = db.core.create_note(None, "body").unwrap();
    db.core.set_note_title(n.id, Some("Persisted")).unwrap();
    drop(db.core);

    let core2 = StickyCore::open(&db.path).unwrap();

    assert_eq!(
        core2.get_note(n.id, false).unwrap().title.as_deref(),
        Some("Persisted")
    );
}