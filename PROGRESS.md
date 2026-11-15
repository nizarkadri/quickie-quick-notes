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
| 3 | done | AUTOMATED GATE PASSED 2026-11-15 (18/18 + 54/54) | 7d065b4 (+ review fixes bf1101b) | stronger-model review APPROVED (Dev-2); F1-F4,F6 fixed & re-reviewed PASS; F5 nit | n/a |
| 4 | fix | AUTOMATED GATE PASSED 2026-11-15 (Task=3\|Task=4 41/41) | 10abeeb | stronger-model review APPROVED (Dev-2); F1-F5+F8 fixes dispatched | — |
| 5 | todo | — | — | — | — |
| 6 | todo | — | — | — | — |
| 7+8 | todo | — | — | — | — |
| 9 | todo | — | — | — | — |
| 10 | todo | — | — | — | — |

## Decisions
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