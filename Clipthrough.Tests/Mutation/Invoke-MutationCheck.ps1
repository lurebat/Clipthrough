#Requires -Version 7
<#
.SYNOPSIS
    Proves that regression tests actually fail when the code they guard is broken.

.DESCRIPTION
    A test that passes against the bug it was written to catch is worse than no
    test: it reports safety that does not exist. This script checks the tests
    rather than the code. For each mutant in the manifest it breaks the guarded
    code on purpose, rebuilds, runs only the covering test, and requires that
    test to FAIL. A mutant that survives means the test is vacuous.

    Outcomes per mutant:
      KILLED       the test failed as it should - the guard is real
      SURVIVED     the test passed against broken code - the guard is fake
      INCONCLUSIVE the build broke or the filter matched nothing - proves nothing
      STALE        the anchor text no longer exists - the mutant needs updating

    INCONCLUSIVE is reported separately and on purpose. A compile error also
    makes "dotnet test" exit non-zero, and counting that as a kill is how you
    end up trusting a test that was never actually run.

.EXAMPLE
    pwsh Clipthrough.Tests\Mutation\Invoke-MutationCheck.ps1
    pwsh Clipthrough.Tests\Mutation\Invoke-MutationCheck.ps1 -Id deferred-refresh-rethrows
    pwsh Clipthrough.Tests\Mutation\Invoke-MutationCheck.ps1 -ValidateOnly
