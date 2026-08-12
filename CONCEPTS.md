# Clipthrough Concepts

Project-specific terms used across the codebase, docs, and commit messages.
Excludes framework defaults and obvious .NET / Avalonia vocabulary.

---

## Core domain

**ClipEntry** — The canonical model for a captured clipboard item. Carries
content (text or binary), content type/format, source app metadata, hash,
timestamps (`FirstCopiedAt`, `LastCopiedAt`), and optional OCR/embedding status.
`CapturedAt` is an alias for `LastCopiedAt`.

**ContentType / ContentFormat** — `ContentType` distinguishes Text, Image,
RichText, and Files. `ContentFormat` refines stored format to PlainText, Html,
Rtf, Bitmap, FileDropList, etc. Both are stored in the DB and drive rendering
decisions in the UI.

**clip hash** — SHA-256 of the raw content bytes. Used for deduplication:
re-copying identical content increments `CopyCount` and updates
`LastCopiedAt` rather than inserting a new row.

**CopyCount / PasteCount** — Denormalized counters on the clip row, incremented
atomically. CopyCount tracks how many times the same content was copied;
PasteCount tracks how many times Clipthrough pasted it back.

**sensitivity** — A clip is marked sensitive when a `SensitivityRule` matches
its content. Sensitive clips are displayed with a warning badge in the UI.

**SensitivityMatch** — A join result indicating which rule(s) triggered for a
clip. Stored in a separate `sensitivity_matches` table; recomputed on rule
change via `RebuildSensitivityMatchesAsync`.

**SourceApp / SourceWindowTitle / SourceUrl** — Process name, window title, and
URL harvested from the foreground window at capture time via
`ISourceApplicationResolver`. All three are nullable; the URL is extracted by
the Windows source resolver from browser window titles.

**ImportKind** — How the clip entered Clipthrough. `NULL` = ordinary clipboard
capture; `"drag_drop"` = imported via the drag-and-drop surface. Surfaced as a
badge in the clip list.

**TransformKind / SourceClipId** — When a clip is produced by a transform or AI
operation, `TransformKind` names the operation and `SourceClipId` links back to
the original.

---

## Capture pipeline

**ClipCaptureRequest** — Input DTO for `IClipStoreService.CaptureAsync`. Carries
content bytes, optional plain-text rendering, metadata, and flags.

**CaptureFastAsync** — Variant of `CaptureAsync` that skips the FTS5 `INSERT`
on duplicate content (used by the high-frequency clipboard monitor loop).

**SuppressNext** — A one-shot flag on `IClipboardMonitorService`. Set before
programmatically placing content on the clipboard (e.g. after a transform) so
the subsequent `ClipboardChanged` event is ignored and the clip is not captured
twice.

**DragDropService** — Handles file and text drops onto the popup window. Calls
`CaptureBatchAsync` so multiple dropped files land as individual clips in a
single transaction.

---

## Storage and encryption

**SQLCipher** — The encryption extension used for the SQLite database file when
a password is set. Key derivation is performed once per connection open;
`busy_timeout=5000` is applied separately via `SqliteConnectionFactory`'s
`StateChange` handler (not persisted, re-applied per connection).

**WAL mode** — Write-Ahead Logging is enabled by `DatabaseInitializer` and is
persistent per database file. It allows concurrent readers while a writer holds
the lock. Coupled with `busy_timeout`, it replaces `SQLITE_LOCKED` errors with
retried `SQLITE_BUSY` waits.

**DatabaseMaintenanceScope** — RAII-style async scope that quiesces all
background workers and clears the SQLite connection pool before whole-database
operations (rekey, path move, backup, restore). Workers are restarted on
disposal even if the operation throws.

**StorageOptions / StorageOptionsService** — Manages the database path,
encryption password (DPAPI-protected at rest), size caps, and retention policy.
Resolves worker services lazily via `IServiceProvider` to avoid a circular DI
dependency.

**DPAPI** — Windows Data Protection API used to protect secrets at rest:
SQLCipher password (`storage.json`) and AI API key (sidecar `.bin` files). Non-Windows falls back to `NoOpDataProtectionService`,
keeping secrets in memory only.

