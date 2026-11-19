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

## F4 - Hover note preview (v0.2.0)
- [x] Hover a note chip in the bar (pinned rail or Recents): a small read-only preview of the note body appears after the normal tooltip delay; moving away dismisses it. **(verified 2026-11-16)**
- [x] Hover a note row inside a group list: the same preview appears. **(verified 2026-11-16)**
- [x] Hovering does NOT steal focus: keep typing in Terminal while hovering a chip - every keystroke still lands in Terminal. **(verified 2026-11-16)**
- [x] Hovering does NOT count as opening: Recents order and `last_opened_at_ms` are unchanged after hovering a note (compare the Recents order before/after a restart). **(verified 2026-11-16)**
- [x] Clicking the chip/row still opens the note normally, and the preview does not linger over the note window. **(verified 2026-11-16)**
- [x] The bar sliding away (cursor leaves) leaves no orphan tooltip on screen. **(verified 2026-11-16)**
- [x] Long bodies truncate at ~200 chars with an ellipsis; an empty note shows `(empty note)`. **(verified 2026-11-16)**
- [x] The preview never creates a Taskbar/Alt+Tab entry and never becomes the foreground window (F11 fullscreen still suppresses the bar). **(verified 2026-11-16)**

## F1 - Move a note into a group (v0.2.0)
- [ ] Open a note that is in a group: the header shows `● <group> ▾`. Clicking it opens a menu with `Standalone`, every group, and `＋ New group…`.
- [ ] Pick another group: the badge updates, the note disappears from the old group list and appears in the new one immediately (with the list open), and it persists after restart.
- [ ] Pick `Standalone`: the note leaves the group, the badge shows `Move to group ▾`, and it persists after restart.
- [ ] Pick `＋ New group…`: an inline text box appears in the note window. Type a name + Enter -> group is created, note moves into it, badge shows `● <name> ▾`, and the group appears in `Groups ▾` / the bar.
- [ ] Blank name + Enter: nothing is created, the inline box keeps focus, no crash.
- [ ] Esc in the inline box cancels it; Esc in the body still closes + flushes the note.
- [ ] Click outside the move menu: the note closes immediately (no 5s grace).
- [ ] The group-list header has a `×` close button; clicking it hides the list immediately.
- [ ] Picking the current group again: no change, no crash.
- [ ] No modal dialogs; focus returns to the note body after a pick.
- [ ] Missing group/note (core error): no crash, note stays open.

## F6 - Right-click context menus (v0.2.1)
- [ ] Right-click a note chip in the bar -> menu: Open, Pin/Unpin, Move to group ▸, Delete.
- [ ] Right-click a group chip in the bar -> menu: Open, New note, Add note… ▸, Rename, Pin/Unpin, Delete.
- [ ] Right-click a note row in a group list -> same note menu; right-click a group row in `Groups ▾` (OpenAllGroups) -> same group menu.
- [ ] Right-click no longer pins; Pin/Unpin is a menu item.
- [ ] Move a note from Group A to Group B via the menu: it disappears from A and appears in B immediately (if B's list is open), the badge updates, and it persists after restart.
- [ ] `New note` on a group creates an empty note in that group and focuses it.
- [ ] `Add note…` lists recent notes (excluding notes already in the group); picking one moves it in. Empty -> `(no notes to add)`.
- [ ] `Rename` on an UNPINNED group (OpenAllGroups row) switches the list to that group and opens the inline rename box.
- [ ] `Delete` on an UNPINNED group (OpenAllGroups row) switches to that group and shows the inline `Delete 'X' and N notes?` confirm.
- [ ] Note Delete: empty -> immediate; non-empty -> `Delete '<title>'?` + Delete/Cancel.
- [ ] The bar stays visible while a context menu is open; clicking away closes the menu and the bar hides normally.
- [ ] No focus theft: right-clicking a bar chip does not take focus from Terminal; right-clicking a list row does not either.
- [ ] `＋ New group…` from a note menu opens the note (if not already open) with the inline new-group box; typing a name + Enter creates the group and moves the note. If the note is already open, it reuses it (no recents bump).
- [ ] No modal dialogs.
- [ ] Focused open (`+ New Note` or a group's `New note`) focuses the body and never throws "Cannot call Activate before a Window is shown".

## v0.2.2 - Consistent close + 2s bar exit fade
- [ ] Cursor outside every app surface (no editor focused): quick note commits + closes, note flushes + closes, group list closes; the bar fades 1 -> 0 over ~2 s and is gone.
- [ ] Re-enter any app surface during the fade: the fade cancels, the bar returns to full opacity and stays.
- [ ] Focused editor (typing in a note or the quick note) + cursor on the desktop: the editor stays open and the bar stays fully opaque (no fade).
- [ ] Editor closes (Esc / click another app) while the cursor is outside: the bar starts the 2 s fade instead of vanishing instantly.
- [ ] Moving between bar <-> group list <-> note <-> quick note: nothing closes, no fade.
- [ ] No orphan windows or tooltips after the bar disappears.
- [ ] Focused open (`+ New Note` / group `New note`) focuses the body and never throws "Cannot call Activate before a Window is shown".

## v0.2.2+ bug-fix batch (post-sign-off)
- [ ] Cursor leaving the bar during the 2 s fade then returning: the fade CANCELS (bar returns to opacity 1) — the hit tracker keeps polling while the bar is visible, so re-entry is seen.
- [ ] Hovering `Groups ▾` (~180 ms) opens the all-groups dropdown on its own; a quick pass opens nothing; clicking the chip still opens/toggles immediately, and a click on a dropdown that was opened by hover KEEPS it open instead of closing it.
- [ ] Hovering a GROUP ROW inside the `Groups ▾` dropdown (~180 ms) opens a SIDE LIST of that group's notes to the RIGHT of the dropdown, vertically aligned with the row; the dropdown stays open; a quick pass does nothing; hovering another group row switches the side list's content; bar group chips stay click-only; nothing steals focus.
- [ ] Moving the cursor from the group row into the side list keeps it open (no 4 px gap blink); leaving the row AND the side list closes the side list after ~200 ms; closing the `Groups ▾` dropdown closes the side list too; no ghost surface or window is left behind.
- [ ] Clicking a note in the side list opens the note AND the side list stays visible; clicking a group row still switches the dropdown to that group (and closes the side list).
- [ ] `+` opens the quick note WITH keyboard focus (caret blinking in the box) even when the foreground app is elevated.
- [ ] Quick note no longer closes when it merely loses the OS foreground while the user keeps typing in it (focus still inside the window).
- [ ] Quick note has a × close button (top right): discards, same as Esc.
- [ ] Quick note still closes on click-outside (cursor leaves all app surfaces) and commits if non-empty.
