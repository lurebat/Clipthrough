# U-C8 - encrypted-SQLite replacement evaluation (F-21 / OD-2b)

> The harness, fixtures, per-check results and `run-log.txt` live in `tools/sqlite-compat/`; every relative path below (`run-log.txt`, `results/`, `fixtures/`, `Common/` ...) is relative to that folder. `Clipthrough.Tests/Compatibility/SqlCipherFixtureCompatibilityTests` opens the committed fixtures through the app's own connection factory on every test run, so an adoption unit has to keep it green.

**Evaluation only.** The evaluation changed no application code and installed nothing into the app,
and nothing here authorises adoption: that needs a separate owner decision.

Everything below was measured on 2026-10-26 on Windows x64 (32 logical CPUs, .NET SDK 10.0.403), with all
`dotnet` processes at BelowNormal priority on a shared machine. `run-log.txt` holds the full output of the
final `run-all.ps1` run. Per-check JSON is in `results/`. Citations: `[R:x]` points to a section of
`run-log.txt`, and `[U:n]` to a URL in the evidence index at the end.

## Summary

- **SQLite3MC.PCLRaw.bundle 2.6.1** (SQLite3 Multiple Ciphers, in SQLCipher v4 legacy mode) is the only
  candidate I could test that ships SQLite >= 3.50.2. It embeds **SQLite 3.54.0** [R:B sqlite3mc].
  - It opens all four existing fixtures: plaintext, SQLCipher 4.5.2, rekeyed, and one with an
    uncheckpointed -wal. The data is identical down to checksum, FTS counts and integrity checks.
  - Everything it writes reads back in the current build: 14 of 14 files, plus a negative control.
  - The KDF and cipher parameters match the current ones.
  - One app feature has no direct equivalent: `sqlcipher_export()` does not exist. Measured
    replacements exist for both directions (§3).
- Two adoption hazards matter most. First, every connection must carry the cipher recipe. Without it
  3mc falls back to its default cipher (sqleet/ChaCha20), which the current build cannot read. Second,
  the bundle pulls in SQLitePCLRaw.core 3.x underneath Microsoft.Data.Sqlite 10.0.11. That pair worked in
  every test here, but Microsoft has not tested it.
- The other candidates:
  - `SQLitePCLRaw.bundle_e_sqlcipher` has no version after 2.1.11, and nuget.org marks it deprecated.
  - `SQLitePCLRaw.bundle_e_sqlite3mc` 2.1.11 is also deprecated. It embeds SQLite 3.49.1, which is still
    vulnerable to CVE-2025-6965, and it refuses `PRAGMA rekey` in WAL mode.
  - Zetetic's commercial SQLCipher and a self-built SQLCipher were **not tested empirically** (§1, §4).

## 1. Candidates

