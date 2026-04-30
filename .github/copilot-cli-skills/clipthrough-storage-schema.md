# Clipthrough storage & SQLite schema skill

Use this when adding columns, indexes, queries, or full-text-search behavior. It mirrors what's in `Clipthrough/Database/DatabaseInitializer.cs` and the storage-related rules from `AGENTS.md`.

## Files

- `Clipthrough/Database/DatabaseInitializer.cs` — owns all `CREATE TABLE`, `CREATE INDEX`, `CREATE TRIGGER`, and forward-only `ALTER TABLE` migrations. Idempotent: every block uses `IF NOT EXISTS` or a `PRAGMA table_info` check before `ALTER`.
- `Clipthrough/Database/SqliteConnectionFactory.cs` — single source of `SqliteConnection`. Sets `Mode=ReadWriteCreate`, `ForeignKeys=true`, `Cache=Shared`, and (when configured) `Password=` for SQLCipher. Re-applies `PRAGMA busy_timeout = 5000;` on every `StateChange→Open`.
- `Clipthrough/Services/Storage/ClipStoreService.cs` — all reads/writes go through this `IClipStoreService`. Capture, query, paste-tracking, sensitivity tagging, OCR / embedding status updates.

## Engine + connection settings

- **Encryption**: SQLCipher via `SQLitePCLRaw.bundle_e_sqlcipher`. The password is supplied through `IStorageOptionsService.Current.DatabasePassword` and propagated by the connection factory. Workers must not start until the password has been applied — see "Service initialization order" in `AGENTS.md`.
- **WAL mode**: enabled once during `DatabaseInitializer.InitializeAsync` (`PRAGMA journal_mode = WAL;`). Persistent per-database.
- **`busy_timeout=5000`**: NOT persistent. Set per-connection from the `StateChange` handler in `SqliteConnectionFactory`. Don't rely on a one-shot pragma.
- **One writer at a time**: SQLite serialises writers. Background workers (embedding, OCR) compete with UI-triggered writes. Keep transactions short, never hold them across `await` boundaries.

## UI threading rule

All `IClipStoreService` calls from ViewModel command handlers must be wrapped in `Task.Run(() => _clipStoreService.XxxAsync(...))`. COM clipboard reads (e.g. `Clipboard.GetDataAsync`) MUST stay on the UI thread — only offload the DB write portion. When fixing a UI-freeze bug, audit all call sites of the affected service method first (`grep _clipStoreService\. MainWindowViewModel.cs`).

## Schema (current)

### `clips`

| Column                | Type    | Notes                                                          |
| --------------------- | ------- | -------------------------------------------------------------- |
| `id`                  | INTEGER | PK AUTOINCREMENT.                                              |
| `content`             | TEXT    | UTF-8 text representation (also for OCR/AI inputs).            |
| `content_bytes`       | BLOB    | Raw payload for images/files.                                  |
| `content_type`        | TEXT    | `Text` / `RichText` / `Image` / `Files`.                       |
| `content_format`      | TEXT    | `text` / `html` / `rtf` / `image-png` / etc. Default `text`.   |
| `source_app`          | TEXT    | Friendly app name.                                             |
| `source_app_path`     | TEXT    | Resolved exe path (Windows).                                   |
| `source_app_icon`     | BLOB    | PNG bytes for the app icon.                                    |
| `hash`                | TEXT    | NOT NULL — content hash used for dedup / paste tracking.       |
| `is_favorite`         | INTEGER | 0/1.                                                           |
| `is_sensitive`        | INTEGER | 0/1, set by `sensitivity_rules` matches.                       |
| `captured_at`         | TEXT    | ISO-8601 UTC.                                                  |
| `copy_count`          | INTEGER | Times the same content has been re-copied.                     |
| `first_copied_at`     | TEXT    | NOT NULL.                                                      |
| `last_copied_at`      | TEXT    | NOT NULL — drives default sort.                                |
| `byte_size`           | INTEGER | Total bytes; powers size-sort and retention.                   |
| `image_width`         | INTEGER | Optional, image clips.                                         |
| `image_height`        | INTEGER | Optional, image clips.                                         |
| `source_window_title` | TEXT    | Optional.                                                      |
| `source_url`          | TEXT    | Optional (browser captures).                                   |
| `is_pasted`           | INTEGER | 0/1.                                                           |
| `paste_count`         | INTEGER | Drives MostUsed sort.                                          |
| `last_pasted_at`      | TEXT    | Optional.                                                      |
| `pinned_at`           | TEXT    | NULL = unpinned. Pinned clips sort to the top.                 |
| `ocr_text`            | TEXT    | Recognised image text (also indexed in FTS).                   |
| `ocr_status`          | TEXT    | `pending` / `done` / `failed`.                                 |
| `ocr_attempted_at`    | TEXT    | Last OCR attempt.                                              |
| `ocr_error`           | TEXT    | Last OCR error message.                                        |
| `source_clip_id`      | INTEGER | When this clip was created from a transform, points at parent. |
| `transform_kind`      | TEXT    | `builtin:UpperCase`, `script:Name`, `ai:Preset`, etc.          |
| `embedding_status`    | TEXT    | `pending` / `done` / `failed`.                                 |

