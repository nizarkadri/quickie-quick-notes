# PROGRESS — StickyNotes v1 (Windows)

Orchestrator ledger. A task is `done` only when: (a) `scripts\gate.ps1 -Task N`
prints AUTOMATED GATE PASSED (run by orchestrator), (b) diff review passes,
(c) human signs off the MANUAL_ACCEPTANCE section (Tasks 5-9 + final),
(d) committed.

## Preflight (run before any task)

| Check | Result |
|---|---|
| OS = Windows x64 | PASS — Windows 11 Pro 10.0.26300 |
| Rust 1.80+ (MSVC) | **FAIL — rustc/cargo not installed, no ~/.cargo or ~/.rustup** |
| .NET SDK 8.0.x | **FAIL — dotnet host present but "No .NET SDKs were found"** |
| VS 2022 C++ Build Tools | PASS — Visual Studio Build Tools 2022 |
| blueprint.md + sources of truth | PASS — blueprint, spec, gate.ps1, MANUAL_ACCEPTANCE, all test files present |
| Task 5/7/8 test contracts match blueprint pure classes | PASS — BarStateMachineTests (BarStateMachine), NoteSessionTests (NoteEditSession + QuickNoteSession). Note: README/blueprint name them `NoteEditSessionTests.cs` / `QuickNoteSessionTests.cs` but they are combined in `NoteSessionTests.cs`; contracts match. |
| git initialized | PASS — branch `main` |
| git clean | PARTIAL — untracked: `README.md`, `context-aware-sticky-notes-spec.md`, `*.Zone.Identifier` metadata. Tracked tests/blueprint/gate are committed. |
| Implementation files (Task 1 outputs) | Not present yet — expected (Task 1 creates them). |

**BLOCKER:** Rust + .NET SDK missing → `cargo`/`dotnet`/`gate.ps1` cannot run.
No task can be gated until installed. Workers must not produce "done" work until
toolchain is confirmed.

## Task order & flags

| Task | Title | Parallel? | Review flag |
|---|---|---|---|
| 1 | Workspace skeleton & build/test scripts | — | — |
| 2 | Rust SQLite layer, migrations, domain logic | with Task 4 (after Task 1) | — |
| 3 | Rust extern "C" FFI + C# StickyCoreClient | — | **STRONGER-MODEL REVIEW** |
| 4 | Win32 interop, DPI positioning, fullscreen | with Task 2 (after Task 1) | **STRONGER-MODEL REVIEW** |
| 5 | BarStateMachine, TriggerWindow, BarWindow + focus spike | — | **STRONGER-MODEL REVIEW** |
| 6 | GroupListWindow & group management | — | — |
| 7 | NoteEditSession & NoteWindow | — | **STRONGER-MODEL REVIEW** |
| 8 | QuickNoteSession & QuickNoteWindow | — | — |
| 9 | Hotplug resilience, idle trim, polish | — | — |
| 10 | Final end-to-end gate | — | — |

Parallelizable: **only Task 2 and Task 4**, both after Task 1.

## Task status

| Task | Status | Gate result + date | Commit | Reviewer notes | Manual sign-off |
|---|---|---|---|---|---|
| 1 | todo | — | — | — | — |
| 2 | todo | — | — | — | — |
| 3 | todo | — | — | — | — |
| 4 | todo | — | — | — | — |
| 5 | todo | — | — | — | — |
| 6 | todo | — | — | — | — |
| 7 | todo | — | — | — | — |
| 8 | todo | — | — | — | — |
| 9 | todo | — | — | — | — |
| 10 | todo | — | — | — | — |

## Decisions
- (pending human) — none yet.

## Open questions
- Toolchain: Rust 1.80+ (MSVC) and .NET SDK 8.0.x are not installed. Need them
  to run any gate. Ask human to install / confirm.
- git housekeeping: untracked `*.Zone.Identifier` files clutter status. Propose
  `.gitignore` entry and committing `README.md` + `context-aware-sticky-notes-spec.md`.

## Test renames
- (none yet)