| # | Package (resolved) | Version | Licence | SQLite / cipher (measured) | Native RIDs | Size | Notes |
|---|---|---|---|---|---|---|---|
| 0 (current) | `SQLitePCLRaw.bundle_e_sqlcipher` (+`lib.e_sqlcipher`, `provider.e_sqlcipher`; `SQLitePCLRaw.core` 2.1.12) | 2.1.11 (newest; no 2.1.12+ or 3.x) [U:1] | Apache-2.0 package [U:2]; SQLCipher BSD-3 [U:12] | **3.39.2** / SQLCipher **4.5.2 community** [R:B current] | 27 folders: win-x64/x86/arm64/arm, win10-*, linux-* incl. musl, osx, maccatalyst, browser-wasm [R:package sizes] | lib nupkg 21,094,009 B; win-x64 dll 1,852,928 B | Deprecated "Legacy" on nuget.org [R:supplementary]; author stopped free encryption builds in v3 [U:3][U:4] |
| C1 | `SQLite3MC.PCLRaw.bundle` (+`.lib`, `.provider` 2.6.1; pulls `SQLitePCLRaw.core` >= 3.0.2, resolved 3.0.2) | 2.6.1 (2026-10-12) [U:1] | MIT [U:2][U:5] | **3.54.0** / SQLite3 Multiple Ciphers **2.6.1** [R:B sqlite3mc] | 31 folders: win-x64/x86/arm64 (no 32-bit win-arm), linux-* incl. musl/riscv64/s390x, osx-x64/arm64, maccatalyst, android-*, ios/iossimulator, browser-wasm | lib nupkg 38,171,190 B; win-x64 dll 2,474,320 B (+621 KB) | Not deprecated. win-x64 dll Authenticode-signed by SignPath Foundation. Package id not prefix-verified. Single maintainer (U. Telle). |
| C2 | `SQLitePCLRaw.bundle_e_sqlite3mc` (+`lib`/`provider` 2.1.11; core 2.1.12) | 2.1.11 (newest) | Apache-2.0 | **3.49.1** / 3mc **2.1.0** [R:B e_sqlite3mc] | 27, same set as current | lib nupkg 25,683,797 B; win-x64 dll 2,204,160 B | Deprecated "Legacy"; frozen like the current bundle |
| C3 | Zetetic SQLCipher for .NET (Commercial Edition; packages from a local NuGet source, used with `SQLitePCLRaw.provider.sqlcipher`) [U:7] | unknown (not purchased) | Commercial, "starting at $999 USD per application per year", revenue < $50M [U:6] | Upstream SQLCipher 4.18.0 is based on SQLite 3.53.4 [U:8] - not verified for the .NET package | unknown | unknown | **Not tested** |
| C4 | Community source build of SQLCipher (e.g. 4.18.0) + `SQLitePCLRaw.provider.sqlcipher` 3.0.5 or `config.e_sqlite3` | n/a | BSD-3 [U:12] | 3.53.4 per changelog [U:8] | whatever we build | unknown | **Not built.** We would own a per-RID native build with a crypto provider (LibTomCrypt was restored in 4.14 [U:8]) |
| - | SourceGear SQLite build service (3mc or SEE builds) [U:4] | paid | paid | - | - | - | Not tested. SEE cannot open SQLCipher databases [U:4] |
| - | `SQLitePCLRaw.bundle_e_sqlite3` 3.0.5 / `SourceGear.sqlite3` | 3.0.5 | Apache-2.0 | 3.53.4 per its dependency | - | - | No encryption, so N-A |

Other nuget.org search hits were rejected: `Shiny.DocumentDb.Sqlite.SqlCipher` and `sqlite-net-sqlcipher`
just depend on `bundle_e_sqlcipher`, and `SQLCipherSharp3`, `BetterPractice.SqlCipher.Core` and
`NSystem.Data.SQLiteCipher` are tiny, not SQLitePCLRaw bundles, or of unclear provenance (nuget search API
query, see the evidence index).

## 2. Compatibility matrix

The fixtures were generated by the **current** stack: `fixtures/`, `manifest.json`, 199 rows each,
non-ASCII text, an FTS5 trigram external-content table with its three triggers, and an ON DELETE CASCADE
child table. The control column runs the same harness against the current stack, which validates the
harness itself.

