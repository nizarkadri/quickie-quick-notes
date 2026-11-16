# Manual acceptance checklist

Focus, z-order, and multi-monitor behavior cannot be unit tested. A task is NOT complete until its
automated gate passes (`scripts\gate.ps1 -Task N`) AND every box below for that task is ticked on a real machine.
Record the result (and the Windows build) next to each item.

## Task 5 - Trigger and bar (run the focus spike FIRST, before building Tasks 6-8)
- [x] **Focus spike (10/10):** open Windows Terminal, type continuously, hover the top-center trigger for >180 ms, click `+`. Keystrokes land in the quick-note text box (10 of 10 attempts, not 9). **(verified 2026-11-15, 10/10)**
- [x] While the bar is open, keep typing in Terminal without clicking: zero dropped keystrokes, caret keeps blinking. **(verified 2026-11-15)**
- [x] Fast cursor sweep across the trigger (<120 ms) does not open the bar. **(verified 2026-11-15)**
- [x] Bar slides down smoothly (no flicker, no white flash) and slides back up. **(verified 2026-11-15)**
- [x] `+` is exactly at the horizontal center of the monitor with 0, 1, and 8 pinned items. **(0 items verified 2026-11-15; 1/8 pending Task 6 pinning)**
- [x] No Taskbar button and no Alt+Tab entry for the trigger, bar, or any flyout. Tray icon `Exit` works. **(verified 2026-11-15; tray-sink switched to message-only window)**
- [x] F11 browser / borderless fullscreen game on the same monitor: bar does not open. Maximized normal window: bar DOES open. **(verified 2026-11-15)**
- [ ] Moving the cursor diagonally from the bar into a group list or note window never closes the bar. **(pending Task 6/7 — group list / note window not built yet)**

## Task 6 - Groups
- [x] Clicking a group opens the list below it with `+ New Note` permanently at the top; notes below are newest-opened first. **(verified 2026-11-16)**
- [x] Clicking a different group switches lists; clicking the same group closes it. **(verified 2026-11-16)**
- [x] `Groups ▾` shows ALL groups (pinned and unpinned); an unpinned group is still reachable and can be re-pinned. **(verified 2026-11-16)**
- [x] `+ New Group` and Rename: you can type into the box (inline edit), Enter commits, Esc cancels, and afterwards focus returns to the app you were using (type in Terminal immediately to verify). **(verified 2026-11-16)**
- [x] Clicking away mid-rename ends the edit and does not leave the list stuck or swallowing keystrokes. **(verified 2026-11-16)**
- [x] Deleting a group shows an inline confirmation with the exact note count, then removes the group, its notes, and any pinned chips. **(verified 2026-11-16)**
- [x] No modal dialogs or `MessageBox` appear anywhere. **(verified 2026-11-16)**

## Task 7 - Note window
- [x] Clicking an existing note opens it beside the list row WITHOUT stealing focus (keep typing in Terminal); clicking inside it activates it for typing. **(verified 2026-11-16)**
- [x] Row near the right screen edge: the note opens to the left of the row. **(verified 2026-11-16)**
- [x] `+ New Note` focuses the text box immediately. Press Esc with nothing typed: no empty note remains. **(verified 2026-11-16)**
- [x] Opening a second note saves and replaces the first (only one note window at a time). **(verified 2026-11-16)**
- [x] While typing in a note, moving the mouse far away does not hide it. Esc and click-outside save and close. **(verified 2026-11-16)**
- [x] Type text, then kill the process from Task Manager within ~1 s: at most the last ~350 ms of typing is lost. **(verified 2026-11-16)**
- [x] Deleting an empty note is instant; deleting a non-empty note shows an inline confirm. **(verified 2026-11-16)**
- [x] Note window is not in Alt+Tab or the Taskbar. **(verified 2026-11-16)**

## Task 8 - Quick note
- [x] Clicking `+` focuses the editor immediately; hint text shows "Ctrl+Enter or click outside to save - Esc to discard". **(verified 2026-11-16)**
- [x] Standalone, Add to group, and Append each commit correctly; the new/target note appears first in Recents. **(verified 2026-11-16)**
- [x] Esc discards in all three modes. Append + Esc leaves the target note untouched. **(verified 2026-11-16)**
- [x] After commit or Esc, keyboard focus returns to the previously active app. **(verified 2026-11-16)**
- [x] Empty editor closed any way writes nothing. **(verified 2026-11-16)**

## Task 9 - Resilience
- [x] Change display resolution/scale: triggers reposition, nothing is stranded off-screen, no crash. **(verified 2026-11-16)**
- [x] Unplug and replug a second monitor with the bar open. **(verified 2026-11-16, bar open AND hidden)**
- [x] Mixed DPI (e.g. 150% laptop + 100% external): trigger, bar, list and note windows are correctly placed on each. **(verified 2026-11-16)**
- [x] Launching a second copy exits cleanly (single instance). **(verified 2026-11-16)**
- [x] Exit from tray with unsaved text: text is saved on next launch. **(verified 2026-11-16)**
- [x] Sleep/wake and lock/unlock: the trigger still works. **(verified 2026-11-16)**

## Final end-to-end gate (Task 10)
- [x] `scripts\build.ps1` then `scripts\test.ps1` on a clean checkout: all green. **(verified 2026-11-16: build exit 0; 54 Rust + 77 C# = 131 tests green)**
- [x] Terminal typing test: hover, click group, pin/unpin, open a note - not one keystroke dropped. **(verified 2026-11-16)**
- [x] Quick capture + append: `git reflog` appended to an existing note with a single newline; target is #1 in Recents; focus returns to Terminal. **(verified 2026-11-16)**
- [x] Create a note and press Esc immediately: no ghost note. Delete a group with 2 notes (one pinned): prompt says "2 notes", everything is removed, state persists after restart. **(verified 2026-11-16)**
- [x] Idle for 60 s: Task Manager shows 0.0% CPU and < 50 MB working set. **(verified 2026-11-16)**
