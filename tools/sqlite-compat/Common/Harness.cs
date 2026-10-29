using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Uc8;

public sealed class Harness(Stack s, string fixturesDir, string workDir)
{
    public List<CheckResult> Checks { get; } = new();
    public List<RoundTripItem> RoundTrips { get; } = new();
    readonly Manifest m = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(Path.Combine(fixturesDir, "manifest.json")))!;

    FixtureInfo F(string name) => m.Fixtures.Single(f => f.Name == name);

    void Rec(string id, int row, string status, string detail)
    {
        Checks.Add(new CheckResult { Id = id, Row = row, Status = status, Detail = detail });
        Console.WriteLine($"[{status}] {id}: {detail}");
    }

    void Try(string id, int row, Func<(string Status, string Detail)> f)
    {
        try
        {
            var (st, d) = f();
            Rec(id, row, st, d);
        }
        catch (Exception ex)
        {
            Rec(id, row, "FAIL", Err(ex));
        }
    }

    public static string Err(Exception ex) => ex is SqliteException se
        ? $"SqliteException code={se.SqliteErrorCode} ext={se.SqliteExtendedErrorCode}: {se.Message}"
        : $"{ex.GetType().Name}: {ex.Message}";

    string W(string name) => Path.Combine(workDir, name);

    string CopyFixture(FixtureInfo f, string newName)
    {
        var dst = W(newName);
        Db.DeleteDb(dst);
        File.Copy(Path.Combine(fixturesDir, f.File), dst);
        if (f.WalFile is not null)
        {
            File.Copy(Path.Combine(fixturesDir, f.WalFile), dst + "-wal");
        }
        return dst;
    }

    Snapshot SnapAt(string path, string? pw)
    {
        using var c = s.Open(path, pw);
        return Db.Snap(c);
    }

    void AddRoundTrip(string desc, string path, string pw, bool expectOpenFailure = false)
    {
        // Freeze a closed copy so later checks that keep writing to `path` cannot change what is verified.
        var frozen = Path.ChangeExtension(path, ".rt.db");
        Db.DeleteDb(frozen);
        File.Copy(path, frozen);
        if (File.Exists(path + "-wal")) File.Copy(path + "-wal", frozen + "-wal");
        var snap = SnapAt(frozen, pw);
        RoundTrips.Add(new RoundTripItem { Description = desc, File = frozen, Password = pw, Rows = snap.Rows, Tags = snap.Tags, Checksum = snap.Checksum, Fts = snap.Fts, ExpectOpenFailure = expectOpenFailure });
    }

    static string Expect(FixtureInfo f, Snapshot snap)
    {
        var diffs = new List<string>();
        if (snap.Rows != f.Rows) diffs.Add($"rows {snap.Rows}!={f.Rows}");
        if (snap.Tags != f.Tags) diffs.Add($"tags {snap.Tags}!={f.Tags}");
        if (snap.Checksum != f.Checksum) diffs.Add("checksum mismatch");
        foreach (var kv in f.Fts)
        {
            if (!snap.Fts.TryGetValue(kv.Key, out var v) || v != kv.Value) diffs.Add($"fts '{kv.Key}' {v}!={kv.Value}");
        }
        if (snap.Integrity != "ok") diffs.Add("integrity " + snap.Integrity);
        if (snap.FtsIntegrity != "ok") diffs.Add("fts-integrity " + snap.FtsIntegrity);
        return string.Join("; ", diffs);
    }

    int ErrorCodeOpening(string path, string? pw, string? rawCs = null)
    {
        try
        {
            using var c = rawCs is null ? s.Open(path, pw) : Stack.OpenRaw(rawCs);
            Db.Scalar(c, "SELECT count(*) FROM sqlite_master;");
            return 0;
        }
        catch (SqliteException ex)
        {
            return ex.SqliteErrorCode;
        }
    }

    public void Run()
    {
        Directory.CreateDirectory(workDir);

        // ---- Row 6: versions -------------------------------------------------
        Try("6.versions", 6, () =>
        {
            using var c = s.Open(W("version-probe.db"), null);
            var v = s.Versions(c);
            return ("INFO", string.Join(" | ", v.Select(kv => $"{kv.Key}={kv.Value}")));
        });

        // ---- Row 1: existing fixtures open -----------------------------------
        foreach (var f in m.Fixtures)
        {
            Try($"1.open.{f.Name}", 1, () =>
            {
                var path = CopyFixture(f, $"open-{f.Name}.db");
                var walBytes = File.Exists(path + "-wal") ? new FileInfo(path + "-wal").Length : 0;
                using var c = s.Open(path, f.Password);
                var snap = Db.Snap(c);
                var diff = Expect(f, snap);
                return (diff.Length == 0 ? "PASS" : "FAIL", $"{(walBytes > 0 ? $"(with {walBytes}-byte -wal) " : "")}{snap.Describe()}{(diff.Length > 0 ? " DIFF: " + diff : "")}");
            });
        }

        if (s.IsMultipleCiphers)
        {
            Try("1.open.b-with-app-connection-string-unchanged", 1, () =>
            {
                var path = CopyFixture(F("b-enc-sqlcipher452"), "open-b-noparams.db");
                var code = ErrorCodeOpening(path, null, Stack.AppStyleConnectionString(path, F("b-enc-sqlcipher452").Password, SqliteOpenMode.ReadWriteCreate));
                return ("INFO", code == 0 ? "opened WITHOUT cipher params (unexpected)" : $"fails without cipher=sqlcipher&legacy=4: SQLite error {code} (3mc default cipher is chacha20/sqleet), so the recipe is mandatory");
            });

            Try("1.open.b-with-pragma-recipe", 1, () =>
            {
                var f = F("b-enc-sqlcipher452");
                var path = CopyFixture(f, "open-b-pragma.db");
                // App-style builder, plain path, NO Password keyword; cipher scheme + key via PRAGMAs after open.
                using var c = Stack.OpenRaw(Stack.AppStyleConnectionString(path, null, SqliteOpenMode.ReadWriteCreate));
                Db.Exec(c, $"PRAGMA cipher = 'sqlcipher'; PRAGMA legacy = 4; PRAGMA key = '{f.Password}';");
                var snap = Db.Snap(c);
                var diff = Expect(f, snap);
                return (diff.Length == 0 ? "PASS" : "FAIL", "PRAGMA cipher/legacy/key after Open (ForeignKeys=true and busy_timeout already ran): " + (diff.Length == 0 ? "data matches manifest" : diff));
            });
        }

        if (s.IsMultipleCiphers)
        {
            Try("1.open.a-with-app-connection-string-unchanged", 1, () =>
            {
                var f = F("a-plain-wal");
                var path = CopyFixture(f, "open-a-noparams.db");
                using var c = Stack.OpenRaw(Stack.AppStyleConnectionString(path, null, SqliteOpenMode.ReadWriteCreate));
                var diff = Expect(f, Db.Snap(c));
                return (diff.Length == 0 ? "PASS" : "FAIL", "plaintext DB via plain path, no Password, no URI (ClipAngelImportService / RequiresPassword shape): " + (diff.Length == 0 ? "data matches manifest" : diff));
            });

            Try("4.non-legacy-sqlcipher-scheme (cipher=sqlcipher without legacy=4)", 4, () =>
            {
                var p = W("rt-sqlcipher-nonlegacy.db");
                Db.DeleteDb(p);
                var cs = Stack.AppStyleConnectionString(Mc3Stack.FileUri(p, "cipher=sqlcipher"), "nonlegacy-pass", SqliteOpenMode.ReadWriteCreate);
                using (var c = Stack.OpenRaw(cs))
                {
                    Db.CreateSchema(c);
                    Db.Populate(c, 1, 50);
                }
                using (var c = Stack.OpenRaw(cs))
                {
                    var snap = Db.Snap(c);
                    RoundTrips.Add(new RoundTripItem { Description = "NEGATIVE CONTROL: created with cipher=sqlcipher but WITHOUT legacy=4", File = p, Password = "nonlegacy-pass", Rows = snap.Rows, Tags = snap.Tags, Checksum = snap.Checksum, Fts = snap.Fts, ExpectOpenFailure = true });
                }
                return ("INFO", "written for the round-trip step; whether the current stack opens it shows if legacy=4 is required for writing");
            });
        }

        // ---- Row 2: writes by the candidate (round trip verified later by the current stack) ----
        foreach (var f in m.Fixtures)
        {
            Try($"2.write.{f.Name}", 2, () =>
            {
                var path = CopyFixture(f, $"rt-write-{f.Name}.db");
                using (var c = s.Open(path, f.Password))
                {
                    Db.Populate(c, 1001, 50);
                    Db.Mutate(c, 20, 30);
                }
                AddRoundTrip($"{f.Name} fixture after candidate wrote 50 rows + update + cascading delete", path, f.Password);
                var snap = SnapAt(path, f.Password);
                return (Db.Healthy(snap) ? "PASS" : "FAIL", $"header={Db.Header(path)} {snap.Describe()}");
            });
        }

        var newEnc = W("rt-new-enc.db");
        Try("2.create.new-encrypted-db", 2, () =>
        {
            Db.DeleteDb(newEnc);
            using (var c = s.Open(newEnc, "rt-new-pass"))
            {
                Db.CreateSchema(c);
                Db.Populate(c, 1, 300, batch: 100);
                Db.Mutate(c, 7, 8);
            }
            AddRoundTrip("new encrypted DB created from scratch by the candidate", newEnc, "rt-new-pass");
            var snap = SnapAt(newEnc, "rt-new-pass");
            return (Db.Healthy(snap) && Db.Header(newEnc) != "plaintext" ? "PASS" : "FAIL", $"header={Db.Header(newEnc)} {snap.Describe()}");
        });

        Try("2.wal-left-behind-by-candidate (crash-then-rollback scenario)", 2, () =>
        {
            var live = W("rt-wal-live.db");
            var left = W("rt-wal-left.db");
            Db.DeleteDb(live);
            Db.DeleteDb(left);
            using var c = s.Open(live, "wal-pass");
            Db.CreateSchema(c);
            Db.Populate(c, 1, 100);
            Db.Rows(c, "PRAGMA wal_checkpoint(TRUNCATE);");
            Db.Exec(c, "PRAGMA wal_autocheckpoint = 0;");
            Db.Populate(c, 101, 100);
            Db.Mutate(c, 5, 10);
            var snap = Db.Snap(c, checkFtsIntegrity: false);
            Db.CopyShared(live, left);
            Db.CopyShared(live + "-wal", left + "-wal");
            var walLen = new FileInfo(left + "-wal").Length;
            // Recorded straight from the live connection: opening `left` here would checkpoint away the -wal under test.
            RoundTrips.Add(new RoundTripItem { Description = $"encrypted .db + {walLen}-byte uncheckpointed -wal written by the candidate (copied while open)", File = left, Password = "wal-pass", Rows = snap.Rows, Tags = snap.Tags, Checksum = snap.Checksum, Fts = snap.Fts });
            return (walLen > 0 ? "PASS" : "FAIL", $"left .db {new FileInfo(left).Length} B + -wal {walLen} B for the current stack to replay; {snap.Describe()}");
        });

        // ---- Row 3: features ---------------------------------------------------
        Try("3.fts5-trigram+triggers", 3, () =>
        {
            using var c = s.Open(newEnc, "rt-new-pass");
            long Count(string q)
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT count(*) FROM clips_fts WHERE clips_fts MATCH $q;";
                cmd.Parameters.AddWithValue("$q", q);
                return Convert.ToInt64(cmd.ExecuteScalar());
            }
            var edited = Count("\"edited\"");
            var deleted = Count("\"Clip 00008\"");
            var kept = Count("\"Clip 00007\"");
            var hebrew = Count("\"עולם\"");
            var short2 = Count("\"He\"");
            Db.Exec(c, "INSERT INTO clips_fts(clips_fts) VALUES('optimize');");
            var snap = Db.Snap(c);
            var ok = edited == 1 && deleted == 0 && kept == 1 && hebrew == 30 && snap.Integrity == "ok" && snap.FtsIntegrity == "ok";
            return (ok ? "PASS" : "FAIL", $"MATCH edited={edited} (exp 1, UPDATE trigger) deleted-row={deleted} (exp 0, DELETE trigger) kept-row={kept} (exp 1) hebrew={hebrew} (exp 30) 2-char-query={short2}; after 'optimize': integrity={snap.Integrity} fts-integrity={snap.FtsIntegrity}");
        });

        Try("3.foreign-key-cascade", 3, () =>
        {
            using var c = s.Open(newEnc, "rt-new-pass");
            var fk = Db.ScalarString(c, "PRAGMA foreign_keys;");
            var orphans = Db.ScalarString(c, "SELECT count(*) FROM clip_tags WHERE clip_id NOT IN (SELECT id FROM clips);");
            var tagOf8 = Db.ScalarString(c, "SELECT count(*) FROM clip_tags WHERE clip_id = 8;");
            Db.Exec(c, "DELETE FROM clips WHERE id = 12;");
            var tagOf12 = Db.ScalarString(c, "SELECT count(*) FROM clip_tags WHERE clip_id = 12;");
            var ok = fk == "1" && orphans == "0" && tagOf8 == "0" && tagOf12 == "0";
            return (ok ? "PASS" : "FAIL", $"PRAGMA foreign_keys={fk} orphans={orphans} tags-of-deleted-8={tagOf8} tags-of-deleted-12={tagOf12}");
        });

        var b = F("b-enc-sqlcipher452");
        var bkSrc = CopyFixture(b, "bk-src.db");
        var bkDst = W("bk-dst.db");
        Try("3.backup-api.encrypted-to-encrypted", 3, () =>
        {
            // Mirrors SqliteDatabaseCopier.CopyDatabase: delete dst, open both with the same password, BackupDatabase.
            Db.DeleteDb(bkDst);
            using (var src = s.Open(bkSrc, b.Password))
            using (var dst = s.Open(bkDst, b.Password))
            {
                src.BackupDatabase(dst);
            }
            var a = SnapAt(bkSrc, b.Password);
            var d = SnapAt(bkDst, b.Password);
            AddRoundTrip("backup-API copy (encrypted->encrypted) made by the candidate", bkDst, b.Password);
            var ok = Db.SameData(a, d) && Db.Healthy(d) && Db.Header(bkDst) != "plaintext";
            return (ok ? "PASS" : "FAIL", $"dst header={Db.Header(bkDst)} journal={JournalMode(bkDst, b.Password)} {d.Describe()}");
        });

        Try("3.backup-api.plaintext-to-encrypted", 3, () =>
        {
            var src = CopyFixture(F("a-plain-wal"), "bk-plain-src.db");
            var dst = W("bk-plain-to-enc.db");
            Db.DeleteDb(dst);
            using (var sc = s.Open(src, null))
            using (var dc = s.Open(dst, "bk-enc-pass"))
            {
                try
                {
                    sc.BackupDatabase(dc);
                }
                catch (SqliteException ex)
                {
                    return ("INFO", "not used by the app; refused: " + Err(ex));
                }
            }
            var hdr = Db.Header(dst);
            var code = ErrorCodeOpening(dst, "bk-enc-pass");
            return ("INFO", $"not used by the app; dst header={hdr}, reopen-with-key error={code}");
        });

        Try("3.rekey.encrypted-to-encrypted (app RekeyAsync path)", 3, () =>
        {
            var rk = W("rk-enc-enc.db");
            Db.DeleteDb(rk);
            using (var src = s.Open(bkSrc, b.Password))
            using (var dst = s.Open(rk, b.Password))
            {
                src.BackupDatabase(dst);
            }
            string jm;
            var hdrBefore = Db.Header(rk);
            using (var c = s.Open(rk, b.Password))
            {
                jm = Db.ScalarString(c, "PRAGMA journal_mode;");
                Db.Exec(c, "PRAGMA rekey = 'rekeyed-pass';");
            }
            var oldCode = ErrorCodeOpening(rk, b.Password);
            var snap = SnapAt(rk, "rekeyed-pass");
            var ok = oldCode == 26 && snap.Checksum == b.Checksum && Db.Healthy(snap);
            if (ok) AddRoundTrip("backup-API copy rekeyed encrypted->encrypted by the candidate (PRAGMA rekey in WAL mode)", rk, "rekeyed-pass");
            return (ok ? "PASS" : "FAIL", $"journal_mode during rekey={jm}; header(salt) before={hdrBefore} after={Db.Header(rk)} ({(hdrBefore == Db.Header(rk) ? "salt REUSED" : "new salt")}); old password now -> error {oldCode}; new password -> {snap.Describe()}");
        });

        var a0 = F("a-plain-wal");
        // Add encryption: every mechanism the candidate might offer.
        Try("3.encrypt-plaintext.attach-key+sqlcipher_export (app WriteRekeyedCopy path)", 3, () =>
            Export(a0, "", "enc-export-pass", "enc-via-export.db"));
        Try("3.encrypt-plaintext.pragma-rekey-in-WAL", 3, () =>
            NativeRekey(a0, "", "enc-rekey-pass", "enc-via-rekey-wal.db", switchToDelete: false));
        Try("3.encrypt-plaintext.pragma-rekey-after-journal_mode=DELETE", 3, () =>
            NativeRekey(a0, "", "enc-rekey-pass", "enc-via-rekey-delete.db", switchToDelete: true));
        Try("3.encrypt-plaintext.vacuum-into-keyed-uri", 3, () =>
        {
            var dst = W("enc-via-vacuum-into.db");
            Db.DeleteDb(dst);
            var target = s.EncryptedVacuumIntoTarget(dst, "enc-vacuum-pass");
            if (target is null) return ("N-A", "stack has no keyed VACUUM INTO target");
            var src = CopyFixture(a0, "enc-vacuum-src.db");
            using (var c = s.Open(src, null))
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "VACUUM INTO $t;";
                cmd.Parameters.AddWithValue("$t", target);
                cmd.ExecuteNonQuery();
            }
            return VerifyProduced(dst, "enc-vacuum-pass", a0, "plaintext fixture VACUUM INTO keyed URI");
        });

        // Remove encryption.
        Try("3.decrypt.attach-key-empty+sqlcipher_export (app WriteRekeyedCopy path)", 3, () =>
            Export(b, b.Password, "", "dec-via-export.db"));
        Try("3.decrypt.pragma-rekey-empty-in-WAL", 3, () =>
            NativeRekey(b, b.Password, "", "dec-via-rekey-wal.db", switchToDelete: false));
        Try("3.decrypt.pragma-rekey-empty-after-journal_mode=DELETE", 3, () =>
            NativeRekey(b, b.Password, "", "dec-via-rekey-delete.db", switchToDelete: true));
        Try("3.decrypt.vacuum-into-plain-path", 3, () =>
        {
            var dst = W("dec-via-vacuum-into.db");
            Db.DeleteDb(dst);
            var src = CopyFixture(b, "dec-vacuum-src.db");
            using (var c = s.Open(src, b.Password))
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "VACUUM INTO $t;";
                cmd.Parameters.AddWithValue("$t", dst);
                cmd.ExecuteNonQuery();
            }
            var hdr = Db.Header(dst);
            if (hdr == "plaintext") return VerifyProduced(dst, "", b, "encrypted fixture VACUUM INTO plain path");
            var withKey = ErrorCodeOpening(dst, b.Password);
            return ("INFO", $"VACUUM INTO a plain path wrote header={hdr}; opens with the source key: {(withKey == 0 ? "yes" : "no, error " + withKey)} (i.e. not a decrypt mechanism)");
        });

        Try("3.busy_timeout", 3, () =>
        {
            using var a = s.Open(newEnc, "rt-new-pass");
            using var bconn = s.Open(newEnc, "rt-new-pass");
            var bt = Db.ScalarString(bconn, "PRAGMA busy_timeout;");
            using var tx = a.BeginTransaction(deferred: false);
            Db.Exec(a, "INSERT INTO clips(content) VALUES('lock holder');", tx);
            var sw = Stopwatch.StartNew();
            var t = Task.Run(() => Db.Exec(bconn, "INSERT INTO clips(content) VALUES('waiter');"));
            Thread.Sleep(600);
            tx.Commit();
            t.Wait();
            var waited = sw.ElapsedMilliseconds;
            Db.Exec(bconn, "PRAGMA busy_timeout = 0;");
            using var tx2 = a.BeginTransaction(deferred: false);
            Db.Exec(a, "INSERT INTO clips(content) VALUES('lock holder 2');", tx2);
            int code = 0;
            try { Db.Exec(bconn, "INSERT INTO clips(content) VALUES('no wait');"); }
            catch (SqliteException ex) { code = ex.SqliteErrorCode; }
            tx2.Rollback();
            var ok = bt == "5000" && waited >= 500 && code == 5;
            return (ok ? "PASS" : "FAIL", $"busy_timeout read back={bt}; writer blocked by IMMEDIATE tx waited {waited} ms then succeeded; with busy_timeout=0 -> error {code} (5=SQLITE_BUSY)");
        });

        Try("3.wal+checkpoint(TRUNCATE)", 3, () =>
        {
            using var c = s.Open(newEnc, "rt-new-pass");
            var jm = Db.ScalarString(c, "PRAGMA journal_mode;");
            Db.Populate(c, 5001, 20);
            var walBefore = new FileInfo(newEnc + "-wal").Length;
            var cp = string.Join(";", Db.Rows(c, "PRAGMA wal_checkpoint(TRUNCATE);"));
            var walAfter = new FileInfo(newEnc + "-wal").Length;
            var ok = jm == "wal" && cp.StartsWith("0|", StringComparison.Ordinal) && walAfter == 0 && walBefore > 0;
            return (ok ? "PASS" : "FAIL", $"journal_mode={jm}; -wal {walBefore} bytes -> checkpoint row ({cp}) -> {walAfter} bytes");
        });

        Try("3.vacuum+integrity", 3, () =>
        {
            using (var c = s.Open(newEnc, "rt-new-pass"))
            {
                Db.Exec(c, "VACUUM;");
            }
            var snap = SnapAt(newEnc, "rt-new-pass");
            return (Db.Healthy(snap) && Db.Header(newEnc) != "plaintext" ? "PASS" : "FAIL", $"after VACUUM header={Db.Header(newEnc)} integrity={snap.Integrity} fts-integrity={snap.FtsIntegrity}");
        });

        Try("3.purge-sensitive (DatabaseBackupService.PurgeSensitiveClips sequence)", 3, () =>
        {
            var p = CopyFixture(b, "purge.db");
            long removed;
            using (var c = s.Open(p, b.Password, SqliteOpenMode.ReadWrite))
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = "DELETE FROM clips WHERE is_sensitive = 1;";
                removed = cmd.ExecuteNonQuery();
                Db.Exec(c, "INSERT INTO clips_fts(clips_fts) VALUES('optimize');");
                Db.Exec(c, "VACUUM;");
            }
            var snap = SnapAt(p, b.Password);
            AddRoundTrip("fixture b after candidate purged sensitive rows + FTS optimize + VACUUM", p, b.Password);
            var sens = 0L;
            using (var c = s.Open(p, b.Password)) sens = Convert.ToInt64(Db.Scalar(c, "SELECT count(*) FROM clips WHERE is_sensitive = 1;"));
            return (Db.Healthy(snap) && sens == 0 && removed > 0 ? "PASS" : "FAIL", $"removed {removed} rows (cascade), remaining sensitive={sens}, {snap.Describe()}");
        });

        Try("3.read-only-open", 3, () =>
        {
            using var c = s.Open(bkSrc, b.Password, SqliteOpenMode.ReadOnly);
            var n = Db.ScalarString(c, "SELECT count(*) FROM clips;");
            int code = 0;
            try { Db.Exec(c, "INSERT INTO clips(content) VALUES('x');"); }
            catch (SqliteException ex) { code = ex.SqliteErrorCode; }
            return (n == b.Rows.ToString(System.Globalization.CultureInfo.InvariantCulture) && code == 8 ? "PASS" : "FAIL", $"Mode=ReadOnly read {n} rows; write -> error {code} (8=SQLITE_READONLY)");
        });

        Try("3.password-failure-codes (SqliteErrors.IsPasswordFailure expects 26)", 3, () =>
        {
            var wrong = ErrorCodeOpening(bkSrc, "wrong-password");
            var none = ErrorCodeOpening(bkSrc, null);
            var txt = W("not-a-db.txt");
            File.WriteAllText(txt, "this is a text file, not a database. ".PadRight(8192, 'x'));
            var text = ErrorCodeOpening(txt, "whatever");
            var missing = 0;
            try { using var c = s.Open(W("missing-dir\\missing.db"), b.Password, SqliteOpenMode.ReadOnly); }
            catch (SqliteException ex) { missing = ex.SqliteErrorCode; }
            var ok = wrong == 26 && none == 26 && text == 26 && missing == 14;
            return (ok ? "PASS" : "FAIL", $"wrong key -> {wrong}, no key -> {none}, text file -> {text}, missing file -> {missing} (app expects 26/26/26/14)");
        });

        Try("3.special-characters-in-path", 3, () =>
        {
            var dir = W("dir with space #1 %41 ü");
            Directory.CreateDirectory(dir);
            var p = Path.Combine(dir, "clip db #2.db");
            Db.DeleteDb(p);
            using (var c = s.Open(p, "path-pass"))
            {
                Db.CreateSchema(c);
                Db.Populate(c, 1, 20);
            }
            var exists = File.Exists(p);
            var strays = Directory.GetFiles(workDir, "*", SearchOption.AllDirectories).Where(x => x.Contains("%", StringComparison.Ordinal) && !x.StartsWith(dir, StringComparison.Ordinal)).ToList();
            var snap = SnapAt(p, "path-pass");
            if (exists) AddRoundTrip("encrypted DB created by the candidate under a path with space/#/%/non-ASCII", p, "path-pass");
            return (exists && strays.Count == 0 && snap.Rows == 20 ? "PASS" : "FAIL", $"file at intended path={exists}, stray files={strays.Count}, rows={snap.Rows}");
        });

        // ---- Row 4: cipher parameters ------------------------------------------
        Try("4.cipher-params.on-fixture-b", 4, () =>
        {
            using var c = s.Open(bkSrc, b.Password);
            return ("INFO", string.Join(" | ", s.CipherParams(c).Select(kv => $"{kv.Key}={kv.Value}")));
        });
        Try("4.cipher-params.on-candidate-created-db", 4, () =>
        {
            using var c = s.Open(newEnc, "rt-new-pass");
            return ("INFO", string.Join(" | ", s.CipherParams(c).Select(kv => $"{kv.Key}={kv.Value}")));
        });
    }

    string JournalMode(string path, string pw)
    {
        using var c = s.Open(path, pw);
        return Db.ScalarString(c, "PRAGMA journal_mode;");
    }

    (string, string) VerifyProduced(string dst, string newPw, FixtureInfo source, string what)
    {
        var hdr = Db.Header(dst);
        var wantPlain = newPw.Length == 0;
        if ((hdr == "plaintext") != wantPlain)
        {
            return ("FAIL", $"{what}: header={hdr} - expected {(wantPlain ? "plaintext" : "encrypted")} (operation was a silent no-op or wrong)");
        }
        var snap = SnapAt(dst, newPw);
        var diff = Expect(source, snap);
        if (diff.Length > 0) return ("FAIL", $"{what}: header={hdr}; {diff}");
        var jm = JournalMode(dst, newPw);
        AddRoundTrip($"{what} (candidate) -> {(wantPlain ? "plaintext" : "encrypted")}", dst, newPw);
        return ("PASS", $"{what}: header={hdr}, journal_mode={jm}, data identical to fixture '{source.Name}'");
    }

    (string, string) Export(FixtureInfo src, string srcPw, string newPw, string name)
    {
        var dst = W(name);
        Db.DeleteDb(dst);
        var copy = CopyFixture(src, "export-src-" + name);
        using (var c = s.Open(copy, srcPw))
        {
            var jm = Db.ScalarString(c, "PRAGMA main.journal_mode;");
            using var cmd = c.CreateCommand();
            cmd.CommandText = "ATTACH DATABASE $path AS rekeyed KEY $key; SELECT sqlcipher_export('rekeyed'); " +
                (jm == "wal" ? "PRAGMA rekeyed.journal_mode = WAL; " : "") + "DETACH DATABASE rekeyed;";
            cmd.Parameters.AddWithValue("$path", newPw.Length == 0 ? dst : s.AttachFileName(dst));
            cmd.Parameters.AddWithValue("$key", newPw);
            cmd.ExecuteNonQuery();
        }
        return VerifyProduced(dst, newPw, src, "ATTACH..KEY + sqlcipher_export");
    }

    (string, string) NativeRekey(FixtureInfo src, string srcPw, string newPw, string name, bool switchToDelete)
    {
        var p = CopyFixture(src, name);
        string before, after;
        using (var c = s.Open(p, srcPw))
        {
            before = Db.ScalarString(c, "PRAGMA journal_mode;");
            if (switchToDelete) Db.ScalarString(c, "PRAGMA journal_mode = DELETE;");
            Db.Exec(c, $"PRAGMA rekey = '{newPw}';");
            if (switchToDelete) Db.ScalarString(c, "PRAGMA journal_mode = WAL;");
            after = Db.ScalarString(c, "PRAGMA journal_mode;");
        }
        var (st, d) = VerifyProduced(p, newPw, src, $"PRAGMA rekey '{(srcPw.Length == 0 ? "plain" : "enc")}'->'{(newPw.Length == 0 ? "plain" : "enc")}' (journal {before}{(switchToDelete ? "->delete->wal" : "")}, ends {after})");
        return (st, d);
    }

    public static Dictionary<string, string> Bench(Stack s, string workDir, int iterations = 3, int n = 20000, int searches = 200)
    {
        string[] terms = ["Hello", "עולם", "日本語", "café", "😀🚀✨", "Русс", "example", "42;", "Clip 001", "#12", "naïve", "Ελλη", "quotes", "world", "SELECT", "テキスト", "party", "поиска", "hello%20", "Tab"];
        var result = new Dictionary<string, string>();
        foreach (var pw in new[] { "bench-pass", "" })
        {
            var create = new List<double>(); var insert = new List<double>(); var search = new List<double>(); var reopen = new List<double>(); long hits = 0;
            for (int it = 0; it < iterations; it++)
            {
                var path = Path.Combine(workDir, $"bench-{(pw.Length > 0 ? "enc" : "plain")}-{it}.db");
                Db.DeleteDb(path);
                var sw = Stopwatch.StartNew();
                using (var c = s.Open(path, pw))
                {
                    Db.CreateSchema(c);
                    create.Add(sw.Elapsed.TotalMilliseconds);
                    sw.Restart();
                    Db.Populate(c, 1, n, batch: 100);
                    insert.Add(sw.Elapsed.TotalMilliseconds);
                    sw.Restart();
                    hits = 0;
                    using var cmd = c.CreateCommand();
                    cmd.CommandText = "SELECT c.id, c.content FROM clips_fts f JOIN clips c ON c.id = f.rowid WHERE clips_fts MATCH $q ORDER BY c.id DESC LIMIT 50;";
                    var q = cmd.Parameters.Add("$q", SqliteType.Text);
                    for (int i = 0; i < searches; i++)
                    {
                        q.Value = "\"" + terms[i % terms.Length] + "\"";
                        using var r = cmd.ExecuteReader();
                        while (r.Read()) { hits++; _ = r.GetString(1); }
                    }
                    search.Add(sw.Elapsed.TotalMilliseconds);
                }
                sw.Restart();
                for (int k = 0; k < 5; k++)
                {
                    using var c = s.Open(path, pw);
                    Db.Scalar(c, "SELECT count(*) FROM sqlite_master;");
                }
                reopen.Add(sw.Elapsed.TotalMilliseconds / 5);
                Db.DeleteDb(path);
            }
            static string Med(List<double> l) { var o = l.OrderBy(x => x).ToList(); return o[o.Count / 2].ToString("F0", System.Globalization.CultureInfo.InvariantCulture); }
            var tag = pw.Length > 0 ? "encrypted" : "plaintext";
            result[$"{tag}.open+schema_ms"] = Med(create);
            result[$"{tag}.insert_{n}_rows_batch100_ms"] = Med(insert);
            result[$"{tag}.trigram_searches_{searches}_ms"] = Med(search);
            result[$"{tag}.open+first_query_ms (KDF)"] = Med(reopen);
            result[$"{tag}.search_hits"] = hits.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        result["iterations(median of)"] = iterations.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return result;
    }
}

