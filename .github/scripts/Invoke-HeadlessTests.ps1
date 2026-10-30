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
        once, and the build fails if any of them fails again or is not run.
    An assertion failure is never retried.

    A run counts only if it demonstrably ran. It fails the build, and is never
    retried, when its TRX is missing, records no executed test, or carries a
    run-level error, or when dotnet test exits non-zero without recording a
    failed test. A crashed test host looks like that last case: xUnit reports
    "Catastrophic failure", every result in the TRX is a pass, and only the
    exit code and the run-level error say the rest never ran.

.PARAMETER ClassifyOnly
    Path to an existing TRX. Classifies its failures and reports what would
    happen, without running anything. Exit 0 = clean, 1 = real failure or a
    run that did not complete, 2 = cleanup failures only (would re-run).
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
# NotExecuted is how the TRX records a skipped test.
$passingOutcomes = @('Passed', 'NotExecuted')

function Read-TestRun([string]$trxPath) {
    if (-not (Test-Path -LiteralPath $trxPath)) {
        return $null
    }

    [xml]$trx = Get-Content -Raw -LiteralPath $trxPath
    $ns = @{ t = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010' }

    $methods = @{}
    foreach ($def in Select-Xml -Xml $trx -XPath '//t:TestDefinitions/t:UnitTest' -Namespace $ns) {
        $method = $def.Node.TestMethod
        $methods[$def.Node.id] = "$($method.className).$($method.name)"
    }

    $results = @(foreach ($hit in Select-Xml -Xml $trx -XPath '//t:UnitTestResult' -Namespace $ns) {
        $result = $hit.Node
        $message = [string]$result.Output.ErrorInfo.Message
        [pscustomobject]@{
            Name     = $result.testName
            Method   = $methods[$result.testId]
            Outcome  = $result.outcome
            Cleanup  = $message.Contains($cleanupMarker, [StringComparison]::Ordinal)
            Message  = ($message -split "`n")[0]
        }
    })

    $counters = Select-Xml -Xml $trx -XPath '//t:ResultSummary/t:Counters' -Namespace $ns | Select-Object -First 1
    $runErrors = @(Select-Xml -Xml $trx -XPath '//t:ResultSummary/t:RunInfos/t:RunInfo[@outcome="Error"]' -Namespace $ns |
        ForEach-Object { ([string]$_.Node.Text).Trim() })

    [pscustomobject]@{
        Executed  = if ($counters) { [int]$counters.Node.executed } else { 0 }
        RunErrors = $runErrors
        Results   = $results
        Failures  = @($results | Where-Object { $_.Outcome -notin $passingOutcomes })
    }
}

# Fails the build unless the run demonstrably completed. $exitCode is $null
# when classifying an existing TRX, whose exit code is unknown.
function Assert-RunCompleted($run, $exitCode, [string]$trxPath, [string]$what) {
    $problem = if ($null -eq $run) {
        "No test results at '$trxPath' - the test run did not complete." + $(if ($null -ne $exitCode) { " dotnet test exited with code $exitCode." })
    } elseif ($run.RunErrors.Count -gt 0) {
        "The test run reported an error, so it did not complete: $($run.RunErrors -join ' | ')"
    } elseif ($run.Executed -eq 0) {
        "No tests ran."
    } elseif ($null -ne $exitCode -and $exitCode -ne 0 -and $run.Failures.Count -eq 0) {
        "dotnet test exited with code $exitCode but recorded no failed test - the run did not complete."
    }

    if ($problem) {
        Write-Host "::error title=Headless $what did not complete::$problem"
        exit 1
    }
}

function Invoke-HeadlessRun([string]$filter, [string]$logName, [string]$what) {
    $trxPath = Join-Path $ResultsDirectory "$logName.trx"
    # The name is fixed, so a TRX left by an earlier invocation would otherwise
    # stand in for a run that wrote none.
    if (Test-Path -LiteralPath $trxPath) { Remove-Item -LiteralPath $trxPath -Force }

    $arguments = @('test', $Project, '--configuration', $Configuration, '--filter', $filter,
        '--logger', "trx;LogFileName=$logName.trx", '--results-directory', $ResultsDirectory)
    if ($NoBuild) { $arguments += '--no-build' }
    & dotnet @arguments | Out-Host
    $exitCode = $LASTEXITCODE

    $run = Read-TestRun $trxPath
    Assert-RunCompleted $run $exitCode $trxPath $what
    return $run
}

if ($ClassifyOnly) {
    $run = Read-TestRun $ClassifyOnly
    Assert-RunCompleted $run $null $ClassifyOnly 'test run'
} else {
    $run = Invoke-HeadlessRun 'FullyQualifiedName~HeadlessTests' 'headless' 'test run'
}
$failures = $run.Failures

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

if ($methods.Count -eq 0) {
    Write-Host '::error title=Headless re-run impossible::The cleanup failures name no test method to re-run.'
    exit 1
}

if ($ClassifyOnly) {
    Write-Host "Cleanup failures only; would re-run once: $($methods -join ', ')"
    exit 2
}

Write-Host "Cleanup failures only; re-running $($methods.Count) test(s) once."
$rerunFilter = ($methods | ForEach-Object { "FullyQualifiedName=$_" }) -join '|'
$again = Invoke-HeadlessRun $rerunFilter 'headless-rerun' 're-run'
if ($again.Failures.Count -gt 0) {
    foreach ($f in $again.Failures) {
        Write-Host "::error title=Headless test failed on re-run::$($f.Name): $($f.Message)"
    }
    exit 1
}

$rerunPassed = @($again.Results | Where-Object { $_.Outcome -eq 'Passed' } | ForEach-Object { $_.Method })
$missing = @($methods | Where-Object { $_ -notin $rerunPassed })
if ($missing.Count -gt 0) {
    Write-Host "::error title=Headless re-run incomplete::Not re-run: $($missing -join ', ')"
    exit 1
}

Write-Host 'Headless tests: passed on re-run.'
exit 0