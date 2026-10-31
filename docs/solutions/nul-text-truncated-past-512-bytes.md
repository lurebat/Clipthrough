---
tags: [persistence, sqlite, data-loss]
version: v0.17.1
severity: p1
status: active
---

# Text after a NUL is lost when a clip passes 512 UTF-8 bytes

## Problem

A clip whose text held U+0000 (Base64 Decode can produce one; so can a paste
from a binary-ish source) was stored as only the text before the NUL once the
text passed about 500 UTF-8 bytes. Shorter clips kept everything. Nothing
reported it: the capture succeeded and the shortened text became the only copy.

## Root cause

Not SQLite and not Clipthrough's SQL: the parameter binding.
Microsoft.Data.Sqlite binds a string through SQLitePCLRaw's
`raw.sqlite3_bind_text(stmt, index, string)`. That overload converts strings of
up to 512 UTF-8 bytes on the stack and binds them with their length, but binds a
longer one through `to_utf8z()` - NUL-terminated - so SQLite reads it only up to
the first NUL. The threshold is the constant `OptimizedLengthThreshold = 512` in
SQLitePCLRaw's `raw.cs`, which is why the loss started at "about 500 bytes".

Check whether the dependency still does this: run mutant
`clip-text-with-nul-bound-as-string` (or `ClipStoreServiceTests` with the
helper below forced to bind a string). If it survives, the binding no longer
truncates and the workaround can go.

## Fix

`ClipStoreService.ClipTextParameter` binds a clip's text as its UTF-8 bytes when
it contains a NUL, and the SQL reads every use as `CAST($content AS TEXT)`.
A BLOB carries its own length, and casting it to TEXT gives back the same text,
NUL and all. Text without a NUL still binds as a string; the cast is a no-op on
TEXT. All three writers go through it: `InsertClipAsync`, `CaptureBatchCoreAsync`
and `UpdateDeferredContentCoreAsync`.

## Guarding it

- `ClipStoreServiceTests.*_KeepsTextAfterANul_AtEveryLength` capture, batch-capture
  and update text of 16, 505, 520 and 5000 characters. Before the fix, 520 and
  5000 came back as `"left"`; 16 and 505 already passed, which is what pins the
  threshold.
- The capture test also asserts `typeof(content) = 'text'`, so dropping the cast
  (a NUL-holding clip stored as a BLOB) fails it.
- Mutants `clip-text-with-nul-bound-as-string` and `clip-text-bytes-stored-as-blob`.

Any new statement that writes user text which can hold a NUL must use the same
pair: bind with `ClipTextParameter`, read with `CAST(... AS TEXT)`.