#>
[CmdletBinding()]
param(
    # Manifest of mutants to apply.
    [string]$Manifest = "$PSScriptRoot/mutants.json",

    # Run only these mutant ids. Default: all of them.
    [string[]]$Id,

    # Skip the unmutated baseline run. Faster, but a test that is already
    # failing will then look like a kill.
    [switch]$SkipBaseline,

    # Check every anchor and stop, without building or running anything.
    # Seconds rather than an hour, so manifest rot is worth checking often.
    [switch]$ValidateOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = (Resolve-Path "$PSScriptRoot/../..").Path
$TestProject = Join-Path $RepoRoot 'Clipthrough.Tests/Clipthrough.Tests.csproj'
# Absolute, because a relative BaseOutputPath is resolved per project and
# scatters an output tree under every project in the build.
$OutputPath = Join-Path $RepoRoot 'Clipthrough.Tests/artifacts/bin/'
$PendingFile = Join-Path $RepoRoot 'Clipthrough.Tests/artifacts/mutation-pending.json'

New-Item -ItemType Directory -Force -Path (Split-Path $PendingFile) | Out-Null

function Write-Utf8NoBom([string]$Path, [string]$Text) {
    [System.IO.File]::WriteAllText($Path, $Text, [System.Text.UTF8Encoding]::new($false))
}

# Restore byte-for-byte, so a file with a BOM or unusual line endings comes back
# exactly as it was rather than as whatever the text round-trip produced.
function Restore-Files([object[]]$Saved) {
    foreach ($s in $Saved) {
        [System.IO.File]::WriteAllBytes($s.path, [Convert]::FromBase64String($s.bytes))
        # MSBuild decides staleness by timestamp, and a restore can hand back an
        # older one than the mutant it replaces - which silently leaves the
        # mutant assembly in place for the next run.
        (Get-Item $s.path).LastWriteTime = Get-Date
    }
}

# A previous run that was killed mid-mutation leaves production code broken on
# disk. Recover before doing anything else, so it can never be committed.
if (Test-Path $PendingFile) {
    Write-Warning "Found interrupted mutation run - restoring original sources."
    Restore-Files ([object[]](Get-Content $PendingFile -Raw | ConvertFrom-Json))
    Remove-Item $PendingFile -Force
}

function Invoke-TestFilter([string]$Filter) {
    $raw = & dotnet test $TestProject --filter $Filter -p:BaseOutputPath=$OutputPath --nologo 2>&1 | Out-String

    if ($raw -match 'error [A-Z]{2}\d+' -or $raw -match 'Build FAILED') {
        return [pscustomobject]@{ Ran = $false; Failed = 0; Passed = 0; Reason = 'build failed'; Raw = $raw }
    }
    # CS0436 means a type in the test project's own sources is shadowing the
    # same type from the referenced assembly, so the tests bind to the copy and
    # mutating the real file changes nothing. It is only a warning, and it made
    # four mutants look survivable when the guards were fine - the tests simply
    # were not running against the code under test.
    if ($raw -match 'warning CS0436') {
        $shadowed = [regex]::Match($raw, "The type '([^']+)'").Groups[1].Value
        return [pscustomobject]@{ Ran = $false; Failed = 0; Passed = 0; Reason = "type '$shadowed' is shadowed by a copy in the test project (CS0436)"; Raw = $raw }
    }
    if ($raw -match 'No test matches the given testcase filter') {
        return [pscustomobject]@{ Ran = $false; Failed = 0; Passed = 0; Reason = 'filter matched no tests'; Raw = $raw }
    }
    $m = [regex]::Match($raw, 'Failed:\s+(\d+),\s+Passed:\s+(\d+)')
    if (-not $m.Success) {
        return [pscustomobject]@{ Ran = $false; Failed = 0; Passed = 0; Reason = 'no test summary in output'; Raw = $raw }
    }
    $failed = [int]$m.Groups[1].Value
    $passed = [int]$m.Groups[2].Value
    if (($failed + $passed) -eq 0) {
        return [pscustomobject]@{ Ran = $false; Failed = 0; Passed = 0; Reason = 'zero tests executed'; Raw = $raw }
    }
    return [pscustomobject]@{ Ran = $true; Failed = $failed; Passed = $passed; Reason = ''; Raw = $raw }
}

$mutants = [object[]](Get-Content $Manifest -Raw | ConvertFrom-Json)
if ($Id) {
    # "pwsh -File script.ps1 -Id a,b" hands over the single string "a,b", so
    # split defensively rather than silently selecting nothing.
    $wanted = @($Id | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    $mutants = [object[]]($mutants | Where-Object { $wanted -contains $_.id })
    $missing = @($wanted | Where-Object { $mutants.id -notcontains $_ })
    if ($missing) { throw "No such mutant id: $($missing -join ', ')" }
}
if (-not $mutants) { throw "No mutants selected." }

# Pre-flight. The per-mutant check below already refuses a stale anchor, but it
# only gets there after every earlier mutant has built and run - so a manifest
# that rotted months ago is discovered an hour into the sweep, if at all. Every
# anchor is cheap to check, so check them all first and report them together.
#
# Anchors rot silently: they name code that a later change renamed, reworded or
# renumbered. The mutant then tests nothing while still reading as coverage.
$preflight = foreach ($mutant in $mutants) {
    $target = Join-Path $RepoRoot $mutant.file
    $detail =
        if (-not (Test-Path $target)) { 'file is missing' }
        else {
            $hits = [regex]::Matches([System.IO.File]::ReadAllText($target), [regex]::Escape($mutant.find)).Count
            if ($hits -ne 1) { "anchor matched $hits times, expected exactly 1" }
        }
    if ($detail) { [pscustomobject]@{ Id = $mutant.id; File = $mutant.file; Detail = $detail } }
}

# Two mutants sharing an id makes -Id ambiguous and the summary misleading.
$duplicateIds = @($mutants | Group-Object id | Where-Object Count -gt 1 | ForEach-Object Name)
if ($duplicateIds) {
    Write-Host "Duplicate mutant id(s): $($duplicateIds -join ', ')" -ForegroundColor Red
}

if ($preflight) {
    Write-Host ""
    Write-Host "$(@($preflight).Count) of $($mutants.Count) anchor(s) stale:" -ForegroundColor Red
    $preflight | Format-Table -AutoSize | Out-String | Write-Host
}

if ($ValidateOnly) {
    if (-not $preflight -and -not $duplicateIds) {
        Write-Host "All $($mutants.Count) anchor(s) match exactly once." -ForegroundColor Green
        exit 0
    }
    exit 1
}

$baselineCache = @{}
$results = [System.Collections.Generic.List[object]]::new()

foreach ($mutant in $mutants) {
    Write-Host ""
    Write-Host "=== $($mutant.id) ===" -ForegroundColor Cyan
    Write-Host "    guards: $($mutant.filter)"

    $target = Join-Path $RepoRoot $mutant.file
    if (-not (Test-Path $target)) { throw "Mutant '$($mutant.id)' targets a missing file: $target" }

    $text = [System.IO.File]::ReadAllText($target)
    $hits = [regex]::Matches($text, [regex]::Escape($mutant.find)).Count

    # A mutant whose anchor no longer matches is a mutant that silently tests
    # nothing. Refuse rather than skip - a stale anchor recreates the exact
    # false-confidence problem this script exists to prevent, one level up.
    if ($hits -ne 1) {
        Write-Host "    STALE: anchor matched $hits times, expected exactly 1" -ForegroundColor Red
        $results.Add([pscustomobject]@{ Id = $mutant.id; Outcome = 'STALE'; Detail = "anchor matched $hits times" })
        continue
    }

    if (-not $SkipBaseline) {
        if (-not $baselineCache.ContainsKey($mutant.filter)) {
            Write-Host "    baseline..." -NoNewline
            $b = Invoke-TestFilter $mutant.filter
            $baselineCache[$mutant.filter] = $b
            Write-Host " $(if ($b.Ran) { "$($b.Passed) passed, $($b.Failed) failed" } else { $b.Reason })"
        }
        $baseline = $baselineCache[$mutant.filter]
        if (-not $baseline.Ran -or $baseline.Failed -gt 0) {
            $why = if ($baseline.Ran) { "$($baseline.Failed) test(s) already failing" } else { $baseline.Reason }
            Write-Host "    INCONCLUSIVE: $why before mutating" -ForegroundColor Yellow
            $results.Add([pscustomobject]@{ Id = $mutant.id; Outcome = 'INCONCLUSIVE'; Detail = "baseline: $why" })
            continue
        }
    }

    $saved = @([pscustomobject]@{
        path  = $target
        bytes = [Convert]::ToBase64String([System.IO.File]::ReadAllBytes($target))
    })
    Write-Utf8NoBom $PendingFile ($saved | ConvertTo-Json -Depth 4 -AsArray)

    try {
        Write-Utf8NoBom $target ($text.Replace($mutant.find, $mutant.replace))
        (Get-Item $target).LastWriteTime = Get-Date

        Write-Host "    mutated, running..." -NoNewline
        $r = Invoke-TestFilter $mutant.filter

        if (-not $r.Ran) {
            Write-Host " INCONCLUSIVE ($($r.Reason))" -ForegroundColor Yellow
            $results.Add([pscustomobject]@{ Id = $mutant.id; Outcome = 'INCONCLUSIVE'; Detail = $r.Reason })
        }
        elseif ($r.Failed -gt 0) {
            Write-Host " KILLED ($($r.Failed) failed)" -ForegroundColor Green
            $results.Add([pscustomobject]@{ Id = $mutant.id; Outcome = 'KILLED'; Detail = "$($r.Failed) test(s) failed" })
        }
        else {
            Write-Host " SURVIVED ($($r.Passed) passed)" -ForegroundColor Red
            $results.Add([pscustomobject]@{ Id = $mutant.id; Outcome = 'SURVIVED'; Detail = "$($r.Passed) test(s) passed against broken code" })
        }
    }
    finally {
        Restore-Files $saved
        Remove-Item $PendingFile -Force -ErrorAction SilentlyContinue
    }
}

Write-Host ""
Write-Host "==================== summary ====================" -ForegroundColor Cyan
$results | Format-Table -AutoSize | Out-String | Write-Host

$bad = @($results | Where-Object { $_.Outcome -ne 'KILLED' })
if ($bad.Count -gt 0) {
    Write-Host "$($bad.Count) mutant(s) not killed." -ForegroundColor Red
    exit 1
}
Write-Host "All $($results.Count) mutant(s) killed." -ForegroundColor Green
exit 0