**schema version** — Integer stored in `PRAGMA user_version`. Migrations in
`DatabaseInitializer` gate on the current value and apply idempotent `ALTER
TABLE` statements. Bump only when adding a non-idempotent change.

**ClipListSelectColumns vs. ClipSelectColumns** — Two SQL column lists in
`ClipStoreService`. `ClipSelectColumns` fetches all columns including large
BLOBs (`content_bytes`, `source_app_icon`). `ClipListSelectColumns` omits the
BLOBs and replaces `source_app_icon` with a 0/1 presence flag
(`SourceAppIconAvailable`). List and search queries use the latter to avoid
materialising megabytes per row.

**lazy hydration** — Pattern used in `ClipItemViewModel`: the item is
constructed from a metadata-only `ClipEntry`; full content bytes are loaded via
`EnsureContentHydratedAsync` only when the user selects the clip or triggers an
image operation.

---

## Search

**FTS5** — SQLite's full-text search extension. Clipthrough uses a trigram
tokenizer for substring matching. The FTS table is populated via triggers on the
`clips` table.

**SemanticSearchService** — Cosine-similarity search over MiniLM-L6-v2 sentence
embeddings. Maintains an in-memory cache of all clip vectors; `QueryAsync`
operates on an immutable snapshot of the cache so cache refreshes never race
with active queries.

**EmbeddingWorker** — Background loop that claims pending clips from
`ClipStoreService`, calls `IEmbeddingService.EmbedBatchAsync`, and persists
the resulting vectors. Idles (30 s back-off) on inference failure or missing
ONNX model; caps retries at `MaxEmbeddingAttempts = 3` via the
`embedding_attempts` column (schema v4).

**ClipEmbeddingCandidate** — Projection returned by `ClaimPendingEmbeddingsAsync`:
clip id plus the text to embed. Claims rows atomically by setting
`embedding_status = 'processing'`; failed batches are flagged via
`SetEmbeddingFailureAsync` to release claimed rows rather than orphaning them.

**embedding_attempts** — Column on `clips` (schema v4). Incremented each time a
clip is flagged failed. `ClaimPendingEmbeddingsAsync` excludes rows where
`embedding_attempts >= MaxEmbeddingAttempts` so a poison clip cannot spin the
worker indefinitely.

---

## Background workers

**BackgroundOcrQueue** — Serialised queue for Windows.Media.Ocr calls. Started
after DB init (see worker start ordering). Claims via `TryClaimForOcrAsync`.

**EmbeddingWorker** — See Search section.

**worker start ordering** — Both `BackgroundOcrQueue.Start()` and
`EmbeddingWorker.Start()` must be called inside `StartDatabaseAsync`, after
`DatabaseInitializer.InitializeAsync()` and after the encryption password is
configured. Starting before the DB is ready causes "file is not a database"
errors.

---

## ViewModels and decomposition

**MainWindowViewModel** — Primary ViewModel. Large; decomposed in v0.13.0 via
issue #10 into sub-ViewModels: `UpdateViewModel` (self-update commands),
`DatabaseMaintenanceViewModel` (integrity, backup, restore),
`CopilotViewModel` (device-code sign-in), and `SettingsViewModel` (settings
draft). The decomposition is ongoing; AI-transform and clip-list sections remain
on `MainWindowViewModel`.

**SettingsViewModel** — Holds the editable draft for each settings section. `LoadSettingsDraft` mirrors the current `AppSettings`; `SaveSettingsAsync` applies and persists. Cancel reverts by reloading. Exposed as `MainWindowViewModel.Settings`.

**ClipItemViewModel** — Per-clip VM wrapping a `ClipEntry`. Owns the lazy
hydration callback and presentation-computed properties (MetaSegments, display
text, content preview).

**MetaSegments / MetaInlines** — v0.13.0 row-meta optimisation. Each clip row
renders metadata as a single `TextBlock` with coloured `Run` inlines (via the
`MetaInlines` attached property) instead of a `WrapPanel` of ~14 controls,
cutting per-row binding count.

---

## Transforms

**TextTransformation** — Enum of built-in text operations (case, whitespace,
lines, `BoxTableToHtml`). Applied by `TextTransformationService.Apply` — a pure
static function.

**AiTransformService** — OpenAI-compatible chat-completions client. Base URL /
key / model come from `AppSettings.Ai*` with env-var fallback.
