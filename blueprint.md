# StickyNotes v1 (Windows) — Architecture & Implementation Blueprint

> **Scope:** v1 only (auto-hiding top-center trigger, bookmark-style bar, pinned notes/groups, recents, group dropdowns, single side-branching note editor, central `+` quick-note capture/append). No active-window context engine, cloud sync, search UI, or macOS UI.

---

## 1. Key Decisions

### UI Framework: WPF on `.NET 8` (`net8.0-windows`)
**Recommendation & Default:** Use **WPF (`.NET 8`)** with `WindowChrome` (`GlassFrameThickness = 0`, Win11 `DwmSetWindowAttribute` `DWMWCP_ROUND`) and `PerMonitorV2` DPI awareness. Do **not** set `AllowsTransparency="True"` on the bar, dropdown, or note windows (avoid `WS_EX_LAYERED` bitmap redirection, mixed-DPI blur, and animation stutter).
**Why & Trade-off:** Compared to WinUI 3, WPF starts faster, idles at ~35–50 MB working set, deploys without MSIX/Windows App SDK runtime friction, and exposes raw HWNDs cleanly for `WS_EX_NOACTIVATE` and `SW_SHOWNOACTIVATE`. Trade-off: you style clean modern templates manually instead of getting WinUI 3 controls out of the box.

### Rust <-> C# Boundary: Hand-Written `extern "C"` ABI + UTF-8 JSON DTOs
**Recommendation & Default:** Do **not** use UniFFI or any code generator for C#. Keep a 100% platform-neutral `StickyCore` Rust API internally, exposed via 16 `extern "C"` functions (`cdylib` `sticky_core.dll`). Pass scalar inputs as C primitives (`i64`, `bool`, null-terminated UTF-8 `[MarshalAs(UnmanagedType.LPUTF8Str)] string`); return complex read results as a single `#[repr(C)] FfiResult { status: i32, payload: *mut c_char }` containing UTF-8 JSON (on success) or an error message (on failure), freed once in C# `finally` via `sn_string_free`. Serialize SQLite access via a single `std::sync::Mutex<rusqlite::Connection>` inside `StickyCore` (`WAL` mode).
**Why & Trade-off:** Upstream UniFFI does not support C# in-tree, and third-party generators have strict version lockstep issues that break clean builds. With 16 functions and <3 KB JSON snapshots (<20 µs `System.Text.Json` cost), one allocation + one free function eliminates nested C-struct pointer bugs while leaving pure `StickyCore` untouched for future Swift reuse. Trade-off: DTO definitions exist in both Rust (`serde`) and C# (`record` types), kept in sync by an integration test.

### Group Expansion: Click-to-Open, Single Open List
**Recommendation & Default:** Expand a group on **left-click**, never hover. Allow **at most one** group dropdown open at a time; clicking another group switches immediately (saving and closing any open `NoteWindow`).
**Why & Trade-off:** Hover-opening along a horizontal strip triggers accidental flyouts when moving diagonally from the center `+` toward pinned notes. Trade-off: browsing between two groups takes one extra click per group, in exchange for zero accidental visual churn.

### Trigger & Keep-Open Geometry: Separate `100×4 DIP` Trigger Per Monitor + Inflated Surface Polling
**Recommendation & Default:** Create one tiny borderless `TriggerWindow` (`100×4 DIPs`, centered horizontally at `rcWork.Top`) per connected monitor, rendering a subtle `100×3 DIP` pill (`#38888888`, `#80888888` on hover). Keep `BarWindow` pre-created (`Visibility.Hidden` when idle) and reveal it after a **180ms hover dwell** using `SW_SHOWNOACTIVATE` + a **140ms** GPU `TranslateTransform.Y` (`-44 -> 0`) slide. While visible, run a **50ms `GetCursorPos` timer** (stopped completely when hidden -> 0% idle CPU) checking whether the cursor lies inside any visible surface (`Trigger`, `Bar`, `GroupList`, `NoteWindow`, `QuickNote`) inflated by **`12 DIP` corridor padding**. Start a **200ms exit grace timer** when outside.
**Why & Trade-off:** Separate `100×4` triggers never block maximized browser tabs or window `Close` buttons and avoid global mouse hooks across monitors. Polling `GetCursorPos` *only while open* avoids `WM_MOUSELEAVE` flicker across multi-HWND gaps. Trade-off: 2–3 tiny HWNDs (~150 KB total) instead of one window.

### Focus & Activation Model
**Recommendation & Default:**
- `TriggerWindow`, `BarWindow`, `GroupListWindow`: `WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE` + `ShowActivated = false` + gated `WM_MOUSEACTIVATE -> MA_NOACTIVATE`. Clicking groups, pins, or inline delete never steals focus from the active app.
- **Inline text fields inside `GroupListWindow` (`+ New Group` and `Rename Group`):** A `WS_EX_NOACTIVATE` window cannot receive `WM_KEYDOWN`/`WM_CHAR`. When the user clicks `+ New Group` or `Rename` (`Button.Click`, *not* `GotFocus`), call `FocusHelper.BeginInlineEdit(window, textBox)` to capture `prevForegroundHwnd`, pause the `WM_MOUSEACTIVATE` block, clear `WS_EX_NOACTIVATE`, `Activate()` + focus the `TextBox`, and enter `OpenFocusedLock`. On `Enter`, `Esc`, or `Deactivated`, `EndInlineEdit` commits/cancels, re-applies `WS_EX_NOACTIVATE` + `MA_NOACTIVATE`, and restores `prevForegroundHwnd` (on `Enter`/`Esc`). `BarWindow` itself contains no `TextBox` (`+ New Group` lives inside `Groups ▾` in `GroupListWindow`).
- `NoteWindow`: `WS_EX_TOPMOST | WS_EX_TOOLWINDOW`, `ShowInTaskbar = false`, **without** `WS_EX_NOACTIVATE`. When clicking an existing note, open with `ShowActivated = false` (`SW_SHOWNOACTIVATE`) so the user can read a reference note while continuing to type in their terminal; clicking inside its `TextBox` naturally activates it for full IME/clipboard editing. When clicking **"New Note"**, explicitly `Activate()` and focus the `TextBox`.
- `QuickNoteWindow` (`+`): `WS_EX_TOPMOST | WS_EX_TOOLWINDOW`, `ShowInTaskbar = false`. Record `prevForegroundHwnd = GetForegroundWindow()` before opening, then activate via `FocusHelper.ForceActivate` (`SetForegroundWindow` with `AttachThreadInput` fallback if Windows foreground-lock blocks handoff from a `NOACTIVATE` click; note UIPI prevents stealing focus from an elevated Admin window unless clicked directly). Restore `prevForegroundHwnd` on `Ctrl+Enter` or `Esc`.
**Why & Trade-off:** Supports zero-focus-steal reference reading, full native text input, and inline group naming with zero extra popup HWNDs, while keeping all windows out of Taskbar and `Alt+Tab`.

### Autosave, Empty Notes, Recents, and Dismissal Rules
**Recommendation & Default:**
- **Autosave & Quick-Note commit:** `NoteWindow` uses a **350ms trailing debounce** after edits, plus **immediate synchronous flush** before switching notes, hiding, losing focus, `Esc`, or app exit. `QuickNoteWindow` (`+`) has no Save button: **`Ctrl+Enter` or click-outside commits** (`QuickCapture`) if text is non-whitespace, while **`Esc` always cancels/discards** (preserving universal "panic cancel" muscle memory so `Esc` never accidentally appends onto a note).
- **Empty new note:** Insert row immediately on "New Note" so a real `note_id` exists for pinning/autosave; mark session `CreatedThisOpen = true`. On close, if `CreatedThisOpen && !IsPinned && string.IsNullOrWhiteSpace(Body)`, delete the row. Existing notes cleared by the user are kept empty (and can be deleted without confirmation).
- **Recents:** Update `last_opened_at_ms` on note creation, opening in `NoteWindow`, and Quick-Note **append** (not on pin toggles or autosave ticks). Cap Bar Recents at **5 items**, excluding **standalone pinned notes** (`is_pinned = 0`) so chips never duplicate on the bar, while allowing recently opened notes inside pinned groups to surface in Recents for 1-click return.
- **Dismissal:** While `NoteWindow`, `QuickNoteWindow`, or inline group edit holds keyboard focus, **pause cursor-exit auto-hide** so stray mouse movement never closes an active editor mid-sentence. **Click-outside** (external focus loss while cursor is outside app surfaces) flushes `NoteWindow` (or commits non-empty `QuickNoteWindow`) and hides all flyouts + Bar. **`Esc`** flushes `NoteWindow` (or **discards** `QuickNoteWindow`), closes that editor, hides the bar if cursor is outside, and restores `prevForegroundHwnd`. **Bar hide** always flushes and closes `NoteWindow` + `GroupList` atomically.

