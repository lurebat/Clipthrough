using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace Clipthrough.Tests.Integration;

/// <summary>
/// Rubber-duck F-08-1. <c>.github/scripts/Invoke-HeadlessTests.ps1</c> is the CI
/// gate for the headless tests, and it judged a run only by the failed results
/// in its TRX. A crashed test host leaves a TRX with no failed result and exits
/// 1; a filter that matches nothing leaves one with nothing in it and exits 0.
/// Both were reported "all passed", so CI went green without running the suite.
///
/// These run the real script under pwsh with <c>dotnet</c> replaced by a
/// function that drops a hand-written TRX and sets an exit code, one per call.
/// The crash fixture has the shape a real crash produced (xUnit v3, VSTest TRX
/// logger, <c>Environment.FailFast</c> in a test): summary outcome "Failed",
/// counters all passed, a run-level Error RunInfo, and exit code 1.
/// </summary>
public sealed class CiHeadlessGateScriptTests
{
    private const string Class = "Clipthrough.Tests.Headless.SampleHeadlessTests";
    private const string CleanupMessage = "[Test Case Cleanup Failure (Clipthrough.Tests.Headless.OtherHeadlessTests.Innocent)]: System.InvalidOperationException : Dispatcher job outlived its test";

    private static readonly XNamespace T = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    private const string FakeDotnetDriver = """
        param([string]$Gate, [string]$Plan, [string]$Results, [string]$ClassifyOnly)
        $ErrorActionPreference = 'Stop'
        $global:GatePlan = $Plan
        $global:GateCall = 0

        # Shadows dotnet.exe for the gate: each call drops runN.trx (if the plan
        # has one) where the gate's --logger and --results-directory point, and
        # exits with runN.exit.
        function dotnet {
            $global:GateCall++
            $n = $global:GateCall
            $a = @($args)
            $dir = $a[[array]::IndexOf($a, '--results-directory') + 1]
            $log = $a[[array]::IndexOf($a, '--logger') + 1] -replace '^trx;LogFileName=', ''
            Add-Content -LiteralPath (Join-Path $global:GatePlan 'calls.txt') -Value $a[[array]::IndexOf($a, '--filter') + 1]
            $fixture = Join-Path $global:GatePlan "run$n.trx"
            if (Test-Path -LiteralPath $fixture) {
                New-Item -ItemType Directory -Force -Path $dir | Out-Null
                Copy-Item -LiteralPath $fixture -Destination (Join-Path $dir $log) -Force
            }
            $global:LASTEXITCODE = [int](Get-Content -LiteralPath (Join-Path $global:GatePlan "run$n.exit"))
        }

        if ($ClassifyOnly) {
            & $Gate -ClassifyOnly $ClassifyOnly
        } else {
            & $Gate -NoBuild -ResultsDirectory $Results
        }
        exit $LASTEXITCODE
        """;

    private sealed record Result(string Method, string Outcome, string? Message = null);

    private sealed record Run(int ExitCode, XDocument? Trx);

