---
tags: [sqlite, search, performance, oom]
version: v0.13.0
severity: p1
status: active
---

# Metadata-only list reads and lazy hydration

## Problem

List and search queries against `clips` loaded the full row including
`content_bytes` (image pixel data, up to the configured max-clip size) and
`source_app_icon` (PNG icon bytes). For a history with hundreds of image clips
this materialised tens to hundreds of megabytes per search keystroke —
visible as OOM pressure and sluggish scroll/rebuild performance.

## Root cause

A single `SELECT` column list (`ClipSelectColumns`) was used for all reads.
It included every column by name. The list UI path did not need the large
BLOBs — it only displays text previews, icon presence badges, and metadata
chips — but it paid the full I/O and allocation cost regardless.

## Solution

`ClipStoreService` defines two SQL column lists:

- **`ClipSelectColumns`** — full row including `content_bytes` and
  `source_app_icon`. Used only for single-clip fetches (`GetByIdAsync`,
  `GetClipAtOffsetAsync`, capture return value).
- **`ClipListSelectColumns`** — omits both BLOBs; replaces `source_app_icon`
  with a 0/1 presence flag expression:
  ```sql
  (CASE WHEN c.source_app_icon IS NOT NULL AND LENGTH(c.source_app_icon) > 0
        THEN 1 ELSE 0 END)
  ```
  Used by `SearchAsync` and `GetByIdsAsync`.

`ClipEntry.SourceAppIconAvailable` carries the presence flag so the UI can
show an icon badge without loading the bytes. The companion reader
`ReadClipMeta` maps the presence-flag column at index 7; `ReadClip` maps the
actual bytes at the same index. Column ordinals between the two lists are
intentionally aligned so a mis-routed reader fails loudly rather than silently
returning wrong data.

`ClipItemViewModel` wraps the metadata-only entry. When the user selects a
clip, or an image operation requires the bytes, `EnsureContentHydratedAsync`
calls `GetByIdAsync` (which uses `ClipSelectColumns`) to load the full entry
and swaps it in-place. This is the **lazy hydration** pattern.

## Prevention

- Any new read-many path (batch fetch, export enumeration, reporting) MUST
  use `ClipListSelectColumns` + `ReadClipMeta` unless it explicitly needs
  image bytes.
- `GetByIdsAsync` was switched to the metadata-only model in v0.13.0 (P2
  fix); treat any regression to `ClipSelectColumns` there as a bug.
- Integration tests `ClipStoreServiceTests.GetByIdsAsync_OmitsImageBytes_ButFullReadCarriesThem`
  and the list/search read-path coverage guard that read-many paths return
  entries with null `ContentBytes` / `SourceAppIconBytes` while full reads still
  hydrate bytes.
- When adding a new large BLOB column to `clips`, add it to `ClipSelectColumns`
  only and update `ClipListSelectColumns` with a presence-flag equivalent.
