//! TASK 2 gate - schema, migrations, v1 scope boundary.
mod common;
use common::*;
use rusqlite::Connection;
use sticky_core::{CoreError, StickyCore, WindowContext};

#[test]
fn open_creates_schema_version_1() {
    let db = new_db();
    let conn = Connection::open(&db.path).unwrap();
    let v: i64 = conn.query_row("PRAGMA user_version", [], |r| r.get(0)).unwrap();
    assert_eq!(v, 1);
}

#[test]
fn wal_mode_is_enabled() {
    let db = new_db();
    let conn = Connection::open(&db.path).unwrap();
    let mode: String = conn.query_row("PRAGMA journal_mode", [], |r| r.get(0)).unwrap();
    assert_eq!(mode.to_lowercase(), "wal");
}

#[test]
fn v1_has_groups_and_notes_but_no_context_rules_table() {
    let db = new_db();
    let conn = Connection::open(&db.path).unwrap();
    let mut stmt = conn
        .prepare("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'")
        .unwrap();
    let tables: Vec<String> = stmt.query_map([], |r| r.get(0)).unwrap().map(|r| r.unwrap()).collect();
    assert!(tables.contains(&"groups".to_string()));
    assert!(tables.contains(&"notes".to_string()));
    assert!(
        !tables.contains(&"context_rules".to_string()),
        "context_rules is a v2 seam and must NOT exist in v1"
    );
}

#[test]
fn reopening_is_idempotent_and_keeps_data() {
    let db = new_db();
    let g = db.core.create_group("Work", true).unwrap();
    let n = db.core.create_note(Some(g.id), "keep me").unwrap();
    let path = db.path.clone();
    drop(db.core);

    let core2 = StickyCore::open(&path).expect("reopen");
    assert_eq!(core2.get_note(n.id, false).unwrap().body, "keep me");
    drop(core2);

    let core3 = StickyCore::open(&path).expect("reopen again");
    assert_eq!(core3.get_bar_snapshot(None).unwrap().all_groups.len(), 1);
}

#[test]
fn newer_schema_version_is_rejected() {
    let db = new_db();
    let path = db.path.clone();
    drop(db.core);
    Connection::open(&path).unwrap().execute_batch("PRAGMA user_version = 99;").unwrap();

    match StickyCore::open(&path) {
        Err(CoreError::UnsupportedSchema) => {}
        Err(e) => panic!("wrong error: {e}"),
        Ok(_) => panic!("must refuse a database from a newer schema"),
    }
}

#[test]
fn fresh_database_snapshot_is_empty() {
    let db = new_db();
    let s = db.core.get_bar_snapshot(None).unwrap();
    assert!(s.pinned_items.is_empty());
    assert!(s.recent_notes.is_empty());
    assert!(s.all_groups.is_empty());
}

#[test]
fn snapshot_context_seam_exists_and_is_ignored_in_v1() {
    let db = new_db();
    db.core.create_note(None, "a").unwrap();
    db.core.create_group("G", true).unwrap();
    let ctx = WindowContext {
        exe_name: Some("WindowsTerminal.exe".into()),
        cwd: Some("C:\\repo".into()),
        window_title: Some("pwsh".into()),
    };
    let without = serde_json::to_string(&db.core.get_bar_snapshot(None).unwrap()).unwrap();
    let with = serde_json::to_string(&db.core.get_bar_snapshot(Some(&ctx)).unwrap()).unwrap();
    assert_eq!(without, with, "v1 must ignore context entirely");
}
