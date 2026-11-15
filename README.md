# Acceptance tests for the Sticky Notes v1 blueprint

A feature/task is **done only when its gate passes**:

    powershell -ExecutionPolicy Bypass -File .\scripts\gate.ps1 -Task N

The gate runs every automated test for tasks 1..N (so earlier work can't silently break), then you tick the
matching section of `docs\MANUAL_ACCEPTANCE.md` (focus/window behavior can't be unit tested).

## Install
Copy the folders into the repo root (they mirror the blueprint's tree), then:

1. `crates/sticky-core/Cargo.toml`: add `[dev-dependencies]` -> `tempfile = "3"`.
2. `crates/sticky-core/src/lib.rs` must re-export: `StickyCore, CoreError, QuickCaptureTarget, PinnedItem, WindowContext` (and the model types).
3. `win/StickyNotes.Tests`: reference xunit + the App project; add `<InternalsVisibleTo Include="StickyNotes.Tests" />` to the App csproj if types are internal.
4. Copy `scripts/gate.ps1` next to `build.ps1`.

## Feature -> tests

| Task | Feature | Automated gate | Manual gate |
|---|---|---|---|
| 1 | Skeleton, build scripts, PerMonitorV2 | `gate.ps1 -Task 1` (build + manifest + dll copy checks) | - |
| 2 | SQLite core: schema, CRUD, pin/group ordering, recents (max 5, no duplicates), delete cascade, quick-capture routing, append newline, persistence, thread safety | 5 files `t02_*.rs` (54 tests) | - |
| 3 | FFI boundary: UTF-8, error codes, no leaks, 8-thread safety, dispose | `CoreFfiIntegrationTests.cs` | - |
| 4 | DPI/multi-monitor placement, flip-left, clamping | `ScreenPositionerTests.cs` | - |
| 5 | Trigger + bar: 180 ms dwell, 200 ms grace, fullscreen suppression, focus lock, 0 idle polling | `BarStateMachineTests.cs` | Task 5 (incl. **focus spike**) |
| 6 | Group lists, group management | (covered by Task 2 core tests) | Task 6 |
| 7 | Note window: 350 ms autosave, flush, empty-new-note cleanup | `NoteEditSessionTests` | Task 7 |
| 8 | Quick note: Esc discards, Ctrl+Enter / click-outside commits, append | `QuickNoteSessionTests` | Task 8 |
| 9 | Hotplug, DPI changes, single instance, clean exit | - | Task 9 |
| 10 | End to end | all automated tests | Final gate |

## Things the blueprint must add (so these tests are possible)
The blueprint doesn't define these pure-logic seams, and UI code can't be unit tested without them. Add one line to the
Task 5/7/8 descriptions each; the exact contracts are in the header comment of each test file:

- `BarStateMachine` (+ `BarState`, `BarProbe`, `ShellAction`) - pure class extracted from `ShellCoordinator`, virtual clock. (Task 5)
- `NoteEditSession` - debounce, flush, empty-new-note cleanup. (Task 7)
- `QuickNoteSession` - Esc/Ctrl+Enter/click-outside commit rules. (Task 8)

## Names the tests assume (adapt to the real code ONLY so it compiles)
`StickyCoreClient(string dbPath)`, `StickyCoreException.Status`, `QuickCaptureTarget.Standalone()/Group(id)/Append(id)`,
`Note.UpdatedAtMs`, `PxRect/PxSize/MonitorBounds(PxRect Monitor, PxRect WorkArea)`, `ScreenPositioner`, plus the property
names listed at the top of each test file.

## Rules for the coding models
- Do not edit, skip, `#[ignore]`, or loosen any assertion to make a task pass. If a test seems wrong or contradicts the blueprint, stop and ask.
- Renaming types/constructors so the tests compile is allowed; changing expected values is not.

## Honest caveats
- These tests were written against the blueprint's spec, before any implementation exists, and have **not been compiled or run**. Expect a round of compile fixes (names, constructors) on first contact; that is normal and is the allowed kind of edit above.
- Timing tests use a virtual clock with ~10 ms slack at boundaries, so they are deterministic; the leak test (20k calls, <40 MB growth) is the one threshold you may need to tune.
- A green gate proves the logic; only the manual checklist proves focus behavior on a real desktop.
