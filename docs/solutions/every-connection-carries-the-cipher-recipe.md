---
tags: [sqlite, encryption, storage, dependencies]
version: unreleased (after v0.17.1)
severity: p0
status: active
---

# Every connection carries the cipher recipe

## Problem

Clipthrough encrypts the library with SQLite3 Multiple Ciphers
(`SQLite3MC.PCLRaw.bundle`), configured to write the SQLCipher 4 format that
every installed library already uses. That configuration is per connection, and
a connection opened without it does not fail. It quietly uses the library's
default cipher (sqleet/ChaCha20):

- opening an existing library fails with error 26, which `SqliteErrors` reports
  as "incorrect password", so the user is told their password is wrong;
- creating or encrypting a library writes a file that no SQLCipher build can
  read. It reads back fine through the same misconfigured connection, so nothing
  notices until a correctly configured connection, or a rollback to the old
  build, meets it.

The second case is the dangerous one, because a test that builds its own
connection reproduces it and passes. When the package was swapped, about forty
test sites did exactly that, and stayed green.

## Root cause

The old bundle (`SQLitePCLRaw.bundle_e_sqlcipher`) spoke only one cipher, so a
key alone was enough. SQLite3 Multiple Ciphers supports several, and which one a
key applies to is chosen by connection parameters the caller must supply.

## Solution

`SqliteConnectionFactory.DataSourceFor` turns every path into a SQLite URI that
carries `cipher=sqlcipher&legacy=4`, and `SqliteConnectionFactory.Create` is the
only place a connection is built. The recipe is added with or without a key:
encrypting a plaintext library opens it without one and then runs
`PRAGMA rekey`, and the key takes the scheme of the connection it is set on.

`legacy=4` is not optional. `cipher=sqlcipher` without it is a different scheme
that the SQLCipher 4.5.2 build cannot read, which would make the switch one-way.

Rekeying no longer needs `sqlcipher_export` (which this library does not have).
`StorageOptionsService.WriteRekeyedCopy` takes a backup-API copy under the
current key and runs `PRAGMA rekey` on it. Encrypting a plaintext copy is
refused in WAL mode, so that one direction switches the copy to a rollback
journal and back.

## Prevention

- `ConnectionConstructionGuardTests` fails on a hand-built connection, the
  ADO.NET provider factory or a reassigned connection string, in shipping code
  and in tests. Tests use `TestConnections.Create(connectionString)`, which
  passes the string's settings to the factory. Its rules are patterns, not
  substrings, so a target-typed `new(...)`, a namespace-qualified type name or
  different spacing is still caught; each spelling is pinned by one of its
  `*_CatchesEverySpelling` theories.
- `CipherRecipeTests` checks the written format without asking the library:
  it decrypts page 1 with .NET's PBKDF2 and AES under SQLCipher 4's parameters.
  It runs that check against a fixture the old build wrote first, so it can
  tell a real SQLCipher file from anything else.
- `SqlCipherFixtureCompatibilityTests` opens the four committed fixtures the old
  build wrote (`tools/sqlite-compat/fixtures`) through the factory.
- Mutants `cipher-recipe-dropped`, `cipher-recipe-without-legacy`,
  `cipher-recipe-only-for-keyed-connections`, `a-test-builds-its-own-connection`,
  `guard-target-typed-construction`, `guard-qualified-construction` and
  `guard-connectionstring-spacing`.
- After any bump of `SQLite3MC.PCLRaw.bundle`, `SQLitePCLRaw.core` or
  `Microsoft.Data.Sqlite`, re-run the storage mutants: the pairing of
  Microsoft.Data.Sqlite 10 with SQLitePCLRaw.core 3 is ours to verify, not
  Microsoft's. The evaluation and its harness are in
  `docs/plans/2026-10-26-001-encrypted-sqlite-evaluation.md` and
  `tools/sqlite-compat/`.
