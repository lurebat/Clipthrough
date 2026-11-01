using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Clipthrough.Tests.Unit;

/// <summary>
/// The release workflow tells Velopack which .NET runtime the installer must
/// find, or install, before the app is installed or updated.
/// </summary>
/// <remarks>
/// <para>
/// The app is published framework-dependent, so the runtime is whatever the
/// user's machine has, and runtime security fixes reach users only through
/// it. The patch floor is what makes the installer and every update insist on
/// a serviced runtime instead of accepting any 10.0.x.
/// </para>
/// <para>
/// This needs a test because a wrong string fails silently. Velopack's
/// Setup and Update parse the dependency list with a regex and skip any entry
/// that does not match (<c>parse_dependency_list</c> in
/// <c>src/bins/src/windows/runtimes.rs</c>): a typo produces a package that
/// requires nothing, packs without a warning, and installs everywhere. The
/// pattern below is Velopack 1.2.0's own, so the string is checked the way
/// the client on users' machines will read it.
/// </para>
/// </remarks>
public sealed class ReleasePackagingTests
{
    // Velopack 1.2.0, src/bins/src/windows/runtimes.rs, REGEX_DOTNET.
    private static readonly Regex VelopackDotnetDependency = new(
        @"^net(?:coreapp)?(?<version>(?<major>\d+)(\.(?<minor>\d+))?(\.(?<build>\d+))?)(?:-(?<arch>[a-zA-Z]+\d\d))?(?:-(?<type>[a-zA-Z]+))?$",
        RegexOptions.CultureInvariant);

    // The oldest 10.0 runtime this app is released for: 10.0.14 is the
    // out-of-band Brotli fix that followed 10.0.13's ~44 CVEs.
    private const int MinimumRuntimePatch = 14;

