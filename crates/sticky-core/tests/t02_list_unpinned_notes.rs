//! "All notes" dropdown gate — Recents shows five, so without this a note that falls out of the
//! rail and off the recents is unreachable unless it is grouped. These pin the ordering contract.
//! Pinned notes are excluded: they are already on the rail.
mod common;
use common::*;
use sticky_core::NoteSort;

#[test]
fn sort_codes_match_the_csharp_enum_and_reject_anything_else() {
    assert_eq!(NoteSort::from_code(0), Some(NoteSort::Recent));
    assert_eq!(NoteSort::from_code(1), Some(NoteSort::Alphabetical));
    assert_eq!(
        NoteSort::from_code(2),
        None,
        "an unknown code must be a validation error, never a silent fallback"
    );
}

#[test]
fn all_notes_lists_grouped_and_standalone_but_never_pinned() {
    let db = new_db();
    let g = db.core.create_group("G", true).unwrap();
    let grouped = db.core.create_note(Some(g.id), "inside").unwrap();
    let pinned = db.core.create_note(None, "pinned").unwrap();
    db.core.set_note_pinned(pinned.id, true).unwrap();
    let loose = db.core.create_note(None, "loose").unwrap();

    let notes = db.core.list_unpinned_notes(NoteSort::Recent).unwrap();
    let ids: Vec<i64> = notes.iter().map(|n| n.id).collect();

    assert!(ids.contains(&grouped.id), "a grouped note is still listed");
    assert!(ids.contains(&loose.id), "a standalone note is listed");
    assert!(
        !ids.contains(&pinned.id),
        "a pinned note is already on the rail and must not be duplicated here"
    );
}

#[test]
fn all_notes_is_not_capped_the_way_recents_is() {
    let db = new_db();
    for i in 0..8 {
        db.core.create_note(None, &format!("note {i}")).unwrap();
        tick();
    }

    assert_eq!(recent_ids(&db.core).len(), 5, "recents stays capped at five");
    assert_eq!(
        db.core.list_unpinned_notes(NoteSort::Recent).unwrap().len(),
        8,
        "the dropdown is the way back to the notes the recents dropped"
    );
}

#[test]
fn recent_order_is_most_recently_opened_first() {
    let db = new_db();
    let a = db.core.create_note(None, "a").unwrap();
    tick();
    let b = db.core.create_note(None, "b").unwrap();
    tick();
    let c = db.core.create_note(None, "c").unwrap();
    tick();
    db.core.get_note(a.id, true).unwrap(); // reopen the oldest last

    let order: Vec<i64> = db
        .core
        .list_unpinned_notes(NoteSort::Recent)
        .unwrap()
        .iter()
        .map(|n| n.id)
        .collect();

    assert_eq!(order[0], a.id, "the reopened note is first");
    assert_eq!(&order[1..], &[c.id, b.id]);
}

#[test]
fn alphabetical_order_is_case_insensitive_by_title() {
    let db = new_db();
    db.core.create_note(None, "beta").unwrap();
    tick();
    db.core.create_note(None, "Alpha").unwrap(); // capital A must not sort after lowercase
    tick();
    db.core.create_note(None, "gamma").unwrap();

    let titles: Vec<String> = db
        .core
        .list_unpinned_notes(NoteSort::Alphabetical)
        .unwrap()
        .iter()
        .map(|n| n.title_preview.clone())
        .collect();

    assert_eq!(titles, vec!["Alpha", "beta", "gamma"]);
}

#[test]
fn alphabetical_ties_fall_back_to_newest_opened_first() {
    let db = new_db();
    let first = db.core.create_note(None, "same").unwrap();
    tick();
    let second = db.core.create_note(None, "same").unwrap();
    tick();
    db.core.get_note(first.id, true).unwrap(); // reopen the older duplicate last

    let ids: Vec<i64> = db
        .core
        .list_unpinned_notes(NoteSort::Alphabetical)
        .unwrap()
        .iter()
        .map(|n| n.id)
        .collect();

    assert_eq!(
        ids,
        vec![first.id, second.id],
        "same title -> most recently opened first, so the order is stable across refreshes"
    );
}

#[test]
fn alphabetical_uses_the_title_preview_not_the_whole_body() {
    let db = new_db();
    // "zebra" is the first line (the title); the body starts with "apple".
    db.core.create_note(None, "zebra\napple").unwrap();
    tick();
    db.core.create_note(None, "yak").unwrap();

    let titles: Vec<String> = db
        .core
        .list_unpinned_notes(NoteSort::Alphabetical)
        .unwrap()
        .iter()
        .map(|n| n.title_preview.clone())
        .collect();

    assert_eq!(
        titles,
        vec!["yak", "zebra"],
        "A→Z sorts by the derived title, not by the raw body"
    );
}