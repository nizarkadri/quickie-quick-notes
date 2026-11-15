# Context-Aware Auto-Hiding Sticky Note App

## Initial Concept & Core Requirements

> I wanna build a sticky note for windows which works likes this:
> 1. the sticky note app stays hidden and only shows up when user moves the mouse to top the screen ! Just like the taskbar feature of windows when its hidden
> 2. when user does action mentioned in 1, a strip shows up like a browser's bookmark bar which shows all the pinned and most recently written sticky notes.
> 3. user can pin any sticky  note to the bar as  well as a consolidated group which would then open down in vertical tree showing the sticky notes in it and clicking any of them would open the sticky note as a branch from vertical list  
> 4. Advanced feature:  The bar dynamically shows the notes based on user's window. For example if terminal window is active and user does action 1., it would show terminal commands, linux commands, git commands cheat sheet.
> 5. User can create mapping of their grouped stikcy notes to the directory of their choice for feature 4. 
> 6. A quick write new note should always be ready at the top of every vertical list from bar and also a + icon in the center of bar to take a quick note which comes with an option to add that note to any group or append to an existing note

---

## Key Design Refinements

* **100px Top-Center Trigger Zone:** Instead of triggering across the entire top edge of the screen, only hovering over a **100px-wide area at the top center** of the screen acts as the trigger.
  * Prevents accidental hover collisions with browser tabs (top-left) and window controls (`Minimize`, `Maximize`, `Close` on the top-right).
  * Can display a subtle, semi-transparent `100px × 3px` notch/pill at the top center as a visual anchor.
  * Naturally aligns the cursor with the central **`+` Quick Note** icon when the bar expands.
* **Cross-Platform Target (Windows & macOS):**
  * **Windows:** Borderless trigger window at `Y = 0` (`100px × 4px`) centered on the active monitor (`MonitorFromPoint` + `GetMonitorInfo`, DPI-aware via `GetDpiForMonitor`).
  * **macOS:** Uses a non-activating floating `NSPanel` (`NSWindow.Level.floating` / `.statusBar`) positioned right along the bottom edge of the Apple Menu Bar / MacBook hardware notch (`Y = menuBarHeight`).
* **Hover Debounce & Exit Behavior:**
  * Require a **120ms–250ms hover dwell delay** before expanding the bar so fast cursor sweeps do not trigger it accidentally.
  * Disable auto-triggering during full-screen games or video playback (`SHQueryUserNotificationState` on Windows).
  * Keep the bar open while the cursor remains within the combined bounding box of the bookmark bar, any open vertical group tree, and any open side-branching sticky note windows.

---

## System Architecture

### Hybrid Shared-Core + Native-UI Model
To achieve minimal idle memory usage (~20–45 MB total), instant startup, and avoid multi-window WebView overhead when multiple floating notes are open simultaneously, the architecture splits business logic from native OS rendering:

```text
┌──────────────────────────────────────────────────────────┐
│  UI Layer (Platform Native — Thin & Fast)                │
│  • Windows: C# (WinUI 3 / WPF) — 100px trigger, bar, UI  │
│  • macOS:   Swift (AppKit / SwiftUI) — Notch bar, UI     │
└──────────────────────────┬───────────────────────────────┘
                           │  Auto-generated bindings
                           ▼  (Mozilla UniFFI)
┌──────────────────────────────────────────────────────────┐
│  Shared Rust Core (compiled to .dll on Win / .dylib Mac) │
│  • SQLite database & full-text search                    │
│  • Note groups, pinning & recents ordering               │
│  • Directory / App-to-Group rule matching engine         │
│  • Quick-note append & cloud sync logic                  │
└──────────────────────────────────────────────────────────┘
```

### Layer Responsibilities

| Responsibility | Shared Rust Core | Native UI Shell (C# on Windows / Swift on macOS) |
| :--- | :--- | :--- |
| **Database & Storage** | SQLite schema, CRUD operations, full-text search, note ordering (pinned & recents) | — |
| **Quick-Note Routing** | Logic to create standalone notes, assign notes to groups, or append text to existing notes | Floating quick-note editor with **Move to Group** and **Append to Existing Note** controls |
| **Context Matching Engine** | Matches active app/process name and folder path rules against mapped note groups | Captures active foreground app, window title, and working directory |
| **OS Triggers & Windowing** | — | 100px top-center hover trigger, always-on-top slide-down bar, vertical dropdown trees, side-branching note windows |

---

## Technical Implementation Decisions

### 1. Active Window & Directory Detection
* **Windows:**
  * Use `SetWinEventHook` listening for `EVENT_SYSTEM_FOREGROUND` to detect app switches without polling.
  * Read the active executable name (e.g., `WindowsTerminal.exe`, `Code.exe`) and window title (`GetWindowText`).
  * Match rules against process names, window titles, or optional shell prompt integration (PowerShell/Bash/Zsh writing the current working directory to a local state file).
  * Handle UIPI (User Interface Privilege Isolation) gracefully when interacting alongside elevated (`Administrator`) terminal windows.
* **macOS:**
  * Listen for `NSWorkspace.didActivateApplicationNotification` for zero-polling app switch detection.
  * Read active window titles and document paths via the macOS Accessibility API (`AXUIElement` / `AXDocument`) or AppleScript (for Terminal / iTerm2 session directories), prompting once for **Accessibility** and **Automation** permissions during onboarding.

### 2. UI Interaction Flow
* **Bookmark Bar:** Displays pinned groups, standalone pinned notes, a central **`+` Quick Note** icon, and recent notes. Dynamically swaps or prioritizes groups matching the active window/directory context.
* **Vertical Group Tree:** Hovering or clicking a group expands a vertical list downward, with a **`+ New Note in Group`** button fixed at the top of the list.
* **Branching Sticky Notes:** Clicking any note in a vertical list opens an editable floating sticky note window anchored directly beside that list item (flipping left if near the right screen edge).
* **Central `+` Quick Note:** Opens an immediately focused borderless note editor with options to save into a specific group or append directly to an existing note.

### 3. Suggested Build Phases
1. **Phase 1 (Shared Core & Trigger):** Set up the Rust core (`SQLite` + `UniFFI` bindings) alongside the 100px top-center trigger and animated slide-down bar.
2. **Phase 2 (Trees, Branching Notes & Quick Capture):** Build the vertical group dropdowns, side-branching floating note windows, and the central `+` quick-note modal with group/append options.
3. **Phase 3 (Context & Directory Mapping):** Implement foreground window/directory watchers on Windows and macOS and connect them to the Rust directory-to-group rule matcher.
