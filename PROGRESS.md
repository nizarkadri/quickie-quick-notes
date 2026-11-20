# PROGRESS — StickyNotes v1 (Windows)

Orchestrator ledger. A task is `done` only when: (a) `scripts\gate.ps1 -Task N`
prints AUTOMATED GATE PASSED (run by orchestrator), (b) diff review passes,
(c) human signs off the MANUAL_ACCEPTANCE section (Tasks 5-9 + final),
(d) committed.

## Preflight (run before any task)

| Check | Result |
|---|---|
| OS = Windows x64 | PASS — Windows 11 Pro 10.0.26300 |
| Rust 1.80+ (MSVC) | PASS — rustc/cargo 1.100.0 stable, `x86_64-pc-windows-msvc` |
| .NET SDK 8.0.x | PASS — .NET SDK 8.0.428 |
| VS 2022 C++ Build Tools | PASS — Visual Studio Build Tools 2022 (MSVC linker present) |
| blueprint.md + sources of truth | PASS — blueprint, spec, gate.ps1, MANUAL_ACCEPTANCE, all test files present |
| Task 5/7/8 test contracts match blueprint pure classes | PASS — BarStateMachineTests (BarStateMachine), NoteSessionTests (NoteEditSession + QuickNoteSession). Note: README/blueprint name them `NoteEditSessionTests.cs` / `QuickNoteSessionTests.cs` but they are combined in `NoteSessionTests.cs`; contracts match. |
| git initialized | PASS — branch `main` |
| git clean | PASS — tree clean after adding .gitignore (build outputs + `*Zone.Identifier`) and committing README/spec/PROGRESS |
| Implementation files (Task 1 outputs) | Not present yet — expected (Task 1 creates them). |

**BLOCKER RESOLVED:** Rust + .NET SDK installed. Preflight GREEN. Note: this
orchestrator session's PATH was captured before install; gate runs refresh PATH
from registry (cargo bin already in User PATH).

## Task order & flags

| Task | Title | Parallel? | Review flag |
|---|---|---|---|
| 1 | Workspace skeleton & build/test scripts | — | — |
| 2 | Rust SQLite layer, migrations, domain logic | with Task 4 (after Task 1) | — |
| 3 | Rust extern "C" FFI + C# StickyCoreClient | — | **STRONGER-MODEL REVIEW** |
| 4 | Win32 interop, DPI positioning, fullscreen | with Task 2 (after Task 1) | **STRONGER-MODEL REVIEW** |
| 5 | BarStateMachine, TriggerWindow, BarWindow + focus spike | — | **STRONGER-MODEL REVIEW** |
| 6 | GroupListWindow & group management | — | — |
| 7+8 | NoteEditSession + QuickNoteSession (NoteWindow + QuickNoteWindow) — MERGED | — | **STRONGER-MODEL REVIEW** |
| 9 | Hotplug resilience, idle trim, polish | — | — |
| 10 | Final end-to-end gate | — | — |

Parallelizable: **only Task 2 and Task 4**, both after Task 1.

## Task status

