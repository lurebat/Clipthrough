using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Models;
using Clipthrough.Services;
using Microsoft.Data.Sqlite;

namespace Clipthrough.Database;

public sealed class DatabaseInitializer : Clipthrough.Services.Search.IPausableWorker
{
    private const string Schema = """
        PRAGMA foreign_keys = ON;

        CREATE TABLE IF NOT EXISTS clips (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            content      TEXT,
            content_bytes BLOB,
            content_type TEXT NOT NULL,
            content_format TEXT NOT NULL DEFAULT 'text',
            source_app   TEXT,
            source_app_path TEXT,
            source_app_icon BLOB,
            hash         TEXT NOT NULL,
            is_favorite  INTEGER NOT NULL DEFAULT 0,
            is_sensitive INTEGER NOT NULL DEFAULT 0,
            captured_at  TEXT NOT NULL,
            copy_count   INTEGER NOT NULL DEFAULT 1,
            first_copied_at TEXT NOT NULL,
            last_copied_at  TEXT NOT NULL,
            byte_size    INTEGER NOT NULL DEFAULT 0,
            stored_bytes INTEGER NOT NULL DEFAULT 0,
            image_width  INTEGER,
            image_height INTEGER,
            source_window_title TEXT,
            source_url   TEXT,
            is_pasted    INTEGER NOT NULL DEFAULT 0,
            paste_count  INTEGER NOT NULL DEFAULT 0,
            last_pasted_at TEXT,
            pinned_at    TEXT,
            ocr_text     TEXT,
            ocr_status   TEXT,
            ocr_attempted_at TEXT,
            ocr_error    TEXT,
            sensitivity_scanned_at TEXT,
            source_clip_id INTEGER,
            transform_kind TEXT,
            import_kind  TEXT
        );

        CREATE VIRTUAL TABLE IF NOT EXISTS clips_fts USING fts5(
            content,
            source_app,
            source_window_title,
            source_url,
            ocr_text,
            content='clips',
            content_rowid='id',
            tokenize='trigram'
        );

        CREATE TRIGGER IF NOT EXISTS clips_ai AFTER INSERT ON clips BEGIN
            INSERT INTO clips_fts(rowid, content, source_app, source_window_title, source_url, ocr_text)
            VALUES (new.id, new.content, new.source_app, new.source_window_title, new.source_url, new.ocr_text);
        END;

        CREATE TRIGGER IF NOT EXISTS clips_ad AFTER DELETE ON clips BEGIN
            INSERT INTO clips_fts(clips_fts, rowid, content, source_app, source_window_title, source_url, ocr_text)
            VALUES ('delete', old.id, old.content, old.source_app, old.source_window_title, old.source_url, old.ocr_text);
        END;

        -- Dropped unconditionally so the UPDATE OF column list below stays authoritative:
        -- CREATE TRIGGER IF NOT EXISTS would silently keep an older unscoped trigger on
        -- databases created before this scope existed.
        DROP TRIGGER IF EXISTS clips_au;

        -- Scoped to the five columns clips_fts actually indexes. An unscoped AFTER UPDATE
        -- re-tokenises the entire clip (content can be megabytes) on every metadata-only
        -- write — paste counters, OCR status, embedding status, pinning — which is the
        -- overwhelming majority of updates.
        --
        -- The WHEN skips an assignment that changes nothing (F-26). Re-copying a
        -- clip and the deferred HTML/RTF update both write the same text back,
        -- and re-tokenising a 1.8 MB clip for an identical index cost 714-763 ms
        -- under the write lock. IS NOT, so a change to or from NULL still counts.
        CREATE TRIGGER clips_au AFTER UPDATE OF content, source_app, source_window_title, source_url, ocr_text ON clips
        WHEN old.content IS NOT new.content
          OR old.source_app IS NOT new.source_app
          OR old.source_window_title IS NOT new.source_window_title
          OR old.source_url IS NOT new.source_url
          OR old.ocr_text IS NOT new.ocr_text
        BEGIN
            INSERT INTO clips_fts(clips_fts, rowid, content, source_app, source_window_title, source_url, ocr_text)
            VALUES ('delete', old.id, old.content, old.source_app, old.source_window_title, old.source_url, old.ocr_text);
            INSERT INTO clips_fts(rowid, content, source_app, source_window_title, source_url, ocr_text)
            VALUES (new.id, new.content, new.source_app, new.source_window_title, new.source_url, new.ocr_text);
        END;

        CREATE INDEX IF NOT EXISTS idx_clips_captured_at ON clips(captured_at DESC);
        CREATE INDEX IF NOT EXISTS idx_clips_content_type ON clips(content_type);
        CREATE INDEX IF NOT EXISTS idx_clips_is_favorite ON clips(is_favorite) WHERE is_favorite = 1;
        CREATE INDEX IF NOT EXISTS idx_clips_is_sensitive ON clips(is_sensitive) WHERE is_sensitive = 1;

        CREATE TABLE IF NOT EXISTS app_metadata (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS sensitivity_rules (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            name        TEXT NOT NULL,
            pattern     TEXT NOT NULL,
            severity    TEXT NOT NULL DEFAULT 'warning',
            is_enabled  INTEGER NOT NULL DEFAULT 1,
            is_builtin  INTEGER NOT NULL DEFAULT 0
        );

        CREATE UNIQUE INDEX IF NOT EXISTS idx_sensitivity_rules_name ON sensitivity_rules(name);

        CREATE TABLE IF NOT EXISTS clip_sensitivity_matches (
            clip_id INTEGER NOT NULL REFERENCES clips(id) ON DELETE CASCADE,
            rule_id INTEGER NOT NULL REFERENCES sensitivity_rules(id) ON DELETE CASCADE,
            PRIMARY KEY (clip_id, rule_id)
        );

        CREATE TABLE IF NOT EXISTS search_history (
            id       INTEGER PRIMARY KEY AUTOINCREMENT,
            query    TEXT NOT NULL UNIQUE,
            used_at  TEXT NOT NULL
        );

        -- Payloads live outside clips (F-06, U-E1). clips stores content_bytes
        -- and source_app_icon ahead of every metadata column, so any metadata
        -- read walked the blob's overflow chain: ocr_status for 700 images took
        -- 1,375 ms against 10.5 ms for content. Both old columns stay in the
        -- table, always NULL, because SQLite cannot reorder or cheaply drop them.
        CREATE TABLE IF NOT EXISTS clip_payloads (
            clip_id       INTEGER PRIMARY KEY REFERENCES clips(id) ON DELETE CASCADE,
            content_bytes BLOB
        );

        -- One row per distinct source-app icon (F-39): the same PNG was stored
        -- again in every clip from that app.
        CREATE TABLE IF NOT EXISTS app_icons (
            hash TEXT PRIMARY KEY,
            png  BLOB NOT NULL
        );

        -- Belt and braces for the cascade: a connection opened without
        -- foreign_keys would otherwise leave a deleted clip's payload - a
        -- sensitive clip's included - behind.
        CREATE TRIGGER IF NOT EXISTS clips_payload_ad AFTER DELETE ON clips BEGIN
            DELETE FROM clip_payloads WHERE clip_id = old.id;
        END;
        """;

    /// <summary>
    /// Indexes over columns that only exist after the Ensure*Columns helpers
    /// have run. They are deliberately kept out of <see cref="Schema"/>: that
    /// batch executes before the migrations, so declaring them there made the
    /// whole batch abort with "no such column" on any database predating
    /// pinned_at, last_copied_at, paste_count or byte_size - the app could not
    /// open the user's history at all, and because the batch drops clips_au
    /// before recreating it, a database could also be left without its FTS
    /// update trigger.
    ///
    /// This runs on every startup rather than behind the schema-version gate,
    /// so an index missing for any reason is restored on the next launch. The
    /// statements are all IF NOT EXISTS and cost one round trip in total.
    /// </summary>
    /// <summary>
    /// Orphaned app_icons rows are found through this (U-E1).
    /// </summary>
    /// <remarks>
    /// Built only once nothing is left inline. The column is new, so building the
    /// index still reads every row of clips, and before the payload move each row
    /// spans a page: 19.5-20.7 s cold on the 60k fixture, on the deferred upgrade
    /// start that is meant to open at once, against 0.85 s after the move. Until
    /// it exists, <see cref="DeleteOrphanedAppIconsAsync"/> does nothing, and the
    /// background run builds it and then removes the orphans.
    /// </remarks>
    private const string AppIconHashIndex = """
        CREATE INDEX IF NOT EXISTS idx_clips_source_app_icon_hash ON clips(source_app_icon_hash)
            WHERE source_app_icon_hash IS NOT NULL;
        """;

