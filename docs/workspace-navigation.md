# Project URLs and remembered positions

Project links use the project name, for example `/projects/the-lantern-festival/shots`. Renaming a project updates its URL; previous names and older UUID links still open the same project. Redirects replace the current history entry and retain item, setup, request, view and fragment links. The former `/production` route opens Shots.

Names become lowercase, hyphenated slugs, with accents folded where possible and other Unicode letters retained. Duplicate names gain `-2`, `-3`, and so on. Current names and historical aliases stay reserved. A routing index beside the project library is published atomically, with serialized allocation and reconciliation after interrupted renames. Project IDs, folders, captured inputs and media URLs do not change.

Browsing positions are saved automatically in this browser, per project ID and workspace:

| Workspace | Remembered position |
| --- | --- |
| Script | Passage and cursor, outline selection, folded acts, tools tab, pane scrolling |
| Assets | Asset, selected media, Create/Edit mode, image/reel choice, library and gallery filters, scrolling per asset |
| Shots | Shot and its last setup, center/tools tabs, search, take filter and pane scrolling |
| Cut | Selected clip, source-frame playhead position, timeline zoom and scrolling; always returns paused |
| Overview and Settings | Page scroll |

Returning to a workspace first loads its data and resolves saved selections, then restores the view. Stable block/media IDs anchor pane scrolling when available. Missing items fall back to available ones; invalid browser data or disabled local storage never prevents opening a workspace.

Explicit item, setup, view and AI-request links take precedence, including clearing filters that would hide their target. New tabs start from the most recently saved browsing position. Existing tabs keep independent positions and do not react to another tab's navigation. Positions remain associated with the project after a rename.

This storage contains browsing metadata only. Document contents, generation drafts, Undo history, bulk selections, dialogs and active gestures keep their existing behavior. Existing save-before-navigation and failed-save guards still apply. No inactive editors stay mounted, and no cross-device synchronization is added.

Browser storage uses versioned `lumibelle.position.<project-id>.<workspace>.v1.*` keys. Clearing site data clears remembered positions without deleting project content.