| Task | Status | Gate result + date | Commit | Reviewer notes | Manual sign-off |
|---|---|---|---|---|---|
| 1 | done | AUTOMATED GATE PASSED 2026-11-15 | 983367e | 3 worker deviations accepted (EditorDeactivated named params, Tests.csproj System.IO using, sln Any CPU); orchestrator fixed 3 stub param names (nowMs→ms, how→reason); no manual items | n/a |
| 2 | done | AUTOMATED GATE PASSED 2026-11-15 (54/54) | 24cc78a | no test edits, no #[ignore], no new deps; 15 §6 methods verified | n/a |
| 3 | done | AUTOMATED GATE PASSED 2026-11-15 (18/18 + 54/54) | 7d065b4 (+ review fixes bf1101b) | stronger-model review APPROVED (Dev-1 + Dev-2); F1-F4,F6 fixed & re-reviewed PASS; F5 nit | n/a |
| 4 | done | AUTOMATED GATE PASSED 2026-11-15 (Task=3\|Task=4 41/41) | 10abeeb (+ fixes df22308) | stronger-model review APPROVED (Dev-1 + Dev-2); F1-F5+F8 fixed & re-reviewed | — |
| 5 | done | AUTOMATED GATE PASSED 2026-11-15 (Task=3\|4\|5 59/59) | b6bade1 + f698a90 + a559d2c | Dev-1+Dev-2 review PASS; MANUAL signed off 2026-11-15 (item 6 fixed) | n/a |
| 6 | done | AUTOMATED GATE PASSED 2026-11-15 (regression 59/59; no automated tests) | a256578 + 2d48c42 + f12581e | MANUAL signed off 2026-11-16; QA PASS (stale-_group crash fixed f12581e) | n/a |
| 7+8 | done | AUTOMATED GATE PASSED 2026-11-16 (Task=3\|4\|5\|7\|8 77/77) | c7a8274 + 89785b3 + c4305ca (5s grace) | Dev-2 re-review 9/9 PASS; MANUAL signed off 2026-11-16 (all pass) | n/a |
| 9 | done | AUTOMATED GATE PASSED 2026-11-16 (regression 77/77; no automated tests) | 3074f9f + b188d1b + b96ce41 | Dev-2 review T9-1..T9-4 PASS; MANUAL signed off 2026-11-16 (all pass) | n/a |
| F1-F5 | deferred (v0.2) | — | — | human deferred ALL five 2026-11-16: move-note-to-group (F1), note titles (F2), larger hover target (F3, blocked by pinned 100x4 trigger tests), hover note preview (F4), hover-to-open groups (F5, reverses blueprint 1) | — |
| F4 | done | AUTOMATED GATE PASSED 2026-11-16 (77/77 filtered; +32 F4 = 109 C#; Rust 54/54) | d8be9b5 | UI-only; no schema/API change; fixed the WPF `StaysOpen=false` crash by attaching a `TextBlock` through ToolTipService; dark tooltip style + 350 ms delay | MANUAL signed off 2026-11-16 ("perfect") |
| 10 | done | build.ps1 + test.ps1 exit 0 on CLEAN tree (2026-11-16): Rust 54/54, C# 77/77 = 131 automated | 6693af0 | all tasks 1-9 done; MANUAL final checklist signed off 2026-11-16 | n/a |

## Decisions
- **2026-11-19 — v0.2.0 F2/F3 implemented (automated green; manual pending).** F2: additive `notes.title` via `ensure_additive_columns` (no `user_version` bump), `StickyCore::set_note_title` with trim/collapse/empty→NULL/64-char cap, `title`/`DisplayTitle` on `Note`+`NoteSummary`, `sn_set_note_title` (19th export), `NoteEditSession` title state (title-only new note survives close), `NoteWindow` title row (Enter/Esc, 64 max), and A→Z sort by `title ?? title_preview`. F3: `ScreenPositioner.GetTriggerHitRect` (200×4 DIPs), `TriggerWindow` 200×4 + 200×3 pill with window-level hover feedback, `BarWindow.BringToFront()` after `RebuildTriggers`. Tests: `t02_set_note_title.rs` (12), `NoteTitleTests.cs` (10), `TriggerHitRectTests.cs` (7). Automated: 81 Rust + 221 C# = 302 green; `scripts/test.ps1` exit 0. Docs: `README.md`, `blueprint.md` (v0.2 amendments), `ROADMAP-v0.2.0.md`, `MANUAL_ACCEPTANCE.md`. Manual sign-off pending.
- **2026-11-19 — v0.2.0 F2/F3 design grilled and locked (no code yet).** F2 titles: dual model (`title` nullable + derived `title_preview`), `DisplayTitle = title ?? title_preview` everywhere including A→Z sort; additive `notes.title` column via idempotent `ALTER TABLE` at `open()` with **no `user_version` bump** (fresh DBs pinned to 1 by `open_creates_schema_version_1`; v2 stays reserved for `context_rules`) — ADR `docs/adr/0001-additive-columns-without-version-bump.md`; normalization trim/collapse/empty→NULL/64-char cap; `is_empty` = body blank AND title null; `NoteEditSession` gains title state so a title-only new note survives close; always-visible title row in `NoteWindow`, focus stays in body; `sn_set_note_title` (19th export, NULL clears) + `IStickyCoreClient.SetNoteTitle`. F3: the roadmap's "widen CursorInTrigger" is insufficient because the hidden tick is stopped and `TriggerWindow.MouseEnter` is the real trigger; instead add `GetTriggerHitRect` = **200×4 DIPs** (2× width, same height), `TriggerWindow` uses it, visible pill 200×3, `GetTriggerRect` stays 100×4 so pinned tests pass; accept 200×4 click swallow; re-raise bar after `RebuildTriggers`. Docs: `ROADMAP-v0.2.0.md` F2/F3 sections updated to match.
- **2026-11-16 — Post-v0.2.2 bug-fix batch (human-reported; NOT committed; reviewer + testwriter own follow-ups).**
  - **Bug 1 (fade never stopped on re-entry):** `HitRegionTracker.GraceElapsed` used to set `_visible=false` + stop the 50ms poll after the legacy 200ms grace, so `_tracker.IsCursorInside` froze during the 2s fade and re-entry was invisible. Fix: the tracker keeps polling while visible; `SetVisible(false)`/`Dispose` remain the only stop; the legacy grace timer is parked in `PollTick` so `ExitCompleted` stays inert. `ShellCoordinator.OnCursorLeftApp` also gained a re-entry guard (`_outsideSinceUtc is not null` → return) so a stale edge cannot restart a running fade, and a pose-injection seam (`SetCursorPose`) was added for headless test hosts where the OS cursor probe is unavailable.
  - **Bug 2 (CORRECTED by orch-0.2 / human clarification):** "the group IN the list, not the list of groups" — `Groups ▾` stays CLICK-OPEN; hovering a GROUP ROW inside the all-groups dropdown opens a SIDE LIST of that group's notes to the RIGHT of the dropdown, aligned with the row. Bar group chips do NOT hover-open (the bar-chip hover path was removed). Implementation: pure `State/HoverDwellTracker.cs` (180ms dwell) driven by row enter/leave events (`GroupListWindow.GroupRowHover`) + the existing 50ms tick; new `GroupPreviewWindow` (non-activating, clickable note rows → existing OpenNote path, hidden never closed); `ScreenPositioner.GetGroupPreviewRect` (8px gap, left-flip + work-area clamps); ~200ms leave grace for row+list, close-on-dropdown-close, content switch on another row's dwell, no focus theft. Click geometry unchanged. Blueprint §1 amendment recorded here + in MANUAL_ACCEPTANCE.md.
  - **Bug 3 (QuickNote focus/close/×):** `FocusHelper.ForceActivateAndFocus` now forces `Visibility.Visible` before `Activate()`, wraps `Activate()` in try/catch, and applies `Keyboard.Focus` + `Window.Focus` unconditionally; `ShellCoordinator.OpenQuickNote` takes `Machine.EditorFocused()` only when focus really landed (`win.IsActive || FocusHelper.IsKeyboardFocusInside(win)`) so a failed activation cannot park the machine in OpenFocusedLock; `QuickNoteWindow` gained a × close button (discard, same as Esc) and its `Deactivated`/Esc paths ignore the event while `IsFocusWithin` (WPF focus scope still inside the window) so an editor the user is still typing in never self-closes on a foreground hand-off.
- **2026-11-16 — Human ruling: remove the NoteWindow 5s click-outside grace; add a group-list close button.** Click-outside now flushes and closes the note immediately (restores blueprint §1). Removed `NoteWindow._clickOutsideTimer` / `ClickOutsideGraceMs` / `ClickOutsideGracePending`; `ShellCoordinator.HideAll` always calls `CloseNoteWindow()`. `GroupListWindow` header gained an explicit `×` (`CloseRequested` -> coordinator `CloseList`), so an opened group can always be dismissed.
- **2026-11-16 — F4 (hover note preview) implemented and signed off (v0.2.1).** Bar chips and group-list note rows show a dark read-only tooltip with the first ~200 chars, resolved with `GetNote(id, markOpened: false)` so hovering never bumps `last_opened_at_ms` or reorders Recents. Implementation notes: (a) WPF throws `NotSupportedException` when an element-attached `ToolTip` has `StaysOpen=false` — attach the `TextBlock` content via `ToolTipService` instead; (b) WPF's default `InitialShowDelay` is 1000 ms, so F4 sets 350 ms explicitly. Commit d8be9b5.
- **2026-11-16 — Five post-v1 features DEFERRED to v0.2 (human ruling).** Requested: (F1) move an existing note into a new/existing group; (F2) explicit note titles; (F3) double the hover target area while keeping it centered; (F4) hover a note to preview it; (F5) hover a group to open its note list. Deferred after risk review, NOT implemented in v1. Analysis recorded for v0.2:
  - **F3 is blocked as literally specified** — `ScreenPositionerTests.cs:40-47,54,68` pin `GetTriggerRect` to 100x4 DIPs centered (also on negative-coordinate monitors). Enlarging the notch breaks 4 assertions and tests must not be edited. Only non-breaking route: keep 100x4 and widen the *invisible* cursor hit-test band in `ShellCoordinator.CursorInTrigger()`.
  - **F2 constrains the data model** — `t02_notes_and_groups.rs:41-59` pins `title_preview` as DERIVED (first non-empty line, <=28 chars, fallback "New note"). An explicit title must therefore be a separate nullable `title` column with UI preferring `title ?? title_preview`, leaving two competing title concepts permanently. Needs `user_version = 2` migration + a new FFI export.
  - **F1 is additive and cheap** — `group_id` already exists and is asserted (`t02_notes_and_groups.rs:25`, `t02_quick_capture.rs:86`); needs `set_note_group` + a 17th `sn_*` export + a UI control. No migration.
  - **F4 is low risk** — read-only `GetNote(id, markOpened:false)` + a tooltip; no schema/API/test contact.
  - **F5 reverses blueprint 1** ("Group Expansion: Click-to-Open, Single Open List") and needs an explicit blueprint amendment.
- **2026-11-17 — Code-review findings applied (reviewer P0/P1/P2/lows; not committed).** P0-1 QuickNote Esc/Ctrl+Enter/× zombie: deleted the `IsFocusWithin && reason != ClickOutside` early-return that sat AFTER `_closed=true` + `_session.Close` (the Deactivated guard already covers the lost-foreground intent; reviewer's repro now closes). P0-2 side-list dwell: WPF MouseEnter fires once per entry and the coordinator tick cannot be assumed (bar hidden until dwell; headless hosts), so the 180ms dwell clock now lives in GroupListWindow itself — a 50ms DispatcherTimer polls HoverDwellTracker while a row is hovered and raises GroupRowHover(DwellReady:true) once; ShellCoordinator shows the side list on DwellReady only (row wiring seeds the clock on enter; re-entry refreshes the row rect). P1-3 GroupPreviewWindow gained OnSourceInitialized (NOACTIVATE/TOOLWINDOW styles + hook + rounded corners) — no focus theft, no Alt+Tab, and no stranded focus after a row click. P1-4 per-row MouseEnter/Leave wiring removed from the preview rows (row→background flap) — window-level events only. P1-5 HideGroupPreview re-publishes surfaces (no ghost hit-surface blocking the fade). P2-6 HideAll resets _outsideSinceUtc + bar opacity + _exitPose.Reset() (new) so a mid-fade hide can't kill the next reveal; chip/list note-row/group-row/new-note clicks cancel the fade synchronously via OnCursorEnteredApp. P2-7 ForceActivateAndFocus returns bool (OS foreground really landed) and OpenQuickNote gates Machine.EditorFocused on it; the IsKeyboardFocusInside gate was self-defeating because Keyboard.Focus lands even when the OS steal fails. Lows: preview closed in Shutdown; SizeToContent removed (Height = clamped rect, so SetWindowPos can't fight layout); TextPrimary brush lookup made null-safe; ScreenPositioner double blank line removed; ShowNoteContextMenu "Open" now uses AfterMenu like every other item; force-visible guarded by IsLoaded (no mid-close resurrection). Reviewer answers: (a) ExitGraceStarted/ExitGraceResolved deleted (unreachable CS0067), ExitCompleted kept inert + documented; (c) SetCursorPose made internal + InternalsVisibleTo("StickyNotes.Tests") in StickyNotes.App.csproj. NOT FIXED (harness/expectation issues reported to orch-0.2 for testwriter): GroupPreviewHoverTests.Leaving_the_row... asserts Tick() leaves the side list open, but the FIRST tick fires the real exit cascade because the headless HitRegionTracker reports the cursor outside (their harness never injects an inside pose — the fix is tracker.SetCursorPose(() => true) in StartCoordinator, now internal-visible); QuickNoteWindowBehaviorTests asserts 29/33-char strings against the core-pinned 28-char TitlePreview cap (the commits themselves pass).
- **2026-11-16 — Human-approved v1 tweak: NoteWindow click-outside 5s grace.** Amends blueprint §8.3: clicking outside a FOCUSED note starts a 5s timer instead of closing immediately (to allow copy/paste from other windows); re-focusing within 5s cancels; on expiry it flushes + closes. `Esc` / `×` remain immediate. `NoteEditSession`/`QuickNoteSession` and all tests are unchanged. Implemented in `NoteWindow.xaml.cs` only.
- **2026-11-15 — Task 4 F2 ruling (blueprint-internal conflict).** §7's `IsFullscreenOnMonitor` snippet checks QUNS globally (blocks a 2nd monitor), conflicting with §10 Task 5 acceptance (d) "borderless fullscreen window on that monitor suppresses reveal **without blocking a second monitor**". Per the human's "follow the blueprint" ruling, the more specific acceptance criterion wins: QUNS D3D/PRESENTATION only suppresses when the foreground window is on the target monitor.
- **2026-11-15 — Human ruling: "follow the blueprint whenever there is a conflict."** Applied: blueprint.md is the default source of truth. Where §7's illustrative snippets conflict with the un-editable test headers for `BarStateMachine` / `NoteEditSession` / `QuickNoteSession`, the TEST HEADERS govern — because the blueprint itself (README "Things the blueprint must add" + each test header's "CONTRACT THE BLUEPRINT MUST ADOPT") defers to them, and the tests are the gate and must not be edited. All other blueprint design (§1–§6, §7 `IStickyCoreClient`/`IScreenPositioner`/Win32 snippets, §8, §9, §10) is followed exactly.
- **2026-11-15 — Task 3 split across the FFI boundary.** Task 3 spans Rust
  (`ffi.rs`) and C# (`Core/*`). Because the files are disjoint and the C ABI was
  already agreed (stickyDev-3 verified §6 verbatim), stickyDev-2 implements the
  Rust half and stickyDev-3 the C# half in parallel. Orchestrator runs the
  combined gate once both land.
- **2026-11-15 — Task 1 contradiction resolution (human-approved).** The Task 1
  gate is unsatisfiable as written (App WinExe has no source; Tests reference App
  types from Tasks 3/4/5/7/8; monolithic Tests project can't compile until all App
  types exist). Human approved (a)+(b)+(c):
  - (a) Add `win/StickyNotes.App/App.xaml` + `App.xaml.cs` (minimal WPF
    Application, no tray) to **Task 1** so the App builds.
  - (b) Task 1 also adds **compiling stubs** (exact signatures from the test
    headers + blueprint §7/§8; bodies `throw new NotImplementedException()`) for
    the App types the tests reference. This makes the whole sln compile so
    `dotnet test --filter Task=N` works at every gate. Each owning task replaces
    its stubs with real code. Stubs are approved Task-1 scope, not dead code.
  - (c) **Merge Task 7 + Task 8** into one task (implement NoteEditSession +
    QuickNoteSession together) because they share `NoteSessionTests.cs`.
- **2026-11-15 — Toolchain installed** by human: Rust 1.100.0 (MSVC) + .NET SDK
  8.0.428. Preflight green.

## Open questions
- ~~[BLOCKER] Task 1 blueprint contradiction~~ — RESOLVED by human (see Decisions).
- ~~Toolchain missing~~ — installed (see Decisions).

## Test renames
- None. (Worker renamed `EditorDeactivated` *parameters* in a stub to match the test's named args — not a test edit.)
- **2026-11-17 — Reviewer re-review fixes (N1-N5, no P0/P1 outstanding).** N1 (P2): the side list re-rendered ~5x/s while the pointer sat on a row — PollRowDwell now STOPS the 50ms dwell timer after firing DwellReady once (Reset+reseed was cycling it every ~180ms), and OnListGroupRowHovered de-dups on GroupPreviewWindow.ShownForGroupId so a re-fired Ready never rebuilds rows. N2: GroupListWindow.CloseList stops the dwell timer, resets the tracker and clears _hoveredGroupId (no stale Ready from a hidden list). N3: an editor closing while the cursor is outside AND a dropdown is open now seeds the 2s fade via StartExitFade() from the three close handlers (EditorClosedOutside), instead of relying on the tick branch that the dropdown-open guard suppresses. N4: removed the write-only _previewHoverGroupId field. N5: QuickNoteWindow.ShowQuickNote's focus retry now runs immediately when IsLoaded is already true instead of subscribing to a fired event. Verified: build 0 errors/0 warnings; dotnet test 203/203; cargo test 62/62; gate.ps1 -Task 10 PASSED; not committed.

- **2026-11-17 — Human bug: clicking a side-list note closed the note AND the group list instantly.** Root cause: OnPreviewNoteClicked called OpenNote() then HideGroupPreview(); hiding the side list left the cursor (still resting on the row) outside every surface, so the tick's OnCursorLeftApp closed the fresh note + dropdown. Fix: REMOVED the HideGroupPreview() call — the side list stays open while the note is open (matches 'side list stays while you read'); the normal row+list leave grace (~200ms) hides it when the cursor actually moves away. Old test GroupPreviewHoverTests.Clicking_a_note_in_the_side_list_opens_it asserts the obsolete fold-on-click behaviour (orch-0.2 has it with the human; NOT edited). The new regression test GroupPreviewNoteClickTests passes in isolation; its phase-2 assert is flaky under full-suite load because the STA host spuriously activates the passively-shown note (production uses ShowActivated=false + SWP_NOACTIVATE). Build 0 errors/0 warnings; cargo 62/62; gate 10 PASSED. Not committed; window-interaction tests are run manually per human instruction.