### Fullscreen Suppression
**Recommendation & Default:** Check fullscreen state **only when the 180ms trigger dwell completes** on target monitor `M` (zero background hooks). Suppress reveal if: (1) `SHQueryUserNotificationState` returns `QUNS_RUNNING_D3D_FULL_SCREEN` or `QUNS_PRESENTATION_MODE` (`QUNS_BUSY` is intentionally excluded because it is system-global across all monitors and triggers false positives during DND/screen-share), OR (2) `GetForegroundWindow()` (excluding desktop `Progman`/`WorkerW`/`GetShellWindow()`) is on `M`, is visible, lacks `WS_CAPTION`, and its `GetWindowRect` covers `GetMonitorInfo(M).rcMonitor`.
**Limitations:** Suppresses borderless/fullscreen (`F11`) apps and games on `M`, *not* video playback specifically — an `F11` browser reading docs is suppressed just like `F11` YouTube, whereas windowed YouTube inside a normal maximized browser is **not** suppressed. The `100×3` subtle notch remains visible unless hidden on foreground changes (acceptable for v1 at ~22% opacity).

---

## 2. Concerns & Pushback on Spec / Fixed Choices

1. **Spec specifies Mozilla UniFFI for Windows C# (`System Architecture` diagram):** Upstream UniFFI does not ship a C# generator, and third-party C# generators have strict version lockstep issues that add brittle codegen steps for cheaper coding models. **Smallest fix:** Use pure Rust `StickyCore` + a 16-function `extern "C"` JSON-payload ABI now; Swift can adopt UniFFI or C interop later with zero `StickyCore` changes.
2. **Spec says "combined bounding box" of bar + vertical list + side note keeps bar open:** An axis-aligned union bounding box of a wide top bar and a tall dropdown + side note forms a huge dead rectangle over the user's editor, keeping the bar stuck open when moving the mouse back into work. **Smallest fix:** Hit-test the **union of individual window rects**, each inflated by `12 DIPs`, plus a `200ms` exit grace timer.
3. **Center `+` alignment vs variable-length Pinned & Recent items:** If the bar uses a centered horizontal stack, having 7 pinned items and 1 recent item shifts `+` far off screen-center, breaking the spec's promise that hovering the center trigger lands directly over `+`. **Smallest fix:** Use a 3-column `Grid` (`1* | Auto | 1*`) centered on the monitor so `+` is geometrically pinned to `MonitorCenterX`, with independent horizontal overflow/scroll for Left (Pinned) and Right (Recents).
4. **Delete confirmation dialogs vs `TOPMOST` + auto-hide bar:** Calling `MessageBox.Show` or opening a modal window triggers focus/cursor transitions that race the auto-hide timer or z-order behind `WS_EX_TOPMOST`. **Smallest fix:** Render **inline confirmation** (`Delete (4 notes)? [Delete] [Cancel]`) directly inside the group header / note toolbar. Zero extra HWNDs, zero focus steal.

---

## 3. Top Risks & Mitigations

| Risk | Why It Happens | Mitigation |
| :--- | :--- | :--- |
| **Mixed-DPI positioning drift** (e.g., 150% laptop + 100% external monitor) | Mixing Win32 physical pixels (`GetCursorPos`, `rcMonitor`) with WPF logical DIPs (`Window.Left/Top`). | Declare `PerMonitorV2` in `app.manifest`. Compute all screen placement in **physical pixels** via Win32 `SetWindowPos`, converting WPF `ActualWidth/Height` using `VisualTreeHelper.GetDpi(visual)`. |
| **FFI memory leaks or panics crashing CLR** | Unwinding a Rust `panic!` across `extern "C"` is UB; forgetting to free strings leaks RAM. | Wrap every `extern "C"` body in `std::panic::catch_unwind`. Return exactly one `FfiResult` struct freed in a single C# helper `InvokeJson<T>` `try/finally` block. |
| **Hover flicker across multi-window gaps** | Relying on WPF `MouseLeave` fires transiently when crossing from `BarWindow` to `GroupListWindow` or `NoteWindow`. | Ignore `MouseLeave` once open; drive visibility solely from one `HitRegionTracker` (`50ms` `GetCursorPos` check against `12 DIP`-inflated rects + `200ms` exit delay). |
| **Note Title UX friction** | Requiring a separate title field slows quick capture; having none makes bar chips unreadable. | Store only `body` on notes; compute `title_preview` in Rust from the first non-empty trimmed line (max 28 chars, fallback `"New note"`). |
| **KVM / sleep / monitor hotplug orphaning windows** | `HMONITOR` handles invalidate when displays disconnect or change resolution. | Subscribe to `SystemEvents.DisplaySettingsChanged` + `WM_DISPLAYCHANGE`; force-flush/hide open flyouts and rebuild `TriggerWindow` instances per active monitor. |

---

## 4. Project Structure

```text
stickyNotes/
├── Cargo.toml                                    # Workspace root configuring sticky-core crate and release profile (opt-level, strip)
├── scripts/
│   ├── build.ps1                                 # Single clean-checkout build script (cargo build --release + copy DLL + dotnet build)
│   └── test.ps1                                  # Single test runner script (cargo test + dotnet test)
├── crates/sticky-core/
│   ├── Cargo.toml                                # Rust crate (cdylib + rlib): rusqlite (bundled), serde, serde_json, thiserror
│   └── src/
│       ├── lib.rs                                # Public Rust exports (StickyCore, models, errors) + module wiring
│       ├── models.rs                             # Platform-neutral DTOs (Note, NoteSummary, GroupSummary, PinnedItem, BarSnapshot, QuickCaptureReq, WindowContext)
│       ├── errors.rs                             # CoreError enum (Sqlite, NotFound, Validation, Internal) with error codes
│       ├── db.rs                                 # SQLite connection setup (WAL, FK), PRAGMA user_version migrations, SQL queries
│       ├── core.rs                               # Thread-safe StickyCore API (Mutex<Connection>, CRUD, pin ordering, recents, append routing)
│       └── ffi.rs                                # Thin extern "C" ABI (16 functions), catch_unwind wrapper, FfiResult & sn_string_free
└── win/
    ├── StickyNotes.sln                           # .NET 8 solution containing StickyNotes.App and StickyNotes.Tests
    ├── StickyNotes.App/
    │   ├── StickyNotes.App.csproj                # net8.0-windows WPF exe; copies sticky_core.dll to output
    │   ├── app.manifest                          # PerMonitorV2 DPI awareness declaration
    │   ├── App.xaml                              # Shared lightweight ResourceDictionary (colors, chip/button styles, zero heavy themes)
    │   ├── App.xaml.cs                           # Single-instance mutex, tray icon (Exit), DB path init (%LOCALAPPDATA%\StickyNotes), lifecycle
    │   ├── Core/
    │   │   ├── NativeCoreMethods.cs              # P/Invoke declarations for sticky_core.dll extern "C" functions
    │   │   ├── Models.cs                         # System.Text.Json C# records matching Rust DTOs
    │   │   └── StickyCoreClient.cs               # Safe IDisposable wrapper: InvokeJson<T>, try/finally sn_string_free, error mapping
    │   ├── Interop/
    │   │   ├── Win32.cs                          # P/Invoke for User32/Shell32/Shcore/Dwmapi (SetWindowPos, MonitorFromWindow, SHQueryUserNotificationState, etc.)
    │   │   ├── ScreenPositioner.cs               # Physical-pixel monitor math, DPI conversion, bar/dropdown/side-note flip-left placement
    │   │   ├── FocusHelper.cs                    # Non-activating window styles, foreground capture/restore, fullscreen check
    │   │   └── HitRegionTracker.cs               # 50ms active-only cursor hit-test across inflated window rects + 200ms exit grace timer
    │   ├── State/
    │   │   ├── BarStateMachine.cs                # Pure HWND-free trigger/bar/focus-lock/grace transition logic
    │   │   ├── NoteEditSession.cs                # Pure HWND-free note edit session (dirty tracking, flush, empty-new delete, delete-confirm rule)
    │   │   ├── QuickNoteSession.cs               # Pure HWND-free quick-capture session (target state, commit-if-non-empty vs Esc-cancel)
    │   │   └── ShellCoordinator.cs               # Thin UI orchestrator wiring HWNDs/timers to BarStateMachine & sessions
    │   └── Windows/
    │       ├── TriggerWindow.xaml(.cs)           # 100x4 DIP top-center notch window (one per monitor) with 180ms hover dwell timer
    │       ├── BarWindow.xaml(.cs)               # Permanently non-activating bar: [Pinned Rail + Groups ▾] | [+] | [Recents]
    │       ├── GroupListWindow.xaml(.cs)         # Vertical list: "+ New Note", group notes, pin/inline-delete, temporary-activate rename/new-group
    │       ├── NoteWindow.xaml(.cs)              # Thin floating note view + 350ms DispatcherTimer delegating to NoteEditSession
    │       └── QuickNoteWindow.xaml(.cs)         # Thin focused "+" view delegating to QuickNoteSession
    └── StickyNotes.Tests/
        ├── StickyNotes.Tests.csproj              # net8.0-windows xUnit test project referencing StickyNotes.App
        ├── CoreFfiIntegrationTests.cs            # P/Invoke round-trip tests against compiled sticky_core.dll (CRUD, pins, append, Unicode, errors)
        ├── ScreenPositionerTests.cs              # Pure math unit tests for multi-monitor bounds, DPI scaling, and right-edge note flip
        ├── BarStateMachineTests.cs               # Unit tests for BarStateMachine (dwell, fullscreen guard, focus-lock, exit grace)
        ├── NoteEditSessionTests.cs               # Unit tests for NoteEditSession (debounce save, flush, empty-new delete, conditional delete confirm)
        └── QuickNoteSessionTests.cs              # Unit tests for QuickNoteSession (Standalone/Group/Append commit, empty no-op, Esc discard)
```