| Check | Current (control) | C1 SQLite3MC.PCLRaw 2.6.1 | C2 e_sqlite3mc 2.1.11 | C3 Zetetic | C4 source build |
|---|---|---|---|---|---|
| **1. Existing DBs open** (plaintext / 4.5.2 / rekeyed / with -wal) | PASS: 4/4 | **PASS**: 4/4. Rows, checksum, 9 trigram MATCH counts, `integrity_check` and FTS5 `integrity-check` all identical, including the fixture with a 156,592-byte uncheckpointed -wal. *Requires the recipe:* without it the open fails with error 26. The PRAGMA recipe also PASSes. | PASS: 4/4, same recipe | N-A (not tested; expected to work since it is SQLCipher, but unverified) | N-A |
| **2. Round trip to the current build** | PASS: 12/12 | **PASS: 14/14 files**, each re-read with the same checksum and FTS counts and then written by the current stack: writes into all 4 fixtures, a new DB, a backup copy, a rekeyed copy, encrypt and decrypt outputs, purge + VACUUM, an odd path, and an **uncheckpointed -wal written by 3mc**. **Not one-way.** The negative control (`cipher=sqlcipher` *without* `legacy=4`) is unreadable by the current build (26), so `legacy=4` is what keeps rollback possible. | PASS: 12/12, same negative control | N-A | N-A |
| **3. Features we use** | PASS, apart from the known limitation: `PRAGMA rekey` to or from '' is a silent no-op (measured), so `sqlcipher_export` is needed | **PASS with one code change.** PASS: FTS5 trigram + triggers + `optimize`; FK cascade; backup API enc->enc; `PRAGMA rekey` enc->enc **in WAL**; `busy_timeout` (5000 read back, waited 600+ ms, error 5 at 0); WAL + `wal_checkpoint(TRUNCATE)`; VACUUM; PurgeSensitiveClips sequence; `Mode=ReadOnly` (write -> 8); wrong key / no key / text file / missing file -> 26/26/26/14, as `SqliteErrors` expects; a path with space, `#`, `%` and `ü`. FAIL: `sqlcipher_export` does not exist. The 3mc equivalents PASS: encrypt with `journal_mode=DELETE` + `PRAGMA rekey='k'` (refused in WAL with an explicit error), or `VACUUM INTO` a keyed URI; decrypt with `PRAGMA rekey=''`, which works **in WAL**. | **FAIL**: `PRAGMA rekey` is refused in WAL, which breaks the app's RekeyAsync path, and `sqlcipher_export` is missing. Workaround: DELETE mode for every rekey. | N-A | N-A |
| **4. KDF / cipher params** | PBKDF2-HMAC-SHA512, 256000 iterations, HMAC-SHA512, 4096-byte pages, `use_hmac=1`, no plaintext header | **PASS**: `cipher=sqlcipher legacy=4 kdf_iter=256000 kdf_algorithm=2 (SHA512) hmac_algorithm=2 (SHA512) hmac_use=1 legacy_page_size=4096 plaintext_header_size=0`. Proven in both directions by cross-opening. No migration needed. | PASS (same) | N-A | N-A |
| **5. Size / RIDs / indicative perf** | see §1 and the table below | PASS: +621 KB native on win-x64; covers win-x64/x86/arm64. Not slower on this workload. | PASS (same caveats) | unknown | unknown |
| **6. SQLite version / CVE** | **FAIL**: 3.39.2 < 3.50.2 (CVE-2025-6965) | **PASS**: 3.54.0 >= 3.50.2 | **FAIL**: 3.49.1 < 3.50.2 | Upstream 3.53.4 (unverified for the package) | depends on what we build |

**Indicative timing only** (median of 3, 20,000 inserts in batches of 100, plus 200 trigram searches with
LIMIT 50) [R:B *]. The real comparison is deferred to the U-D0 60k-clip fixture.

| Stack | enc: open+schema | enc: 20k inserts | enc: 200 searches | enc: open + first query (KDF) | plain: 20k inserts | plain: 200 searches |
|---|---|---|---|---|---|---|
| current e_sqlcipher 2.1.11 | 569 ms | 2016 ms | 466 ms | 573 ms | 1531 ms | 290 ms |
| C1 SQLite3MC 2.6.1 | 213 ms | 1838 ms | 378 ms | 203 ms | 1533 ms | 327 ms |
| C2 e_sqlite3mc 2.1.11 | 209 ms | 1824 ms | 393 ms | 199 ms | 1510 ms | 323 ms |

Key derivation is about 2.8x faster under 3mc at identical parameters (256000 iterations, SHA-512). I have
not explained why; the likely cause is a different PBKDF2 / SHA-512 implementation. The work factor for an
attacker is unchanged, because the same key comes out. Earlier runs gave similar ratios.

### CVE check (sqlite.org's CVE table, fetched 2026-10-26 [U:9])

The fix version for each CVE is in brackets.

