# StickyNotes v0.2.0 — Roadmap & spec

Status: **planned** (not started). v1.0 is shipped; everything here was deferred from v1 after a risk review.
Baseline: `main` @ v0.1.0 release (131 automated tests green, Tasks 1–10 done).

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
- Schema migration **v1 → v2**: `ALTER TABLE notes ADD COLUMN title TEXT NULL;` and bump `user_version = 2`. Migration must be idempotent (existing DBs upgrade on open).
- `StickyCore::set_note_title(note_id, Option<&str>)`; `Note` gains `title: Option<String>`.
- 18th FFI export `sn_set_note_title(h, note_id, title_utf8_or_null)`.
- C#: `Note.Title`, `IStickyCoreClient.SetNoteTitle(long, string?)`.

**UI:** the note window header shows the title (editable); the bar chip / group row / recents / append picker show `title ?? title_preview`.

**Risk:** medium–high. Permanent dual-title concept (`title` vs `title_preview`) in the data model. Migration must not break existing DBs.

**Open design question (needs human):** what should the title field do when the user clears it? (Fall back to `title_preview` — recommended.) And should the title be capped in length? (Suggest ≤ 64 chars.)

**Test impact:** existing `title_preview` tests stay valid (they test the derived field). New behaviour is additive.

**Acceptance:** manual — set a title, see it everywhere; clear it, see the derived preview; restart, persists.

---

### F3 — Larger hover target (still centered)

**What:** make it easier to trigger the bar with the mouse.

**Blocker:** `ScreenPositionerTests.cs:40-47,54,68` pin `GetTriggerRect` to **100×4 DIPs, centered** (including on negative-coordinate monitors). Enlarging the notch breaks 4 assertions, and tests must not be edited.

**Only non-breaking route:** keep the tested `GetTriggerRect` at 100×4 and **widen the invisible cursor hit-test band** in `ShellCoordinator.CursorInTrigger()` — e.g. treat a taller/wider region above the notch as "in trigger" for hover purposes, while the visible notch stays 100×4.

**Risk:** low. Pure logic in the coordinator; no test contact.

**Open design question (needs human):** how much wider/taller should the invisible band be? (Suggest 2× width, ~24 DIPs tall, anchored at WorkArea.Top.)

**Acceptance:** manual — easier to trigger; fast sweeps still don't; the visible notch is unchanged.

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

Cheapest/safest first, riskiest last:

1. **F4** — hover note preview (read-only, no schema)
2. **F1** — move note to group (additive, no migration)
3. **F5** — hover group to open (blueprint amendment)
4. **F2** — note titles (schema migration v2 + FFI + UI)
5. **F3** — larger invisible hover band (test-safe workaround)

Each ships behind the same discipline as v1: `scripts\gate.ps1 -Task N` green, diff review, manual sign-off, commit.

## Design decisions needed from the human

1. **F2:** dual-title model — confirm `title ?? title_preview`, clear-title falls back to the derived preview, and a length cap (suggest 64).
2. **F3:** accept the "keep 100×4 visible notch + wider invisible hit band" approach (the only test-safe option).
3. **F5:** approve reversing blueprint §1 (click-to-open → hover-or-click).

## Out of scope for v0.2

Active-window detection / context-aware bar, app-directory mapping, macOS UI, cloud sync, search UI. (Same as v1.)

## Release criteria for v0.2.0

- All five features implemented and manually signed off.
- `scripts\gate.ps1` green for every touched task; 131+ automated tests still green.
- Updated `blueprint.md` (F5 amendment, F2 title field) and `PROGRESS.md` decisions.
- New distributable `StickyNotes-0.2.0-win-x64.zip` published.