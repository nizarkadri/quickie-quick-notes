//! TASK 2 gate - note and group CRUD, validation, cascade rules.
mod common;
use common::*;
use sticky_core::CoreError;

#[test]
fn create_standalone_note() {
    let db = new_db();
    let n = db.core.create_note(None, "hello").unwrap();
    assert!(n.id > 0);
    assert_eq!(n.group_id, None);
    assert_eq!(n.body, "hello");
    assert!(!n.is_pinned);
    assert_eq!(n.pinned_at_ms, None);
    assert!(n.created_at_ms > 0);
    assert!(n.updated_at_ms >= n.created_at_ms);
    assert!(n.last_opened_at_ms >= n.created_at_ms);
}

#[test]
fn create_note_in_group() {
    let db = new_db();
    let g = db.core.create_group("Work", true).unwrap();
    let n = db.core.create_note(Some(g.id), "x").unwrap();
    assert_eq!(n.group_id, Some(g.id));
}

#[test]
fn create_note_in_missing_group_fails() {
    let db = new_db();
    assert!(db.core.create_note(Some(99_999), "x").is_err());
}

#[test]
fn get_missing_note_is_not_found() {
    let db = new_db();
    assert!(matches!(db.core.get_note(424_242, false), Err(CoreError::NotFound(_))));
}

#[test]
fn title_preview_uses_first_non_empty_trimmed_line() {
    let db = new_db();
    let n = db.core.create_note(None, "\n\n   first line  \nsecond").unwrap();
    assert_eq!(n.title_preview, "first line");
}

#[test]
fn title_preview_is_capped_at_28_chars() {
    let db = new_db();
    let n = db.core.create_note(None, &"x".repeat(60)).unwrap();
    let len = n.title_preview.chars().count();
    assert!(len > 0 && len <= 28, "got {len}");
}

#[test]
fn title_preview_falls_back_to_new_note() {
    let db = new_db();
    assert_eq!(db.core.create_note(None, "").unwrap().title_preview, "New note");
    assert_eq!(db.core.create_note(None, "  \n\t ").unwrap().title_preview, "New note");
}

#[test]
fn unicode_body_round_trips() {
    let db = new_db();
    let body = "héllo 🌍 日本語\nline2 ✅";
    let n = db.core.create_note(None, body).unwrap();
    assert_eq!(db.core.get_note(n.id, false).unwrap().body, body);
}

#[test]
fn large_body_round_trips() {
    let db = new_db();
    let body = "abc\n".repeat(50_000);
    let n = db.core.create_note(None, &body).unwrap();
    assert_eq!(db.core.get_note(n.id, false).unwrap().body, body);
}

#[test]
fn update_body_persists_and_touches_updated_not_opened() {
    let db = new_db();
    let n = db.core.create_note(None, "a").unwrap();
    tick();
    db.core.update_note_body(n.id, "b").unwrap();
    let after = db.core.get_note(n.id, false).unwrap();
    assert_eq!(after.body, "b");
    assert!(after.updated_at_ms > n.updated_at_ms);
    assert_eq!(after.last_opened_at_ms, n.last_opened_at_ms, "autosave must not count as opening");
}

#[test]
fn get_note_marks_opened_only_when_asked() {
    let db = new_db();
    let n = db.core.create_note(None, "a").unwrap();
    tick();
    db.core.get_note(n.id, false).unwrap();
    assert_eq!(db.core.get_note(n.id, false).unwrap().last_opened_at_ms, n.last_opened_at_ms);
    db.core.get_note(n.id, true).unwrap();
    assert!(db.core.get_note(n.id, false).unwrap().last_opened_at_ms > n.last_opened_at_ms);
}

#[test]
fn delete_note_is_permanent() {
    let db = new_db();
    let n = db.core.create_note(None, "bye").unwrap();
    db.core.delete_note(n.id).unwrap();
    assert!(matches!(db.core.get_note(n.id, false), Err(CoreError::NotFound(_))));
    assert!(recent_ids(&db.core).is_empty());
}

#[test]
fn deleting_a_pinned_note_removes_it_from_the_rail() {
    let db = new_db();
    let n = db.core.create_note(None, "p").unwrap();
    db.core.set_note_pinned(n.id, true).unwrap();
    assert_eq!(pinned_labels(&db.core).len(), 1);
    db.core.delete_note(n.id).unwrap();
    assert!(pinned_labels(&db.core).is_empty());
}

#[test]
fn group_name_must_not_be_blank() {
    let db = new_db();
    assert!(matches!(db.core.create_group("", true), Err(CoreError::Validation(_))));
    assert!(matches!(db.core.create_group("   ", true), Err(CoreError::Validation(_))));
}

#[test]
fn rename_group_updates_name_and_rejects_blank() {
    let db = new_db();
    let g = db.core.create_group("Old", true).unwrap();
    assert_eq!(db.core.rename_group(g.id, "New").unwrap().name, "New");
    assert!(matches!(db.core.rename_group(g.id, " "), Err(CoreError::Validation(_))));
}

#[test]
fn group_summary_reports_note_count() {
    let db = new_db();
    let g = db.core.create_group("Work", true).unwrap();
    for i in 0..3 {
        db.core.create_note(Some(g.id), &format!("n{i}")).unwrap();
    }
    db.core.create_note(None, "standalone").unwrap();
    let groups = db.core.get_bar_snapshot(None).unwrap().all_groups;
    assert_eq!(groups[0].note_count, 3);
}

#[test]
fn summary_is_empty_flag_drives_delete_confirmation() {
    let db = new_db();
    db.core.create_note(None, "").unwrap();
    let s = db.core.get_bar_snapshot(None).unwrap();
    assert!(s.recent_notes[0].is_empty);
    tick();
    db.core.create_note(None, "text").unwrap();
    let s = db.core.get_bar_snapshot(None).unwrap();
    assert!(!s.recent_notes[0].is_empty);
}

#[test]
fn delete_group_cascades_to_notes_and_pins() {
    let db = new_db();
    let g = db.core.create_group("Work", true).unwrap();
    let a = db.core.create_note(Some(g.id), "a").unwrap();
    let b = db.core.create_note(Some(g.id), "b").unwrap();
    db.core.set_note_pinned(b.id, true).unwrap();
    assert_eq!(pinned_labels(&db.core).len(), 2); // group + pinned member note

    db.core.delete_group(g.id).unwrap();

    assert!(matches!(db.core.get_note(a.id, false), Err(CoreError::NotFound(_))));
    assert!(matches!(db.core.get_note(b.id, false), Err(CoreError::NotFound(_))));
    let s = db.core.get_bar_snapshot(None).unwrap();
    assert!(s.pinned_items.is_empty());
    assert!(s.all_groups.is_empty());
}

#[test]
fn delete_group_leaves_other_data_alone() {
    let db = new_db();
    let g1 = db.core.create_group("A", true).unwrap();
    let g2 = db.core.create_group("B", true).unwrap();
    let keep_in_g2 = db.core.create_note(Some(g2.id), "keep").unwrap();
    let standalone = db.core.create_note(None, "solo").unwrap();
    db.core.create_note(Some(g1.id), "gone").unwrap();

    db.core.delete_group(g1.id).unwrap();

    assert_eq!(db.core.get_note(keep_in_g2.id, false).unwrap().body, "keep");
    assert_eq!(db.core.get_note(standalone.id, false).unwrap().body, "solo");
    assert_eq!(db.core.get_bar_snapshot(None).unwrap().all_groups.len(), 1);
}
