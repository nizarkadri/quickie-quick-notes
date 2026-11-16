# Testing & gates

This project is test-driven: a feature/task is **done only when its gate passes**.

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\gate.ps1 -Task N
```

`gate.ps1 -Task N` runs every automated test for tasks 1..N (so earlier work can't silently break), then you tick the matching section of `docs/MANUAL_ACCEPTANCE.md` (focus/window behaviour can't be unit tested).

## Feature → tests

| Task | Feature | Automated gate | Manual gate |
|---|---|---|---|
| 1 | Skeleton, build scripts, PerMonitorV2 | `gate.ps1 -Task 1` (build + manifest + dll copy checks) | - |
| 2 | SQLite core: schema, CRUD, pin/group ordering, recents (max 5, no duplicates), delete cascade, quick-capture routing, append newline, persistence, thread safety | 5 files `t02_*.rs` (54 tests) | - |
| 3 | FFI boundary: UTF-8, error codes, no leaks, 8-thread safety, dispose | `CoreFfiIntegrationTests.cs` | - |
| 4 | DPI/multi-monitor placement, flip-left, clamping | `ScreenPositionerTests.cs` | - |
| 5 | Trigger + bar: 180 ms dwell, 200 ms grace, fullscreen suppression, focus lock, 0 idle polling | `BarStateMachineTests.cs` | Task 5 (incl. **focus spike**) |
| 6 | Group lists, group management | (covered by Task 2 core tests) | Task 6 |
| 7 | Note window: 350 ms autosave, flush, empty-new-note cleanup | `NoteSessionTests.cs` (`NoteEditSessionTests`) | Task 7 |
| 8 | Quick note: Esc discards, Ctrl+Enter / click-outside commits, append | `NoteSessionTests.cs` (`QuickNoteSessionTests`) | Task 8 |
| 9 | Hotplug, DPI changes, single instance, clean exit | - | Task 9 |
| 10 | End to end | all automated tests | Final gate |

## Pure-logic seams the tests pin

The blueprint doesn't define these seams, and UI code can't be unit tested without them. The exact contracts are in the header comment of each test file:

- `BarStateMachine` (+ `BarState`, `BarProbe`, `ShellAction`) — pure class extracted from `ShellCoordinator`, virtual clock. (Task 5)
- `NoteEditSession` — debounce, flush, empty-new-note cleanup. (Task 7)
- `QuickNoteSession` — Esc / Ctrl+Enter / click-outside commit rules. (Task 8)

## Names the tests assume (adapt to the real code ONLY so it compiles)

`StickyCoreClient(string dbPath)`, `StickyCoreException.Status`, `QuickCaptureTarget.Standalone()/Group(id)/Append(id)`, `Note.UpdatedAtMs`, `PxRect/PxSize/MonitorBounds(PxRect Monitor, PxRect WorkArea)`, `ScreenPositioner`, plus the property names listed at the top of each test file.

## Rules for contributors / coding models

- Do **not** edit, skip, `#[ignore]`, or loosen any assertion to make a task pass. If a test seems wrong or contradicts the blueprint, stop and ask.
- Renaming types/constructors so the tests compile is allowed; changing expected values is not.

## Honest caveats

- A green gate proves the logic; only the manual checklist proves focus behaviour on a real desktop.
- Timing tests use a virtual clock with ~10 ms slack at boundaries, so they are deterministic; the leak test (20k calls, <40 MB growth) is the one threshold you may need to tune.
- Fullscreen suppression is a heuristic (QUNS + caption-less borderless window covering `rcMonitor`); generic windowed video with a caption may not suppress the bar.

## Current status

131 automated tests (54 Rust + 77 C#), all green on a clean checkout. Tasks 1–9 complete; manual acceptance signed off. See `PROGRESS.md` for the full ledger.