    private static string RepoFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(Path.Combine(parts) + " was not found above " + AppContext.BaseDirectory);
    }

    private static string FrameworkArgument()
    {
        var workflow = File.ReadAllText(RepoFile(".github", "workflows", "release.yml"));
        var arguments = Regex.Matches(workflow, @"--framework\s+(?<value>\S+)");
        Assert.True(arguments.Count == 1, $"release.yml should pass vpk pack exactly one --framework; found {arguments.Count}.");
        return arguments[0].Groups["value"].Value.Trim('\'', '"', '`');
    }

    private static void AssertFrameworkDependentWinX64Publish(string workflowPath)
    {
        var workflow = File.ReadAllText(workflowPath);
        var publish = Regex.Match(workflow, @"(?s)dotnet publish(?<arguments>.*?--output\s+\S+)");
        Assert.True(publish.Success, workflowPath + " has no dotnet publish command with an output path.");

        var arguments = publish.Groups["arguments"].Value;
        Assert.Contains("-r win-x64", arguments, StringComparison.Ordinal);
        Assert.Contains("--self-contained false", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsBuildAndReleasePublishForWinX64FrameworkDependent()
    {
        AssertFrameworkDependentWinX64Publish(RepoFile(".github", "workflows", "build.yml"));
        AssertFrameworkDependentWinX64Publish(RepoFile(".github", "workflows", "release.yml"));
    }

    [Fact]
    public void WindowsPublishWorkflowsStageNativeSymbolsSeparately()
    {
        var build = File.ReadAllText(RepoFile(".github", "workflows", "build.yml")).Replace('\\', '/');
        var release = File.ReadAllText(RepoFile(".github", "workflows", "release.yml")).Replace('\\', '/');
        var stager = File.ReadAllText(RepoFile(".github", "scripts", "Stage-PublishSymbols.ps1"));

        Assert.Contains(".github/scripts/Stage-PublishSymbols.ps1", build, StringComparison.Ordinal);
        Assert.Contains("Clipthrough-win-x64-symbols", build, StringComparison.Ordinal);
        Assert.Contains(".github/scripts/Stage-PublishSymbols.ps1", release, StringComparison.Ordinal);
        Assert.Contains("-win-x64-symbols.zip", release, StringComparison.Ordinal);
        Assert.True(
            build.IndexOf("Stage-PublishSymbols.ps1", StringComparison.Ordinal) < build.IndexOf("name: Upload binary artifact", StringComparison.Ordinal),
            "The build workflow must remove native symbols before uploading the user artifact.");
        Assert.True(
            release.IndexOf("Stage-PublishSymbols.ps1", StringComparison.Ordinal) < release.IndexOf("name: Package zip", StringComparison.Ordinal),
            "The release workflow must remove native symbols before creating the user ZIP.");
        Assert.True(
            release.IndexOf("Stage-PublishSymbols.ps1", StringComparison.Ordinal) < release.IndexOf("name: Build Velopack package", StringComparison.Ordinal),
            "The release workflow must remove native symbols before creating the Velopack package.");
        Assert.Contains("libSkiaSharp.pdb", stager, StringComparison.Ordinal);
        Assert.Contains("libHarfBuzzSharp.pdb", stager, StringComparison.Ordinal);
        Assert.Contains("Copy-Item", stager, StringComparison.Ordinal);
        Assert.Contains("Remove-Item", stager, StringComparison.Ordinal);
        Assert.Contains("Clipthrough.deps.json", stager, StringComparison.Ordinal);
        Assert.Contains("runtimeTarget", stager, StringComparison.Ordinal);
        Assert.Contains("'runtime', 'native'", stager, StringComparison.Ordinal);
    }

    [Fact]
    public void TheInstallerRequiresAServicedDotnetRuntime()
    {
        var dependencies = FrameworkArgument().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var dotnet = dependencies.Where(d => d.StartsWith("net", StringComparison.Ordinal)).ToArray();
        Assert.Single(dotnet);

        var match = VelopackDotnetDependency.Match(dotnet[0]);
        Assert.True(match.Success, $"'{dotnet[0]}' does not parse as a Velopack dotnet dependency; Velopack would silently ignore it.");

        // The floor is only a floor with all three parts: "net10.0" accepts
        // any 10.0.x, including the ones the floor exists to refuse.
        Assert.True(match.Groups["build"].Success, $"'{dotnet[0]}' has no patch part, so it sets no floor.");
        Assert.True(int.Parse(match.Groups["build"].Value, System.Globalization.CultureInfo.InvariantCulture) >= MinimumRuntimePatch);
        Assert.Equal("x64", match.Groups["arch"].Value);

        // "runtime", not "desktop" (owner decision): the app loads only
        // Microsoft.NETCore.App - Avalonia does not use the Windows Desktop
        // framework - so requiring the desktop bundle would make users
        // download one they never load. That stops being true the day the
        // project opts into WPF or Windows Forms, which is checked below.
        Assert.Equal("runtime", match.Groups["type"].Value);

        // Velopack compares major and minor exactly, so the dependency has to
        // name the runtime the app actually targets.
        var project = XDocument.Load(RepoFile("Clipthrough", "Clipthrough.csproj"));
        foreach (var desktopFramework in new[] { "UseWPF", "UseWindowsForms" })
        {
            Assert.False(
                project.Descendants(desktopFramework).Any(e => string.Equals(e.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase)),
                $"Clipthrough.csproj sets {desktopFramework}, so the app now loads Microsoft.WindowsDesktop.App: the installer must require the desktop runtime.");
        }

        var targetFramework = project.Descendants("TargetFramework").Single().Value;
        var target = Regex.Match(targetFramework, @"^net(?<major>\d+)\.(?<minor>\d+)");
        Assert.True(target.Success, $"Unexpected TargetFramework '{targetFramework}'.");
        Assert.Equal(target.Groups["major"].Value, match.Groups["major"].Value);
        Assert.Equal(target.Groups["minor"].Value, match.Groups["minor"].Value);
    }

    [Fact]
    public void VelopackKeepsManagedSymbolsAfterNativeSymbolsAreStaged()
    {
        var workflow = File.ReadAllText(RepoFile(".github", "workflows", "release.yml"));
        var pack = Regex.Match(workflow, @"(?s)vpk pack(?<arguments>.*?--outputDir\s+\S+)");
        Assert.True(pack.Success, "release.yml has no vpk pack command with an output directory.");

        // vpk 1.2.0 defaults --exclude to .*\.pdb, even after our stager has
        // removed native symbols. Override it so managed symbols survive packaging.
        var exclude = Regex.Match(pack.Groups["arguments"].Value, @"--exclude\s+['""](?<pattern>[^'""]+)['""]");
        Assert.True(exclude.Success, "vpk pack must override its default PDB exclusion.");
        var pattern = new Regex(exclude.Groups["pattern"].Value, RegexOptions.CultureInvariant);
        Assert.False(pattern.IsMatch("Clipthrough.pdb"));
        Assert.False(pattern.IsMatch("ShareX.ImageEditor.pdb"));
    }

    /// <summary>
    /// The release ZIP is the same publish output without Velopack in front
    /// of it, so the only thing that can refuse a runtime below the floor is
    /// the .NET host, which reads the framework version from
    /// Clipthrough.runtimeconfig.json. With the SDK default of 10.0.0 the ZIP
    /// ran on any 10.0.x. The host treats the version as a minimum (the default
    /// roll-forward still picks the latest installed patch), so it has to be
    /// the same floor the installer enforces.
    /// </summary>
    [Fact]
    public void TheAppItselfRequiresTheSameServicedRuntime()
    {
        // The referenced app project's runtimeconfig is copied next to the
        // tests; it is the file the host reads, built from the same project
        // properties the release publish uses.
        var path = Path.Combine(AppContext.BaseDirectory, "Clipthrough.runtimeconfig.json");
        Assert.True(File.Exists(path), path + " was not found; the app's runtimeconfig is no longer copied next to the tests.");

        using var config = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var options = config.RootElement.GetProperty("runtimeOptions");
        Assert.False(options.TryGetProperty("frameworks", out _), "The app now loads more than one shared framework; the floor below covers only one.");
        var framework = options.GetProperty("framework");
        Assert.Equal("Microsoft.NETCore.App", framework.GetProperty("name").GetString());

        var version = Version.Parse(framework.GetProperty("version").GetString()!);
        Assert.True(
            version.Build >= MinimumRuntimePatch,
            $"Clipthrough.runtimeconfig.json requires Microsoft.NETCore.App {version}, so the release ZIP runs on any {version.Major}.{version.Minor}.x below the {MinimumRuntimePatch} floor.");

        var installer = VelopackDotnetDependency.Match(FrameworkArgument().Split(',', StringSplitOptions.TrimEntries).Single(d => d.StartsWith("net", StringComparison.Ordinal)));
        Assert.Equal(installer.Groups["version"].Value, version.ToString(3));
    }

    /// <summary>
    /// Anti-vacuity: the pattern refuses what Velopack refuses, so the test
    /// above cannot pass on a string the client would ignore.
    /// </summary>
    [Theory]
    [InlineData("net10.0.14-x64-desktop", true)]
    [InlineData("net10.0.14-x64-runtime", true)]
    [InlineData("net10.0-x64-desktop", true)]
    [InlineData("dotnet10.0.14-x64-desktop", false)]
    [InlineData("net10.0.14-desktop-x64", false)]
    [InlineData("net10.0.14 -x64-desktop", false)]
    public void ThePatternMatchesVelopacksParser(string dependency, bool parses)
        => Assert.Equal(parses, VelopackDotnetDependency.IsMatch(dependency));
}