    private sealed record GateOutcome(int ExitCode, string Output, IReadOnlyList<string> Filters);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("AGENTS.md was not found above " + AppContext.BaseDirectory);
    }

    private static XDocument Trx(string summaryOutcome, IReadOnlyList<Result> results, params (string Outcome, string Text)[] runInfos)
    {
        var ids = results.Select(_ => Guid.NewGuid().ToString()).ToArray();
        var executed = results.Count(r => r.Outcome != "NotExecuted");

        return new XDocument(new XElement(T + "TestRun",
            new XElement(T + "TestDefinitions", results.Select((r, i) =>
            {
                var dot = r.Method.LastIndexOf('.');
                return new XElement(T + "UnitTest",
                    new XAttribute("name", r.Method),
                    new XAttribute("id", ids[i]),
                    new XElement(T + "TestMethod",
                        new XAttribute("className", r.Method[..dot]),
                        new XAttribute("name", r.Method[(dot + 1)..])));
            })),
            new XElement(T + "Results", results.Select((r, i) =>
                new XElement(T + "UnitTestResult",
                    new XAttribute("testId", ids[i]),
                    new XAttribute("testName", r.Method),
                    new XAttribute("outcome", r.Outcome),
                    r.Message is null ? null : new XElement(T + "Output",
                        new XElement(T + "ErrorInfo", new XElement(T + "Message", r.Message)))))),
            new XElement(T + "ResultSummary",
                new XAttribute("outcome", summaryOutcome),
                new XElement(T + "Counters",
                    new XAttribute("total", results.Count),
                    new XAttribute("executed", executed),
                    new XAttribute("passed", results.Count(r => r.Outcome == "Passed")),
                    new XAttribute("failed", results.Count(r => r.Outcome == "Failed"))),
                runInfos.Length == 0 ? null : new XElement(T + "RunInfos", runInfos.Select(info =>
                    new XElement(T + "RunInfo",
                        new XAttribute("outcome", info.Outcome),
                        new XElement(T + "Text", info.Text)))))));
    }

    private static XDocument Passing(params string[] methods)
        => Trx("Completed", methods.Select(m => new Result(m, "Passed")).ToArray());

    private static XDocument Crashed(params string[] passedMethods)
        => Trx("Failed", passedMethods.Select(m => new Result(m, "Passed")).ToArray(),
            ("Error", "[xUnit.net 00:01:04.90]     [FATAL ERROR] Xunit.Sdk.TestPipelineException"),
            ("Error", "[xUnit.net 00:01:04.90] Catastrophic failure: Test process crashed with exit code -2146232797."));

    private static XDocument NothingRan()
        => Trx("Completed", [], ("Warning", "No test matches the given testcase filter `FullyQualifiedName~HeadlessTests` in Clipthrough.Tests.dll"));

    private static GateOutcome RunGate(Run[] runs, Action<string>? prepareResults = null, XDocument? classifyOnly = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ct-headless-gate-" + Guid.NewGuid().ToString("N"));
        var plan = Path.Combine(dir, "plan");
        var results = Path.Combine(dir, "results");
        Directory.CreateDirectory(plan);
        Directory.CreateDirectory(results);
        try
        {
            for (var i = 0; i < runs.Length; i++)
            {
                var n = (i + 1).ToString(CultureInfo.InvariantCulture);
                File.WriteAllText(Path.Combine(plan, "run" + n + ".exit"), runs[i].ExitCode.ToString(CultureInfo.InvariantCulture));
                runs[i].Trx?.Save(Path.Combine(plan, "run" + n + ".trx"));
            }

            prepareResults?.Invoke(results);
            var driver = Path.Combine(dir, "drive.ps1");
            File.WriteAllText(driver, FakeDotnetDriver);

            var start = new ProcessStartInfo("pwsh")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = dir,
            };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-File", driver,
                "-Gate", Path.Combine(RepositoryRoot(), ".github", "scripts", "Invoke-HeadlessTests.ps1"),
                "-Plan", plan, "-Results", results })
            {
                start.ArgumentList.Add(arg);
            }

            if (classifyOnly is not null)
            {
                var trx = Path.Combine(dir, "classify.trx");
                classifyOnly.Save(trx);
                start.ArgumentList.Add("-ClassifyOnly");
                start.ArgumentList.Add(trx);
            }

            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromSeconds(60)))
            {
                process.Kill(entireProcessTree: true);
                Assert.Fail("The gate script did not finish.");
            }

            var calls = Path.Combine(plan, "calls.txt");
            return new GateOutcome(
                process.ExitCode,
                stdout.Result + stderr.Result,
                File.Exists(calls) ? File.ReadAllLines(calls) : []);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* test cleanup */ }
        }
    }

    [Fact]
    public void CleanPass_IsGreen()
    {
        var gate = RunGate([new Run(0, Passing(Class + ".A", Class + ".B"))]);

        Assert.True(gate.ExitCode == 0, gate.Output);
        Assert.Contains("all passed", gate.Output, StringComparison.Ordinal);
        Assert.Single(gate.Filters);
    }

    [Fact]
    public void CrashedTestHost_IsRed()
    {
        var gate = RunGate([new Run(1, Crashed(Class + ".A"))]);

        Assert.True(gate.ExitCode == 1, gate.Output);
        Assert.Contains("Catastrophic failure", gate.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The exit code alone condemns a run that recorded no failed test, so a crash
    /// is caught even when it leaves no run-level error in the TRX.
    /// </summary>
    [Fact]
    public void NonZeroExitWithNoFailedTest_IsRed()
    {
        var gate = RunGate([new Run(1, Trx("Failed", [new Result(Class + ".A", "Passed")]))]);

        Assert.True(gate.ExitCode == 1, gate.Output);
        Assert.Contains("exited with code 1", gate.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void NoTrxAtAll_IsRed()
    {
        var gate = RunGate([new Run(1, null)]);

        Assert.True(gate.ExitCode == 1, gate.Output);
        Assert.Contains("did not complete", gate.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A TRX of the same fixed name left by an earlier invocation must not stand in
    /// for a run that wrote none. The run exits 0 so that only the missing TRX can
    /// condemn it.
    /// </summary>
    [Fact]
    public void StaleTrxFromAnEarlierRun_IsNotRead()
    {
        var gate = RunGate(
            [new Run(0, null)],
            results => Passing(Class + ".A").Save(Path.Combine(results, "headless.trx")));

        Assert.True(gate.ExitCode == 1, gate.Output);
        Assert.Contains("No test results", gate.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void ZeroTestsRan_IsRed()
    {
        var gate = RunGate([new Run(0, NothingRan())]);

        Assert.True(gate.ExitCode == 1, gate.Output);
        Assert.Contains("No tests ran", gate.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void GenuineFailure_IsRedAndNotRetried()
    {
        var gate = RunGate([
            new Run(1, Trx("Failed", [
                new Result(Class + ".A", "Passed"),
                new Result(Class + ".B", "Failed", "Assert.Equal() Failure: Values differ")])),
            new Run(0, Passing(Class + ".B")),
        ]);

        Assert.True(gate.ExitCode == 1, gate.Output);
        Assert.Single(gate.Filters);
    }

    [Fact]
    public void CleanupFlakeThatPassesOnRetry_IsGreen()
    {
        var gate = RunGate([
            new Run(1, Trx("Failed", [
                new Result(Class + ".A", "Passed"),
                new Result(Class + ".B", "Failed", CleanupMessage)])),
            new Run(0, Passing(Class + ".B")),
        ]);

        Assert.True(gate.ExitCode == 0, gate.Output);
        Assert.Equal(new[] { "FullyQualifiedName~HeadlessTests", "FullyQualifiedName=" + Class + ".B" }, gate.Filters);
        Assert.Contains("passed on re-run", gate.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void CleanupFlakeThatFailsAgain_IsRed()
    {
        var gate = RunGate([
            new Run(1, Trx("Failed", [new Result(Class + ".B", "Failed", CleanupMessage)])),
            new Run(1, Trx("Failed", [new Result(Class + ".B", "Failed", CleanupMessage)])),
        ]);

        Assert.True(gate.ExitCode == 1, gate.Output);
        Assert.Equal(2, gate.Filters.Count);
    }

    [Fact]
    public void CleanupFlakeWhoseRetryRunsNothing_IsRed()
    {
        var gate = RunGate([
            new Run(1, Trx("Failed", [new Result(Class + ".B", "Failed", CleanupMessage)])),
            new Run(0, NothingRan()),
        ]);

        Assert.True(gate.ExitCode == 1, gate.Output);
        Assert.Equal(2, gate.Filters.Count);
    }

    [Fact]
    public void CleanupFlakeWhoseRetryCrashes_IsRed()
    {
        var gate = RunGate([
            new Run(1, Trx("Failed", [new Result(Class + ".B", "Failed", CleanupMessage)])),
            new Run(1, Crashed()),
        ]);

        Assert.True(gate.ExitCode == 1, gate.Output);
        Assert.Equal(2, gate.Filters.Count);
    }

    /// <summary>
    /// The retry must re-run every test it asked for: passing a different test
    /// does not clear the flaky one.
    /// </summary>
    [Fact]
    public void CleanupFlakeWhoseRetryOmitsTheFlakyTest_IsRed()
    {
        var gate = RunGate([
            new Run(1, Trx("Failed", [
                new Result(Class + ".A", "Failed", CleanupMessage),
                new Result(Class + ".B", "Failed", CleanupMessage)])),
            new Run(0, Passing(Class + ".A")),
        ]);

        Assert.True(gate.ExitCode == 1, gate.Output);
        Assert.Contains(Class + ".B", gate.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A crash that also recorded a cleanup failure must not be retried: the
    /// retry re-runs only that test, and the tests the crash never reached would
    /// go unrun.
    /// </summary>
    [Fact]
    public void CleanupFailureInACrashedRun_IsRedAndNotRetried()
    {
        var crashed = Crashed(Class + ".A");
        var results = crashed.Descendants(T + "Results").Single();
        var definitions = crashed.Descendants(T + "TestDefinitions").Single();
        var id = Guid.NewGuid().ToString();
        definitions.Add(new XElement(T + "UnitTest", new XAttribute("name", Class + ".B"), new XAttribute("id", id),
            new XElement(T + "TestMethod", new XAttribute("className", Class), new XAttribute("name", "B"))));
        results.Add(new XElement(T + "UnitTestResult", new XAttribute("testId", id), new XAttribute("testName", Class + ".B"),
            new XAttribute("outcome", "Failed"),
            new XElement(T + "Output", new XElement(T + "ErrorInfo", new XElement(T + "Message", CleanupMessage)))));

        var gate = RunGate([new Run(1, crashed), new Run(0, Passing(Class + ".B"))]);

        Assert.True(gate.ExitCode == 1, gate.Output);
        Assert.Single(gate.Filters);
    }

    [Fact]
    public void ClassifyOnly_CondemnsACrashedOrEmptyRun()
    {
        var crashed = RunGate([], classifyOnly: Crashed(Class + ".A"));
        var empty = RunGate([], classifyOnly: NothingRan());
        var clean = RunGate([], classifyOnly: Passing(Class + ".A"));

        Assert.True(crashed.ExitCode == 1, crashed.Output);
        Assert.True(empty.ExitCode == 1, empty.Output);
        Assert.True(clean.ExitCode == 0, clean.Output);
    }
}