- **Current, 3.39.2.** Affected by:
  - CVE-2025-6965 [3.50.2];
  - CVE-2025-7709 [3.50.3]: a corrupt FTS5 index, relevant because restore opens user-supplied files;
  - CVE-2026-11822/11824 [3.53.2]: FTS5 with DEFENSIVE off;
  - CVE-2025-3277, CVE-2025-29087 and CVE-2025-29088 [3.49.1];
  - CVE-2024-0232 [3.43.2];
  - CVE-2025-7458 [3.42.0].
  - CVE-2023-7104 [3.43.1] applies only if the session extension is compiled in, which I did not check.
- **C2, 3.49.1.** Still affected by 6965, 7709 and 11822/11824.
- **C1, 3.54.0.** Above every fix version in that table.
- **SQLCipher itself.** The 4.15.0 changelog sanitised the source DB name passed to `sqlcipher_export`, after
  a security report [U:8]. 4.5.2 lacks that fix, but the app only passes the constant alias `rekeyed`.
- **Unknown.** Whether separate CVEs exist for SQLCipher or for SQLite3 Multiple Ciphers: the web search for
  them failed (HTTP 504), and I did not retry.

## 3. Exact recipes (C1; C2 is the same SQL)

**Connection (URI recipe, used for every check above).** The `Password` keyword works unchanged with
Microsoft.Data.Sqlite 10.0.11:

```csharp
// DataSource must be a SQLite URI; '%', '?', '#' and ' ' percent-encoded (Mc3Stack.FileUri in Common/Stacks.cs)
var b = new SqliteConnectionStringBuilder {
    DataSource = "file:///C:/Users/x/AppData/Local/Clipthrough/clips.db?cipher=sqlcipher&legacy=4",
    Mode = mode, ForeignKeys = true, Pooling = pooling,
};
if (password.Length > 0) b.Password = password;   // MDS issues the key; 3mc applies the URI cipher config first
```

**PRAGMA recipe (alternative, PASS).** Use a plain path and *no* `Password`, then make these the first
statements on the connection:
`PRAGMA cipher='sqlcipher'; PRAGMA legacy=4; PRAGMA key='<escaped>';`. This still worked after MDS's
`foreign_keys` pragma and the app's `busy_timeout` StateChange handler had already run
[R:1.open.b-with-pragma-recipe]. 3mc documents that these pragmas on `main` set connection defaults [U:10].

**Plaintext DBs** open with no URI and no password: the ClipAngel and RequiresPassword shape PASSes.

| Operation | Current (4.5.2) | C1 (measured) |
|---|---|---|
| enc -> enc | backup copy + `PRAGMA rekey='new'` | **unchanged**; works in WAL. Like the current stack, the salt is reused. |
| plain -> enc | `ATTACH $p AS rekeyed KEY $k; SELECT sqlcipher_export('rekeyed'); PRAGMA rekeyed.journal_mode=WAL;` | Backup copy, then on the copy (URI recipe, no key): `PRAGMA journal_mode=DELETE; PRAGMA rekey='k'; PRAGMA journal_mode=WAL;`. Alternative: `VACUUM INTO 'file:///...?cipher=sqlcipher&legacy=4&key=<url-encoded>'`, which then needs `journal_mode=WAL` and puts the key into the SQL text. |
| enc -> plain | `ATTACH ... KEY ''` + `sqlcipher_export` | Backup copy, then `PRAGMA rekey='';`, which works in WAL. |
| backup | `src.BackupDatabase(dst)`, same key on both sides | unchanged (PASS). Plain<->enc backup is refused by **both** stacks. |

## 4. Recommendation and risks

If the owner chooses to replace the stack, **C1 (`SQLite3MC.PCLRaw.bundle` 2.6.1, SQLCipher v4 legacy
mode)** is the candidate to take into an adoption unit. It is the only option I tested that fixes F-21,
keeps the on-disk format, and leaves rollback to the current build possible. **This is not a request to
proceed.** Adoption needs the owner's separate approval. If a paid, vendor-supported SQLCipher is
preferable, C3 is the alternative to price and test: it would keep `sqlcipher_export` and the 4.5.2
semantics. It is unverified.

