//! TASK 2 gate - quick-note routing: standalone, add to group, append.
mod common;
use common::*;
use sticky_core::{CoreError, QuickCaptureTarget};

#[test]
fn standalone_creates_an_ungrouped_note_that_shows_in_recents() {
    let db = new_db();
    let n = db.core.quick_capture("hi", QuickCaptureTarget::Standalone).unwrap();
    assert_eq!(n.body, "hi");
    assert_eq!(n.group_id, None);
    assert_eq!(recent_ids(&db.core), vec![n.id]);
}

#[test]
fn group_target_creates_the_note_inside_that_group() {
    let db = new_db();
    let g = db.core.create_group("Cheats", true).unwrap();
    let n = db.core.quick_capture("git reflog", QuickCaptureTarget::Group { group_id: g.id }).unwrap();
    assert_eq!(n.group_id, Some(g.id));
    assert_eq!(db.core.list_group_notes(g.id).unwrap().len(), 1);
}

#[test]
fn group_target_with_missing_group_fails_and_writes_nothing() {
    let db = new_db();
    assert!(db.core.quick_capture("x", QuickCaptureTarget::Group { group_id: 777 }).is_err());
    assert!(recent_ids(&db.core).is_empty());
}

#[test]
fn append_to_non_empty_note_adds_exactly_one_newline() {
    let db = new_db();
    let target = db.core.create_note(None, "line1").unwrap();
    let out = db.core.quick_capture("line2", QuickCaptureTarget::Append { note_id: target.id }).unwrap();
    assert_eq!(out.body, "line1\nline2");
    assert_eq!(db.core.get_note(target.id, false).unwrap().body, "line1\nline2");
}

#[test]
fn append_to_empty_note_has_no_leading_newline() {
    let db = new_db();
    let target = db.core.create_note(None, "").unwrap();
    let out = db.core.quick_capture("first", QuickCaptureTarget::Append { note_id: target.id }).unwrap();
    assert_eq!(out.body, "first");
}

#[test]
fn repeated_appends_stack_one_line_each() {
    let db = new_db();
    let t = db.core.create_note(None, "a").unwrap();
    db.core.quick_capture("b", QuickCaptureTarget::Append { note_id: t.id }).unwrap();
    db.core.quick_capture("c", QuickCaptureTarget::Append { note_id: t.id }).unwrap();
    assert_eq!(db.core.get_note(t.id, false).unwrap().body, "a\nb\nc");
}

#[test]
fn append_counts_as_opening_so_target_moves_to_top_of_recents() {
    let db = new_db();
    let target = db.core.create_note(None, "target").unwrap();
    tick();
    let newer = db.core.create_note(None, "newer").unwrap();
    assert_eq!(recent_ids(&db.core)[0], newer.id);
    tick();
    db.core.quick_capture("more", QuickCaptureTarget::Append { note_id: target.id }).unwrap();
    assert_eq!(recent_ids(&db.core)[0], target.id);
}

#[test]
fn append_updates_updated_timestamp() {
    let db = new_db();
    let t = db.core.create_note(None, "a").unwrap();
    tick();
    db.core.quick_capture("b", QuickCaptureTarget::Append { note_id: t.id }).unwrap();
    assert!(db.core.get_note(t.id, false).unwrap().updated_at_ms > t.updated_at_ms);
}

#[test]
fn append_keeps_group_and_pin_state() {
    let db = new_db();
    let g = db.core.create_group("G", true).unwrap();
    let t = db.core.create_note(Some(g.id), "a").unwrap();
    db.core.set_note_pinned(t.id, true).unwrap();
    db.core.quick_capture("b", QuickCaptureTarget::Append { note_id: t.id }).unwrap();
    let after = db.core.get_note(t.id, false).unwrap();
    assert_eq!(after.group_id, Some(g.id));
    assert!(after.is_pinned);
}

#[test]
fn append_to_missing_note_is_not_found_and_creates_nothing() {
    let db = new_db();
    let r = db.core.quick_capture("x", QuickCaptureTarget::Append { note_id: 555 });
    assert!(matches!(r, Err(CoreError::NotFound(_))));
    assert!(recent_ids(&db.core).is_empty());
}

#[test]
fn append_handles_unicode() {
    let db = new_db();
    let t = db.core.create_note(None, "日本").unwrap();
    let out = db.core.quick_capture("🌍 ok", QuickCaptureTarget::Append { note_id: t.id }).unwrap();
    assert_eq!(out.body, "日本\n🌍 ok");
}
