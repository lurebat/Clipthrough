using System;
using System.Threading.Tasks;
using Clipthrough.Services.Platform;
using Xunit;

namespace Clipthrough.Tests.Unit;

/// <summary>
/// The opener "Open source URL" relies on refuses anything but web and mail
/// links, so a crafted SourceUrl cannot reach the shell (F-20).
/// </summary>
public sealed class OpenUrlSchemeTests
{
    [Theory]
    [InlineData(@"\\host\share\x.exe")]
    [InlineData(@"file:///C:/Windows/System32/cmd.exe")]
    [InlineData(@"C:\Users\me\script.cmd")]
    [InlineData("javascript:alert(1)")]
    public async Task NonWebTargets_AreRejectedBeforeAnythingStarts(string target)
    {
        var service = new SystemInteractionService();

        await Assert.ThrowsAsync<ArgumentException>(() => service.OpenUrlAsync(target));
    }
}