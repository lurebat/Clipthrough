# Clipthrough storage & SQLite schema skill

Use this when adding columns, indexes, queries, or full-text-search behavior. It mirrors what's in `Clipthrough/Database/DatabaseInitializer.cs` and the storage-related rules from `AGENTS.md`.

## Files

- `Clipthrough/Database/DatabaseInitializer.cs` — owns all `CREATE TABLE`, `CREATE INDEX`, `CREATE TRIGGER`, and forward-only `ALTER TABLE` migrations. Idempotent: every block uses `IF NOT EXISTS` or a `PRAGMA table_info` check before `ALTER`.
- `Clipthrough/Database/SqliteConnectionFactory.cs` - single source of `SqliteConnection`. Sets `ForeignKeys=true` and, when configured, a SQLCipher key. `CreateConnection()` opens `ReadWriteCreate`; `CreateReadOnlyConnection()` opens `ReadOnly`. Re-applies `PRAGMA busy_timeout = 5000;` on every `StateChange` to Open.
  **Do not add `Cache=Shared`.** The private cache is deliberate: shared cache reports in-process write contention as `SQLITE_LOCKED`, which `busy_timeout` does *not* retry, while private cache reports `SQLITE_BUSY`, which it does. The reasoning is recorded at the call site.
- `Clipthrough/Services/Storage/ClipStoreService.cs` — all reads/writes go through this `IClipStoreService`. Capture, query, paste-tracking, sensitivity tagging, OCR / embedding status updates.

## Engine + connection settings

- **Encryption**: SQLCipher via `SQLitePCLRaw.bundle_e_sqlcipher`. The password is supplied through `IStorageOptionsService.Current.DatabasePassword` and propagated by the connection factory. Workers must not start until the password has been applied — see "Service initialization order" in `AGENTS.md`.
- **WAL mode**: enabled once during `DatabaseInitializer.InitializeAsync` (`PRAGMA journal_mode = WAL;`). Persistent per-database.
- **`busy_timeout=5000`**: NOT persistent. Set per-connection from the `StateChange` handler in `SqliteConnectionFactory`. Don't rely on a one-shot pragma.
- **One writer at a time**: SQLite serialises writers. Background workers (embedding, OCR) compete with UI-triggered writes. Keep transactions short, never hold them across `await` boundaries.

## UI threading rule

`ClipStoreService` moves its own body to the thread pool — SQLite has no async I/O, so every method would otherwise run to completion on its caller. Just `await` store calls; do not wrap them in `Task.Run`. `ClipStoreThreadingTests` fails if a method stops delegating to `RunOffCallerAsync` or becomes `async` again. COM clipboard reads (e.g. `Clipboard.GetDataAsync`) MUST stay on the UI thread. Work that is *not* a store call is not covered — `ISensitivityService` and `ISearchHistoryService` use their own connection factory — so a mixed block still needs a `Task.Run` around the uncovered part. See `docs/solutions/storage-calls-hop-off-the-caller.md`.

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
| `transform_kind`      | TEXT    | `builtin:UpperCase`, `ai:Preset`, etc. Rows written before user scripting was removed may carry `script:Name`. |
| `import_kind`         | TEXT    | NULL for clipboard captures; `"drag_drop"` for popup drag-and-drop imports. |
| `embedding_status`    | TEXT    | `pending` / `done` / `failed`.                                 |

### Indexes

- `idx_clips_captured_at` — `captured_at DESC`.
- `idx_clips_content_type`.
- `idx_clips_is_favorite` — partial, `WHERE is_favorite = 1`.
- `idx_clips_is_sensitive` — partial, `WHERE is_sensitive = 1`.
- `idx_clips_pinned_at` — partial, pinned-only.
- `idx_clips_ocr_status` — partial, non-null only.
- `idx_clips_source_clip_id` — partial, non-null only.
- `idx_clips_embedding_backlog` — `embedding_status`. Not partial: the backlog scan looks for a specific status, not for non-null.
- `idx_clips_stored_bytes` — `stored_bytes`.
- `idx_clips_retention` — `(is_sensitive, last_copied_at)`. Retention purges run after every capture and both lifetime deletes filter on exactly this pair.
- `idx_clips_recency` — `COALESCE(last_copied_at, captured_at)`.
- `idx_clips_hash_unique` — UNIQUE on `hash`, created by the migration that de-duplicates first. It replaces the older non-unique `idx_clips_hash`.
- `idx_sensitivity_rules_name` — on `sensitivity_rules(name)`.

This list is verified: `StorageSchemaDocTests` fails if an index exists that is
not named here, or if a name here is not an index the schema creates.

#### Sort-order indexes

**Maintain these as a complete set.** There is one per
`ClipSortOption` arm of `BuildOrderClause`, and every one of them starts with
the same `(pinned_at IS NULL), pinned_at DESC` prefix, because every ORDER BY
does. An index without that prefix can never satisfy a list query, which is why
the old `idx_clips_paste_count` and `idx_clips_byte_size` are now dropped by the
schema DDL rather than kept.

Do not add a sort without adding its index. Partial coverage is *worse than
none*: SQLite still picks some other pinned-prefixed index to satisfy the
prefix, then fetches each row by rowid in an order uncorrelated with the table.
Measured at 20k clips, Alphabetical went 118 ms (no new indexes) -> 206 ms
(three of the four) -> 0.2 ms (all four).

