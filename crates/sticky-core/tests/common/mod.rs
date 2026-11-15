#![allow(dead_code)]
use std::{path::PathBuf, thread, time::Duration};
use sticky_core::{PinnedItem, StickyCore};
use tempfile::TempDir;

pub struct TestDb {
    pub _dir: TempDir,
    pub path: PathBuf,
    pub core: StickyCore,
}

pub fn new_db() -> TestDb {
    let dir = tempfile::tempdir().expect("tempdir");
    let path = dir.path().join("sticky.db");
    let core = StickyCore::open(&path).expect("open db");
    TestDb { _dir: dir, path, core }
}

/// Timestamps are millisecond-based; sleep so consecutive operations get distinct, ordered times.
pub fn tick() {
    thread::sleep(Duration::from_millis(3));
}

/// Pinned rail as ["n12", "g3", ...] in bar order (left to right).
pub fn pinned_labels(core: &StickyCore) -> Vec<String> {
    core.get_bar_snapshot(None)
        .unwrap()
        .pinned_items
        .iter()
        .map(|p| match p {
            PinnedItem::Group(g) => format!("g{}", g.id),
            PinnedItem::Note(n) => format!("n{}", n.id),
        })
        .collect()
}

/// Recent note ids in bar order (most recently opened first).
pub fn recent_ids(core: &StickyCore) -> Vec<i64> {
    core.get_bar_snapshot(None)
        .unwrap()
        .recent_notes
        .iter()
        .map(|n| n.id)
        .collect()
}
