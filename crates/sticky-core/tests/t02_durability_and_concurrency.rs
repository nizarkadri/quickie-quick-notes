//! TASK 2 gate - persistence and thread safety (the core is called from any C# thread).
mod common;
use common::*;
use std::{sync::Arc, thread};
use sticky_core::StickyCore;

#[test]
fn core_is_send_and_sync() {
    fn assert_send_sync<T: Send + Sync>() {}
    assert_send_sync::<StickyCore>();
}

#[test]
fn data_survives_close_and_reopen() {
    let db = new_db();
    let g = db.core.create_group("Persist", true).unwrap();
    let n = db.core.create_note(Some(g.id), "still here").unwrap();
    db.core.set_note_pinned(n.id, true).unwrap();
    let path = db.path.clone();
    drop(db.core);

    let core = StickyCore::open(&path).unwrap();
    let back = core.get_note(n.id, false).unwrap();
    assert_eq!(back.body, "still here");
    assert!(back.is_pinned);
    assert_eq!(core.get_bar_snapshot(None).unwrap().all_groups[0].name, "Persist");
}

#[test]
fn concurrent_writers_and_readers_do_not_lose_or_corrupt_data() {
    let db = new_db();
    let core = Arc::new(db.core);

    let handles: Vec<_> = (0..8)
        .map(|t| {
            let c = Arc::clone(&core);
            thread::spawn(move || {
                for i in 0..25 {
                    c.create_note(None, &format!("t{t}-n{i}")).unwrap();
                    c.get_bar_snapshot(None).unwrap();
                }
            })
        })
        .collect();
    for h in handles {
        h.join().expect("worker thread panicked");
    }

    assert_eq!(core.list_append_targets(1000).unwrap().len(), 200);
}