public static class Runner
{
    /// <summary>harness &lt;fixturesDir&gt; &lt;workDir&gt; &lt;resultsDir&gt; [--no-bench]</summary>
    public static int RunHarness(Stack s, string tag, string[] args)
    {
        var fx = Path.GetFullPath(args[0]);
        var work = Path.GetFullPath(args[1]);
        var results = Path.GetFullPath(args[2]);
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        Directory.CreateDirectory(work);
        Directory.CreateDirectory(results);
        Console.WriteLine($"=== Harness: {s.Name} ===");
        var h = new Harness(s, fx, work);
        h.Run();
        if (!args.Contains("--no-bench"))
        {
            Console.WriteLine("--- bench (indicative only) ---");
            var bench = Harness.Bench(s, work);
            foreach (var kv in bench) Console.WriteLine($"[BENCH] {kv.Key} = {kv.Value}");
            h.Checks.Add(new CheckResult { Id = "5.bench", Row = 5, Status = "INFO", Detail = string.Join(" | ", bench.Select(kv => $"{kv.Key}={kv.Value}")) });
        }
        File.WriteAllText(Path.Combine(results, $"{tag}.checks.json"), JsonSerializer.Serialize(h.Checks, Db.Json));
        File.WriteAllText(Path.Combine(results, $"{tag}.roundtrip.json"), JsonSerializer.Serialize(h.RoundTrips, Db.Json));
        var fails = h.Checks.Count(c => c.Status == "FAIL");
        Console.WriteLine($"=== {tag}: {h.Checks.Count} checks, {fails} FAIL, {h.RoundTrips.Count} round-trip files ===");
        return 0;
    }
}