    private const string SortOrderIndexes = """
        -- One index per BuildOrderClause arm. Every arm leads with the pinned
        -- prefix, so an index lacking it can never satisfy the ORDER BY - which
        -- is why the two below are dropped rather than kept.
        --
        -- These must be maintained as a COMPLETE SET. With partial coverage
        -- SQLite still picks whichever pinned-prefixed index exists for an
        -- uncovered sort, then fetches each row by rowid in an order
        -- uncorrelated with the table, which measured ~2x SLOWER than having no
        -- index at all (Alphabetical: 118ms unindexed -> 206ms with only the
        -- three scalar indexes -> 0.2ms with all four, at 20k clips).
        DROP INDEX IF EXISTS idx_clips_paste_count;
        DROP INDEX IF EXISTS idx_clips_byte_size;
        -- Superseded by the case-insensitive index below. A redefinition cannot
        -- reuse the name: CREATE INDEX IF NOT EXISTS would see the old one and
        -- silently keep it, leaving the clause unservable.
        DROP INDEX IF EXISTS idx_clips_alpha_order;

        CREATE INDEX IF NOT EXISTS idx_clips_default_order ON clips(
            (pinned_at IS NULL),
            pinned_at DESC,
            COALESCE(last_copied_at, captured_at) DESC,
            id DESC
        );
        -- The type dropdown with nothing typed (F-09). Every index above leads
        -- with the pinned prefix, so "content_type = ?" could only go through
        -- idx_clips_content_type and then sort every clip of that type, reading
        -- each image's payload on the way: 1.5-1.7 s at 60k clips, 9.7 s
        -- encrypted. Leading with the type turns the filter into a range of
        -- this index already in MostRecent order.
        --
        -- The other sorts with a type filter now start here too (EXPLAIN:
        -- "USE TEMP B-TREE FOR RIGHT PART OF ORDER BY") instead of from
        -- idx_clips_content_type. Both read every row of the type; this one in
        -- recency order rather than rowid order, and the two are close to the
        -- same order because ids are assigned at capture. That is the pattern
        -- the complete-set note warns about, so the type-filtered sorts are
        -- benchmarked (PerfProbes P1) rather than assumed equal.
        CREATE INDEX IF NOT EXISTS idx_clips_type_default_order ON clips(
            content_type,
            (pinned_at IS NULL),
            pinned_at DESC,
            COALESCE(last_copied_at, captured_at) DESC,
            id DESC
        );
        CREATE INDEX IF NOT EXISTS idx_clips_oldest_order ON clips(
            (pinned_at IS NULL),
            pinned_at DESC,
            COALESCE(last_copied_at, captured_at) ASC,
            id ASC
        );
        CREATE INDEX IF NOT EXISTS idx_clips_paste_order ON clips(
            (pinned_at IS NULL),
            pinned_at DESC,
            paste_count DESC,
            id DESC
        );
        CREATE INDEX IF NOT EXISTS idx_clips_size_order ON clips(
            (pinned_at IS NULL),
            pinned_at DESC,
            byte_size DESC,
            id DESC
        );
        -- Indexing content itself would copy the entire text corpus into the
        -- index (+71% database on a 2KB-average fixture). A bounded prefix
        -- costs +2.6% and orders just as well, because BuildOrderClause's
        -- Alphabetical arm leads with this same expression and falls back to
        -- the full content only to break prefix ties.
        --
        -- NOCASE because users read "Alphabetical" as a dictionary order, and
        -- BINARY puts every capitalised word ahead of every lowercase one
        -- ("Zebra" before "apple"). The collation has to be spelled here as
        -- well as in the clause: an index is only usable by an ORDER BY that
        -- compares with the same collation.
        CREATE INDEX IF NOT EXISTS idx_clips_alpha_order_ci ON clips(
            (pinned_at IS NULL),
            pinned_at DESC,
            substr(content, 1, 64) COLLATE NOCASE ASC,
            id ASC
        );

        -- Startup and coverage scans (F-25). With nothing pending they still read
        -- every clip: the sensitivity recovery 2.9 s and the OCR coverage
        -- aggregate 1.9 s at 60k clips, 16 s and 9.9 s encrypted. Each partial
        -- index holds only the rows its query counts. The OCR one leads with
        -- content_type so the planner takes it over idx_clips_content_type, and
        -- carries ocr_status, which the row stores after the payload - reading it
        -- there walked each image's overflow chain. Since U-E1 moved payloads to
        -- clip_payloads, the queries check that an image has one with an EXISTS
        -- probe on that table's primary key; the predicate here is the image
        -- type alone (MovePayloadsOutOfClipsAsync drops the older index that also
        -- required content_bytes, which is now always NULL).
        -- Both WHERE clauses match the queries' own terms exactly
        -- (ClipStoreService.PendingSensitivitySql / OcrCoverageSql); SQLite
        -- only uses a partial index whose condition the query implies.
        CREATE INDEX IF NOT EXISTS idx_clips_sensitivity_pending ON clips(id)
            WHERE sensitivity_scanned_at IS NULL;
        CREATE INDEX IF NOT EXISTS idx_clips_ocr_images ON clips(content_type, ocr_status)
            WHERE content_type = 'image';

        -- idx_clips_source_app_icon_hash is not here: see AppIconHashIndex.

        -- Retention purges, which run after every capture. Both lifetime deletes
        -- filter on exactly this pair.
        --
        -- Deliberately NOT COALESCE(last_copied_at, captured_at), even though that
        -- is how every sort clause spells the same idea. last_copied_at is NOT NULL
        -- in this schema, so SQLite simplifies the COALESCE away and stores the
        -- index over the bare column - but it does not apply the same simplification
        -- to a WHERE-clause range term, so an index written with COALESCE ends up
        -- serving only the is_sensitive equality and the date range still scans.
        -- Spelling both sides as the bare column is the only form that matches in
        -- both schema shapes (a legacy-migrated database declares the column
        -- nullable, where the COALESCE would survive as a real expression index).
        --
        -- Without a usable range term each delete scans even when nothing has
        -- expired, which is the normal case: maintenance measured 110ms per capture
        -- at 100k clips with nothing to purge, and 25ms with this index.
        CREATE INDEX IF NOT EXISTS idx_clips_retention ON clips(
            is_sensitive,
            last_copied_at
        );

        -- Recency, spelled exactly as every stats/sort clause spells it. The library
        -- stats read MAX(COALESCE(last_copied_at, captured_at)), which without this
        -- index is a full table scan - and because the row carries content and the
        -- source-app icon inline, that scan drags the entire library off disk:
        -- measured 458 ms at 60k clips, on every refresh, against 0.05 ms here.
        --
        -- Spelled WITH the COALESCE, unlike idx_clips_retention above, because this
        -- one has to match an expression in a projection rather than a WHERE range
        -- term. Where last_copied_at is NOT NULL SQLite simplifies the COALESCE away
        -- on both the index and the query, so they still match; where a legacy-
        -- migrated schema leaves it nullable the COALESCE survives on both sides and
        -- they match as a real expression index.
        CREATE INDEX IF NOT EXISTS idx_clips_recency ON clips(
            COALESCE(last_copied_at, captured_at)
        );
        """;

    /// <summary>
    /// Bump this constant whenever any of the schema-evolution helpers below
    /// (Ensure*Columns / Backfill* / DeduplicateClipsByHash /
    /// EnsureUniqueClipHashIndex / RebuildClipSearchIndex) gains new work that
    /// needs to run on existing databases. On every startup we compare against
    /// the value stored in <c>app_metadata.schema_version</c>; if the stored
    /// value matches, all the migration helpers are skipped (they're idempotent
    /// no-ops on a current database but still pay several SQLite round trips
    /// each, which adds up to ~800ms on a cold OS file cache).
    /// </summary>
    /// <remarks>
    /// 9: BackfillUtcTimestampsAsync (F-35, U-G7). 10: payloads and icons out of the
    /// clips row (U-E1). Both were written against 8 and both took 9; the layout
    /// moved to 10 so that a library the backfill stamped 9 is still moved.
    /// </remarks>
    private const int CurrentSchemaVersion = 10;

    /// <summary>The version that added BackfillUtcTimestampsAsync (F-35, U-G7).</summary>
    private const int UtcTimestampsIntroducedVersion = 9;

    /// <summary>
    /// The last schema version whose migration changed what clips_fts holds:
    /// version 3, which moved the index to five columns and the trigram tokenizer.
    /// A version bump rebuilds the index only for a library stored before this, or
    /// when the FTS schema itself was just recreated.
    /// </summary>
    /// <remarks>
    /// The rebuild is the largest part of the first start after an upgrade (17.2 s
    /// of 28.5 s on a 420 MB library). Until v0.9.3 it ran on every start; that
    /// release moved it behind the version gate with the other helpers, so it ran
    /// on every bump although no later bump changed the index - the insert, update
    /// and delete triggers keep it current. Raise this to the new
    /// <see cref="CurrentSchemaVersion"/> when a migration changes the indexed
    /// columns or the tokenizer, or rewrites indexed text with the triggers
    /// bypassed.
    /// </remarks>
    internal const int LastSearchIndexChangeVersion = 3;

    /// <summary>
    /// The first schema version, stamped by v0.9.3 (b1dd717). The aggregation and
    /// content-format backfills have not changed since, so every library stamped
    /// with a version has had them; only an unstamped one (0) still needs them.
    /// </summary>
    private const int GateIntroducedVersion = 1;

    /// <summary>
    /// The version whose migration added byte_size and its backfill (bf05b15).
    /// A library at or past it already has every row sized; rows written since
    /// are sized at capture.
    /// </summary>
    private const int ByteSizeIntroducedVersion = 5;

    /// <summary>
    /// The version that added stored_bytes (86b97c8). From here an upgrade needs
    /// no scanning backfill, so its payload move can wait for the background.
    /// </summary>
    private const int StoredBytesIntroducedVersion = 8;

    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly ISensitivityService _sensitivityService;

    // The background upgrade's pause state (F-13-1). Each running attempt is
    // listed with the token that stops it and the task that completes once it
    // has closed its connection.
    private readonly object _backgroundGate = new();
    private readonly List<(CancellationTokenSource Stop, Task Done)> _backgroundAttempts = [];
    private int _backgroundPauseCount;
    private TaskCompletionSource _backgroundResumed = CompletedSignal();

    public DatabaseInitializer(SqliteConnectionFactory connectionFactory, ISensitivityService sensitivityService)
    {
        _connectionFactory = connectionFactory;
        _sensitivityService = sensitivityService;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) => InitializeAsync(progress: null, cancellationToken);

    /// <param name="progress">
    /// Told what the first start after an upgrade is doing, so the loading overlay
    /// can show it (U-E1). Only an existing library with clips reports; a new or
    /// empty one opens too quickly to need it, and is not being upgraded.
    /// </param>
    public Task InitializeAsync(IProgress<LibraryUpgradeProgress>? progress, CancellationToken cancellationToken = default)
        => InitializeAsync(progress, allowDeferredPayloadMove: false, cancellationToken);

