# StickyNotes v0.3.0 — Roadmap (planned)

Status: **planned** (not started). Post-v0.2 ideas; nothing here is committed to a release date.

## F7 — Bar empty-space context menu

Right-clicking **empty space on the bar** (not a chip) opens a menu with:

- **Change theme…** — switch the bar / note palette (dark, light, custom accent).
- **Select notes/groups…** — enter a bar selection mode:
  - click chips / group rows to toggle selection;
  - selected items get a visual marker;
  - selection is transient (not persisted).
- **Delete selected** — delete all selected notes and/or groups after an inline confirmation showing counts.
- **Select all / Clear selection** — visible only while selection mode is active.

### Notes

- Distinct from the per-item right-click context menus (v0.2.1), which act on one note or group.
- Bulk delete must reuse the existing inline-confirmation style (no modal dialogs).
- Theme selection is a v0.3 design task (palette definitions + persistence); the menu item can land first with a stub.

## Out of scope for v0.3.0

- Cloud sync, search UI, macOS UI (same as v0.2).