Risks, measured unless marked:

1. **Format fork if any connection misses the recipe.**
   - Without the recipe, opening an existing DB fails with 26, which `SqliteErrors` reports as "incorrect
     password". A *new* DB created by such a site is written in sqleet/ChaCha20, which the current build
     cannot read; I demonstrated this with `cipher=sqlcipher` minus `legacy=4`.
   - Mitigation: one helper builds every connection string, plus a test that fails on any other builder
     that sets `Password`.
   - A process-wide default via `sqlite3mc_config(NULL, ...)` exists [U:11]. *Untested.*
2. **Microsoft.Data.Sqlite 10.0.11 running on SQLitePCLRaw.core 3.0.2.** It worked in every check here, but
   MDS 10.0.11 declares `SQLitePCLRaw.core >= 2.1.12` and MDS 11 rc targets 3.0.5 [U:1], so Microsoft has
   not tested this pair. Pin `SQLitePCLRaw.core` explicitly.
3. **Supply chain.** One maintainer, MIT licence, a package id that is not prefix-reserved, and signed
   binaries. The project is actively released (12 versions, 2.2.5-rc.1 to 2.6.1) [U:1][U:5].
4. **Encrypting a plaintext DB needs a non-WAL journal on the copy**, and `WriteRekeyedCopy` changes
   shape, so it needs its own tests and mutants.
5. **`VACUUM INTO` with `key=`** puts the passphrase in SQL and URI text. Prefer the rekey path.
6. **Behaviour of SQLite 3.39.2 -> 3.54.0 beyond these fixtures** (query plans, the 60k-clip workload) is
   unmeasured. That is U-D0.
7. **Not covered here.** C3 and C4 were not run. No Linux, macOS or ARM64 run. 3mc's `cipher=rc4` as a
   replacement for ClipAngel's own CAPI decryption was not tested; the current ClipAngel path works
   unchanged.

## 5. What an adoption unit would change

- **`Clipthrough/Clipthrough.csproj`**: replace `SQLitePCLRaw.bundle_e_sqlcipher` 2.1.11 with
  `SQLite3MC.PCLRaw.bundle` 2.6.1 and pin `SQLitePCLRaw.core` 3.0.x. `Program.cs` `Batteries_V2.Init()`
  keeps working, because the bundle ships `SQLitePCLRaw.batteries_v2.dll` and the harness calls it.
- **A new single connection-string helper** (URI recipe), used by everything below.
- **`Database/SqliteConnectionFactory.Create`**: DataSource becomes the URI recipe.
- **`Services/Storage/SqliteDatabaseCopier.OpenConnection`**: same.
- **`StorageOptionsService`**:
  - `TryOpenWithPassword` / `CanOpenWithPassword`. Without the recipe, every unlock fails with 26.
  - `OpenUnpooled` and the other builder (around line 463).
  - `WriteRekeyedCopy`: replace `ATTACH ... KEY` + `sqlcipher_export` with backup copy + `PRAGMA rekey`
    (encrypt branch: DELETE -> rekey -> WAL).
  - `RequiresPassword` works unchanged, but route it through the helper too.
  - The `RekeyAsync` SQL is unchanged.
- **`DatabaseBackupService.PurgeSensitiveClips`**, `ValidateBackupReadable` and restore validation: use the
  helper.
- **`ClipAngelImportService`**: no change needed (plaintext temp DB, plain path: PASS).
- **`Database/SqliteErrors`**: logic unchanged (26 preserved); update the provider name in the doc comment.
- **Tests**: `StorageOptionsServiceTests`, `DatabaseBackupServiceTests`, `SqliteErrorsTests`,
  `TestDoubles`. Add the 4.5.2 fixtures from this folder as a compatibility test, a test that a
  recipe-less builder is caught, and mutants for the recipe and the encrypt branch.
