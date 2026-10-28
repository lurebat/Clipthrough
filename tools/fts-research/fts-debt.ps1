param(
    [string]$Bin = (Join-Path $PSScriptRoot '..\..\Clipthrough.Tests\bin\Release\net10.0-windows10.0.19041.0'),
    [string]$Dir = (Join-Path ([IO.Path]::GetTempPath()) 'clipthrough-fts-research'),
    [int]$Docs = 9000,
    [string]$Mode = 'all'
)
# FTS5 post-rebuild merge debt: a small standalone fixture. See docs/plans/2026-10-28-001-fts-rebuild-and-upgrade-start-research.md.
# Build Clipthrough.Tests in Release first; it supplies Microsoft.Data.Sqlite and the native SQLite.
# Mirrors Clipthrough's index shape: an external-content trigram table over a content table,
# plain (no cipher). Each "capture" is one transaction that inserts one row and its FTS row, as
# the clips_ai trigger does.
$ErrorActionPreference = 'Stop'
$env:PATH = "$Bin\runtimes\win-x64\native;$env:PATH"
Add-Type -Path "$Bin\SQLitePCLRaw.core.dll"; Add-Type -Path "$Bin\SQLitePCLRaw.batteries_v2.dll"; Add-Type -Path "$Bin\Microsoft.Data.Sqlite.dll"
[SQLitePCL.Batteries_V2]::Init()
[Diagnostics.Process]::GetCurrentProcess().PriorityClass = 'BelowNormal'
New-Item -ItemType Directory -Force $Dir | Out-Null
$out = Join-Path $Dir 'results.txt'
function Log($m) { $l = "$(Get-Date -Format HH:mm:ss) $m"; Add-Content $out $l; Write-Host $l }