---

## 5. SQLite Schema, Migrations & Context-Rules Seam

### Connection Pragmas (applied on every `StickyCore::open`)
```sql
PRAGMA journal_mode = WAL;
PRAGMA synchronous = NORMAL;
PRAGMA foreign_keys = ON;
PRAGMA busy_timeout = 5000;
```

### Schema (`user_version = 1`)
Pin state lives directly on `groups` and `notes`. Because `notes.group_id` uses `ON DELETE CASCADE`, deleting a group atomically deletes all of its notes *and* removes both the group's pin and any member note's pin in one statement.

```sql
CREATE TABLE groups (
    id              INTEGER PRIMARY KEY,
    name            TEXT    NOT NULL CHECK (length(trim(name)) > 0),
    is_pinned       INTEGER NOT NULL DEFAULT 1 CHECK (is_pinned IN (0, 1)),
    pinned_at_ms    INTEGER NULL,
    created_at_ms   INTEGER NOT NULL,
    CHECK ((is_pinned = 1 AND pinned_at_ms IS NOT NULL) OR (is_pinned = 0 AND pinned_at_ms IS NULL))
);

CREATE TABLE notes (
    id                  INTEGER PRIMARY KEY,
    group_id            INTEGER NULL REFERENCES groups(id) ON DELETE CASCADE,
    body                TEXT    NOT NULL DEFAULT '',
    is_pinned           INTEGER NOT NULL DEFAULT 0 CHECK (is_pinned IN (0, 1)),
    pinned_at_ms        INTEGER NULL,
    created_at_ms       INTEGER NOT NULL,
    updated_at_ms       INTEGER NOT NULL,
    last_opened_at_ms   INTEGER NOT NULL,
    CHECK ((is_pinned = 1 AND pinned_at_ms IS NOT NULL) OR (is_pinned = 0 AND pinned_at_ms IS NULL))
);

-- Pinned rail: groups + notes ordered by pinned_at_ms ASC (newest on right)
CREATE INDEX idx_groups_pinned ON groups(pinned_at_ms ASC, id ASC) WHERE is_pinned = 1;
CREATE INDEX idx_notes_pinned  ON notes(pinned_at_ms ASC, id ASC)  WHERE is_pinned = 1;

-- Bar Recents: top 5 unpinned notes ordered by last_opened_at_ms DESC
CREATE INDEX idx_notes_recents ON notes(is_pinned, last_opened_at_ms DESC, id DESC);

-- Group vertical list: notes in group ordered by last_opened_at_ms DESC
CREATE INDEX idx_notes_group_opened ON notes(group_id, last_opened_at_ms DESC, id DESC);
```

### Migration Approach
No external migration library. Embed static SQL slices `const MIGRATIONS: &[(i32, &str)] = &[(1, V1_SQL)];`. On `StickyCore::open(db_path)`:
1. Read `PRAGMA user_version`. If `user_version > CURRENT_SCHEMA_VERSION`, return `CoreError::UnsupportedSchema`.
2. For each `(ver, sql)` where `ver > current`, run `BEGIN IMMEDIATE; <sql>; PRAGMA user_version = <ver>; COMMIT;`.

### Future Context-Rules Seam (Do NOT build in v1)
- **Reserved v2 table (`user_version = 2`):**
  ```sql
  CREATE TABLE context_rules (
      id            INTEGER PRIMARY KEY,
      group_id      INTEGER NOT NULL REFERENCES groups(id) ON DELETE CASCADE,
      match_kind    TEXT    NOT NULL CHECK (match_kind IN ('exe_name', 'dir_prefix', 'title_substr')),
      pattern       TEXT    NOT NULL CHECK (length(trim(pattern)) > 0),
      priority      INTEGER NOT NULL DEFAULT 0,
      created_at_ms INTEGER NOT NULL
  );
  ```
