//! TASK 2 gate - pin ordering, group ordering, recents rules.
mod common;
use common::*;
use std::collections::HashSet;

#[test]
fn new_group_respects_pinned_flag() {
    let db = new_db();
    let a = db.core.create_group("A", true).unwrap();
    assert!(a.is_pinned && a.pinned_at_ms.is_some());
    let b = db.core.create_group("B", false).unwrap();
    assert!(!b.is_pinned && b.pinned_at_ms.is_none());
}

#[test]
fn pinned_rail_interleaves_groups_and_notes_by_pin_time() {
    let db = new_db();
    let n1 = db.core.create_note(None, "a").unwrap();
    tick();
    db.core.set_note_pinned(n1.id, true).unwrap();
    tick();
    let g = db.core.create_group("G", true).unwrap();
    tick();
    let n2 = db.core.create_note(None, "b").unwrap();
    tick();
    db.core.set_note_pinned(n2.id, true).unwrap();

    assert_eq!(
        pinned_labels(&db.core),
        vec![format!("n{}", n1.id), format!("g{}", g.id), format!("n{}", n2.id)],
        "oldest pin on the left, newest on the right"
    );
}

#[test]
fn unpin_removes_from_rail_and_clears_timestamp() {
    let db = new_db();
    let n = db.core.create_note(None, "a").unwrap();
    assert!(db.core.set_note_pinned(n.id, true).unwrap().pinned_at_ms.is_some());
    let s = db.core.set_note_pinned(n.id, false).unwrap();
    assert!(!s.is_pinned);
    assert_eq!(s.pinned_at_ms, None);
    assert!(pinned_labels(&db.core).is_empty());
}

#[test]
fn repinning_moves_item_to_the_right_end() {
    let db = new_db();
    let n1 = db.core.create_note(None, "1").unwrap();
    let n2 = db.core.create_note(None, "2").unwrap();
    db.core.set_note_pinned(n1.id, true).unwrap();
    tick();
    db.core.set_note_pinned(n2.id, true).unwrap();
    tick();
    db.core.set_note_pinned(n1.id, false).unwrap();
    tick();
    db.core.set_note_pinned(n1.id, true).unwrap();
    assert_eq!(pinned_labels(&db.core), vec![format!("n{}", n2.id), format!("n{}", n1.id)]);
}

#[test]
fn unpinned_group_leaves_rail_but_stays_in_all_groups() {
    let db = new_db();
    let g = db.core.create_group("G", true).unwrap();
    db.core.set_group_pinned(g.id, false).unwrap();
    let s = db.core.get_bar_snapshot(None).unwrap();
    assert!(s.pinned_items.is_empty());
    assert_eq!(s.all_groups.len(), 1);
    assert!(!s.all_groups[0].is_pinned);
}

#[test]
fn all_groups_are_ordered_by_creation_not_by_name() {
    let db = new_db();
    let z = db.core.create_group("Zed", true).unwrap();
    tick();
    let a = db.core.create_group("Alpha", true).unwrap();
    let ids: Vec<i64> = db.core.get_bar_snapshot(None).unwrap().all_groups.iter().map(|g| g.id).collect();
    assert_eq!(ids, vec![z.id, a.id]);
}

#[test]
fn recents_are_capped_at_five_newest_first() {
    let db = new_db();
    let ids: Vec<i64> = (0..7)
        .map(|i| {
            tick();
            db.core.create_note(None, &format!("n{i}")).unwrap().id
        })
        .collect();
    let expected: Vec<i64> = ids.iter().rev().take(5).cloned().collect();
    assert_eq!(recent_ids(&db.core), expected);
}

#[test]
fn opening_a_note_moves_it_to_the_top_of_recents() {
    let db = new_db();
    let n1 = db.core.create_note(None, "1").unwrap();
    tick();
    db.core.create_note(None, "2").unwrap();
    tick();
    db.core.create_note(None, "3").unwrap();
    tick();
    db.core.get_note(n1.id, true).unwrap();
    assert_eq!(recent_ids(&db.core)[0], n1.id);
}

#[test]
fn pinned_notes_are_excluded_from_recents_until_unpinned() {
    let db = new_db();
    let n = db.core.create_note(None, "p").unwrap();
    db.core.set_note_pinned(n.id, true).unwrap();
    assert!(!recent_ids(&db.core).contains(&n.id));
    db.core.set_note_pinned(n.id, false).unwrap();
    assert!(recent_ids(&db.core).contains(&n.id));
}

#[test]
fn unpinned_notes_inside_a_pinned_group_still_appear_in_recents() {
    let db = new_db();
    let g = db.core.create_group("G", true).unwrap();
    let n = db.core.create_note(Some(g.id), "in group").unwrap();
    assert!(recent_ids(&db.core).contains(&n.id));
}

#[test]
fn recents_and_pinned_rail_never_show_the_same_note() {
    let db = new_db();
    let g = db.core.create_group("G", true).unwrap();
    for i in 0..6 {
        tick();
        let n = db.core.create_note(if i % 2 == 0 { Some(g.id) } else { None }, &format!("n{i}")).unwrap();
        if i % 3 == 0 {
            db.core.set_note_pinned(n.id, true).unwrap();
        }
    }
    let s = db.core.get_bar_snapshot(None).unwrap();
    let pinned_note_ids: HashSet<i64> = s
        .pinned_items
        .iter()
        .filter_map(|p| match p {
            sticky_core::PinnedItem::Note(n) => Some(n.id),
            _ => None,
        })
        .collect();
    for r in &s.recent_notes {
        assert!(!pinned_note_ids.contains(&r.id), "duplicate chip for note {}", r.id);
    }
}

#[test]
fn autosave_and_pin_toggles_do_not_reorder_recents() {
    let db = new_db();
    let n1 = db.core.create_note(None, "1").unwrap();
    tick();
    let n2 = db.core.create_note(None, "2").unwrap();
    tick();
    db.core.update_note_body(n1.id, "edited").unwrap();
    db.core.set_note_pinned(n1.id, true).unwrap();
    db.core.set_note_pinned(n1.id, false).unwrap();
    assert_eq!(recent_ids(&db.core), vec![n2.id, n1.id]);
}

#[test]
fn list_group_notes_is_scoped_and_ordered_by_last_opened() {
    let db = new_db();
    let g = db.core.create_group("G", true).unwrap();
    let other = db.core.create_group("Other", true).unwrap();
    let a = db.core.create_note(Some(g.id), "a").unwrap();
    tick();
    let b = db.core.create_note(Some(g.id), "b").unwrap();
    db.core.create_note(Some(other.id), "x").unwrap();
    db.core.create_note(None, "solo").unwrap();
    tick();
    db.core.get_note(a.id, true).unwrap();

    let ids: Vec<i64> = db.core.list_group_notes(g.id).unwrap().iter().map(|n| n.id).collect();
    assert_eq!(ids, vec![a.id, b.id]);
}

#[test]
fn append_targets_include_everything_ordered_and_limited() {
    let db = new_db();
    let g = db.core.create_group("G", true).unwrap();
    let a = db.core.create_note(None, "a").unwrap();
    tick();
    let b = db.core.create_note(Some(g.id), "b").unwrap();
    tick();
    let c = db.core.create_note(None, "c").unwrap();
    db.core.set_note_pinned(c.id, true).unwrap(); // pinned notes are still valid append targets

    let all: Vec<i64> = db.core.list_append_targets(10).unwrap().iter().map(|n| n.id).collect();
    assert_eq!(all, vec![c.id, b.id, a.id]);
    assert_eq!(db.core.list_append_targets(2).unwrap().len(), 2);
}
