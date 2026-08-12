---
tags: [sqlite, storage, backup, encryption, data-loss]
version: v0.13.0
severity: p0
status: active
---

# Copying a live SQLite database file produces a corrupt copy

## Problem

Four places copied the database by checkpointing the WAL and then copying the
main `.db` file with `File.Copy`:

```csharp
CheckpointSafely(sourcePath);          // PRAGMA wal_checkpoint(TRUNCATE)
File.Copy(sourcePath, destination, overwrite: true);
```

Two of those places - the storage-path move and the rekey - then **deleted the
source**, so the copy became the only database the user had.

The checkpoint is not guaranteed to succeed. If any other connection is holding
a read snapshot, SQLite checkpoints as much as it can and reports that it could
not finish. Crucially it reports this **in the result row**, not by throwing:

```
PRAGMA wal_checkpoint(TRUNCATE)  ->  busy=1  log=4  ckpt=3
```

`ExecuteNonQuery` discards result rows, so every call site was blind to it.

The comment in the code claimed the resulting copy would be "valid (if slightly
stale)". Measured against a real database with a reader pinning an older
snapshot, it is not stale - it does not open at all:

```
SQLite Error 11: 'database disk image is malformed'
```

That is the expected outcome, not bad luck. The copied main file is a mix of
pages from two different transactions, and the committed pages that reconcile
them are in the `-wal` file that was not copied.

Clipthrough hits this routinely: the embedding worker and the OCR queue hold
read connections while the UI triggers a backup or a path move.

## Root cause

The checkpoint pragma reports failure in a result row that nobody read, and a
main-file copy taken from a partly checkpointed WAL is not a valid database by
itself - the pages that reconcile it live in the `-wal` file that was left
behind. The code assumed a checkpoint either succeeds or throws, and that a copy
of the main file alone is at worst out of date.

## Solution

Use `SqliteConnection.BackupDatabase` (the SQLite Online Backup API) instead
of a file copy. It reads through the same page cache the writer uses, so it sees
a consistent snapshot including the WAL, and it retries pages the writer touches
mid-copy. `Clipthrough/Services/Storage/SqliteDatabaseCopier.cs` is the only
supported way to copy a database in this codebase.

```csharp
SqliteDatabaseCopier.CopyDatabase(sourcePath, password, destinationPath);
```

Three details that are easy to get wrong:

- **`Pooling = false` on both connections.** Otherwise the pooled handle keeps
  the file open after `Dispose` and the caller's `File.Move` or
  `File.Delete` fails with a sharing violation.
- **Delete an existing destination first.** The backup API overwrites the
  destination's contents, but a *truncated or differently-keyed* leftover fails
  to open, and that failure would block every future copy to that path.
- **Give the destination connection the same password.** It works with
  SQLCipher, and the copy stays encrypted - but only if the key is set. Omit it
  and the backup writes plaintext.

## Prevention

Never call `File.Copy` on a database that any connection may have open. Route
every copy through `SqliteDatabaseCopier`. If a new call site needs different
behaviour, extend the copier rather than open-coding a copy beside it.

`DatabaseBackupServiceTests` and `StorageOptionsServicePhase2Tests` build a
post-crash "hot WAL" fixture: create the database in a staging directory and,
**while the connection is still open**, copy the `.db` and `-wal` to the
target. A clean close would checkpoint and delete the WAL, making the test
vacuous - so the helper self-verifies by copying the main file alone and
asserting the row is *not* readable from it.

Guarded by mutants `backup-copies-the-file-instead-of-the-database`,
`move-copies-the-file-instead-of-the-database`,
`database-copy-loses-the-encryption-key`,
`database-copy-keeps-the-files-open` and
`database-copy-reuses-an-unopenable-destination`.

## Note on the rekey path

`RekeyAsync` verifies the password by opening and cleanly closing the database
before it copies, which already checkpoints any hot WAL. It uses the copier for
consistency, but it is not reachable the way the backup and move paths are, so
there is deliberately no mutant claiming otherwise.