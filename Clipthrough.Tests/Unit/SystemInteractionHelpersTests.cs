using System;
using System.Threading.Tasks;
using Clipthrough.Services.Platform;
using Xunit;

namespace Clipthrough.Tests.Unit;

/// <summary>
/// F-44. Every test used TestSystemInteractionService, and the real helpers were
/// private, so neither the URL allow-list nor the external-tool command parsing
/// was ever exercised: accepting file: URLs, or ignoring the {left}/{right}
/// placeholders, survived every test.
/// </summary>
public sealed class SystemInteractionHelpersTests
{
    private static readonly (string Token, string Value)[] Diff = [("{left}", @"C:\tmp\L.txt"), ("{right}", @"C:\tmp\R.txt")];

    [Fact]
    public void ATemplate_SubstitutesThePlaceholders_AndUnquotesTheExecutable()
    {
        var (exe, args) = SystemInteractionService.ParseCommandTemplate(
            "\"C:\\Program Files\\X\\x.exe\" --diff {left} {right}", Diff, [@"C:\tmp\L.txt", @"C:\tmp\R.txt"]);

        Assert.Equal(@"C:\Program Files\X\x.exe", exe);
        Assert.Equal(["--diff", @"C:\tmp\L.txt", @"C:\tmp\R.txt"], args);
    }

    [Fact]
    public void ATemplateWithoutPlaceholders_AppendsThePaths()
    {
        var (exe, args) = SystemInteractionService.ParseCommandTemplate(@"C:\tools\merge.exe /quiet", Diff, ["A", "B"]);

        Assert.Equal(@"C:\tools\merge.exe", exe);
        Assert.Equal(["/quiet", "A", "B"], args);
    }

    [Fact]
    public void APlaceholderInsideAnArgument_IsSubstitutedInPlace()
    {
        var (_, args) = SystemInteractionService.ParseCommandTemplate("tool.exe --left={left}", Diff, ["unused"]);

        Assert.Equal([@"--left=C:\tmp\L.txt"], args);
    }

    [Fact]
    public void AQuotedArgumentWithSpaces_StaysOneArgument()
        => Assert.Equal(["code", "--goto", @"C:\My Files\a.txt"], SystemInteractionService.TokenizeCommandLine("code --goto \"C:\\My Files\\a.txt\""));

    [Theory]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData(@"\\attacker\share\x.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ms-settings:privacy")]
    [InlineData("not a url")]
    public async Task OpenUrl_RefusesAnythingButWebAndMail_BeforeStartingAnything(string url)
    {
        using var service = new SystemInteractionService(Clipthrough.Models.AppDataRoot.Default(System.IO.Path.GetTempPath()));

        await Assert.ThrowsAsync<ArgumentException>(() => service.OpenUrlAsync(url));
    }
}