- **Core API seam:** `StickyCore::get_bar_snapshot(&self, context: Option<&WindowContext>) -> Result<BarSnapshot, CoreError>`. In v1, `WindowContext { exe_name: Option<String>, cwd: Option<String>, window_title: Option<String> }` is defined in `models.rs`, and `get_bar_snapshot` ignores `context` (C# passes `null` / `sn_get_bar_snapshot(handle)` calls `core.get_bar_snapshot(None)`).

---

## 6. Rust Core API

### Platform-Neutral Rust Types & Signatures (`crates/sticky-core/src/{models,errors,core}.rs`)
```rust
#[derive(Debug, Serialize, Deserialize, Clone)]
pub struct Note {
    pub id: i64,
    pub group_id: Option<i64>,
    pub body: String,
    pub title_preview: String, // Derived: first non-empty line trimmed to <=28 chars, fallback "New note"
    pub is_pinned: bool,
    pub pinned_at_ms: Option<i64>,
    pub created_at_ms: i64,
    pub updated_at_ms: i64,
    pub last_opened_at_ms: i64,
}

#[derive(Debug, Serialize, Deserialize, Clone)]
pub struct NoteSummary {
    pub id: i64,
    pub group_id: Option<i64>,
    pub title_preview: String,
    pub is_empty: bool,        // Used by UI to decide whether delete needs confirmation
    pub is_pinned: bool,
    pub pinned_at_ms: Option<i64>,
    pub last_opened_at_ms: i64,
}

#[derive(Debug, Serialize, Deserialize, Clone)]
pub struct GroupSummary {
    pub id: i64,
    pub name: String,
    pub note_count: usize,     // Used by UI delete confirmation ("Delete 'Work' and 3 notes?")
    pub is_pinned: bool,
    pub pinned_at_ms: Option<i64>,
    pub created_at_ms: i64,
}

#[derive(Debug, Serialize, Deserialize, Clone)]
#[serde(tag = "kind", rename_all = "snake_case")]
pub enum PinnedItem {
    Group(GroupSummary),
    Note(NoteSummary),
}

#[derive(Debug, Serialize, Deserialize, Clone)]
pub struct BarSnapshot {
    pub pinned_items: Vec<PinnedItem>,   // Interleaved by pinned_at_ms ASC (newest on right)
    pub recent_notes: Vec<NoteSummary>,  // Max 5, is_pinned = false, last_opened_at_ms DESC
    pub all_groups: Vec<GroupSummary>,   // Ordered by created_at_ms ASC (powers "Groups ▾" & QuickNote picker)
}

#[derive(Debug, Serialize, Deserialize, Clone)]
#[serde(tag = "mode", rename_all = "snake_case")]
pub enum QuickCaptureTarget {
    Standalone,
    Group { group_id: i64 },
    Append { note_id: i64 },
}

#[derive(Debug, Default, Clone)]
pub struct WindowContext {
    pub exe_name: Option<String>,
    pub cwd: Option<String>,
    pub window_title: Option<String>,
}

#[derive(Debug, thiserror::Error)]
pub enum CoreError {
    #[error("not found: {0}")]        NotFound(String),          // status = 1
    #[error("validation: {0}")]       Validation(String),        // status = 2
    #[error("sqlite error: {0}")]     Sqlite(#[from] rusqlite::Error), // status = 3
    #[error("unsupported schema")]    UnsupportedSchema,         // status = 4
    #[error("internal panic: {0}")]   Internal(String),          // status = 5
}

pub struct StickyCore {
    conn: std::sync::Mutex<rusqlite::Connection>,
}

impl StickyCore {
    pub fn open(db_path: &std::path::Path) -> Result<Self, CoreError>;
    pub fn get_bar_snapshot(&self, _ctx: Option<&WindowContext>) -> Result<BarSnapshot, CoreError>;
    pub fn list_group_notes(&self, group_id: i64) -> Result<Vec<NoteSummary>, CoreError>; // last_opened_at_ms DESC
    pub fn list_append_targets(&self, limit: usize) -> Result<Vec<NoteSummary>, CoreError>; // all notes by last_opened_at_ms DESC
    pub fn get_note(&self, note_id: i64, mark_opened: bool) -> Result<Note, CoreError>;
    pub fn create_note(&self, group_id: Option<i64>, body: &str) -> Result<Note, CoreError>; // sets last_opened_at_ms = now
    pub fn update_note_body(&self, note_id: i64, body: &str) -> Result<NoteSummary, CoreError>; // updates updated_at_ms only
    pub fn delete_note(&self, note_id: i64) -> Result<(), CoreError>;
    pub fn set_note_pinned(&self, note_id: i64, pinned: bool) -> Result<NoteSummary, CoreError>;
    pub fn create_group(&self, name: &str, pinned: bool) -> Result<GroupSummary, CoreError>; // default pinned = true in UI
    pub fn rename_group(&self, group_id: i64, name: &str) -> Result<GroupSummary, CoreError>;
    pub fn delete_group(&self, group_id: i64) -> Result<(), CoreError>; // cascades to notes
    pub fn set_group_pinned(&self, group_id: i64, pinned: bool) -> Result<GroupSummary, CoreError>;
    pub fn quick_capture(&self, text: &str, target: QuickCaptureTarget) -> Result<Note, CoreError>;
    // Append rule inside quick_capture:
    // new_body = if existing.is_empty() { text.to_owned() } else { format!("{existing}\n{text}") };
    // updates updated_at_ms = now AND last_opened_at_ms = now inside one transaction.
}
```

> **Note on Unpinned Groups UX:** Because the user can unpin a group (`set_group_pinned(id, false)`), `BarSnapshot.all_groups` (ordered by `created_at_ms ASC`) powers a compact **`Groups ▾`** button on the bar (and the Quick-Note group dropdown), ensuring unpinned groups remain reachable to browse, re-pin, rename, or delete.

### C ABI, Memory Ownership, Errors & Threading (`crates/sticky-core/src/ffi.rs`)
- **Threading:** `StickyCore` is `Send + Sync` via `std::sync::Mutex<Connection>`. Lock acquisition poisons only if a panic occurred; recover or return `status = 5`. Any C# thread may call any `sn_*` function safely, except `sn_core_close` which must be called once at shutdown after callers stop.
- **Panic safety:** Every `extern "C"` function wraps its body in `std::panic::catch_unwind` and converts panics into `FfiResult { status: 5, payload: c_string("panic...") }`.
- **Memory ownership:**
  - Caller owns all input `*const c_char` buffers (`LPUTF8Str`); Rust borrows `&CStr` only during the call.
  - Rust allocates `FfiResult.payload` via `CString::into_raw()`. If `payload != null`, C# **must** free it exactly once via `sn_string_free(payload)`.

```rust
#[repr(C)]
pub struct FfiResult {
    pub status: i32,              // 0 = OK (payload is UTF-8 JSON), >0 = CoreError code (payload is UTF-8 message)
    pub payload: *mut c_char,     // Always non-null UTF-8 CString ( "{}" for unit () ); free with sn_string_free
}

pub enum StickyCoreHandle {} // Opaque pointer type for *mut StickyCore

// Lifecycle & memory
#[no_mangle] pub extern "C" fn sn_core_open(db_path_utf8: *const c_char, out_handle: *mut *mut StickyCoreHandle) -> FfiResult;
#[no_mangle] pub extern "C" fn sn_core_close(handle: *mut StickyCoreHandle);
#[no_mangle] pub extern "C" fn sn_string_free(ptr: *mut c_char);

// Queries & mutations (13 functions mirroring StickyCore)
#[no_mangle] pub extern "C" fn sn_get_bar_snapshot(h: *mut StickyCoreHandle) -> FfiResult;
#[no_mangle] pub extern "C" fn sn_list_group_notes(h: *mut StickyCoreHandle, group_id: i64) -> FfiResult;
#[no_mangle] pub extern "C" fn sn_list_append_targets(h: *mut StickyCoreHandle, limit: u32) -> FfiResult;
#[no_mangle] pub extern "C" fn sn_get_note(h: *mut StickyCoreHandle, note_id: i64, mark_opened: bool) -> FfiResult;
#[no_mangle] pub extern "C" fn sn_create_note(h: *mut StickyCoreHandle, has_group: bool, group_id: i64, body_utf8: *const c_char) -> FfiResult;
#[no_mangle] pub extern "C" fn sn_update_note_body(h: *mut StickyCoreHandle, note_id: i64, body_utf8: *const c_char) -> FfiResult;
#[no_mangle] pub extern "C" fn sn_delete_note(h: *mut StickyCoreHandle, note_id: i64) -> FfiResult;
#[no_mangle] pub extern "C" fn sn_set_note_pinned(h: *mut StickyCoreHandle, note_id: i64, pinned: bool) -> FfiResult;
#[no_mangle] pub extern "C" fn sn_create_group(h: *mut StickyCoreHandle, name_utf8: *const c_char, pinned: bool) -> FfiResult;
#[no_mangle] pub extern "C" fn sn_rename_group(h: *mut StickyCoreHandle, group_id: i64, name_utf8: *const c_char) -> FfiResult;
#[no_mangle] pub extern "C" fn sn_delete_group(h: *mut StickyCoreHandle, group_id: i64) -> FfiResult;
#[no_mangle] pub extern "C" fn sn_set_group_pinned(h: *mut StickyCoreHandle, group_id: i64, pinned: bool) -> FfiResult;
#[no_mangle] pub extern "C" fn sn_quick_capture(h: *mut StickyCoreHandle, text_utf8: *const c_char, target_json_utf8: *const c_char) -> FfiResult;
```

---

## 7. C# Component Interfaces & Tricky Win32 Snippets

### Core Client (`IStickyCoreClient`)
```csharp
public interface IStickyCoreClient : IDisposable
{
    BarSnapshot GetBarSnapshot();
    IReadOnlyList<NoteSummary> ListGroupNotes(long groupId);
    IReadOnlyList<NoteSummary> ListAppendTargets(int limit = 30);
    Note GetNote(long noteId, bool markOpened);
    Note CreateNote(long? groupId, string body = "");
    NoteSummary UpdateNoteBody(long noteId, string body);
    void DeleteNote(long noteId);
    NoteSummary SetNotePinned(long noteId, bool pinned);
    GroupSummary CreateGroup(string name, bool pinned = true);
    GroupSummary RenameGroup(long groupId, string name);
    void DeleteGroup(long groupId);
    GroupSummary SetGroupPinned(long groupId, bool pinned);
    Note QuickCapture(string text, QuickCaptureTarget target);
}

// Single marshaling helper inside StickyCoreClient:
private static T InvokeJson<T>(Func<FfiResult> call)
{
    FfiResult res = call();
    try
    {
        string text = res.Payload != IntPtr.Zero
            ? Marshal.PtrToStringUTF8(res.Payload) ?? string.Empty
            : string.Empty;
        if (res.Status != 0)
            throw new StickyCoreException(res.Status, text);
        return JsonSerializer.Deserialize<T>(text, JsonOpts)!;
    }
    finally
    {
        if (res.Payload != IntPtr.Zero)
            NativeCoreMethods.sn_string_free(res.Payload);
    }
}
```

### Pure Testable State & Session Classes (`State/`) + Thin UI Contracts
`BarStateMachine`, `NoteEditSession`, and `QuickNoteSession` must contain **zero** references to `System.Windows.Window`, `DispatcherTimer`, or Win32 HWNDs so xUnit tests exercise all behavioral rules directly.

```csharp
// 1. Pure Trigger/Bar state machine (no timers or HWNDs; returns BarEffect flags executed by ShellCoordinator)
public enum BarVisibilityState { Hidden, DwellPending, OpenHover, OpenFocusedLock, ExitGrace }
public enum BarEffect { None, StartDwellTimer, StopDwellTimer, ShowBar, StartExitGraceTimer, CancelExitGraceTimer, FlushAndHideAll }

public sealed class BarStateMachine
{
    public BarVisibilityState State { get; private set; } = BarVisibilityState.Hidden;
    public IntPtr ActiveMonitor { get; private set; }

    public BarEffect OnTriggerEnter(IntPtr monitor);
    public BarEffect OnTriggerLeave();
    public BarEffect OnDwellElapsed(bool cursorInTrigger, bool isFullscreen);
    public BarEffect OnHitTestTick(bool cursorInSurfaces);
    public BarEffect OnEditorFocusChanged(bool anyEditorFocused);
    public BarEffect OnExternalDeactivated(bool cursorInSurfaces);
    public BarEffect OnExitGraceElapsed();
    public BarEffect ForceHide();
}

// 2. Pure floating-note edit session (depends only on IStickyCoreClient; NoteWindow owns the 350ms DispatcherTimer)
public enum DeleteOutcome { DeletedImmediately, ConfirmRequired }

public sealed class NoteEditSession
{
    public long NoteId { get; }
    public string Body { get; private set; }
    public bool IsPinned { get; private set; }
    public bool CreatedThisOpen { get; }
    public bool IsDirty { get; private set; }
    public bool AwaitingDeleteConfirm { get; private set; }

    public static NoteEditSession OpenExisting(IStickyCoreClient core, long noteId); // GetNote(markOpened: true)
    public static NoteEditSession CreateNew(IStickyCoreClient core, long? groupId);  // CreateNote, CreatedThisOpen = true

    public bool OnTextChanged(string newBody);  // Updates Body, sets IsDirty = true, returns true to reset 350ms timer
    public NoteSummary? OnDebounceElapsed();    // If IsDirty: UpdateNoteBody, clears IsDirty
    public NoteSummary TogglePin();             // SetNotePinned(!IsPinned)
    public DeleteOutcome RequestDelete();       // Whitespace body -> DeleteNote & DeletedImmediately; else AwaitingDeleteConfirm = true
    public void ConfirmDelete();                // DeleteNote(NoteId)
    public void CancelDelete();                 // AwaitingDeleteConfirm = false
    public void FlushOnClose();                 // If CreatedThisOpen && !IsPinned && IsWhitespace -> DeleteNote; else if IsDirty -> UpdateNoteBody
}

// 3. Pure "+" quick-note session (depends only on IStickyCoreClient; QuickNoteWindow forwards UI events)
public sealed class QuickNoteSession
{
    public string Text { get; private set; } = string.Empty;
    public QuickCaptureTarget Target { get; private set; } = new QuickCaptureTarget.Standalone();

    public QuickNoteSession(IStickyCoreClient core);
    public void OnTextChanged(string text);
    public void SelectTarget(QuickCaptureTarget target);
    public Note? Commit();  // Ctrl+Enter or click-outside: if !IsNullOrWhiteSpace(Text) -> core.QuickCapture(Text, Target), else null
    public void Cancel();   // Esc: clears Text without calling core
}

// 4. Thin UI Coordinator (wires HWNDs + DispatcherTimers to BarStateMachine, NoteEditSession, QuickNoteSession)
public sealed class ShellCoordinator : IDisposable
{
    public void Initialize();                                   // Enumerate monitors, create TriggerWindows + hidden flyouts
    public void OnTriggerDwellElapsed(IntPtr hMonitor);         // Feed BarStateMachine.OnDwellElapsed -> apply BarEffect
    public void OnGroupClicked(GroupSummary group, Rect chipScreenPx);
    public void OnNoteClicked(long noteId, Rect anchorScreenPx, bool fromBar); // FlushOnClose current session -> OpenExisting
    public void OnNewNoteRequested(long? groupId, Rect anchorScreenPx);        // FlushOnClose -> CreateNew -> ForceActivateAndFocus
    public void OnQuickNoteClicked(Rect plusButtonScreenPx);                   // FlushOnClose NoteWindow -> new QuickNoteSession -> ForceActivateAndFocus
    public void RequestHideAll(HideReason reason);              // FlushOnClose/Commit -> BarStateMachine.ForceHide -> hide windows
    public void RefreshBarAndOpenList();                        // Re-fetch BarSnapshot (+ GroupList if open) after mutations
}

// 5. Positioning & DPI (all inputs/outputs in Win32 physical screen pixels; applied via SetWindowPos)
public interface IScreenPositioner
{
    PxRect GetTriggerRect(MonitorBounds mon, double dpiScale);  // 100*dpi wide x 4*dpi tall, centered at mon.WorkArea.Top
    PxRect GetBarRect(MonitorBounds mon, double dpiScale);      // min(92% WorkArea.Width, 1100*dpi) x 44*dpi at WorkArea.Top
    PxRect GetGroupListRect(PxRect chipPx, PxSize listPx, MonitorBounds mon); // Below chip, clamped inside WorkArea X/Y
    PxRect GetNoteWindowRect(PxRect anchorPx, PxSize notePx, MonitorBounds mon, bool anchorIsHorizontalBarItem);
    // Side-branching rule:
    // - If opened from GroupList item: prefer Right (anchorPx.Right + 6px, anchorPx.Top); if Right + notePx.Width > mon.WorkArea.Right,
    //   flip Left (anchorPx.Left - notePx.Width - 6px). Clamp Y within [mon.WorkArea.Top, mon.WorkArea.Bottom - notePx.Height].
    // - If opened directly from a Bar chip: place below chip (anchorPx.Left, anchorPx.Bottom + 6px), shifting left if overflowing WorkArea.Right.
}
```

### Window Responsibilities (Pre-Created Singletons Except Per-Monitor `TriggerWindow`)
- **`TriggerWindow`:** `100×4 DIPs`, `WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`. Starts a `180ms` `DispatcherTimer` on `MouseEnter`; cancels on `MouseLeave`. On tick, verifies `GetCursorPos` is still inside its `GetWindowRect` before calling `ShellCoordinator.OnTriggerDwellElapsed(hMonitor)`.
- **`BarWindow`:** 3-column `Grid` (`1* | Auto | 1*`), strictly button-only (no `TextBox`, permanently `WS_EX_NOACTIVATE`). Left: horizontal scroll viewer of `PinnedItem` chips (`pinned_at_ms ASC`) + `Groups ▾` button (opens `GroupListWindow` in `OpenAllGroups` mode for unpinned groups and `+ New Group`). Center: `+` Quick-Note button locked at `MonitorCenterX`. Right: up to 5 `Recent` note chips (`last_opened_at_ms DESC`). Right-click or hover-action icon on any chip toggles Pin or triggers Delete (inline confirm if non-empty note or any group).
- **`GroupListWindow`:** Vertical list (`Width = 260 DIPs`, `MaxHeight = 420 DIPs`). Default `WS_EX_NOACTIVATE`; temporarily activates via `FocusHelper.BeginInlineEdit` only when clicking `Rename` (in `OpenGroup` header) or `+ New Group` (in `OpenAllGroups` header), restoring `WS_EX_NOACTIVATE` via `EndInlineEdit` on `Enter`/`Esc`/`Deactivated`. Fixed header also holds Pin toggle, Delete icon (swaps header to `Delete N notes? [Delete] [Cancel]`), and permanent **`+ New Note`** row immediately below header, followed by `NoteSummary` rows (`last_opened_at_ms DESC`).
- **`NoteWindow`:** Thin `320×280 DIPs` view backed by `NoteEditSession`. Header: Pin toggle, group badge, Delete button (`session.RequestDelete()` deletes immediately if empty, else shows inline `Delete note? [Delete] [Cancel]`), Close `×`. Body: plain `TextBox` (`AcceptsReturn="True"`, `TextWrapping="Wrap"`). Owns a `350ms` `DispatcherTimer` calling `session.OnDebounceElapsed()`; `FlushNow()` calls `session.FlushOnClose()`.
- **`QuickNoteWindow`:** Thin `380×220 DIPs` view centered below `+` (no Save button), backed by `QuickNoteSession`. Focused `TextBox` + target selector: `[Standalone]` | `[Group: <Dropdown>]` | `[Append: <Recent Notes Dropdown>]` + subtle hint `Ctrl+Enter or click outside to save · Esc to discard`. Closing via `Ctrl+Enter` or click-outside calls `session.Commit()`; pressing `Esc` calls `session.Cancel()` and restores `prevForegroundHwnd`.

### Tricky Win32 Snippets (`Interop/FocusHelper.cs` & `Interop/Win32.cs`)

**1. Non-Activating Tool Window + Temporary Inline-Edit Activation (`TriggerWindow`, `BarWindow`, `GroupListWindow`):**
```csharp
// Set ShowActivated = false on WPF Window BEFORE first Show(). In OnSourceInitialized:
var hwnd = new WindowInteropHelper(window).Handle;
int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);

bool allowActivate = false; // Must gate WM_MOUSEACTIVATE alongside WS_EX_NOACTIVATE!
HwndSource.FromHwnd(hwnd)!.AddHook((IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
{
    if (msg == WM_MOUSEACTIVATE && !allowActivate) { handled = true; return new IntPtr(MA_NOACTIVATE); }
    return IntPtr.Zero;
});

// Triggered from Button.Click ("Rename" or "+ New Group") — NEVER TextBox.GotFocus (never fires while NOACTIVATE):
void BeginInlineEdit(TextBox box)
{
    CaptureExternalForegroundHwnd();
    allowActivate = true;
    SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) & ~WS_EX_NOACTIVATE);
    box.Visibility = Visibility.Visible;
    ForceActivateAndFocus(window, hwnd, box);
    box.SelectAll();
}

// Triggered on Enter, Esc, or window.Deactivated:
void EndInlineEdit(TextBox box, bool restorePreviousForeground)
{
    box.Visibility = Visibility.Collapsed;
    allowActivate = false;
    SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE);
    if (restorePreviousForeground && _savedForegroundHwnd != IntPtr.Zero && IsWindow(_savedForegroundHwnd))
        SetForegroundWindow(_savedForegroundHwnd); // SetWindowLong alone does NOT relinquish active focus!
}

// Optional Win11 hardware rounded corners (safe no-op on Win10 when PreserveSig = true):
int pref = DWMWCP_ROUND; // 2
DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE /* 33 */, ref pref, sizeof(int));
```

**2. Showing `NoteWindow` Passively vs Focusing `QuickNoteWindow` / New Note:**
```csharp
// Passive show (clicking an existing note to read without stealing terminal focus):
// NoteWindow has WS_EX_TOPMOST | WS_EX_TOOLWINDOW (NO WS_EX_NOACTIVATE) and ShowActivated = false.
SetWindowPos(noteHwnd, HWND_TOPMOST, rect.X, rect.Y, rect.Width, rect.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);

// Active show with focus capture, AttachThreadInput fallback & restore (QuickNote "+" or "+ New Note"):
public static void ForceActivateAndFocus(Window editorWindow, IntPtr editorHwnd, IInputElement focusTarget)
{
    IntPtr prevFg = GetForegroundWindow();
    if (prevFg != IntPtr.Zero && !IsOwnAppWindow(prevFg))
        _savedForegroundHwnd = prevFg;

    SetWindowPos(editorHwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
    if (!SetForegroundWindow(editorHwnd) || GetForegroundWindow() != editorHwnd)
    {
        // Clicking a WS_EX_NOACTIVATE bar does not always grant foreground rights if another app holds foreground lock:
        uint fgThread = GetWindowThreadProcessId(prevFg, out _);
        uint curThread = GetCurrentThreadId();
        if (fgThread != 0 && fgThread != curThread && AttachThreadInput(curThread, fgThread, true))
        {
            try { BringWindowToTop(editorHwnd); SetForegroundWindow(editorHwnd); }
            finally { AttachThreadInput(curThread, fgThread, false); }
        }
        // Honest limitation: if prevFg is elevated (Admin), UIPI blocks AttachThreadInput; clicking inside editorWindow activates natively.
    }
    editorWindow.Activate();
    Keyboard.Focus(focusTarget);
}

// On Esc or Ctrl+Enter:
if (_savedForegroundHwnd != IntPtr.Zero && IsWindow(_savedForegroundHwnd))
    SetForegroundWindow(_savedForegroundHwnd);
```

**3. Cheap Fullscreen Check on Target Monitor:**
```csharp
public static bool IsFullscreenOnMonitor(IntPtr targetMonitor)
{
    if (SHQueryUserNotificationState(out int quns) == 0 &&
        (quns == QUNS_RUNNING_D3D_FULL_SCREEN /* 3 */ || quns == QUNS_PRESENTATION_MODE /* 4 */))
        return true;

    IntPtr fg = GetForegroundWindow();
    if (fg == IntPtr.Zero || fg == GetShellWindow() || IsDesktopClass(fg)) return false;
    if (MonitorFromWindow(fg, MONITOR_DEFAULTTONULL) != targetMonitor) return false;

    int style = GetWindowLong(fg, GWL_STYLE);
    if ((style & WS_VISIBLE) == 0 || (style & WS_CAPTION) != 0) return false; // Normal maximized windows keep WS_CAPTION

    GetWindowRect(fg, out RECT win);
    var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
    GetMonitorInfo(targetMonitor, ref mi);
    return win.Left <= mi.rcMonitor.Left && win.Top <= mi.rcMonitor.Top &&
           win.Right >= mi.rcMonitor.Right && win.Bottom >= mi.rcMonitor.Bottom;
}
```

---

## 8. Interaction State Machines

### 8.1 Trigger & Bar Visibility (`BarStateMachine` + `ShellCoordinator` + `HitRegionTracker`)
*Constants:* `DwellMs = 180`, `RevealAnimMs = 140`, `HideAnimMs = 100`, `PollIntervalMs = 50` (active only while not `Hidden`), `HitInflateDip = 12`, `ExitGraceMs = 200`.

| State | Event / Guard | Action | Next State |
| :--- | :--- | :--- | :--- |
| `Hidden` | `Trigger.MouseEnter(mon)` | Start `180ms` dwell timer on `mon`; stop any other dwell | `DwellPending(mon)` |
| `DwellPending(mon)` | `Trigger.MouseLeave` before `180ms` | Stop dwell timer | `Hidden` |
| `DwellPending(mon)` | `180ms` elapsed & `!CursorInTrigger` OR `IsFullscreenOnMonitor(mon)` | Stop dwell timer | `Hidden` |
| `DwellPending(mon)` | `180ms` elapsed & `CursorInTrigger` & `!Fullscreen` | Fetch `BarSnapshot`; position & `SW_SHOWNOACTIVATE` `BarWindow`; start `140ms` slide-in + `50ms` `HitRegionTracker` | `OpenHover` |
| `OpenHover` | `50ms` tick & cursor outside all visible surfaces (`+12 DIP` inflate) | Start `200ms` exit grace timer | `ExitGrace` |
| `OpenHover` | `NoteWindow` or `QuickNoteWindow` gains keyboard focus (or inline rename focused) | Pause exit checks while editor holds focus | `OpenFocusedLock` |
| `ExitGrace` | `50ms` tick & cursor re-enters any visible surface (`+12 DIP`) | Cancel `200ms` exit grace timer | `OpenHover` |
| `ExitGrace` | Editor gains focus before `200ms` | Cancel `200ms` exit grace timer | `OpenFocusedLock` |
| `ExitGrace` | `200ms` elapsed | Flush `NoteWindow`/`QuickNote`; close children; slide bar up (`100ms`), `Hide()`, stop `50ms` poll | `Hidden` |
| `OpenFocusedLock` | Focused window `Deactivated` to external HWND & cursor inside surfaces | Flush editor; keep bar open | `OpenHover` |
| `OpenFocusedLock` | Focused window `Deactivated` to external HWND & cursor outside surfaces (click-outside) | Flush/commit editor; close children; hide bar; stop `50ms` poll | `Hidden` |
| `Any Open` | `DisplaySettingsChanged` or App `Exit` | Synchronous flush; close flyouts; rebuild triggers (if display changed) | `Hidden` |

### 8.2 Group List (`GroupListWindow`)
| State | Event | Action | Next State |
| :--- | :--- | :--- | :--- |
| `Closed` | Click group chip `G` on Bar | `ListGroupNotes(G.Id)`; position below chip; `SW_SHOWNOACTIVATE` | `OpenGroup(G)` |
| `Closed` | Click `Groups ▾` on Bar | Render `BarSnapshot.AllGroups` (`created_at_ms ASC`) + `+ New Group` row | `OpenAllGroups` |
| `OpenGroup(G1)` | Click same group chip `G1` | Flush & close `NoteWindow` (if anchored to list); hide `GroupListWindow` | `Closed` |
| `OpenGroup(G1)` | Click different group chip `G2` | Flush & close `NoteWindow`; load `G2` notes; reposition under `G2` | `OpenGroup(G2)` |
| `OpenGroup(G)` | Click `+ New Note` at top of list | Flush existing `NoteWindow`; `CreateNote(G.Id, "")`; refresh list; open & `Activate()` `NoteWindow` (`CreatedThisOpen = true`) | `OpenGroup(G)` |
| `OpenGroup(G)` / `OpenAllGroups` | Click `Rename` button or `+ New Group` button | `BeginInlineEdit(textBox)` (clear `WS_EX_NOACTIVATE`, gate `WM_MOUSEACTIVATE`, focus box); coordinator enters `OpenFocusedLock` | `InlineGroupEdit` |
| `InlineGroupEdit` | `Enter` / `Esc` / `Deactivated` | Commit (if `Enter` or non-empty `Deactivated`) or cancel (`Esc`); `EndInlineEdit` (restore `WS_EX_NOACTIVATE`, restore `prevForegroundHwnd` on `Enter`/`Esc`) | `OpenGroup` / `OpenAllGroups` (or `Hidden` on external click-outside) |
| `OpenGroup(G)` | Click Delete icon on group header | Swap header to inline `Delete '{G.Name}' and {G.NoteCount} notes? [Delete] [Cancel]` | `ConfirmDelete(G)` |
| `ConfirmDelete(G)` | Click `[Delete]` | Flush/discard `NoteWindow` if in `G`; `DeleteGroup(G.Id)`; refresh bar; hide list | `Closed` |
| `ConfirmDelete(G)` | Click `[Cancel]` | Restore normal group header | `OpenGroup(G)` |

### 8.3 Floating Note Window (`NoteEditSession` + `NoteWindow` — Max 1 Open)
| State | Event | Action | Next State |
| :--- | :--- | :--- | :--- |
| `Closed` | Click existing note `N` (Bar or GroupList) | `GetNote(N, markOpened: true)`; refresh Bar/List recents; position beside/below anchor (flip left if overflowing `WorkArea.Right`); `SW_SHOWNOACTIVATE` (`CreatedThisOpen = false`) | `OpenPassive(N)` |
| `Closed` | Click `+ New Note` in GroupList | `CreateNote(groupId, "")`; position beside row; show + `Activate()` + focus `TextBox` (`CreatedThisOpen = true`) | `OpenFocused(N)` |
| `OpenPassive(N)` | User clicks inside `NoteWindow` `TextBox` | Capture `prevForegroundHwnd` if external; OS activates window for typing | `OpenFocused(N)` |
| `Open*(N1)` | Click another note `N2` or `+` QuickNote | `FlushNow(N1)` (deletes `N1` if `CreatedThisOpen && !IsPinned && IsWhitespace`); replace with `N2` (or close for QuickNote) | `Open*(N2)` / `Closed` |
| `OpenFocused(N)` | `TextBox.TextChanged` | Mark `isDirty = true`; restart `350ms` autosave `DispatcherTimer` | `OpenFocused(N)` |
| `OpenFocused(N)` | `350ms` autosave timer fires | `UpdateNoteBody(N, body)`; `isDirty = false`; refresh chip title preview | `OpenFocused(N)` |
| `Open*(N)` | Click Delete (`🗑`) & `string.IsNullOrWhiteSpace(body)` | Cancel timer; `DeleteNote(N)`; close `NoteWindow`; refresh Bar/List | `Closed` |
| `Open*(N)` | Click Delete (`🗑`) & `!IsNullOrWhiteSpace(body)` | Show inline toolbar `Delete note? [Delete] [Cancel]` | `ConfirmDelete(N)` |
| `OpenFocused(N)` | `Esc` pressed | `FlushNow(N)`; close `NoteWindow`; restore `prevForegroundHwnd`; if cursor outside Bar/List, hide all | `Closed` |
| `OpenFocused(N)` | Click outside (window deactivates) | **Start a 5s grace timer; keep `NoteWindow` open (passive).** Re-focus within 5s cancels the close; on expiry `FlushNow(N)` + close. `Esc` / `×` remain IMMEDIATE. | `OpenFocused(N)` → `Closed` |

### 8.4 Quick-Note Editor (`QuickNoteSession` + `QuickNoteWindow` via Center `+`)
| State | Event | Action | Next State |
| :--- | :--- | :--- | :--- |
| `Closed` | Click center `+` on Bar | `FlushNow` & close `NoteWindow`/`GroupList`; load `AllGroups` + `ListAppendTargets(30)`; default target = `Standalone`; show centered below `+`; `ForceActivateAndFocus(TextBox)` | `Capturing` |
| `Capturing` | Select `[Standalone]` / `[Group]` / `[Append]` | Update target state in memory (do not write to DB yet) | `Capturing` |
| `Capturing` | `Ctrl+Enter` or external focus loss (click-outside) | If `!IsNullOrWhiteSpace(text)`: call `QuickCapture(text, target)` & refresh Bar. Close `QuickNoteWindow`; if `Ctrl+Enter`, restore `prevForegroundHwnd`; if cursor outside Bar, hide Bar | `Closed` |
| `Capturing` | `Esc` pressed | Discard text without calling `QuickCapture`; close `QuickNoteWindow`; restore `prevForegroundHwnd`; if cursor outside Bar, hide Bar | `Closed` |

---

## 9. Build & Test

### Pinned Toolchain Versions
- **OS:** Windows 10 (21H2+) or Windows 11 (`x64`).
- **Rust:** `1.80+` stable (`x86_64-pc-windows-msvc`), Visual Studio 2022 C++ Build Tools.
- **Crates (`crates/sticky-core/Cargo.toml`):** `rusqlite = { version = "0.32", features = ["bundled"] }`, `serde = { version = "1.0", features = ["derive"] }`, `serde_json = "1.0"`, `thiserror = "1.0"`. *(Assumption: exact minor patch versions resolve from crates.io via `Cargo.lock` committed in Task 1.)*
- **.NET SDK:** `8.0.x` targeting `net8.0-windows` (`<UseWPF>true</UseWPF>`). Test runner: `xunit` `2.9.x`.

### Single Command to Build from Clean Checkout
```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1
```
Contents of `scripts/build.ps1` (3 steps, fail-fast `$ErrorActionPreference = 'Stop'`):
1. `cargo build --release --manifest-path Cargo.toml`
2. Copy `target\release\sticky_core.dll` -> `win\StickyNotes.App\runtimes\win-x64\native\sticky_core.dll` (marked `<CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>` in `.csproj`)
3. `dotnet build .\win\StickyNotes.sln -c Release`

### Single Command to Run All Tests
```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\test.ps1
```
Contents of `scripts/test.ps1`:
1. `cargo test --manifest-path Cargo.toml` (unit tests for schema migrations, pin ordering, recents filtering, cascade group delete, append newline logic)
2. `cargo build --release --manifest-path Cargo.toml` + copy `sticky_core.dll`
3. `dotnet test .\win\StickyNotes.sln -c Release` (FFI P/Invoke round-trips, positioning/DPI math, state-machine unit tests)

---

## 10. Ordered Task List

### Task 1: Workspace Skeleton & Build/Test Scripts
- **Goal:** Establish compiling Rust + .NET 8 workspace with working `build.ps1` and `test.ps1`.
- **Prerequisites:** None.
- **Files:** `Cargo.toml`, `crates/sticky-core/Cargo.toml`, `crates/sticky-core/src/lib.rs`, `win/StickyNotes.sln`, `win/StickyNotes.App/StickyNotes.App.csproj`, `win/StickyNotes.App/app.manifest`, `win/StickyNotes.Tests/StickyNotes.Tests.csproj`, `scripts/build.ps1`, `scripts/test.ps1`.
- **Acceptance Criteria:** From clean checkout, `scripts/build.ps1` and `scripts/test.ps1` both exit `0`; `sticky_core.dll` lands next to `StickyNotes.App.exe`; `app.manifest` enables `PerMonitorV2`.

### Task 2: Rust SQLite Layer, Migrations & Domain Logic
- **Goal:** Implement platform-neutral `StickyCore` with schema v1, all CRUD, pin ordering, recents rules, and quick-capture append logic.
- **Prerequisites:** Task 1.
- **Files:** `crates/sticky-core/src/{models.rs, errors.rs, db.rs, core.rs, lib.rs}`.
- **Acceptance Criteria:** `cargo test` covers: (a) `WAL` + `user_version = 1` migration idempotency, (b) `pinned_items` interleaves groups and notes by `pinned_at_ms ASC`, (c) `recent_notes` caps at 5, excludes standalone pinned notes, includes group notes, and updates `last_opened_at_ms` on create/open/append, (d) `delete_group` cascades to member notes and removes their pins, (e) `quick_capture` append adds `"text"` when empty and `"\ntext"` when non-empty, (f) `get_bar_snapshot(None)` compiles with `Option<&WindowContext>` seam.

### Task 3: Rust `extern "C"` FFI Layer & C# `StickyCoreClient` `[REVIEW BY STRONGER MODEL]`
- **Goal:** Expose `StickyCore` safely across `cdylib` C ABI and wrap it in managed `StickyCoreClient` with leak-free UTF-8 marshaling.
- **Prerequisites:** Task 2.
- **Files:** `crates/sticky-core/src/ffi.rs`, `win/StickyNotes.App/Core/{NativeCoreMethods.cs, Models.cs, StickyCoreClient.cs}`, `win/StickyNotes.Tests/CoreFfiIntegrationTests.cs`.
- **Acceptance Criteria:** Every `extern "C"` entrypoint uses `catch_unwind`; every `FfiResult.payload` is freed in `finally`; xUnit `CoreFfiIntegrationTests` passes full CRUD, emoji/CJK UTF-8 round-trips, error-code propagation (`NotFound`, `Validation`), and 8-thread concurrent read/write stress against a temp SQLite file.

### Task 4: Win32 Interop, DPI Positioning Math & Fullscreen Detection `[REVIEW BY STRONGER MODEL]`
- **Goal:** Implement Win32 P/Invoke helpers, physical-pixel monitor positioning (including right-edge flip), non-activating styles, focus capture/restore, and fullscreen suppression.
- **Prerequisites:** Task 1.
- **Files:** `win/StickyNotes.App/Interop/{Win32.cs, ScreenPositioner.cs, FocusHelper.cs, HitRegionTracker.cs}`, `win/StickyNotes.Tests/ScreenPositionerTests.cs`.
- **Acceptance Criteria:** `ScreenPositionerTests` verifies trigger centering, bar clamping, dropdown clamping, and `NoteWindow` flipping left when `anchor.Right + width > WorkArea.Right` across 100%, 150%, and negative-coordinate secondary monitors. `HitRegionTracker` stops its timer when hidden.

### Task 5: `BarStateMachine`, Per-Monitor `TriggerWindow`, `BarWindow` & Focus-Handoff Spike `[REVIEW BY STRONGER MODEL]`
- **Goal:** Implement pure `BarStateMachine`, render subtle `100×4 DIP` top-center triggers on each monitor, slide down `BarWindow` on 180ms hover without stealing focus, and prove focus handoff early.
- **Prerequisites:** Tasks 3, 4.
- **Files:** `win/StickyNotes.App/State/{BarStateMachine.cs, ShellCoordinator.cs}`, `win/StickyNotes.App/{App.xaml, App.xaml.cs}`, `win/StickyNotes.App/Windows/{TriggerWindow.xaml, TriggerWindow.xaml.cs, BarWindow.xaml, BarWindow.xaml.cs}`, `win/StickyNotes.Tests/BarStateMachineTests.cs`.
- **Acceptance Criteria:** (a) `BarStateMachineTests` covers dwell cancel, fullscreen guard, exit grace recovery, `OpenFocusedLock` suppression, and external click-outside hide without instantiating any WPF `Window`; (b) hovering top-center for 180ms smoothly slides down `BarWindow` (3-column layout with `+` dead-center) while an active Notepad/Windows Terminal window retains blinking caret focus; (c) **Focus-handoff spike:** clicking `+` on the non-activating `BarWindow` immediately after typing in a non-elevated Windows Terminal reliably transfers keyboard focus into a temporary activating `TextBox` popup via `ForceActivateAndFocus` (`SetForegroundWindow` + `AttachThreadInput` fallback) on 10/10 attempts; (d) borderless fullscreen window (`F11` browser) on that monitor suppresses reveal without blocking a second monitor; (e) zero Taskbar or `Alt+Tab` icons appear (system tray Exit icon works).

### Task 6: `GroupListWindow` & Group Management (Create, Rename, Pin, Cascade Delete)
- **Goal:** Implement click-to-open vertical group dropdowns, `Groups ▾` all-groups menu (for unpinned groups + group creation), group pinning, and inline delete confirmation with note count.
- **Prerequisites:** Task 5.
- **Files:** `win/StickyNotes.App/Windows/{GroupListWindow.xaml, GroupListWindow.xaml.cs}`, updates to `BarWindow.xaml(.cs)` and `ShellCoordinator.cs`.
- **Acceptance Criteria:** Clicking a group opens its vertical list with permanent `+ New Note` at top and notes sorted by `last_opened_at_ms DESC`; clicking another group switches lists; clicking `+ New Group` or `Rename` temporarily enables activation (`BeginInlineEdit`), accepts keyboard input, and restores `WS_EX_NOACTIVATE` + prior foreground focus on `Enter`/`Esc` (`EndInlineEdit`); unpinning a group removes its bar chip while keeping it accessible under `Groups ▾` (`created_at_ms ASC`); deleting a group always shows inline confirmation with exact note count and removes group + member notes.

### Task 7: `NoteEditSession` & Thin `NoteWindow` `[REVIEW BY STRONGER MODEL]`
- **Goal:** Implement pure `NoteEditSession` (autosave, flush, empty-new-note cleanup, conditional delete confirm) and wire it to the side-branching `NoteWindow`.
- **Prerequisites:** Task 6.
- **Files:** `win/StickyNotes.App/State/NoteEditSession.cs`, `win/StickyNotes.App/Windows/{NoteWindow.xaml, NoteWindow.xaml.cs}`, updates to `ShellCoordinator.cs`, `win/StickyNotes.Tests/NoteEditSessionTests.cs`.
- **Acceptance Criteria:** (a) `NoteEditSessionTests` verifies dirty tracking, debounce save, `FlushOnClose` deleting empty unpinned new notes while preserving cleared existing notes and pinned empty notes, and `RequestDelete` returning `DeletedImmediately` vs `ConfirmRequired` without WPF windows; (b) clicking an existing note opens `NoteWindow` beside the row (flipping left near right screen edge) *without* stealing focus until clicked inside; (c) clicking `+ New Note` focuses `TextBox` immediately; (d) opening a second note flushes and replaces the first; (e) while focused, moving the mouse off the note does *not* auto-hide it; `Esc` or clicking outside flushes and dismisses cleanly.

### Task 8: `QuickNoteSession` & Thin `QuickNoteWindow` (`+` Capture & Append)
- **Goal:** Implement pure `QuickNoteSession` and wire it to the center `+` quick-capture editor supporting Standalone, Add to Group, and Append to Existing Note without a Save button.
- **Prerequisites:** Task 7.
- **Files:** `win/StickyNotes.App/State/QuickNoteSession.cs`, `win/StickyNotes.App/Windows/{QuickNoteWindow.xaml, QuickNoteWindow.xaml.cs}`, updates to `ShellCoordinator.cs`, `win/StickyNotes.Tests/QuickNoteSessionTests.cs`.
- **Acceptance Criteria:** (a) `QuickNoteSessionTests` verifies `Commit()` for Standalone, Group, and Append targets, no-op on whitespace-only text, and `Cancel()` (`Esc`) never invoking `QuickCapture`; (b) clicking `+` immediately focuses `QuickNoteWindow` via `ForceActivateAndFocus`; (c) `Ctrl+Enter` or click-outside commits once, moves an appended note to #1 in Recents, and restores prior foreground focus (`Ctrl+Enter`); (d) `Esc` discards text without writing to SQLite and restores prior foreground focus.

### Task 9: Display Hotplug Resilience, Idle Memory Trim & Final Polish
- **Goal:** Harden multi-monitor/DPI transitions, single-instance lock, clean shutdown flush, and idle resource footprint.
- **Prerequisites:** Task 8.
- **Files:** `win/StickyNotes.App/App.xaml.cs`, `win/StickyNotes.App/State/ShellCoordinator.cs`.
- **Acceptance Criteria:** Changing monitor resolution/DPI or unplugging a monitor repositions `TriggerWindow`s without crash or off-screen stranded popups; launching a second `.exe` exits cleanly; app exit flushes dirty text before `sn_core_close`.

### End-to-End Verification Check (Final Gate)
1. **Clean Build & Tests:** Run `.\scripts\build.ps1` and `.\scripts\test.ps1` on a clean checkout -> 0 errors, all Rust & xUnit tests green.
2. **Focus Non-Intrusion:** Open Windows Terminal and start typing continuously while hovering top-center (`180ms`), clicking a pinned group, pinning/unpinning a note, and opening an existing note -> **not a single keystroke is dropped from Windows Terminal**, and nothing appears in Taskbar or `Alt+Tab`.
3. **Quick Capture & Append:** Click `+`, type `"git reflog"`, select `Append` to an existing note -> press `Ctrl+Enter` -> verify single `\n` separator (none if target was empty), target note is #1 in Recents, and focus returns to Windows Terminal. Re-open `+`, type `"mistake"`, press `Esc` -> verify nothing was saved or appended.
4. **Data & Edge Rules:** Create a new note and immediately press `Esc` -> verify no empty ghost note in Bar/Group. Delete a group containing 2 notes (one pinned) -> verify inline prompt states `2 notes`, and confirming removes the group, both notes, and the pinned chip; restart app and confirm SQLite state persists.
5. **Resource Budget:** Leave app hidden at idle for 60 seconds -> Task Manager shows **0.0% CPU** (no active timers) and **<50 MB working set**.
