using System.Text.Json;
using Microsoft.Data.Sqlite;
using Uc8;

SQLitePCL.Batteries_V2.Init();
var stack = new CurrentStack();

switch (args.FirstOrDefault())
{
    case "gen":
        return Gen(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
    case "verify":
        return Verify(args[1], Path.GetFullPath(args[2]), args[3]);
    case "harness":
        return Runner.RunHarness(stack, "current-e_sqlcipher-2.1.11", args[1..]);
    default:
        Console.Error.WriteLine("usage: gen <fixturesDir> <workDir> | verify <roundtrip.json> <workDir> <outJson> | harness <fixturesDir> <workDir> <resultsDir> [--no-bench]");
        return 2;
}

int Gen(string fx, string work)
{
    if (Directory.Exists(fx)) Directory.Delete(fx, true);
    Directory.CreateDirectory(fx);
    Directory.CreateDirectory(work);
    var manifest = new Manifest { GeneratedBy = "FixtureGen: Microsoft.Data.Sqlite.Core 10.0.11 + SQLitePCLRaw.bundle_e_sqlcipher 2.1.11 (resolves SQLitePCLRaw.core 2.1.12)" };
    using (var probe = new SqliteConnection("Data Source=:memory:"))
    {
        probe.Open();
        manifest.Versions = stack.Versions(probe);
    }

    FixtureInfo Make(string name, string file, string pw, string desc)
    {
        var path = Path.Combine(fx, file);
        Db.DeleteDb(path);
        using (var c = stack.Open(path, pw))
        {
            Db.CreateSchema(c);
            Db.Populate(c, 1, 200);
            Db.Mutate(c, 5, 10);
        }
        return Describe(name, file, path, pw, desc);
    }

    FixtureInfo Describe(string name, string file, string path, string pw, string desc)
    {
        using var c = stack.Open(path, pw);
        var s = Db.Snap(c);
        Console.WriteLine($"[GEN] {name}: header={Db.Header(path)} {s.Describe()}");
        return new FixtureInfo { Name = name, File = file, Password = pw, Description = desc, Rows = s.Rows, Tags = s.Tags, Checksum = s.Checksum, Fts = s.Fts };
    }

    // (a) plaintext WAL
    manifest.Fixtures.Add(Make("a-plain-wal", "a-plain-wal.db", "", "plaintext, journal_mode=WAL, checkpointed on close"));

    // (b) SQLCipher 4.5.2 defaults
    var b = Make("b-enc-sqlcipher452", "b-enc-sqlcipher452.db", "fixture-pass", "SQLCipher 4.5.2 defaults, Password=fixture-pass, WAL, checkpointed on close");
    manifest.Fixtures.Add(b);
    using (var c = stack.Open(Path.Combine(fx, b.File), b.Password))
    {
        manifest.CipherSettings = stack.CipherParams(c);
    }

    // (c) encrypted then rekeyed (PRAGMA rekey in WAL mode, as the app's RekeyAsync does on its copy)
    {
        var path = Path.Combine(fx, "c-enc-rekeyed.db");
        Db.DeleteDb(path);
        using (var c = stack.Open(path, "fixture-pass"))
        {
            Db.CreateSchema(c);
            Db.Populate(c, 1, 200);
            Db.Mutate(c, 5, 10);
        }
        string jm;
        using (var c = stack.Open(path, "fixture-pass"))
        {
            jm = Db.ScalarString(c, "PRAGMA journal_mode;");
            Db.Exec(c, "PRAGMA rekey = 'fixture-pass-2';");
        }
        int oldCode = 0;
        try { using var c = stack.Open(path, "fixture-pass"); Db.Scalar(c, "SELECT count(*) FROM sqlite_master;"); }
        catch (SqliteException ex) { oldCode = ex.SqliteErrorCode; }
        Console.WriteLine($"[GEN] c: rekeyed in journal_mode={jm}; old password now -> error {oldCode}");
        if (oldCode != 26) throw new InvalidOperationException("rekey did not take effect");
        manifest.Fixtures.Add(Describe("c-enc-rekeyed", "c-enc-rekeyed.db", path, "fixture-pass-2", $"created with fixture-pass, PRAGMA rekey='fixture-pass-2' in journal_mode={jm}; old password -> SQLite error {oldCode}"));
    }

    // (d) encrypted with a non-empty -wal left behind
    {
        var live = Path.Combine(work, "gen-d-live.db");
        Db.DeleteDb(live);
        var dst = Path.Combine(fx, "d-enc-wal.db");
        using var c = stack.Open(live, "fixture-pass");
        Db.CreateSchema(c);
        Db.Populate(c, 1, 100);
        var cp1 = string.Join(";", Db.Rows(c, "PRAGMA wal_checkpoint(TRUNCATE);"));
        Db.Exec(c, "PRAGMA wal_autocheckpoint = 0;");
        Db.Populate(c, 101, 100);
        Db.Mutate(c, 5, 10);
        var s = Db.Snap(c, checkFtsIntegrity: false);
        Db.CopyShared(live, dst);
        Db.CopyShared(live + "-wal", dst + "-wal");
        var walLen = new FileInfo(dst + "-wal").Length;
        Console.WriteLine($"[GEN] d: first checkpoint ({cp1}); copied .db ({new FileInfo(dst).Length} B) + -wal ({walLen} B) while the writer connection was still open; {s.Describe()}");
        manifest.Fixtures.Add(new FixtureInfo
        {
            Name = "d-enc-wal", File = "d-enc-wal.db", WalFile = "d-enc-wal.db-wal", Password = "fixture-pass",
            Description = $"SQLCipher 4.5.2, Password=fixture-pass; rows 1-100 checkpointed into the .db, then wal_autocheckpoint=0 and rows 101-200 + an update + a cascading delete left only in the {walLen}-byte -wal (copied while the connection was open; no -shm)",
            Rows = s.Rows, Tags = s.Tags, Checksum = s.Checksum, Fts = s.Fts,
        });
    }

    // Self-check: the -wal fixture is readable by the stack that made it.
    {
        var d = manifest.Fixtures.Single(f => f.Name == "d-enc-wal");
        var chk = Path.Combine(work, "gen-d-check.db");
        Db.DeleteDb(chk);
        File.Copy(Path.Combine(fx, d.File), chk);
        File.Copy(Path.Combine(fx, d.WalFile!), chk + "-wal");
        using var c = stack.Open(chk, d.Password);
        var s = Db.Snap(c);
        var ok = s.Checksum == d.Checksum && s.Rows == d.Rows && Db.Healthy(s);
        Console.WriteLine($"[GEN] d self-check with current stack: {(ok ? "PASS" : "FAIL")} {s.Describe()}");
        if (!ok) return 1;
    }

    File.WriteAllText(Path.Combine(fx, "manifest.json"), JsonSerializer.Serialize(manifest, Db.Json));
    Console.WriteLine($"[GEN] manifest written: {manifest.Fixtures.Count} fixtures");
    return 0;
}

int Verify(string roundTripJson, string work, string outJson)
{
    var items = JsonSerializer.Deserialize<List<RoundTripItem>>(File.ReadAllText(roundTripJson))!;
    Directory.CreateDirectory(work);
    var results = new List<CheckResult>();
    int i = 0;
    foreach (var it in items)
    {
        i++;
        string status, detail;
        try
        {
            var copy = Path.Combine(work, $"verify-{i:D2}.db");
            Db.DeleteDb(copy);
            File.Copy(it.File, copy);
            if (File.Exists(it.File + "-wal")) File.Copy(it.File + "-wal", copy + "-wal");
            Snapshot s;
            using (var c = stack.Open(copy, it.Password))
            {
                s = Db.Snap(c);
                Db.Exec(c, "INSERT INTO clips(content) VALUES('written back by the current stack');");
            }
            Snapshot after;
            using (var c = stack.Open(copy, it.Password)) after = Db.Snap(c);
            var same = s.Rows == it.Rows && s.Tags == it.Tags && s.Checksum == it.Checksum && s.Fts.OrderBy(k => k.Key).SequenceEqual(it.Fts.OrderBy(k => k.Key));
            var ok = same && Db.Healthy(s) && Db.Healthy(after) && after.Rows == s.Rows + 1;
            status = ok ? "PASS" : "FAIL";
            detail = $"{Path.GetFileName(it.File)} [{it.Description}] -> current stack read {(same ? "identical data" : "DIFFERENT data")} ({s.Describe()}); current-stack write+reopen integrity={after.Integrity}";
        }
        catch (Exception ex)
        {
            status = it.ExpectOpenFailure && ex is SqliteException { SqliteErrorCode: 26 } ? "EXPECTED-FAIL" : "FAIL";
            detail = $"{Path.GetFileName(it.File)} [{it.Description}] -> {Harness.Err(ex)}";
        }
        if (it.ExpectOpenFailure && status == "PASS")
        {
            status = "UNEXPECTED-PASS";
        }
        Console.WriteLine($"[{status}] 2.roundtrip.{i:D2}: {detail}");
        results.Add(new CheckResult { Id = $"2.roundtrip.{i:D2}", Row = 2, Status = status, Detail = detail });
    }
    File.WriteAllText(outJson, JsonSerializer.Serialize(results, Db.Json));
    var fails = results.Count(r => r.Status == "FAIL");
    var unexpected = results.Count(r => r.Status == "UNEXPECTED-PASS");
    Console.WriteLine($"=== verify: {results.Count} files, {fails} FAIL, {unexpected} UNEXPECTED-PASS ===");
    // This step is the rollback-compatibility gate, so a regression must fail the run, not just print.
    return fails + unexpected == 0 ? 0 : 1;
}
