<#
.SYNOPSIS
    Runs the Avalonia headless tests in CI (F-29, OD-6a).

.DESCRIPTION
    The headless tests hold about a quarter of the mutation guards, including
    "delete with nothing checked deletes every clip", and CI used to exclude
    them because a couple of percent of runs fail with a
    "[Test Case Cleanup Failure (...)]" naming an innocent test - work that
    outlived a previous test (docs/solutions/headless-teardown-leaks-dispatcher-jobs.md).

    So this runs them all and then:
      - any failure that is NOT a cleanup failure fails the build at once;
      - if every failure is a cleanup failure, those tests alone are re-run
        once, and the build fails if any of them fails again.
    An assertion failure is never retried.

.PARAMETER ClassifyOnly
    Path to an existing TRX. Classifies its failures and reports what would
    happen, without running anything. Exit 0 = clean, 1 = real failure,
    2 = cleanup failures only (would re-run).
#>
param(
    [string]$Configuration = 'Release',
    [string]$ResultsDirectory = 'TestResults',
    [string]$Project = '.\Clipthrough.Tests\Clipthrough.Tests.csproj',
    [switch]$NoBuild,
    [string]$ClassifyOnly
)

$ErrorActionPreference = 'Stop'
$cleanupMarker = '[Test Case Cleanup Failure'

function Get-FailedTests([string]$trxPath) {
    if (-not (Test-Path $trxPath)) {
        throw "No test results at '$trxPath' - the test run did not complete."
    }

    [xml]$trx = Get-Content -Raw -LiteralPath $trxPath
    $ns = @{ t = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010' }

    $methods = @{}
    foreach ($def in Select-Xml -Xml $trx -XPath '//t:TestDefinitions/t:UnitTest' -Namespace $ns) {
        $method = $def.Node.TestMethod
        $methods[$def.Node.id] = "$($method.className).$($method.name)"
    }

    foreach ($hit in Select-Xml -Xml $trx -XPath '//t:UnitTestResult[@outcome="Failed"]' -Namespace $ns) {
        $result = $hit.Node
        $message = [string]$result.Output.ErrorInfo.Message
        [pscustomobject]@{
            Name     = $result.testName
            Method   = $methods[$result.testId]
            Cleanup  = $message.Contains($cleanupMarker, [StringComparison]::Ordinal)
            Message  = ($message -split "`n")[0]
        }
    }
}

function Invoke-HeadlessRun([string]$filter, [string]$logName) {
    $arguments = @('test', $Project, '--configuration', $Configuration, '--filter', $filter,
        '--logger', "trx;LogFileName=$logName.trx", '--results-directory', $ResultsDirectory)
    if ($NoBuild) { $arguments += '--no-build' }
    & dotnet @arguments | Out-Host
    return Join-Path $ResultsDirectory "$logName.trx"
}

$failures = @(if ($ClassifyOnly) {
    Get-FailedTests $ClassifyOnly
} else {
    Get-FailedTests (Invoke-HeadlessRun 'FullyQualifiedName~HeadlessTests' 'headless')
})

if ($failures.Count -eq 0) {
    Write-Host 'Headless tests: all passed.'
    exit 0
}

$real = @($failures | Where-Object { -not $_.Cleanup })
if ($real.Count -gt 0) {
    foreach ($f in $real) {
        Write-Host "::error title=Headless test failed::$($f.Name): $($f.Message)"
    }
    exit 1
}

$methods = @($failures.Method | Where-Object { $_ } | Sort-Object -Unique)
foreach ($f in $failures) {
    Write-Host "::warning title=Headless cleanup failure::$($f.Name): $($f.Message)"
}

if ($ClassifyOnly) {
    Write-Host "Cleanup failures only; would re-run once: $($methods -join ', ')"
    exit 2
}

Write-Host "Cleanup failures only; re-running $($methods.Count) test(s) once."
$rerunFilter = ($methods | ForEach-Object { "FullyQualifiedName=$_" }) -join '|'
$again = @(Get-FailedTests (Invoke-HeadlessRun $rerunFilter 'headless-rerun'))
if ($again.Count -gt 0) {
    foreach ($f in $again) {
        Write-Host "::error title=Headless test failed on re-run::$($f.Name): $($f.Message)"
    }
    exit 1
}

Write-Host 'Headless tests: passed on re-run.'
exit 0