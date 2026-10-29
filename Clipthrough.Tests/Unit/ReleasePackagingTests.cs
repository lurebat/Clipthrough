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