- **Docs that mention SQLCipher** (`AGENTS.md`, `.github/copilot-instructions.md`,
  `.github/copilot-cli-skills/clipthrough-storage-schema.md`, `CONCEPTS.md`, `docs/solutions/`): add a
  solution doc on the recipe and fork hazard.

## Evidence index

**Commands** (all inside this folder, recorded in `run-log.txt`):

- `pwsh run-all.ps1`: build, `FixtureGen gen`, the three harnesses with bench, and `FixtureGen verify` per
  candidate. Its sections are `[R:A]`, `[R:B <tag>]`, `[R:B.2]`, `[R:package sizes]`, `[R:summary]`; `[R:supplementary]` holds the nuget.org registration/deprecation, version-list, Authenticode and search outputs.
- NuGet API queries via `Invoke-RestMethod`:
  - `https://api.nuget.org/v3-flatcontainer/<id>/index.json` for version lists;
  - `.../registration5-gz-semver2/<id>/index.json` for dates, licences, dependencies and the `deprecation`
    field. That showed `bundle_e_sqlcipher` and `bundle_e_sqlite3mc` 2.1.11 as Legacy-deprecated, and
    `SQLite3MC.PCLRaw.bundle` 2.6.1 as not deprecated;
  - `https://azuresearch-usnc.nuget.org/query?q=sqlcipher|sqlite3mc` for the package search.
- `Get-AuthenticodeSignature` on both win-x64 dlls: `sqlite3mc.dll` is Valid (SignPath Foundation);
  `e_sqlcipher.dll` is NotSigned.
- Read-only `git show c80100d:.../StorageOptionsService.cs` and `git show ef136d0:.../DatabaseBackupService.cs`
  for the app's `WriteRekeyedCopy` and `PurgeSensitiveClips` code.

**URLs:**

- [U:1] https://api.nuget.org/v3-flatcontainer/{sqlite3mc.pclraw.bundle,sqlitepclraw.bundle_e_sqlcipher,sqlitepclraw.core,microsoft.data.sqlite.core}/index.json and the registration5 endpoints
- [U:2] nuget.org licence fields: SQLite3MC.PCLRaw.* = MIT; SQLitePCLRaw.* = Apache-2.0
- [U:3] https://github.com/ericsink/SQLitePCL.raw/blob/main/v3.md
- [U:4] https://github.com/ericsink/SQLitePCL.raw/wiki/SQLite-encryption-options-for-use-with-SQLitePCLRaw
- [U:5] https://github.com/utelle/SQLite3MultipleCiphers-NuGet (README: URI recipe, the .NET 10 Password note, MIT) and https://github.com/utelle/SQLite3MultipleCiphers/releases (2.6.1 = SQLite 3.54.0)
- [U:6] https://www.zetetic.net/sqlcipher/buy/ (redirects to /sqlcipher/commercial/)
- [U:7] https://www.zetetic.net/sqlcipher/sqlcipher-for-dotnet/
- [U:8] https://github.com/sqlcipher/sqlcipher/blob/master/CHANGELOG.md
- [U:9] https://www.sqlite.org/cves.html
- [U:10] https://utelle.github.io/SQLite3MultipleCiphers/docs/configuration/config_sql_pragmas/ and https://utelle.github.io/SQLite3MultipleCiphers/docs/ciphers/cipher_sqlcipher/
- [U:11] https://utelle.github.io/SQLite3MultipleCiphers/docs/configuration/config_capi/
- [U:12] https://github.com/sqlcipher/sqlcipher/blob/master/LICENSE.md

**Folder layout:**

- `Common/` holds the shared harness;
- `FixtureGen/` is the current stack: `gen`, `verify`, and the control `harness`;
- `Harness3mc/` is C1 and `HarnessE3mc211/` is C2;
- `fixtures/` holds the four fixtures plus `manifest.json`;
- `results/` holds the per-check JSON;
- `dn.ps1` is the BelowNormal dotnet runner;
- `nuget.config` uses nuget.org only.

`bin/`, `obj/` and `work/` are deleted after a run.
