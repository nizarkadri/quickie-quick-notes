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
- **2026-11-16 — F4 (hover note preview) implemented and signed off (v0.2.1).** Bar chips and group-list note rows show a dark read-only tooltip with the first ~200 chars, resolved with `GetNote(id, markOpened: false)` so hovering never bumps `last_opened_at_ms` or reorders Recents. Implementation notes: (a) WPF throws `NotSupportedException` when an element-attached `ToolTip` has `StaysOpen=false` — attach the `TextBlock` content via `ToolTipService` instead; (b) WPF's default `InitialShowDelay` is 1000 ms, so F4 sets 350 ms explicitly. Commit d8be9b5.
- **2026-11-16 — Five post-v1 features DEFERRED to v0.2 (human ruling).** Requested: (F1) move an existing note into a new/existing group; (F2) explicit note titles; (F3) double the hover target area while keeping it centered; (F4) hover a note to preview it; (F5) hover a group to open its note list. Deferred after risk review, NOT implemented in v1. Analysis recorded for v0.2:
  - **F3 is blocked as literally specified** — `ScreenPositionerTests.cs:40-47,54,68` pin `GetTriggerRect` to 100x4 DIPs centered (also on negative-coordinate monitors). Enlarging the notch breaks 4 assertions and tests must not be edited. Only non-breaking route: keep 100x4 and widen the *invisible* cursor hit-test band in `ShellCoordinator.CursorInTrigger()`.
  - **F2 constrains the data model** — `t02_notes_and_groups.rs:41-59` pins `title_preview` as DERIVED (first non-empty line, <=28 chars, fallback "New note"). An explicit title must therefore be a separate nullable `title` column with UI preferring `title ?? title_preview`, leaving two competing title concepts permanently. Needs `user_version = 2` migration + a new FFI export.
  - **F1 is additive and cheap** — `group_id` already exists and is asserted (`t02_notes_and_groups.rs:25`, `t02_quick_capture.rs:86`); needs `set_note_group` + a 17th `sn_*` export + a UI control. No migration.
  - **F4 is low risk** — read-only `GetNote(id, markOpened:false)` + a tooltip; no schema/API/test contact.
  - **F5 reverses blueprint 1** ("Group Expansion: Click-to-Open, Single Open List") and needs an explicit blueprint amendment.
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