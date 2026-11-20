# StickyNotes v0.2.0 — Roadmap & spec

Status: **implemented** (automated green; manual sign-off pending). F1, F4, F5, All Notes, F2, and F3 are in the tree.
Baseline: `main` @ v1.0 + v0.2.0 (81 Rust + 221 C# automated tests green; Tasks 1–10 done).

---

## Why v0.2

Five user-requested features were deferred because each either needs a design decision, collides with a pinned test, or changes the data model. This document pins down the intended behaviour, the files each touches, the constraints, and the open questions — so v0.2 can be built deliberately rather than improvised.

**Hard rule carried over from v1:** the supplied tests are the definition of done and must not be edited, skipped, or loosened. Any feature that would require changing a test must instead be implemented in a test-compatible way (or explicitly deferred).

---

## Feature specs

### F1 — Move an existing note into a new/existing group

**What:** from an open note (or a group-list row), move it into another group, or into a new group created on the spot.

**Why:** notes currently only get a group at creation time (`CreateNote(groupId, body)`); there is no way to re-file one.

**Data layer:** `group_id` already exists and is asserted by tests (`t02_notes_and_groups.rs:25`, `t02_quick_capture.rs:86`). No schema change needed.
- Add `StickyCore::set_note_group(note_id, Option<i64>) -> Result<Note, CoreError>` (validates the target group exists; `None` = standalone).
- Add a 17th FFI export `sn_set_note_group(h, note_id, has_group, group_id) -> FfiResult`.
- C#: `IStickyCoreClient.SetNoteGroup(long noteId, long? groupId)` + `StickyCoreClient` implementation.

**UI:** a "Move to group ▾" control in the `NoteWindow` header (dropdown of `BarSnapshot.AllGroups` + "Standalone"). On pick → `SetNoteGroup` → refresh bar/list.

**Risk:** low–medium. Additive; no migration. Main risk is UI placement in the note header.

**Test impact:** none (no existing test covers it). Optional: add a Rust test in a new file (not modifying existing ones) — but the rule says don't add tests unless the human approves; treat as optional.

**Acceptance:** manual — move a note between groups and to standalone; it disappears from the old group list and appears in the new one; persists after restart.

---

### F2 — Explicit note titles

**What:** let the user give a note a real title, separate from the auto-derived preview.

**Constraint (important):** `title_preview` is **pinned as derived** by `t02_notes_and_groups.rs:41-59` (first non-empty line, ≤28 chars, fallback "New note"). It must stay derived. So an explicit title must be a **separate nullable `title` column**, with the UI preferring `title ?? title_preview`.

**Data layer:**
- **Additive column, no `user_version` bump.** `t02_schema_and_migrations.rs::open_creates_schema_version_1` pins fresh DBs to `user_version = 1`, and tests must not be edited. `open()` runs an idempotent `ALTER TABLE notes ADD COLUMN title TEXT NULL` when `PRAGMA table_info(notes)` lacks `title`. `V1_SQL`/`MIGRATIONS` stay untouched; `user_version` still tracks breaking migrations and v2 stays reserved for `context_rules`. See `docs/adr/0001-additive-columns-without-version-bump.md`.
- `StickyCore::set_note_title(note_id, Option<&str>) -> Result<Note, CoreError>` normalizes (trim / collapse / cap 64 / empty → `None`) and bumps `updated_at_ms`; `Note`/`NoteSummary` gain `title: Option<String>`.
- 19th FFI export `sn_set_note_title(h, note_id, title_utf8_or_null)` — `NULL` clears.
- C#: `Note.Title`, `NoteSummary.Title`, `IStickyCoreClient.SetNoteTitle(long, string?)`, plus computed `DisplayTitle => Title ?? TitlePreview`.

**UI:** the note window header shows the title (editable); the bar chip / group row / recents / append picker show `title ?? title_preview`.

**Risk:** medium–high. Permanent dual-title concept (`title` vs `title_preview`) in the data model. Migration must not break existing DBs.

**Resolved design (grilled 2026-11-19):**
- **Dual model:** `title` (nullable, user-set) and `title_preview` (derived, unchanged). Every display site and the A→Z sort use `DisplayTitle = title ?? title_preview`.
- **Normalization:** trim, collapse internal whitespace/newlines to one space, empty → `NULL`, cap 64 chars. `is_empty` becomes `body.trim().IsEmpty && title is null`.
- **Session:** `NoteEditSession` gains title state; a new note with only a title is **kept** on close (not `DeletedEmptyNew`); title edits share the 350 ms flush.
- **UI:** always-visible single-line title row between header and body; `MaxLength=64`; focus stays in the **body** on open; Enter commits and focuses the body; Esc reverts and focuses the body; delete confirmation requires **both** body and title blank.
- **Sort:** `NoteSort::Alphabetical` keys on `title ?? title_preview`.

**Test impact:** existing `title_preview` tests stay valid (they test the derived field). New tests are new files only: `t02_set_note_title.rs` and `NoteTitleTests.cs`.

**Acceptance:** manual — set a title, see it everywhere; clear it, see the derived preview; restart, persists; title-only new note survives close.

---

### F3 — Larger hover target (still centered)

**What:** make it easier to trigger the bar with the mouse.

**Blocker:** `ScreenPositionerTests.cs:40-47,54,68` pin `GetTriggerRect` to **100×4 DIPs, centered** (including on negative-coordinate monitors). Enlarging the notch breaks 4 assertions, and tests must not be edited.

**Resolved design (grilled 2026-11-19) — wider trigger window, not a wider cursor band:**

The roadmap's original "widen `CursorInTrigger`" plan is **insufficient**: the hidden-state tick is stopped, and `TriggerWindow.MouseEnter` → `Machine.TriggerEntered` is the actual trigger. Widening only the coordinator's hit-test would never start the dwell.

- Add `ScreenPositioner.GetTriggerHitRect(...)` → **200×4 DIPs** (2× width, same 4 DIP height), centered at `WorkArea.Top`.
- `TriggerWindow.Position()` uses the hit rect; the window becomes **200×4**, the visible pill **200×3**, and window-level `MouseEnter`/`MouseLeave` drives the pill hover colour across the full 200 DIP strip.
- `GetTriggerRect` stays **100×4** → the four pinned `ScreenPositionerTests` assertions stay green. `CursorInTrigger()` needs no change because it already uses the window rect.
- Re-raise the bar after `RebuildTriggers()` so a rebuilt trigger never covers a visible bar's chips.
- **Trade-off accepted:** the invisible part is hit-testable and swallows clicks in a 200×4 strip at top-centre (same height as today, 2× width). Idle stays 0% — no timer, no hook.

**Risk:** low. Additive method + one window size change; no test contact.

**Acceptance:** manual — 50 DIPs left/right of the pill opens; 110 DIPs left does not; fast sweep does not; visible pill is 200×3; multi-monitor and fullscreen behave; display-change rebuild doesn't cover the bar.

---

### F4 — Hover a note to preview it

**What:** hovering a note chip/row shows a small read-only preview (tooltip/popover) without opening the full note window.

**Data layer:** none. `GetNote(id, markOpened: false)` already returns the body; `NoteSummary` intentionally omits the body.

**UI:** a lightweight non-activating popup (or `ToolTip`) on chip/row hover, showing the first ~200 chars. Must not steal focus and must not count as "opening" (don't bump `last_opened_at_ms`).

**Risk:** low. Read-only; no schema/API/test contact. Main risk is popup positioning/flicker.

**Acceptance:** manual — hover a chip/row → preview appears; moving away dismisses; no focus change.

---

### F5 — Hover a group to open its note list

**What:** hovering a group chip (after a short dwell) opens its vertical list, in addition to clicking.

**Conflict:** this **reverses blueprint §1** ("Group Expansion: Click-to-Open, Single Open List"). It needs an explicit blueprint amendment.

**AMENDMENT (approved 2026-11-17, shipped in v0.2.2+):** blueprint §1 is amended as the human clarified it — "the group IN the list, not the list of groups": hovering `Groups ▾` for ~180 ms OPENS the all-groups dropdown (a click still opens/toggles it immediately, and a click on a hover-opened dropdown keeps it open). Hovering a GROUP ROW inside that dropdown for ~180 ms opens a SIDE LIST of that group's notes to the RIGHT of the dropdown, vertically aligned with the hovered row (`ScreenPositioner.GetGroupPreviewRect`). The side list is ADDITIVE (the dropdown stays open), never steals focus, closes after a ~200 ms grace once the cursor leaves both the row and the side list, and closes with the dropdown. Clicking a note in the side list opens the note and keeps the side list visible; clicking a group row still switches the dropdown to that group's notes; bar group chips remain click-only.

**Design:** reuse the existing 180 ms `HoverDwellTracker` dwell (one-shot 50 ms poll inside `BarWindow` / `GroupListWindow`, so a stationary pointer never re-fires) and the 50 ms tick for the ~200 ms leave grace; the side list is its own non-activating `GroupPreviewWindow`, republished into the hit surfaces while visible and dropped when hidden. Must not fight the 100 px trigger dwell.

**Risk:** medium (UX, not code). Hover-dwell on a bar chip is twitchy next to the top-edge trigger.

**Open design question (needs human):** approve reversing click-to-open? And should hover-open require the cursor to settle on the chip (not just pass over)?

**Test impact:** none (no automated test covers it).

**Acceptance:** manual — hover a group chip → list opens after ~180 ms; quick pass doesn't; clicking still works.

---

## Suggested sequencing

F4, F1, F5, All Notes, F2, and F3 are implemented. Manual sign-off (window/focus/cursor behaviour) is the remaining gate.

Each ships behind the same discipline as v1: `scripts\gate.ps1 -Task N` green, diff review, manual sign-off, commit.

## Design decisions (resolved 2026-11-19)

1. **F2:** dual-title model accepted; clear → `NULL` → derived preview; 64-char cap; additive column without a `user_version` bump (ADR 0001); title-only new notes survive close.
2. **F3:** wider **trigger window** (200×4 DIPs) accepted, not a wider `CursorInTrigger` band; click trade-off accepted; 0% idle preserved.
3. **F5:** blueprint §1 reversal approved (already shipped).

## Out of scope for v0.2

Active-window detection / context-aware bar, app-directory mapping, macOS UI, cloud sync, search UI. (Same as v1.)

## Release criteria for v0.2.0

- F1, F4, F5, All Notes, F2, and F3 implemented and manually signed off.
- `scripts\gate.ps1` green for every touched task; 81 Rust and 221 C# automated tests green.
- Updated `blueprint.md` (F5 amendment, F2 title field, F3 trigger width), `CONTEXT.md`, and `PROGRESS.md` decisions.
- New distributable `StickyNotes-0.2.0-win-x64.zip` published.