    /// <param name="progress">See the overload above.</param>
    /// <param name="allowDeferredPayloadMove">
    /// Lets an upgrade from a recent library skip the payload move, so the app opens
    /// at once and <see cref="RunDeferredPayloadMoveAsync"/> does the move in the
    /// background (upgrade-start research, U4). Older libraries still move here,
    /// because their backfills scan the table and are only cheap after the move.
    /// </param>
    public async Task InitializeAsync(IProgress<LibraryUpgradeProgress>? progress, bool allowDeferredPayloadMove, CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        TraceStep(sw, "connection-opened");

        if (progress is not null && !await HasStoredClipsAsync(connection, cancellationToken))
        {
            progress = null;
        }

        // WAL mode allows concurrent readers during writes — critical for responsiveness.
        await ExecuteNonQueryAsync(connection, "PRAGMA journal_mode = WAL;", cancellationToken);


        // Only when something is about to rewrite pages. See
        // HasPendingStructuralWorkAsync - the check costs a full read of the
        // database file, and on an established database there is no migration for
        // it to protect.
        // A deferred upgrade start writes nothing the check protects - new columns
        // with defaults, triggers, the version - so the full-file read moves to the
        // background, ahead of the payload move that does rewrite pages
        // (RunDeferredPayloadMoveAsync). Read cold it was 71.5 s of a 73 s start on
        // the 1 GB fixture.
        var deferPayloadMove = allowDeferredPayloadMove && await CanDeferPayloadMoveAsync(connection, cancellationToken);
        if (deferPayloadMove)
        {
            TraceStep(sw, "integrity-check deferred");
        }
        else if (await HasPendingStructuralWorkAsync(connection, cancellationToken))
        {
            progress?.Report(new LibraryUpgradeProgress(LibraryUpgradeStage.CheckingIntegrity));
            await VerifyIntegrityRecordingFailureAsync(connection, cancellationToken);
            TraceStep(sw, "integrity-check");
        }

        var ftsSchemaRecreated = await MigrateFtsSchemaIfNeededAsync(connection, cancellationToken);
        TraceStep(sw, "fts-schema-migrate");

        await using (var schemaCommand = connection.CreateCommand())
        {
            schemaCommand.CommandText = Schema;
            await schemaCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        TraceStep(sw, "schema-ddl");

        // Schema-version gate. On an established DB this short-circuits all
        // the Ensure*/Backfill* helpers (each of which costs at least one
        // round trip and a PRAGMA table_info read) and goes straight to the
        // sensitivity-rules seed. New installs and freshly-imported legacy
        // databases stamp 0, so they still run the full path.
        var storedVersion = await ReadSchemaVersionAsync(connection, cancellationToken);
        TraceStep(sw, $"version-read (stored={storedVersion} current={CurrentSchemaVersion})");

        if (storedVersion < CurrentSchemaVersion)
        {
            progress?.Report(new LibraryUpgradeProgress(LibraryUpgradeStage.UpdatingStructure));
            await EnsureClipAggregationColumnsAsync(connection, cancellationToken);
            TraceStep(sw, "ensure-aggregation-columns");
            await EnsureClipPayloadColumnsAsync(connection, cancellationToken);
            TraceStep(sw, "ensure-payload-columns");
            await EnsureClipTrackingColumnsAsync(connection, cancellationToken);
            TraceStep(sw, "ensure-tracking-columns");
            await EnsureClipPinningColumnsAsync(connection, cancellationToken);
            TraceStep(sw, "ensure-pinning-columns");
            await EnsureClipOcrColumnsAsync(connection, cancellationToken);
            TraceStep(sw, "ensure-ocr-columns");
            await EnsureClipLineageColumnsAsync(connection, cancellationToken);
            TraceStep(sw, "ensure-lineage-columns");
            await EnsureClipEmbeddingSchemaAsync(connection, cancellationToken);
            TraceStep(sw, "ensure-embedding-schema");

            await EnsureSourceAppIconHashColumnAsync(connection, cancellationToken);
            await EnsureClipStoredBytesColumnAsync(connection, cancellationToken);

            // A library from schema 8 needs none of the scanning steps below: its
            // backfills are behind it, its hash index exists, and its search index
            // is current. Only the move is left, and that can run in the
            // background while the app is in use. Anything older moves here,
            // first: until it has, every scan below walks each row's inline
            // payload (F-06), and after it the same scans read small rows.
            // Everything below must therefore read payload sizes from
            // clip_payloads, not content_bytes.
            if (deferPayloadMove)
            {
                // G's UTC backfill rewrites rows too, so it waits for the background
                // run's integrity check like the move. The marker, not the version
                // (written below), is what tells that run it is still owed.
                if (storedVersion < UtcTimestampsIntroducedVersion)
                {
                    await ExecuteNonQueryAsync(connection, "INSERT INTO app_metadata (key, value) VALUES ('utc_backfill_pending', '1') ON CONFLICT(key) DO UPDATE SET value = excluded.value;", cancellationToken);
                }

                TraceStep(sw, "move-payloads deferred");
            }
            else
            {
                await MovePayloadsOutOfClipsAsync(connection, progress, background: false, cancellationToken);
                TraceStep(sw, "move-payloads");

                await RepairClipStoredBytesAsync(connection, cancellationToken);
                TraceStep(sw, "ensure-stored-bytes");

                // After the move: it scans every row's timestamps, which sit after the
                // inline payload until the move has run (F-06).
                if (storedVersion < UtcTimestampsIntroducedVersion)
                {
                    await BackfillUtcTimestampsAsync(connection, cancellationToken);
                    TraceStep(sw, "backfill-utc-timestamps");
                }
            }

            if (storedVersion < GateIntroducedVersion)
            {
                await BackfillClipAggregationColumnsAsync(connection, cancellationToken);
                TraceStep(sw, "backfill-aggregation");
            }
            else
            {
                TraceStep(sw, "backfill-aggregation skipped");
            }

            if (storedVersion < ByteSizeIntroducedVersion)
            {
                await BackfillClipPayloadColumnsAsync(connection, cancellationToken);
                TraceStep(sw, "backfill-payload");
            }
            else
            {
                TraceStep(sw, "backfill-payload skipped");
            }

            if (!await HasUniqueClipHashIndexAsync(connection, cancellationToken))
            {
                await DeduplicateClipsByHashAsync(connection, cancellationToken);
                TraceStep(sw, "dedupe-by-hash");
            }
            else
            {
                TraceStep(sw, "dedupe-by-hash skipped");
            }

            await EnsureUniqueClipHashIndexAsync(connection, cancellationToken);
            TraceStep(sw, "ensure-unique-hash-index");
            if (ftsSchemaRecreated || storedVersion < LastSearchIndexChangeVersion)
            {
                progress?.Report(new LibraryUpgradeProgress(LibraryUpgradeStage.RebuildingSearchIndex));
                await RebuildClipSearchIndexAsync(connection, cancellationToken);
                TraceStep(sw, "rebuild-search-index");
            }
            else
            {
                TraceStep(sw, "search-index-current (rebuild skipped)");
            }

            progress?.Report(new LibraryUpgradeProgress(LibraryUpgradeStage.Finishing));
            await WriteSchemaVersionAsync(connection, CurrentSchemaVersion, cancellationToken);
            TraceStep(sw, $"version-write ({CurrentSchemaVersion})");
        }
        else
        {
            // A repair drops the FTS table and the schema DDL above recreates it
            // empty, so without this every clip already stored stops being
            // findable by keyword until some later launch happens to migrate.
            // The rebuild lives inside the version gate, which only helps when
            // the version is behind as well - and it need not be:
            // HasPendingStructuralWorkAsync ends with a bare FTS check reached
            // only at the current version, and buys a full-file quick_check on
            // the strength of it. (arch-sol A16)
            if (ftsSchemaRecreated)
            {
                progress?.Report(new LibraryUpgradeProgress(LibraryUpgradeStage.RebuildingSearchIndex));
                await RebuildClipSearchIndexAsync(connection, cancellationToken);
                TraceStep(sw, "rebuild-search-index (fts repaired at current version)");
            }
            else
            {
                TraceStep(sw, "schema-up-to-date (migrations skipped)");
            }
        }

        // After the column migrations, never before: these index expressions
        // reference columns the Ensure* helpers add.
        await using (var indexCommand = connection.CreateCommand())
        {
            indexCommand.CommandText = SortOrderIndexes;
            await indexCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        TraceStep(sw, "sort-order-indexes");

        // A move the background still owes leaves this to it (AppIconHashIndex).
        var payloadMoveDone = await IsPayloadMoveDoneAsync(connection, cancellationToken);
        if (!allowDeferredPayloadMove || payloadMoveDone)
        {
            await ExecuteNonQueryAsync(connection, AppIconHashIndex, cancellationToken);
            TraceStep(sw, "app-icon-hash-index");
        }
        else
        {
            TraceStep(sw, "app-icon-hash-index deferred");
        }

        // The same condition RunDeferredPayloadMoveAsync checks before its integrity
        // check: until that check has run, the daily backup waits.
        IsIntegrityCheckOwed = allowDeferredPayloadMove
            && (!payloadMoveDone || await IsUtcBackfillPendingAsync(connection, cancellationToken));

        await DeleteOrphanedAppIconsAsync(connection, cancellationToken);

        foreach (var rule in _sensitivityService.GetDefaultRules())
        {
            await using var ruleCommand = connection.CreateCommand();
            ruleCommand.CommandText = """
                INSERT INTO sensitivity_rules (name, pattern, severity, is_enabled, is_builtin)
                VALUES ($name, $pattern, $severity, 1, 1)
                ON CONFLICT(name) DO UPDATE SET
                    is_builtin = 1;
                """;
            ruleCommand.Parameters.AddWithValue("$name", rule.Name);
            ruleCommand.Parameters.AddWithValue("$pattern", rule.Pattern);
            ruleCommand.Parameters.AddWithValue("$severity", rule.Severity);
            await ruleCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        TraceStep(sw, "sensitivity-rules-seeded");

        await _sensitivityService.ReloadAsync(cancellationToken);
        TraceStep(sw, "sensitivity-reload (total)");
    }

    /// <summary>
    /// Reads the persisted schema version from <c>app_metadata</c>. Returns 0
    /// if the row is missing (new install or pre-versioning database) so the
    /// caller runs the full migration sequence at least once.
    /// </summary>
    private static async Task<int> ReadSchemaVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM app_metadata WHERE key = 'schema_version';";
        var raw = await command.ExecuteScalarAsync(cancellationToken);
        if (raw is null or DBNull) return 0;
        return int.TryParse(raw.ToString(), out var v) ? v : 0;
    }

    private static async Task WriteSchemaVersionAsync(SqliteConnection connection, int version, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO app_metadata (key, value) VALUES ('schema_version', $version)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$version", version.ToString(CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void TraceStep(System.Diagnostics.Stopwatch sw, string step)
        => System.Diagnostics.Trace.TraceInformation($"[init-timing] {step} @ {sw.ElapsedMilliseconds}ms");

    private static async Task EnsureClipAggregationColumnsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info(clips);";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                existingColumns.Add(reader.GetString(1));
            }
        }

        if (!existingColumns.Contains("copy_count"))
        {
            await ExecuteNonQueryAsync(connection, "ALTER TABLE clips ADD COLUMN copy_count INTEGER NOT NULL DEFAULT 1;", cancellationToken);
        }

        if (!existingColumns.Contains("first_copied_at"))
        {
            await ExecuteNonQueryAsync(connection, "ALTER TABLE clips ADD COLUMN first_copied_at TEXT;", cancellationToken);
        }

        if (!existingColumns.Contains("last_copied_at"))
        {
            await ExecuteNonQueryAsync(connection, "ALTER TABLE clips ADD COLUMN last_copied_at TEXT;", cancellationToken);
        }
    }

    private static async Task EnsureClipPayloadColumnsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info(clips);";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                existingColumns.Add(reader.GetString(1));
            }
        }

        if (!existingColumns.Contains("content_bytes"))
        {
            await ExecuteNonQueryAsync(connection, "ALTER TABLE clips ADD COLUMN content_bytes BLOB;", cancellationToken);
        }

        if (!existingColumns.Contains("source_app_path"))
        {
            await ExecuteNonQueryAsync(connection, "ALTER TABLE clips ADD COLUMN source_app_path TEXT;", cancellationToken);
        }

        if (!existingColumns.Contains("content_format"))
        {
            await ExecuteNonQueryAsync(connection, "ALTER TABLE clips ADD COLUMN content_format TEXT NOT NULL DEFAULT 'text';", cancellationToken);
        }

        if (!existingColumns.Contains("source_app_icon"))
        {
            await ExecuteNonQueryAsync(connection, "ALTER TABLE clips ADD COLUMN source_app_icon BLOB;", cancellationToken);
        }

        if (!existingColumns.Contains("image_width"))
        {
            await ExecuteNonQueryAsync(connection, "ALTER TABLE clips ADD COLUMN image_width INTEGER;", cancellationToken);
        }

        if (!existingColumns.Contains("image_height"))
        {
            await ExecuteNonQueryAsync(connection, "ALTER TABLE clips ADD COLUMN image_height INTEGER;", cancellationToken);
        }

        // byte_size was declared in the CREATE TABLE but had no migration, so
        // databases created before it existed never gained the column and the
        // size sort index could not be built over them.
        if (!existingColumns.Contains("byte_size"))
        {
            await ExecuteNonQueryAsync(connection, "ALTER TABLE clips ADD COLUMN byte_size INTEGER NOT NULL DEFAULT 0;", cancellationToken);
        }
    }

    private static async Task EnsureClipTrackingColumnsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info(clips);";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                existingColumns.Add(reader.GetString(1));
            }
        }

        if (!existingColumns.Contains("source_window_title"))
        {
            await ExecuteNonQueryAsync(connection, "ALTER TABLE clips ADD COLUMN source_window_title TEXT;", cancellationToken);
        }

        if (!existingColumns.Contains("source_url"))
        {
            await ExecuteNonQueryAsync(connection, "ALTER TABLE clips ADD COLUMN source_url TEXT;", cancellationToken);
        }

        if (!existingColumns.Contains("is_pasted"))
        {
            await ExecuteNonQueryAsync(connection, "ALTER TABLE clips ADD COLUMN is_pasted INTEGER NOT NULL DEFAULT 0;", cancellationToken);
        }

        if (!existingColumns.Contains("paste_count"))
        {
            await ExecuteNonQueryAsync(connection, "ALTER TABLE clips ADD COLUMN paste_count INTEGER NOT NULL DEFAULT 0;", cancellationToken);
        }

        if (!existingColumns.Contains("last_pasted_at"))
        {
            await ExecuteNonQueryAsync(connection, "ALTER TABLE clips ADD COLUMN last_pasted_at TEXT;", cancellationToken);
        }
    }

    private static async Task EnsureClipPinningColumnsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info(clips);";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                existingColumns.Add(reader.GetString(1));
            }
        }

        if (!existingColumns.Contains("pinned_at"))
        {
            await ExecuteNonQueryAsync(connection, "ALTER TABLE clips ADD COLUMN pinned_at TEXT;", cancellationToken);
        }

        await ExecuteNonQueryAsync(
            connection,
            "CREATE INDEX IF NOT EXISTS idx_clips_pinned_at ON clips(pinned_at) WHERE pinned_at IS NOT NULL;",
            cancellationToken);
    }

    private static async Task EnsureClipOcrColumnsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info(clips);";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                existingColumns.Add(reader.GetString(1));
            }
        }

        if (!existingColumns.Contains("ocr_text"))
        {
            await ExecuteNonQueryAsync(connection, "ALTER TABLE clips ADD COLUMN ocr_text TEXT;", cancellationToken);
        }

        if (!existingColumns.Contains("ocr_status"))
        {
            await ExecuteNonQueryAsync(connection, "ALTER TABLE clips ADD COLUMN ocr_status TEXT;", cancellationToken);
        }

        if (!existingColumns.Contains("ocr_attempted_at"))
        {
            await ExecuteNonQueryAsync(connection, "ALTER TABLE clips ADD COLUMN ocr_attempted_at TEXT;", cancellationToken);
        }

        if (!existingColumns.Contains("ocr_error"))
        {
            await ExecuteNonQueryAsync(connection, "ALTER TABLE clips ADD COLUMN ocr_error TEXT;", cancellationToken);
        }

        if (!existingColumns.Contains("sensitivity_scanned_at"))
        {
            // NULL means "classification has not completed for this clip".
            // CaptureFastAsync defers the scan, so without this marker a clip
            // whose deferred scan never ran was indistinguishable from one that
            // was scanned and found clean — it stayed unflagged forever.
            await ExecuteNonQueryAsync(connection, "ALTER TABLE clips ADD COLUMN sensitivity_scanned_at TEXT;", cancellationToken);

            // Existing rows predate the marker. Treat them as already scanned so
            // upgrading doesn't trigger a full-library rescan and doesn't stall
            // embedding for the whole existing library; only captures from here
            // on are tracked.
            await ExecuteNonQueryAsync(
                connection,
                "UPDATE clips SET sensitivity_scanned_at = COALESCE(captured_at, first_copied_at) WHERE sensitivity_scanned_at IS NULL;",
                cancellationToken);
        }

        await ExecuteNonQueryAsync(
            connection,
            "CREATE INDEX IF NOT EXISTS idx_clips_ocr_status ON clips(ocr_status) WHERE ocr_status IS NOT NULL;",
            cancellationToken);
    }

    private static async Task EnsureClipLineageColumnsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info(clips);";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                existingColumns.Add(reader.GetString(1));
            }
        }

        if (!existingColumns.Contains("source_clip_id"))
        {
            await ExecuteNonQueryAsync(connection, "ALTER TABLE clips ADD COLUMN source_clip_id INTEGER;", cancellationToken);
        }

        if (!existingColumns.Contains("transform_kind"))
        {
            await ExecuteNonQueryAsync(connection, "ALTER TABLE clips ADD COLUMN transform_kind TEXT;", cancellationToken);
        }

        // import_kind marks how the clip entered Clipthrough so the UI can
        // distinguish drag-and-drop imports from real clipboard captures.
        // NULL = clipboard capture (default); "drag_drop" = imported via DnD.
        if (!existingColumns.Contains("import_kind"))
        {
            await ExecuteNonQueryAsync(connection, "ALTER TABLE clips ADD COLUMN import_kind TEXT;", cancellationToken);
        }

        await ExecuteNonQueryAsync(
            connection,
            "CREATE INDEX IF NOT EXISTS idx_clips_source_clip_id ON clips(source_clip_id) WHERE source_clip_id IS NOT NULL;",
            cancellationToken);
    }

    private static async Task EnsureClipEmbeddingSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info(clips);";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                existingColumns.Add(reader.GetString(1));
            }
        }

        if (!existingColumns.Contains("embedding_status"))
        {
            await ExecuteNonQueryAsync(connection, "ALTER TABLE clips ADD COLUMN embedding_status TEXT;", cancellationToken);
        }

        if (!existingColumns.Contains("embedding_attempts"))
        {
            await ExecuteNonQueryAsync(connection, "ALTER TABLE clips ADD COLUMN embedding_attempts INTEGER NOT NULL DEFAULT 0;", cancellationToken);
        }

        // Records that the user marked this clip sensitive by hand rather than a
        // rule matching it. Provenance cannot be inferred from clip_sensitivity_matches:
        // saving rules deletes the old ones, and the ON DELETE CASCADE above takes the
        // match rows with them, so by the time a rebuild runs every rule-matched clip
        // looks hand-marked. Existing rows default to 0, which is the safe direction —
        // a rule change re-derives them exactly as it does today.
        if (!existingColumns.Contains("sensitivity_is_manual"))
        {
            await ExecuteNonQueryAsync(connection, "ALTER TABLE clips ADD COLUMN sensitivity_is_manual INTEGER NOT NULL DEFAULT 0;", cancellationToken);

            // Recover the existing hand-marked clips while it is still possible.
            // Right now the match rows are intact, so "sensitive with no match row"
            // really does mean hand-marked; the ambiguity only appears later, once a
            // rule save has cascaded them away. Defaulting these to 0 would let the
            // next rule change declassify them and hand them to the embedding worker.
            await ExecuteNonQueryAsync(connection, """
                UPDATE clips
                SET sensitivity_is_manual = 1
                WHERE is_sensitive = 1
                  AND NOT EXISTS (SELECT 1 FROM clip_sensitivity_matches m WHERE m.clip_id = clips.id);
                """, cancellationToken);
        }

        await ExecuteNonQueryAsync(connection, """
            CREATE TABLE IF NOT EXISTS clip_embeddings (
                clip_id       INTEGER PRIMARY KEY REFERENCES clips(id) ON DELETE CASCADE,
                model_version TEXT NOT NULL,
                dimensions    INTEGER NOT NULL,
                vector        BLOB NOT NULL,
                created_at    TEXT NOT NULL
            );
            """, cancellationToken);

        // Deliberately NOT partial. The embedding claim query looks for the backlog,
        // which includes 'embedding_status IS NULL', and a "WHERE embedding_status IS
        // NOT NULL" index can never serve that term - so the claim fell back to a full
        // table scan. Measured at 60k rows: 124 ms per claim with *zero* work to do,
        // and the claim runs on every capture (the worker is poked per clip) plus
        // every 30 s idle tick. Indexing every row instead lets SQLite use a
        // MULTI-INDEX OR over the backlog values: 0.17 ms, same rows returned.
        // Partial indexes mirroring the disjunction were tried and are never chosen -
        // SQLite's implication test cannot prove an OR-shaped predicate.
        await ExecuteNonQueryAsync(connection, "DROP INDEX IF EXISTS idx_clips_embedding_status;", cancellationToken);
        await ExecuteNonQueryAsync(
            connection,
            "CREATE INDEX IF NOT EXISTS idx_clips_embedding_backlog ON clips(embedding_status);",
            cancellationToken);

        // Stale reclaim: any clip marked 'processing' from a prior crashed run goes back to 'pending'.
        await ExecuteNonQueryAsync(
            connection,
            "UPDATE clips SET embedding_status = 'pending' WHERE embedding_status = 'processing';",
            cancellationToken);
    }

    /// <summary>
    /// How many rows <see cref="MovePayloadsOutOfClipsAsync"/> moves per
    /// transaction, and a cap on the bytes it holds in memory doing so. A test seam.
    /// </summary>
    internal static int PayloadMoveBatchRows { get; set; } = 200;

    internal const long PayloadMoveBatchBytes = 32L * 1024 * 1024;

    /// <summary>
    /// The background move's smaller batches. Each batch is a write transaction,
    /// and a capture waits for it under busy_timeout (5 s); an encrypted 32 MB
    /// batch can take seconds. The time budget bounds how long a batch reads.
    /// </summary>
    internal const long BackgroundPayloadMoveBatchBytes = 8L * 1024 * 1024;

    internal static TimeSpan BackgroundPayloadMoveBatchBudget { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Gives other writers a turn between background batches.</summary>
    internal static TimeSpan BackgroundPayloadMovePause { get; set; } = TimeSpan.FromMilliseconds(25);

    /// <summary>
    /// Test seam: replaces the pause between background batches, so a test can
    /// hold the move between batches - with no transaction open - and act on the
    /// library meanwhile.
    /// </summary>
    internal static Func<CancellationToken, Task>? BetweenBackgroundPayloadBatches { get; set; }

    /// <summary>
    /// Moves what an upgrade left inline, in the background, while the app is in
    /// use (upgrade-start research, U4). Returns false at once when the move has
    /// already finished. Resumes from the last committed batch after a crash, as
    /// the synchronous move does; reads and writes in the meantime follow the
    /// partly-moved rules (PartlyMovedLibraryTests).
    /// </summary>
    /// <remarks>
    /// Paused by <see cref="PauseAsync"/> for a whole-database operation, after
    /// which it starts over against whatever library is live by then.
    /// </remarks>
    public Task<bool> RunDeferredPayloadMoveAsync(IProgress<LibraryUpgradeProgress>? progress, CancellationToken cancellationToken = default)
        => RunPausableAsync(token => RunDeferredPayloadMoveCoreAsync(progress, token), cancellationToken);

    private async Task<bool> RunDeferredPayloadMoveCoreAsync(IProgress<LibraryUpgradeProgress>? progress, CancellationToken cancellationToken)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var moveDone = await IsPayloadMoveDoneAsync(connection, cancellationToken);
        var backfillPending = await IsUtcBackfillPendingAsync(connection, cancellationToken);
        if (moveDone && !backfillPending)
        {
            IsIntegrityCheckOwed = false;
            return false;
        }

        // The integrity check the deferred start skipped, before anything is
        // rewritten. Runs again on each start until the work is done, which only
        // costs background time.
        await VerifyIntegrityRecordingFailureAsync(connection, cancellationToken);
        if (!moveDone)
        {
            await MovePayloadsOutOfClipsAsync(connection, progress, background: true, cancellationToken);
        }

        // After the move, when the timestamps it scans no longer sit behind an
        // inline payload (F-06). The move commits its done flag with its last
        // batch, so the backfill keeps a marker of its own: a run stopped between
        // the two still owes the backfill, and the next run does it.
        if (backfillPending)
        {
            await BackfillUtcTimestampsAsync(connection, cancellationToken);
            await ExecuteNonQueryAsync(connection, "DELETE FROM app_metadata WHERE key = 'utc_backfill_pending';", cancellationToken);
        }

        // After the move, these read small rows and are cheap. Every row the move
        // cleared already has its stored_bytes; this is the safety net.
        await RepairClipStoredBytesAsync(connection, cancellationToken);
        await ExecuteNonQueryAsync(connection, AppIconHashIndex, cancellationToken);
        await DeleteOrphanedAppIconsAsync(connection, cancellationToken);
        return true;
    }

    /// <summary>
    /// Whether this start is an upgrade whose only rewriting work is the payload
    /// move, which can then wait for the background: a library from schema 8 or
    /// later, behind the current version, whose search index and unique hash index
    /// need nothing. Read before the schema DDL, so it touches nothing that DDL
    /// creates. Anything older runs scanning backfills that are only cheap after
    /// the move, and keeps the blocking path.
    /// </summary>
    private static async Task<bool> CanDeferPayloadMoveAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='app_metadata';";
            if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 0)
            {
                return false;
            }
        }

        var storedVersion = await ReadSchemaVersionAsync(connection, cancellationToken);
        return storedVersion >= StoredBytesIntroducedVersion
            && storedVersion < CurrentSchemaVersion
            && !await FtsSchemaNeedsMigrationAsync(connection, cancellationToken)
            && await HasUniqueClipHashIndexAsync(connection, cancellationToken);
    }

    private static async Task<bool> IsUtcBackfillPendingAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM app_metadata WHERE key = 'utc_backfill_pending';";
        return await command.ExecuteScalarAsync(cancellationToken) is "1";
    }

    private static async Task<bool> IsPayloadMoveDoneAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM app_metadata WHERE key = 'payload_move_done';";
        return await command.ExecuteScalarAsync(cancellationToken) is "1";
    }

    /// <summary>Test seam: runs after each row is moved, inside the batch's transaction.</summary>
    internal static Action<long>? AfterPayloadRowMoved { get; set; }

    /// <summary>
    /// Whether a quick_check failed this session. The daily backup is not taken from
    /// such a library (live check F3), so the older, good backups are kept.
    /// </summary>
    public bool HasFailedIntegrityCheck { get; private set; }

    /// <summary>
    /// Whether this session still owes the integrity check a deferred upgrade start
    /// skipped (it runs at the start of <see cref="RunDeferredPayloadMoveAsync"/>).
    /// The daily backup waits for it.
    /// </summary>
    public bool IsIntegrityCheckOwed { get; private set; }

    /// <summary>
    /// Stops the background upgrade - the deferred move and the merge drain - and
    /// waits until it has closed its connection, for a whole-database operation
    /// (rekey, restore, library move) that must not run beside a writer (F-13-1).
    /// A run stopped this way is not over: it waits, and when the last pause is
    /// released it starts again from the top - every step resumes from what it
    /// committed - against the library that is live by then.
    /// </summary>
    public async Task<IAsyncDisposable> PauseAsync()
    {
        CancellationTokenSource[] running;
        Task[] stopped;
        lock (_backgroundGate)
        {
            if (_backgroundPauseCount++ == 0)
            {
                _backgroundResumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            running = _backgroundAttempts.Select(static a => a.Stop).ToArray();
            stopped = _backgroundAttempts.Select(static a => a.Done).ToArray();
        }

        // Outside the lock: cancelling can run the attempt's unwinding inline.
        foreach (var stop in running)
        {
            try
            {
                stop.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // That attempt finished meanwhile.
            }
        }

        await Task.WhenAll(stopped).ConfigureAwait(false);
        return new BackgroundPause(this);
    }

    private void ReleasePause()
    {
        lock (_backgroundGate)
        {
            if (--_backgroundPauseCount == 0)
            {
                _backgroundResumed.TrySetResult();
            }
        }
    }

    private static TaskCompletionSource CompletedSignal()
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult();
        return signal;
    }

    /// <summary>
    /// Runs one piece of background upgrade work so that <see cref="PauseAsync"/>
    /// can stop it and it then starts over once the pause is released. Only the
    /// caller's own token ends it for good.
    /// </summary>
    private async Task<T> RunPausableAsync<T>(Func<CancellationToken, Task<T>> body, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task? resumed = null;
            CancellationTokenSource? stop = null;
            TaskCompletionSource? done = null;
            lock (_backgroundGate)
            {
                if (_backgroundPauseCount > 0)
                {
                    resumed = _backgroundResumed.Task;
                }
                else
                {
                    stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _backgroundAttempts.Add((stop, done.Task));
                }
            }

            if (resumed is not null)
            {
                await resumed.WaitAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                return await body(stop!.Token).ConfigureAwait(false);
            }
            catch (Exception) when (stop!.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                // Paused. Whatever the stop interrupted rolled back; go round and wait.
            }
            finally
            {
                lock (_backgroundGate)
                {
                    _backgroundAttempts.RemoveAll(a => a.Stop == stop);
                }

                stop!.Dispose();
                done!.SetResult();
            }
        }
    }

    private sealed class BackgroundPause(DatabaseInitializer owner) : IAsyncDisposable
    {
        private int _released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.ReleasePause();
            }

            return ValueTask.CompletedTask;
        }
    }

    private static async Task EnsureSourceAppIconHashColumnAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('clips') WHERE name = 'source_app_icon_hash';";
        if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 0)
        {
            await ExecuteNonQueryAsync(connection, "ALTER TABLE clips ADD COLUMN source_app_icon_hash TEXT;", cancellationToken);
        }
    }

    /// <summary>
    /// Moves every inline <c>content_bytes</c> into <c>clip_payloads</c> and every inline
    /// <c>source_app_icon</c> into <c>app_icons</c> (deduplicated by SHA-256), then
    /// NULLs the old columns (F-06, F-39).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A 1 GB library cannot move in one transaction: it would hold the write lock
    /// and grow the WAL by the whole library. Each batch is one transaction that
    /// copies its rows, NULLs their old columns and records the last id it reached
    /// in <c>app_metadata</c>. A row is therefore always either entirely in the old
    /// place or entirely in the new one, and a crash at any point resumes from the
    /// last committed batch on the next start - the schema version is written only
    /// after this returns.
    /// </para>
    /// <para>
    /// NULLing per batch, rather than after the whole copy as the plan sketched,
    /// lets each batch reuse the pages the previous one freed, so the file does
    /// not double in size mid-migration. Readers see none of this: it runs inside
    /// InitializeAsync, before anything reads a clip.
    /// </para>
    /// </remarks>
    private static async Task MovePayloadsOutOfClipsAsync(SqliteConnection connection, IProgress<LibraryUpgradeProgress>? progress, bool background, CancellationToken cancellationToken)
    {
        // The older OCR index also required content_bytes IS NOT NULL, which is
        // about to be true of no row. Dropped so the index DDL recreates it.
        await ExecuteNonQueryAsync(connection, "DROP INDEX IF EXISTS idx_clips_ocr_images;", cancellationToken);

        long lastId;
        await using (var readLastId = connection.CreateCommand())
        {
            readLastId.CommandText = "SELECT value FROM app_metadata WHERE key = 'payload_move_last_id';";
            var raw = await readLastId.ExecuteScalarAsync(cancellationToken);
            lastId = raw is string text && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
        }

        // Progress by id, not by row count: MIN and MAX of the rowid are free,
        // while counting the rows still to move would read every one of them.
        long firstClipId = 0, lastClipId = 0;
        await using (var range = connection.CreateCommand())
        {
            range.CommandText = "SELECT COALESCE(MIN(id), 0), COALESCE(MAX(id), 0) FROM clips;";
            await using var reader = await range.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                firstClipId = reader.GetInt64(0);
                lastClipId = reader.GetInt64(1);
            }
        }

        void ReportMoved(long reachedId) => progress?.Report(new LibraryUpgradeProgress(
            LibraryUpgradeStage.MovingClipData,
            lastClipId < firstClipId || lastClipId == 0
                ? 1
                : Math.Clamp((double)(reachedId - firstClipId + 1) / (lastClipId - firstClipId + 1), 0, 1)));

        ReportMoved(lastId);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var transaction = connection.BeginTransaction();
            var rows = new List<(long Id, byte[]? Payload, byte[]? Icon, bool HasNewerIcon)>();
            long bytes = 0;
            await using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = """
                    SELECT id, content_bytes, source_app_icon, source_app_icon_hash IS NOT NULL FROM clips
                    WHERE id > $last AND (content_bytes IS NOT NULL OR source_app_icon IS NOT NULL)
                    ORDER BY id
                    LIMIT $limit;
                    """;
                select.Parameters.AddWithValue("$last", lastId);
                select.Parameters.AddWithValue("$limit", PayloadMoveBatchRows);
                await using var reader = await select.ExecuteReaderAsync(cancellationToken);
                var batchCap = background ? BackgroundPayloadMoveBatchBytes : PayloadMoveBatchBytes;
                var batchClock = System.Diagnostics.Stopwatch.StartNew();
                while (bytes < batchCap
                    && (!background || rows.Count == 0 || batchClock.Elapsed < BackgroundPayloadMoveBatchBudget)
                    && await reader.ReadAsync(cancellationToken))
                {
                    var payload = reader.IsDBNull(1) ? null : (byte[])reader.GetValue(1);
                    var icon = reader.IsDBNull(2) ? null : (byte[])reader.GetValue(2);
                    rows.Add((reader.GetInt64(0), payload, icon, reader.GetInt64(3) != 0));
                    bytes += (payload?.LongLength ?? 0) + (icon?.LongLength ?? 0);
                }
            }

            if (rows.Count == 0)
            {
                await using (var done = connection.CreateCommand())
                {
                    done.Transaction = transaction;
                    done.CommandText = """
                        INSERT INTO app_metadata (key, value) VALUES ('payload_move_done', '1')
                        ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                        """;
                    await done.ExecuteNonQueryAsync(cancellationToken);
                }

                transaction.Commit();
                progress?.Report(new LibraryUpgradeProgress(LibraryUpgradeStage.MovingClipData, 1));
                break;
            }

            // Newer data wins. A clip the move has not reached yet can still be
            // written - captured again, edited, given an icon - and every write
            // goes to the new layout, leaving the inline copy behind it stale.
            // So the move never replaces a payload or an icon hash that is already
            // there; it only clears the stale inline copy.
            foreach (var (id, payload, icon, hasNewerIcon) in rows)
            {
                if (payload is not null)
                {
                    await using var insert = connection.CreateCommand();
                    insert.Transaction = transaction;
                    insert.CommandText = """
                        INSERT INTO clip_payloads (clip_id, content_bytes) VALUES ($id, $bytes)
                        ON CONFLICT(clip_id) DO NOTHING;
                        """;
                    insert.Parameters.AddWithValue("$id", id);
                    insert.Parameters.AddWithValue("$bytes", payload);
                    await insert.ExecuteNonQueryAsync(cancellationToken);
                }

                string? iconHash = null;
                if (icon is { Length: > 0 } && !hasNewerIcon)
                {
                    iconHash = AppIconHash(icon);
                    await using var insertIcon = connection.CreateCommand();
                    insertIcon.Transaction = transaction;
                    insertIcon.CommandText = "INSERT OR IGNORE INTO app_icons (hash, png) VALUES ($hash, $png);";
                    insertIcon.Parameters.AddWithValue("$hash", iconHash);
                    insertIcon.Parameters.AddWithValue("$png", icon);
                    await insertIcon.ExecuteNonQueryAsync(cancellationToken);
                }

                await using (var clear = connection.CreateCommand())
                {
                    clear.Transaction = transaction;
                    clear.CommandText = """
                        UPDATE clips
                        SET content_bytes = NULL,
                            source_app_icon = NULL,
                            stored_bytes = byte_size,
                            source_app_icon_hash = COALESCE($hash, source_app_icon_hash)
                        WHERE id = $id;
                        """;
                    clear.Parameters.AddWithValue("$id", id);
                    clear.Parameters.AddWithValue("$hash", (object?)iconHash ?? DBNull.Value);
                    await clear.ExecuteNonQueryAsync(cancellationToken);
                }

                AfterPayloadRowMoved?.Invoke(id);
                lastId = id;
            }

            await using (var record = connection.CreateCommand())
            {
                record.Transaction = transaction;
                record.CommandText = """
                    INSERT INTO app_metadata (key, value) VALUES ('payload_move_last_id', $last)
                    ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                    """;
                record.Parameters.AddWithValue("$last", lastId.ToString(CultureInfo.InvariantCulture));
                await record.ExecuteNonQueryAsync(cancellationToken);
            }

            transaction.Commit();
            ReportMoved(lastId);
            if (background)
            {
                await (BetweenBackgroundPayloadBatches?.Invoke(cancellationToken) ?? Task.Delay(BackgroundPayloadMovePause, cancellationToken));
            }
        }
    }

    private static async Task<bool> HasStoredClipsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'clips';";
        if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 0)
        {
            return false;
        }

        command.CommandText = "SELECT EXISTS (SELECT 1 FROM clips);";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 0;
    }

    /// <summary>The key a source-app icon is stored under in app_icons.</summary>
    internal static string AppIconHash(byte[] png) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(png));

    /// <summary>
    /// Removes icons no clip refers to any more. Icons are not sensitive, but an
    /// orphan would still count against the library size cap.
    /// </summary>
    /// <remarks>
    /// Does nothing until <see cref="AppIconHashIndex"/> exists. Without it each icon
    /// costs a scan of clips, and the index is only missing while a deferred payload
    /// move is owed - when those scans walk every inline payload, under the write
    /// lock of the maintenance that called this. The background run removes the
    /// orphans as soon as it has built the index.
    /// </remarks>
    internal static async Task DeleteOrphanedAppIconsAsync(SqliteConnection connection, CancellationToken cancellationToken, SqliteTransaction? transaction = null)
    {
        await using (var probe = connection.CreateCommand())
        {
            probe.Transaction = transaction;
            probe.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'idx_clips_source_app_icon_hash';";
            if (Convert.ToInt64(await probe.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 0)
            {
                return;
            }
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM app_icons
            WHERE NOT EXISTS (SELECT 1 FROM clips c WHERE c.source_app_icon_hash = app_icons.hash);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Materialises the on-disk size of each clip row into a real, indexed column.
    /// </summary>
    /// <remarks>
    /// The library size cap has to total what a row actually costs, which is its
    /// payload plus the source-app icon stored beside it. Spelling that as
    /// <c>SUM(byte_size + LENGTH(source_app_icon))</c> is correct but unindexable:
    /// SQLite cannot answer it from a covering index, so it reads every row - and
    /// because content and the icon live inline, that drags the whole library off
    /// disk. Measured at 60k clips: 445 ms, against 2.5 ms for a plain
    /// <c>SUM(byte_size)</c> served by a covering index. Maintenance runs after every
    /// capture and the stats read runs on every refresh, so both paid it per copy.
    ///
    /// An expression index does not fix this, and neither does a generated column:
    /// both were measured and SQLite still chose a full scan. The value has to be
    /// stored to be indexable.
    ///
    /// Kept current by triggers rather than by the write paths, because there are six
    /// statements that write byte_size or the icon and a seventh would silently
    /// corrupt the total. Both triggers write only <c>stored_bytes</c>, which appears
    /// in neither trigger's UPDATE OF list, so they cannot re-enter - and it is
    /// likewise absent from the clips_fts trigger's column list, so maintaining it
    /// never re-tokenises a clip.
    ///
    /// Since U-E1 (F-39) the icon is stored once per distinct PNG in app_icons, so a
    /// row costs its payload alone: stored_bytes is byte_size, and the cap adds each
    /// distinct icon once (ClipStoreService.TotalStoredBytesSql).
    /// </remarks>
    /// <summary>
    /// Brings stored_bytes in line with byte_size and indexes it. Both read every
    /// row, so they run after the payload move, when rows are small.
    /// </summary>
    private static async Task RepairClipStoredBytesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        // Also repairs drift, so a database written by a build whose triggers were
        // missing converges the next time it is opened.
        await ExecuteNonQueryAsync(connection, """
            UPDATE clips
            SET stored_bytes = byte_size
            WHERE stored_bytes <> byte_size;
            """, cancellationToken);

        await ExecuteNonQueryAsync(
            connection,
            "CREATE INDEX IF NOT EXISTS idx_clips_stored_bytes ON clips(stored_bytes);",
            cancellationToken);
    }

    /// <summary>
    /// Adds stored_bytes and its triggers. Cheap: ADD COLUMN with a default does
    /// not rewrite rows. Runs before the payload move, which sets the column on
    /// every row it clears.
    /// </summary>
    private static async Task EnsureClipStoredBytesColumnAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var hasColumn = false;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('clips') WHERE name = 'stored_bytes';";
            hasColumn = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) > 0;
        }

        if (!hasColumn)
        {
            await ExecuteNonQueryAsync(
                connection,
                "ALTER TABLE clips ADD COLUMN stored_bytes INTEGER NOT NULL DEFAULT 0;",
                cancellationToken);
        }

        // Dropped rather than IF NOT EXISTS so the definitions here stay authoritative.
        await ExecuteNonQueryAsync(connection, "DROP TRIGGER IF EXISTS clips_stored_bytes_ai;", cancellationToken);
        await ExecuteNonQueryAsync(connection, """
            CREATE TRIGGER clips_stored_bytes_ai AFTER INSERT ON clips BEGIN
                UPDATE clips
                SET stored_bytes = new.byte_size
                WHERE id = new.id;
            END;
            """, cancellationToken);

        await ExecuteNonQueryAsync(connection, "DROP TRIGGER IF EXISTS clips_stored_bytes_au;", cancellationToken);
        await ExecuteNonQueryAsync(connection, """
            CREATE TRIGGER clips_stored_bytes_au AFTER UPDATE OF byte_size ON clips BEGIN
                UPDATE clips
                SET stored_bytes = new.byte_size
                WHERE id = new.id;
            END;
            """, cancellationToken);
    }

    /// <summary>
    /// Rewrites clip timestamps stored with a non-zero UTC offset as UTC (F-35).
    /// </summary>
    /// <remarks>
    /// Timestamps are round-trip ("O") text compared lexically, which orders
    /// correctly only when every value is UTC. Capture always wrote UTC, but a
    /// ClipAngel import kept each row's local offset, so imported clips sorted
    /// and expired wrong by that offset until the store boundary converted them.
    /// Idempotent: a value already at +00:00, and text with no offset at all,
    /// parses with a zero offset and is left exactly as it is.
    /// </remarks>
    private static async Task BackfillUtcTimestampsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var updates = new List<(long Id, string?[] Values)>();
        await using (var select = connection.CreateCommand())
        {
            select.CommandText = """
                SELECT id, captured_at, first_copied_at, last_copied_at FROM clips
                WHERE (captured_at IS NOT NULL AND captured_at NOT LIKE '%+00:00')
                   OR (first_copied_at IS NOT NULL AND first_copied_at NOT LIKE '%+00:00')
                   OR (last_copied_at IS NOT NULL AND last_copied_at NOT LIKE '%+00:00');
                """;
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var values = new string?[3];
                var changed = false;
                for (var i = 0; i < 3; i++)
                {
                    var raw = reader.IsDBNull(i + 1) ? null : reader.GetString(i + 1);
                    values[i] = raw;
                    if (raw is not null
                        && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                        && parsed.Offset != TimeSpan.Zero)
                    {
                        values[i] = parsed.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
                        changed = true;
                    }
                }

                if (changed)
                {
                    updates.Add((reader.GetInt64(0), values));
                }
            }
        }

        if (updates.Count == 0)
        {
            return;
        }

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE clips SET captured_at = $c, first_copied_at = $f, last_copied_at = $l WHERE id = $id;";
        var id = update.Parameters.Add("$id", SqliteType.Integer);
        var c = update.Parameters.Add("$c", SqliteType.Text);
        var f = update.Parameters.Add("$f", SqliteType.Text);
        var l = update.Parameters.Add("$l", SqliteType.Text);
        foreach (var (clipId, values) in updates)
        {
            id.Value = clipId;
            c.Value = (object?)values[0] ?? DBNull.Value;
            f.Value = (object?)values[1] ?? DBNull.Value;
            l.Value = (object?)values[2] ?? DBNull.Value;
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task BackfillClipPayloadColumnsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(connection, """
            UPDATE clips
            SET content_format = CASE
                WHEN content_type = 'image' THEN 'bitmap'
                WHEN content_type = 'files' THEN 'files'
                WHEN content_type = 'richtext' AND content LIKE '{\\rtf%' THEN 'rtf'
                WHEN content_type = 'richtext' THEN 'html'
                ELSE 'text'
            END
            WHERE content_format IS NULL OR TRIM(content_format) = '';
            """, cancellationToken);

        // Rows that predate byte_size default to 0, which sorts them all
        // together at one end of the size ordering. The stored text length is
        // the best measure available without rehydrating every payload.
        await ExecuteNonQueryAsync(
            connection,
            """
            UPDATE clips
            SET byte_size = COALESCE(
                LENGTH(CAST(content AS BLOB)),
                (SELECT LENGTH(p.content_bytes) FROM clip_payloads p WHERE p.clip_id = clips.id),
                LENGTH(content_bytes),
                0)
            WHERE byte_size = 0;
            """,
            cancellationToken);
    }

    private static async Task BackfillClipAggregationColumnsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(connection, "UPDATE clips SET copy_count = 1 WHERE copy_count IS NULL OR copy_count < 1;", cancellationToken);
        await ExecuteNonQueryAsync(connection, "UPDATE clips SET first_copied_at = captured_at WHERE first_copied_at IS NULL OR TRIM(first_copied_at) = '';", cancellationToken);
        await ExecuteNonQueryAsync(connection, "UPDATE clips SET last_copied_at = captured_at WHERE last_copied_at IS NULL OR TRIM(last_copied_at) = '';", cancellationToken);
        await ExecuteNonQueryAsync(connection, "UPDATE clips SET captured_at = last_copied_at WHERE captured_at IS NULL OR TRIM(captured_at) = '';", cancellationToken);
    }

    private static async Task DeduplicateClipsByHashAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var rows = new List<ClipAggregationRow>();

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id,
                       hash,
                       source_app,
                       is_favorite,
                       is_sensitive,
                       captured_at,
                       first_copied_at,
                       last_copied_at,
                       copy_count
                FROM clips
                ORDER BY hash, COALESCE(last_copied_at, captured_at) DESC, id DESC;
                """;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var capturedAt = ParseTimestamp(reader.IsDBNull(5) ? null : reader.GetString(5));
                rows.Add(new ClipAggregationRow(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetInt64(3) == 1,
                    reader.GetInt64(4) == 1,
                    capturedAt,
                    ParseTimestamp(reader.IsDBNull(6) ? null : reader.GetString(6)) ?? capturedAt,
                    ParseTimestamp(reader.IsDBNull(7) ? null : reader.GetString(7)) ?? capturedAt,
                    reader.IsDBNull(8) ? 1 : Convert.ToInt32(reader.GetInt64(8), CultureInfo.InvariantCulture)));
            }
        }

        foreach (var group in rows.GroupBy(static row => row.Hash, StringComparer.Ordinal))
        {
            var duplicates = group.ToList();
            if (duplicates.Count <= 1)
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();

            var keepRow = duplicates[0];
            var fallbackTimestamp = keepRow.LastCopiedAt
                ?? keepRow.FirstCopiedAt
                ?? keepRow.CapturedAt
                ?? DateTimeOffset.UtcNow;
            var firstCopiedAt = duplicates
                .Select(static row => row.FirstCopiedAt ?? row.CapturedAt)
                .Where(static timestamp => timestamp is not null)
                .Select(static timestamp => timestamp!.Value)
                .DefaultIfEmpty(fallbackTimestamp)
                .Min();
            var lastCopiedAt = duplicates
                .Select(static row => row.LastCopiedAt ?? row.CapturedAt)
                .Where(static timestamp => timestamp is not null)
                .Select(static timestamp => timestamp!.Value)
                .DefaultIfEmpty(fallbackTimestamp)
                .Max();
            var copyCount = duplicates.Sum(static row => Math.Max(1, row.CopyCount));
            var isFavorite = duplicates.Any(static row => row.IsFavorite);
            var isSensitive = duplicates.Any(static row => row.IsSensitive);
            var sourceApp = duplicates
                .OrderByDescending(static row => row.LastCopiedAt ?? row.CapturedAt ?? DateTimeOffset.MinValue)
                .Select(static row => row.SourceApp)
                .FirstOrDefault(static source => !string.IsNullOrWhiteSpace(source));
            var duplicateIds = duplicates.Skip(1).Select(static row => row.Id).ToArray();

            using var transaction = connection.BeginTransaction();

            await using (var updateCommand = connection.CreateCommand())
            {
                updateCommand.Transaction = transaction;
                updateCommand.CommandText = """
                    UPDATE clips
                    SET source_app = $sourceApp,
                        is_favorite = $isFavorite,
                        is_sensitive = $isSensitive,
                        copy_count = $copyCount,
                        first_copied_at = $firstCopiedAt,
                        last_copied_at = $lastCopiedAt,
                        captured_at = $lastCopiedAt
                    WHERE id = $id;
                    """;
                updateCommand.Parameters.AddWithValue("$id", keepRow.Id);
                updateCommand.Parameters.AddWithValue("$sourceApp", (object?)sourceApp ?? DBNull.Value);
                updateCommand.Parameters.AddWithValue("$isFavorite", isFavorite ? 1 : 0);
                updateCommand.Parameters.AddWithValue("$isSensitive", isSensitive ? 1 : 0);
                updateCommand.Parameters.AddWithValue("$copyCount", copyCount);
                updateCommand.Parameters.AddWithValue("$firstCopiedAt", firstCopiedAt.ToString("O", CultureInfo.InvariantCulture));
                updateCommand.Parameters.AddWithValue("$lastCopiedAt", lastCopiedAt.ToString("O", CultureInfo.InvariantCulture));
                await updateCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            if (duplicateIds.Length > 0)
            {
                var parameterNames = new List<string>(duplicateIds.Length);

                await using (var mergeMatchesCommand = connection.CreateCommand())
                {
                    mergeMatchesCommand.Transaction = transaction;
                    mergeMatchesCommand.Parameters.AddWithValue("$keepId", keepRow.Id);
                    for (var index = 0; index < duplicateIds.Length; index++)
                    {
                        var parameterName = $"$duplicateId{index}";
                        parameterNames.Add(parameterName);
                        mergeMatchesCommand.Parameters.AddWithValue(parameterName, duplicateIds[index]);
                    }

                    mergeMatchesCommand.CommandText = $"""
                        INSERT OR IGNORE INTO clip_sensitivity_matches (clip_id, rule_id)
                        SELECT $keepId, rule_id
                        FROM clip_sensitivity_matches
                        WHERE clip_id IN ({string.Join(", ", parameterNames)});
                        """;
                    await mergeMatchesCommand.ExecuteNonQueryAsync(cancellationToken);
                }

                await using var deleteCommand = connection.CreateCommand();
                deleteCommand.Transaction = transaction;
                for (var index = 0; index < duplicateIds.Length; index++)
                {
                    deleteCommand.Parameters.AddWithValue(parameterNames[index], duplicateIds[index]);
                }

                deleteCommand.CommandText = $"DELETE FROM clips WHERE id IN ({string.Join(", ", parameterNames)});";
                await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
    }

    private static async Task EnsureUniqueClipHashIndexAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(connection, "DROP INDEX IF EXISTS idx_clips_hash;", cancellationToken);
        await ExecuteNonQueryAsync(connection, "CREATE UNIQUE INDEX IF NOT EXISTS idx_clips_hash_unique ON clips(hash);", cancellationToken);
    }

    private static async Task<bool> HasUniqueClipHashIndexAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'idx_clips_hash_unique';";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) > 0;
    }

    /// <summary>
    /// Rebuilds the search index and records that it left merge work behind, for
    /// <see cref="DrainSearchIndexMergesAsync"/>.
    /// </summary>
    /// <remarks>
    /// A rebuild ends mid-merge, and FTS5 charges the outstanding merges to later
    /// writes in proportion to what each adds: the first 1.9 MB capture after a
    /// rebuild of the 60k fixture took 2.6 s against 0.4 s once the merges were
    /// done (docs/plans/2026-10-28-001-fts-rebuild-and-upgrade-start-research.md).
    /// The record is persisted so a restart before the drain finishes resumes it.
    /// </remarks>
    private static async Task RebuildClipSearchIndexAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(connection, "INSERT INTO clips_fts(clips_fts) VALUES ('rebuild');", cancellationToken);
        await ExecuteNonQueryAsync(connection, """
            INSERT INTO app_metadata (key, value) VALUES ('fts_merge_pending', '1')
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """, cancellationToken);
    }

    /// <summary>Pages each merge step works through; a step measured 10-60 ms at this size.</summary>
    internal const int SearchIndexMergeStepPages = 64;

    internal static TimeSpan SearchIndexMergePause { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>Test seam: replaces the pause between merge steps.</summary>
    internal static Func<CancellationToken, Task>? BetweenSearchIndexMergeSteps { get; set; }

    /// <summary>
    /// Finishes the merges a search-index rebuild left pending, in short steps,
    /// so no capture pays for them (research note, R1). Each step is its own
    /// short write transaction, and steps pause so other writers get the lock.
    /// Returns false at once when no rebuild left work; stops when a step does
    /// nothing, which FTS5 reports as fewer than two changes.
    /// </summary>
    public Task<bool> DrainSearchIndexMergesAsync(CancellationToken cancellationToken = default)
        => RunPausableAsync(DrainSearchIndexMergesCoreAsync, cancellationToken);

    private async Task<bool> DrainSearchIndexMergesCoreAsync(CancellationToken cancellationToken)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using (var pending = connection.CreateCommand())
        {
            pending.CommandText = "SELECT value FROM app_metadata WHERE key = 'fts_merge_pending';";
            if (await pending.ExecuteScalarAsync(cancellationToken) is not "1")
            {
                return false;
            }
        }

        await using var step = connection.CreateCommand();
        step.CommandText = $"INSERT INTO clips_fts(clips_fts, rank) VALUES ('merge', {SearchIndexMergeStepPages});";
        await using var changes = connection.CreateCommand();
        changes.CommandText = "SELECT total_changes();";
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var before = Convert.ToInt64(await changes.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            await step.ExecuteNonQueryAsync(cancellationToken);
            var after = Convert.ToInt64(await changes.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            if (after - before < 2)
            {
                break;
            }

            await (BetweenSearchIndexMergeSteps?.Invoke(cancellationToken) ?? Task.Delay(SearchIndexMergePause, cancellationToken));
        }

        await ExecuteNonQueryAsync(connection, "DELETE FROM app_metadata WHERE key = 'fts_merge_pending';", cancellationToken);
        return true;
    }

    /// <summary>
    /// Detects if the existing FTS table has an older (2- or 4-column) schema
    /// or an older tokenizer (e.g. unicode61) and drops it along with its
    /// triggers so the Schema DDL can recreate them with the current 5-column
    /// schema and the trigram tokenizer (which supports substring matching).
    /// </summary>
    /// <returns>
    /// True when the table was dropped, meaning the caller is responsible for
    /// repopulating the empty index the schema DDL is about to create.
    /// </returns>
    private static async Task<bool> MigrateFtsSchemaIfNeededAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        if (!await FtsSchemaNeedsMigrationAsync(connection, cancellationToken))
        {
            return false;
        }

        // Drop old triggers and FTS table so they're recreated with the new schema/tokenizer.
        await ExecuteNonQueryAsync(connection, "DROP TRIGGER IF EXISTS clips_ai;", cancellationToken);
        await ExecuteNonQueryAsync(connection, "DROP TRIGGER IF EXISTS clips_ad;", cancellationToken);
        await ExecuteNonQueryAsync(connection, "DROP TRIGGER IF EXISTS clips_au;", cancellationToken);
        await ExecuteNonQueryAsync(connection, "DROP TABLE IF EXISTS clips_fts;", cancellationToken);
        return true;
    }

    /// <summary>
    /// The decision half of <see cref="MigrateFtsSchemaIfNeededAsync"/>, split out
    /// because the integrity gate has to ask the same question before any of this
    /// runs. Two callers, one answer: if they disagreed, the gate would wave through
    /// a migration that then rewrites pages unchecked.
    /// </summary>
    private static async Task<bool> FtsSchemaNeedsMigrationAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        // Nothing to migrate if the FTS table isn't there; the Schema DDL creates it.
        await using var checkCommand = connection.CreateCommand();
        checkCommand.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='clips_fts';";
        var exists = Convert.ToInt64(await checkCommand.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) > 0;
        if (!exists)
        {
            return false;
        }

        // Check column count by querying the FTS table's content definition
        var columnCount = 0;
        await using (var colCommand = connection.CreateCommand())
        {
            colCommand.CommandText = "PRAGMA table_info(clips_fts);";
            await using var reader = await colCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                columnCount++;
            }
        }

        // Inspect the stored CREATE statement to determine the tokenizer.
        string storedSql = string.Empty;
        await using (var sqlCommand = connection.CreateCommand())
        {
            sqlCommand.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='clips_fts';";
            var raw = await sqlCommand.ExecuteScalarAsync(cancellationToken);
            storedSql = raw as string ?? string.Empty;
        }

        var hasTrigramTokenizer = storedSql.Contains("trigram", StringComparison.OrdinalIgnoreCase);

        // The current schema has 5 content columns and uses the trigram tokenizer.
        // Older versions had 2 or 4 content columns, or used unicode61.
        return columnCount < 5 || !hasTrigramTokenizer;
    }

    /// <summary>
    /// True when this launch is going to rewrite structure - an FTS rebuild, a
    /// column migration, a backfill, a dedupe. That is the only thing the integrity
    /// check protects, and it is not free: PRAGMA quick_check reads every page, so
    /// on a 352MB library it costs ~800ms even with the file already in the OS
    /// cache, which was 96% of the time this method spent on an established
    /// database. Every launch paid it so that the launches which migrate would be
    /// safe.
    /// </summary>
    private static async Task<bool> HasPendingStructuralWorkAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        // Read the version straight from sqlite_master's table rather than through
        // ReadSchemaVersionAsync: this runs before the Schema DDL, so app_metadata
        // may not exist yet. A database without it has never been stamped and is
        // about to be migrated in full.
        await using (var metadataCommand = connection.CreateCommand())
        {
            metadataCommand.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='app_metadata';";
            if (Convert.ToInt64(await metadataCommand.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 0)
            {
                return true;
            }
        }

        if (await ReadSchemaVersionAsync(connection, cancellationToken) < CurrentSchemaVersion)
        {
            return true;
        }

        return await FtsSchemaNeedsMigrationAsync(connection, cancellationToken);
    }

    private static async Task ExecuteNonQueryAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Runs <c>PRAGMA quick_check</c> before any migration step starts rewriting
    /// pages, so a structurally corrupt file is reported rather than made worse
    /// by a half-finished migration.
    ///
    /// It is not cheap and the cost is not about corruption: quick_check reads
    /// every page, so it scales with the size of the library. Measured at ~800ms
    /// on a 352MB database with the file already in the OS cache. That is why
    /// callers gate it on <see cref="HasPendingStructuralWorkAsync"/> instead of
    /// running it on every launch.
    /// </summary>
    private async Task VerifyIntegrityRecordingFailureAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            await VerifyIntegrityAsync(connection, cancellationToken);
        }
        catch (InvalidOperationException) when (!cancellationToken.IsCancellationRequested)
        {
            HasFailedIntegrityCheck = true;
            throw;
        }
        finally
        {
            // Run, whatever its outcome: a failure is HasFailedIntegrityCheck's to
            // report. A check stopped part-way (a pause, F-13-1) has not run.
            if (!cancellationToken.IsCancellationRequested)
            {
                IsIntegrityCheckOwed = false;
            }
        }
    }

    private static async Task VerifyIntegrityAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var problems = new List<string>();
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check(5);";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var row = reader.GetString(0);
                if (!string.Equals(row, "ok", StringComparison.Ordinal))
                {
                    problems.Add(row);
                }
            }
        }
        catch (SqliteException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Damage bad enough to break the walk itself aborts the pragma instead
            // of reporting rows, so without this the user gets a bare "SQLite Error
            // 11" from the one check whose entire job is to say what to do about it.
            problems.Add(ex.Message);
        }

        if (problems.Count == 0)
        {
            return;
        }

        var summary = string.Join("; ", problems.Take(5));
        if (problems.Count > 5)
        {
            summary += $" (+{problems.Count - 5} more)";
        }
        throw new InvalidOperationException(
            $"Database integrity check failed: {summary}. The file is corrupted; restore a backup or contact support.");
    }

    private static DateTimeOffset? ParseTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
    }

    private sealed record ClipAggregationRow(
        long Id,
        string Hash,
        string? SourceApp,
        bool IsFavorite,
        bool IsSensitive,
        DateTimeOffset? CapturedAt,
        DateTimeOffset? FirstCopiedAt,
        DateTimeOffset? LastCopiedAt,
        int CopyCount);
}

