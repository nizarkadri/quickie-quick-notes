//! F1 gate - set_note_group: move an existing note between groups, or to standalone.
//!
//! A move is not an open and not an edit: it must not touch `last_opened_at_ms`
//! (recents order) or `updated_at_ms` (autosave state).
mod common;
use common::*;
use sticky_core::CoreError;

#[test]
fn move_between_two_groups_updates_membership() {
    let db = new_db();
    let g1 = db.core.create_group("One", true).unwrap();
    let g2 = db.core.create_group("Two", true).unwrap();
    let n = db.core.create_note(Some(g1.id), "movable").unwrap();

    let moved = db.core.set_note_group(n.id, Some(g2.id)).unwrap();
    assert_eq!(moved.group_id, Some(g2.id));
    assert_eq!(moved.id, n.id);

    let in_old = db.core.list_group_notes(g1.id).unwrap();
    let in_new = db.core.list_group_notes(g2.id).unwrap();
    assert!(in_old.iter().all(|x| x.id != n.id), "note must leave the old group");
    assert_eq!(in_new.iter().map(|x| x.id).collect::<Vec<_>>(), vec![n.id]);

    assert_eq!(db.core.get_note(n.id, false).unwrap().group_id, Some(g2.id));
}

#[test]
fn move_to_standalone_and_back_to_a_group() {
    let db = new_db();
    let g = db.core.create_group("G", true).unwrap();
    let n = db.core.create_note(Some(g.id), "x").unwrap();

    let standalone = db.core.set_note_group(n.id, None).unwrap();
    assert_eq!(standalone.group_id, None);
    assert_eq!(db.core.get_note(n.id, false).unwrap().group_id, None);
    assert!(db.core.list_group_notes(g.id).unwrap().is_empty());

    let back = db.core.set_note_group(n.id, Some(g.id)).unwrap();
    assert_eq!(back.group_id, Some(g.id));
    assert_eq!(db.core.list_group_notes(g.id).unwrap().iter().map(|x| x.id).collect::<Vec<_>>(), vec![n.id]);
}

#[test]
fn standalone_note_can_move_into_a_group() {
    let db = new_db();
    let g = db.core.create_group("G", true).unwrap();
    let n = db.core.create_note(None, "solo").unwrap();

    let moved = db.core.set_note_group(n.id, Some(g.id)).unwrap();
    assert_eq!(moved.group_id, Some(g.id));
}

#[test]
fn move_to_missing_group_is_not_found() {
    let db = new_db();
    let n = db.core.create_note(None, "x").unwrap();
    assert!(matches!(db.core.set_note_group(n.id, Some(99_999)), Err(CoreError::NotFound(_))));
    // The note is untouched on failure.
    assert_eq!(db.core.get_note(n.id, false).unwrap().group_id, None);
}

#[test]
fn move_missing_note_is_not_found() {
    let db = new_db();
    let g = db.core.create_group("G", true).unwrap();
    assert!(matches!(db.core.set_note_group(424_242, Some(g.id)), Err(CoreError::NotFound(_))));
    assert!(matches!(db.core.set_note_group(424_242, None), Err(CoreError::NotFound(_))));
}

#[test]
fn move_does_not_bump_last_opened_at_ms_or_reorder_recents() {
    let db = new_db();
    let a = db.core.create_note(None, "note A").unwrap();
    tick();
    let b = db.core.create_note(None, "note B").unwrap(); // unpinned; newer
    assert_eq!(recent_ids(&db.core), vec![b.id, a.id]);

    let g = db.core.create_group("G", true).unwrap();
    tick();
    let before = db.core.get_note(a.id, false).unwrap().last_opened_at_ms;
    tick();
    db.core.set_note_group(a.id, Some(g.id)).unwrap();

    let after = db.core.get_note(a.id, false).unwrap();
    assert_eq!(after.last_opened_at_ms, before, "a move must not count as opening");
    // But "a" is now in a group; recents only list unpinned notes, so it leaves recents? No —
    // group membership does not affect recents eligibility, only pinning does.
    assert_eq!(recent_ids(&db.core), vec![b.id, a.id], "recents order must survive a move");
}

#[test]
fn move_does_not_touch_updated_at_ms() {
    let db = new_db();
    let g = db.core.create_group("G", true).unwrap();
    let n = db.core.create_note(None, "body").unwrap();
    tick();
    let before = db.core.get_note(n.id, false).unwrap().updated_at_ms;
    tick();
    db.core.set_note_group(n.id, Some(g.id)).unwrap();

    assert_eq!(db.core.get_note(n.id, false).unwrap().updated_at_ms, before, "a move must not count as an edit");
}

#[test]
fn move_to_the_same_group_is_a_no_op_but_succeeds() {
    let db = new_db();
    let g = db.core.create_group("G", true).unwrap();
    let n = db.core.create_note(Some(g.id), "x").unwrap();

    let moved = db.core.set_note_group(n.id, Some(g.id)).unwrap();
    assert_eq!(moved.group_id, Some(g.id));
}