function Open($path) { $c = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$path;Pooling=False"); $c.Open(); $c }
function Exec($c, $sql) { $cmd = $c.CreateCommand(); $cmd.CommandTimeout = 0; $cmd.CommandText = $sql; [void]$cmd.ExecuteNonQuery(); $cmd.Dispose() }
function Scalar($c, $sql) { $cmd = $c.CreateCommand(); $cmd.CommandTimeout = 0; $cmd.CommandText = $sql; $r = $cmd.ExecuteScalar(); $cmd.Dispose(); $r }
function Ms([scriptblock]$b) { $sw = [Diagnostics.Stopwatch]::StartNew(); & $b; [math]::Round($sw.Elapsed.TotalMilliseconds, 1) }

$rng = [Random]::new(7)
$letters = 'abcdefghijklmnopqrstuvwxyz'
$vocab = 1..4000 | ForEach-Object { $n = $rng.Next(3, 11); -join (1..$n | ForEach-Object { $letters[$rng.Next(26)] }) }
function Text([int]$len) {
    $sb = [Text.StringBuilder]::new($len + 16)
    while ($sb.Length -lt $len) { [void]$sb.Append($vocab[$rng.Next($vocab.Count)]).Append(' ') }
    $sb.ToString(0, $len)
}

$schema = @"
PRAGMA journal_mode = WAL;
CREATE TABLE docs (id INTEGER PRIMARY KEY, content TEXT);
CREATE VIRTUAL TABLE fts USING fts5(content, content='docs', content_rowid='id', tokenize='trigram');
"@

function Segments($c) {
    $cmd = $c.CreateCommand(); $cmd.CommandText = 'SELECT block FROM fts_data WHERE id = 10'; $b = [byte[]]$cmd.ExecuteScalar(); $cmd.Dispose()
    $i = 4
    $vi = { param([byte[]]$b) [long]$v = 0; for ($k = 0; $k -lt 9; $k++) { $x = $b[$script:i]; $script:i++; if ($k -eq 8) { $v = ($v -shl 8) -bor $x; break }; $v = ($v -shl 7) -bor ($x -band 0x7f); if (($x -band 0x80) -eq 0) { break } }; $v }
    $script:i = 4
    $nLevel = & $vi $b; $nSeg = & $vi $b; [void](& $vi $b)
    $parts = @()
    for ($l = 0; $l -lt $nLevel; $l++) {
        $merge = & $vi $b; $ns = & $vi $b; $pages = 0
        for ($j = 0; $j -lt $ns; $j++) { [void](& $vi $b); $p1 = & $vi $b; $p2 = & $vi $b; $pages += ($p2 - $p1 + 1) }
        if ($ns -gt 0) { $parts += "L${l}:${ns}seg/${pages}p$(if ($merge) { "(merging $merge)" })" }
    }
    "$nSeg segments: $($parts -join ' ')"
}

$big = Text 1900000
function Capture($c, $label) {
    $t = $big + [Guid]::NewGuid().ToString('N')
    $ms = Ms {
        $tx = $c.BeginTransaction()
        $cmd = $c.CreateCommand(); $cmd.Transaction = $tx
        $cmd.CommandText = 'INSERT INTO docs (content) VALUES ($t); INSERT INTO fts (rowid, content) VALUES (last_insert_rowid(), $t);'
        [void]$cmd.Parameters.AddWithValue('$t', $t); [void]$cmd.ExecuteNonQuery(); $cmd.Dispose()
        $tx.Commit()
    }
    Log ("  {0,-44} {1,8} ms" -f $label, $ms)
    $ms
}

$base = Join-Path $Dir 'base-docs.db'
if (-not (Test-Path $base)) {
    Log "seeding $Docs docs"
    $c = Open $base; Exec $c $schema
    $tx = $c.BeginTransaction(); $cmd = $c.CreateCommand(); $cmd.Transaction = $tx
    $cmd.CommandText = 'INSERT INTO docs (content) VALUES ($t)'; $p = $cmd.Parameters.Add('$t', [Microsoft.Data.Sqlite.SqliteType]::Text)
    $total = 0
    for ($n = 0; $n -lt $Docs; $n++) {
        $len = if ($n % 300 -eq 0) { 100000 } elseif ($n % 10 -eq 0) { 12000 } else { $rng.Next(80, 2400) }
        $p.Value = Text $len; $total += $len; [void]$cmd.ExecuteNonQuery()
    }
    $tx.Commit(); $cmd.Dispose()
    Exec $c 'PRAGMA wal_checkpoint(TRUNCATE);'; $c.Close()
    Log "seeded $([math]::Round($total / 1MB, 1)) MB of text"
}

function Fresh($name) {
    $p = Join-Path $Dir "$name.db"
    Remove-Item "$p*" -ErrorAction SilentlyContinue
    Copy-Item $base $p
    $p
}

# 1. Rebuild, then three captures. Records the debt.
if ($Mode -in 'all', 'rebuild') {
    $p = Fresh 'v-rebuild'; $c = Open $p
    $r = Ms { Exec $c "INSERT INTO fts(fts) VALUES('rebuild');" }
    Log "A. rebuild $r ms; $(Segments $c); index $([math]::Round((Scalar $c 'SELECT SUM(length(block)) FROM fts_data') / 1MB, 1)) MB"
    1..3 | ForEach-Object { [void](Capture $c "A. capture $_ after rebuild") }
    Log "   after: $(Segments $c)"
    $c.Close()
}

# 2. Rebuild + optimize, then three captures.
if ($Mode -in 'all', 'optimize') {
    $p = Fresh 'v-optimize'; $c = Open $p
    Exec $c "INSERT INTO fts(fts) VALUES('rebuild');"
    $o = Ms { Exec $c "INSERT INTO fts(fts) VALUES('optimize');" }
    Log "B. optimize after rebuild $o ms; $(Segments $c)"
    1..3 | ForEach-Object { [void](Capture $c "B. capture $_ after optimize") }
    $c.Close()
}

# 3. Rebuild + incremental ('merge', N) calls until no work is left, as idle-time steps would.
if ($Mode -in 'all', 'merge') {
    foreach ($n in 64, 256) {
        $p = Fresh "v-merge$n"; $c = Open $p
        Exec $c "INSERT INTO fts(fts) VALUES('rebuild');"
        $steps = @(); $guard = 0
        while ($guard++ -lt 5000) {
            $before = [long](Scalar $c 'SELECT total_changes()')
            $steps += Ms { Exec $c "INSERT INTO fts(fts, rank) VALUES('merge', $n);" }
            if ([long](Scalar $c 'SELECT total_changes()') - $before -lt 2) { break }
        }
        $s = $steps | Sort-Object
        Log ("C. merge {0}: {1} calls, total {2} ms, median {3} ms, max {4} ms; {5}" -f $n, $steps.Count, [math]::Round(($steps | Measure-Object -Sum).Sum), $s[[int]($s.Count / 2)], $s[-1], (Segments $c))
        [void](Capture $c "C. capture 1 after merge $n drained")
        $c.Close()
    }
}

# 4. Rebuild with automerge raised or disabled, then captures; and the crisis later.
if ($Mode -in 'all', 'automerge') {
    foreach ($am in 0, 8, 16) {
        $p = Fresh "v-automerge$am"; $c = Open $p
        Exec $c "INSERT INTO fts(fts) VALUES('rebuild');"
        Exec $c "INSERT INTO fts(fts, rank) VALUES('automerge', $am);"
        Log "D. automerge=$am after rebuild: $(Segments $c)"
        1..3 | ForEach-Object { [void](Capture $c "D. automerge=$am capture $_") }
        Log "   after: $(Segments $c)"
        $c.Close()
    }
    foreach ($am in 0, 8) {
        $p = Fresh "v-automerge-pre$am"; $c = Open $p
        Exec $c "INSERT INTO fts(fts, rank) VALUES('automerge', $am);"
        $r = Ms { Exec $c "INSERT INTO fts(fts) VALUES('rebuild');" }
        Log "E. automerge=$am set BEFORE rebuild: rebuild $r ms; $(Segments $c)"
        1..3 | ForEach-Object { [void](Capture $c "E. automerge=$am-before capture $_") }
        $c.Close()
    }
}

# 5. Control: an index built incrementally (as the triggers build it), never rebuilt.
if ($Mode -in 'all', 'control') {
    $p = Fresh 'v-control'; $c = Open $p
    $r = Ms {
        $tx = $c.BeginTransaction(); $cmd = $c.CreateCommand(); $cmd.Transaction = $tx
        $cmd.CommandText = 'INSERT INTO fts (rowid, content) SELECT id, content FROM docs WHERE id = $id'
        $pid_ = $cmd.Parameters.Add('$id', [Microsoft.Data.Sqlite.SqliteType]::Integer)
        $ids = [long](Scalar $c 'SELECT MAX(id) FROM docs')
        for ($k = 1; $k -le $ids; $k++) {
            $pid_.Value = $k; [void]$cmd.ExecuteNonQuery()
            if ($k % 50 -eq 0) { $tx.Commit(); $tx = $c.BeginTransaction(); $cmd.Transaction = $tx }
        }
        $tx.Commit(); $cmd.Dispose()
    }
    Log "F. control: built row by row (50 per transaction) in $r ms; $(Segments $c)"
    1..3 | ForEach-Object { [void](Capture $c "F. control capture $_") }
    $c.Close()
}
Log 'DONE'
