---
tags: [sqlite, security, storage, sensitive]
version: unreleased (after v0.17.1)
severity: p1
status: active
---

# Deleted clips stay in the file unless secure_delete is on

## Problem

Deleting a clip, by hand or when the sensitive-clip lifetime expires it,
removed the row but not its bytes. SQLite unlinks the pages a row used and
puts them on the free list; their content stays in the file until a later
write happens to reuse them. A plaintext library therefore kept the text of
expired secrets readable straight off the disk, in every copy of the file
(backups, sync clients) and in tools that read raw pages.

## Root cause

`secure_delete` is off by default in SQLite, and it is a per-connection
setting: nothing persists it in the file.

## Solution

`SqliteConnectionFactory` turns it on for every connection it opens, in the
same `StateChange` handler that sets `busy_timeout`:
`PRAGMA busy_timeout = 5000; PRAGMA secure_delete = ON;`. Freed content is
zeroed as it is freed.

Two things it does not cover, and which are covered elsewhere:

- **The WAL.** Until a checkpoint, the WAL still holds the page images written
  before the delete. Checkpoints happen automatically; the backup purge
  (`DatabaseBackupService.PurgeSensitiveClips`) runs `VACUUM`, which rewrites
  the copy entirely.
- **Encrypted libraries** never show the text either way. There, this only
  removes ciphertext that the key could still decrypt.

## Cost

Measured on the U-D0 fixture (60k clips, ~1.6 GB) on a quiet machine on
2026-10-27. Capture took 0.98-1.06x the time without it, on plaintext and
encrypted libraries alike (the median of four samples per build), which is
under the 10% the plan set as the point to stop and ask.

Deletes and the retention purge were measured in a second quiet window, each
probe in its own process on a fresh copy (`PerfProbes.P15_DeleteAndPurge`).
Single deletes cost about 1.00x. The bulk retention purge of 3,072 clips cost
1.13x on plaintext (5.95 -> 6.70 s) and 1.00x encrypted. The plaintext purge is
over the 10% threshold; the owner kept the pragma on for every library and
accepted that cost (2026-10-28). If it is ever restricted to encrypted
libraries, plaintext libraries lose the guarantee this doc is about.

## Prevention

`SecureDeleteTests` captures a clip with a unique marker in a plaintext
library, deletes it, checkpoints, and asserts the marker is no longer
anywhere in the file. It fails with the pragma removed (mutant
`secure-delete-not-enabled`).