### Indexes

- `idx_clips_captured_at` — `captured_at DESC`.
- `idx_clips_content_type`.
- `idx_clips_is_favorite` — partial, `WHERE is_favorite = 1`.
- `idx_clips_is_sensitive` — partial, `WHERE is_sensitive = 1`.
- `idx_clips_default_order` — expression index on `(pinned_at IS NULL, pinned_at DESC, COALESCE(last_copied_at, captured_at) DESC, id DESC)`. **This is the primary list-view ordering**; keep queries aligned with it.
- `idx_clips_paste_count` — `(paste_count DESC, id DESC)` for MostUsed sort.
- `idx_clips_byte_size` — `(byte_size DESC, id DESC)` for size sort.
- `idx_clips_pinned_at` — partial, pinned-only.
- `idx_clips_ocr_status` — partial, non-null only.
- `idx_clips_source_clip_id` — partial, non-null only.
- `idx_clips_embedding_status` — partial, non-null only.

### `clips_fts` (FTS5)

External-content virtual table over `clips`:

```
fts5(content, source_app, source_window_title, source_url, ocr_text,
     content='clips', content_rowid='id',
     tokenize='unicode61 remove_diacritics 2')
```

Triggers `clips_ai`, `clips_ad`, `clips_au` keep FTS in sync on `INSERT` / `DELETE` / `UPDATE`. If you add a column you want searchable, you must:

1. Add it to the FTS5 column list.
2. Update all three triggers.
3. Rebuild FTS for existing rows (`INSERT INTO clips_fts(clips_fts) VALUES('rebuild');`) inside a forward migration.

### Other tables

- `app_metadata(key TEXT PRIMARY KEY, value TEXT)` — key/value config (e.g. schema version markers).
- `sensitivity_rules(id, …)` + `clip_sensitivity_matches(clip_id, rule_id, …)` — sensitivity tagging.
- `search_history(…)` — recent search-box entries.
- `clip_embeddings(…)` — semantic-search vectors (referenced by `embedding_status`).

## Migration pattern

Migrations are forward-only and idempotent. Don't bump a version number unless a destructive change requires it; the existing pattern is:

```csharp
using (var command = connection.CreateCommand())
{
    command.CommandText = "PRAGMA table_info(clips);";
    using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
    {
        existingColumns.Add(reader.GetString(1));
    }
}

if (!existingColumns.Contains("my_new_col"))
{
    await ExecuteNonQueryAsync(connection,
        "ALTER TABLE clips ADD COLUMN my_new_col TEXT;",
        cancellationToken);
}
```

For a new index or trigger, just emit `CREATE … IF NOT EXISTS …`. SQLite ALTER cannot drop columns; if you need to remove one, accept that it lingers or write a `CREATE TABLE clips_new (…) → INSERT SELECT → DROP → RENAME` block.

When you change FTS columns or triggers, **always** run a `'rebuild'` once after the migration so existing rows reflect the new shape.

## Test conventions

- Service tests use real SQLite (in-memory or a temp file) via `SqliteConnectionFactory`.
- `Clipthrough.Tests/TestDoubles.cs` holds fakes for `IClipStoreService` and friends — keep them realistic if you extend interfaces.
- Schema/migration changes deserve a regression test that constructs a DB at the previous shape, runs `DatabaseInitializer.InitializeAsync`, and asserts the new column/index/trigger is present.

## Build hygiene quirks

- Kill `Clipthrough.exe` before rebuilding — the running app locks its own DLL (`Get-Process -Name Clipthrough | Stop-Process -Id $_.Id -Force`).
- If MSBuild reports `MSB3492` (`Could not read existing file`), nuke `Clipthrough.Tests/obj/` and rebuild.