- `idx_clips_default_order` — `(pinned_at IS NULL, pinned_at DESC, COALESCE(last_copied_at, captured_at) DESC, id DESC)`. **The primary list-view ordering**; serves `MostRecent` and `BestMatching`.
- `idx_clips_oldest_order` — same, ASC. Serves `OldestFirst`.
- `idx_clips_paste_order` — `(..., paste_count DESC, id DESC)`. Serves `MostPasted`.
- `idx_clips_size_order` — `(..., byte_size DESC, id DESC)`. Serves `LargestFirst`.
- `idx_clips_alpha_order_ci` — `(..., substr(content, 1, 64) COLLATE NOCASE ASC, id ASC)`. Serves `Alphabetical`, whose ORDER BY leads with the same `substr` expression and then falls back to full `content` to break prefix ties. That is order-equivalent to ordering by `content` alone, because when two prefixes differ the first differing character is inside the prefix. Indexing full `content` instead would copy the whole text corpus into the index (+71% database on a 2 KB-average fixture); the prefix costs 2.6%.

`COLLATE NOCASE` is spelled explicitly on **both** ORDER BY terms and on the
index, and that is load-bearing. `substr()` is a function, so its result is
always BINARY and does not inherit the column's collation, whereas a bare
`c.content` does. If the two terms disagreed about what "less than" means, the
clause would produce an order that is neither, and the index — which stores the
NOCASE prefix — could not serve it. NOCASE folds ASCII only, so scripts without
case (Hebrew, CJK) keep code-point order, which is already their alphabetical
order. The equivalence and structural tests fail if the clause and the index
drift apart.

Do not assert on `Alphabetical`'s query plan. With `idx_clips_alpha_order_ci` present, ordering by whole `content` and ordering by the prefix produce a byte-identical plan string, so a plan assertion passes even against a full revert. `Alphabetical_OrdersByTheExpressionItsIndexStores` pins the clause to the index definition read from `sqlite_master` instead.

### `clips_fts` (FTS5)

External-content virtual table over `clips`:

```
fts5(content, source_app, source_window_title, source_url, ocr_text,
     content='clips', content_rowid='id',
     tokenize='trigram')
```

The tokenizer is **trigram**, not a word tokenizer, and that is load-bearing:
the index stores 3-character shingles, so a search token shorter than three
characters cannot be looked up in it at all. `ClipStoreService` routes such
queries to the substring path instead — see `HasFtsCompatibleSearchTerm` and
`BuildFtsExpression`, which must agree about what counts as indexable. Measure
that length in **code points**, not `string.Length`: the tokenizer indexes code
points, so a UTF-16 count overstates any token containing an emoji.

Triggers `clips_ai`, `clips_ad`, `clips_au` keep FTS in sync on `INSERT` /
`DELETE` / `UPDATE`. `clips_au` is scoped `AFTER UPDATE OF` the five indexed
columns and is dropped and recreated unconditionally, because
`CREATE TRIGGER IF NOT EXISTS` would silently preserve an older unscoped
trigger that re-tokenises the whole clip on every metadata-only write.

If you add a column you want searchable, you must:

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

### The FTS repair path is not the version-gated path

`MigrateFtsSchemaIfNeededAsync` runs *before* the schema-version gate and drops
`clips_fts` outright when its stored definition no longer matches (wrong column
count, wrong tokenizer). The schema DDL then recreates it **empty**. So a repair
has to repopulate the index itself — it returns `bool` for exactly that reason,
and the caller rebuilds on `true` regardless of `schema_version`.

This matters because "FTS needs repair" and "schema version is behind" are
independent. `HasPendingStructuralWorkAsync` already relies on that: its last
check is a bare FTS-migration test reached only once the version is current, and
it buys a full-file `PRAGMA quick_check` on the strength of it. Putting the
rebuild back inside the version gate would make every clip in an established
database silently unsearchable by keyword, with no error and nothing logged.

Two ways to test this wrong, both of which happened:

- `SELECT COUNT(*) FROM clips_fts` does **not** measure the index. `clips_fts`
  is external-content, so SQLite answers a bare count from `clips` and returns
  the same number over a full index and an empty one. Count what a `MATCH`
  returns instead.
- Asserting the resulting schema and hit count are unchanged does not prove the
  repair was skipped — an unnecessary drop-and-rebuild produces a byte-identical
  result. Assert on the `[init-timing] rebuild-search-index` trace step.

## Test conventions

- Service tests use real SQLite (in-memory or a temp file) via `SqliteConnectionFactory`.
- `Clipthrough.Tests/TestDoubles.cs` holds fakes for `IClipStoreService` and friends — keep them realistic if you extend interfaces.
- Schema/migration changes deserve a regression test that constructs a DB at the previous shape, runs `DatabaseInitializer.InitializeAsync`, and asserts the new column/index/trigger is present.

## Build hygiene quirks

- Kill `Clipthrough.exe` before rebuilding — the running app locks its own DLL (`Get-Process -Name Clipthrough | Stop-Process -Id $_.Id -Force`).
- If MSBuild reports `MSB3492` (`Could not read existing file`), nuke `Clipthrough.Tests/obj/` and rebuild.
