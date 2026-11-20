# StickyNotes

Auto-hiding sticky notes for Windows: a Rust core owns notes and groups, a C# WPF shell owns the bar and note windows.

## Language

**Note**:
A free-text sticky note, optionally filed in a Group.
_Avoid_: Sticky, card, item

**Group**:
A named collection of Notes shown as a vertical list.
_Avoid_: Folder, notebook, category

**Title**:
The user-set label for a Note; nullable and independent of the body. Empty or whitespace-only is treated as absent.
_Avoid_: Name, heading

**Title preview**:
The derived label shown when a Note has no Title: first non-empty body line, capped at 28 characters, fallback "New note".
_Avoid_: Excerpt, snippet, summary

**Empty note**:
A Note with neither body text nor a Title.
_Avoid_: Blank note, new note

**Trigger notch**:
The visible pill at the top-centre of a monitor; hovering it reveals the bar.
_Avoid_: Handle, tab, hot corner

**Hit target**:
The invisible area around the Trigger notch that counts as hovering it.
_Avoid_: Hover zone, trigger area

## Relationships

- A **Note** has at most one **Title**; when absent, its **Title preview** is displayed.
- A **Note** belongs to zero or one **Group**.
- An **Empty note** that was created during the current open and is not pinned is deleted on close.
- A **Trigger notch** is revealed by its **Hit target**; the two need not be the same size.

## Example dialogue

> **Dev:** "If a **Note** has both a **Title** and a first body line, which one shows on the bar?"
> **Domain expert:** "The **Title**. The **Title preview** is only the fallback when there's no **Title** — it's derived from the body and never stored."

## Flagged ambiguities

- "title" was ambiguous between the user-set label and the derived preview — resolved: **Title** is explicit and nullable; **Title preview** is derived and always present.
- "clearing a title" means the Note has no **Title** (stored `NULL`), never an empty string.