---
tags: [persistence, sqlite, threading, background-workers]
version: v0.17.1
severity: p1
status: active
---

# A pooled connection fails its first DELETE with "no such table" after another connection's DDL

## Problem

During a deferred upgrade's background payload move, the periodic maintenance
failed with `SQLite Error 1: 'no such table: clips'` from
`ClipStoreService.DeleteOlderThanAsync`, the first statement on its connection.
The table plainly existed: the same connection read it from `sqlite_master` a
moment later. The error ended the Rx maintenance loop for the session (found in
the live window, check F2; reproduced on an untouched 60k upgrade).

## Cause

A bug in SQLite itself, reproduced on 3.50.4 (Python's sqlite3) and 3.54.0
(SQLite3 Multiple Ciphers 2.6.1) with no app code:

1. Connection S reads a table, so it holds that schema.
2. Another connection changes the schema - any DDL, here the move's
   `DROP INDEX idx_clips_ocr_images` (or the icon-hash index built after it).
3. S runs `DELETE FROM` the table. The prepare fails with "no such table" when
   the table has an FTS5 `'delete'` trigger (`clips_ad`) **and** foreign keys
   are on (S cascades into `clip_payloads` and others).

Drop either ingredient and the DELETE succeeds. `SELECT` and `UPDATE` succeed.
Normally a stale schema is harmless - SQLite notices the cookie change and
re-prepares - but here the FTS5 table is connected for the first time inside
the DELETE's prepare, and the error escapes as a parse error instead.

The pool turns this into a race: any connection opened before the DDL and
returned to the pool is S, and the first caller to DELETE through it fails.
Whether a test or a session sees it depends on which connection the pool hands
out, so it looks intermittent.

## Fix

`SqliteConnectionFactory` brings every connection's schema up to date when it
is opened: `SELECT COUNT(*) FROM sqlite_master WHERE 0` checks the schema cookie
and reloads a stale schema (it costs one empty read on a current one). A
failure there - a wrong key, a file that is not a database - is swallowed and
surfaces at the caller's first statement, as before. `PRAGMA schema_version`
and `BEGIN; COMMIT` do **not** refresh the schema; they were measured.

The maintenance loop also no longer dies on a failed tick: the error is caught
inside the `SelectMany`, reported, and the next tick runs.

A connection opened before a DDL and still checked out when it lands can still
hit the bug on its next DELETE. Every store method opens its own connection, so
that window is one operation long.

## Tests

- `StaleSchemaConnectionTests` - a pooled connection still deletes after another
  connection's DDL; the first maintenance during the background move succeeds.
- `MainWindowViewModelHeadlessTests.MaintenanceLoop_SurvivesAFailingTick`.
- Mutants `stale-schema-not-refreshed-on-open`, `maintenance-loop-dies-on-a-failed-tick`.

## Minimal reproduction (no app code)

```python
import sqlite3, tempfile, os
p = os.path.join(tempfile.mkdtemp(), "r.db")
s = sqlite3.connect(p, isolation_level=None)
s.executescript("""PRAGMA journal_mode=WAL;
CREATE TABLE p (id INTEGER PRIMARY KEY, body TEXT); CREATE INDEX p_body ON p(body);
CREATE TABLE c (pid INTEGER PRIMARY KEY REFERENCES p(id) ON DELETE CASCADE);
CREATE VIRTUAL TABLE p_fts USING fts5(body, content='p', content_rowid='id');
CREATE TRIGGER p_ad AFTER DELETE ON p BEGIN
  INSERT INTO p_fts(p_fts, rowid, body) VALUES('delete', old.id, old.body); END;""")
s.close()
stale = sqlite3.connect(p, isolation_level=None)
stale.execute("PRAGMA foreign_keys=1"); stale.execute("SELECT COUNT(*) FROM p").fetchone()
sqlite3.connect(p, isolation_level=None).execute("DROP INDEX p_body")
stale.execute("DELETE FROM p WHERE id < 0")   # sqlite3.OperationalError: no such table: p
```

Worth reporting upstream; check whether a newer SQLite still fails before
removing the refresh.
