using Clipthrough.Models;

using Xunit;

namespace Clipthrough.Tests;

/// <summary>
/// Custom hotkey targets, which had no test at all.
///
/// The failure mode is silence: the gesture is registered with the OS, the key
/// press is swallowed, and a target that does not parse simply does nothing. So
/// nobody finds a broken one by using the app - they find it by pressing a key
/// and shrugging.
/// </summary>
public sealed class CustomHotkeyTargetTests
{
    [Theory]
    [InlineData("builtin:UpperCase", CustomHotkeyKind.BuiltIn, "builtin", "UpperCase")]
    [InlineData("ai:Summarise", CustomHotkeyKind.AiPreset, "ai", "Summarise")]
    [InlineData("prompt:make it terse", CustomHotkeyKind.InlinePrompt, "prompt", "make it terse")]
    [InlineData("aiprompt:", CustomHotkeyKind.AiPromptDialog, "aiprompt", "")]
    public void EachDocumentedKindParses(string target, CustomHotkeyKind kind, string token, string value)
    {
        var parsed = CustomHotkeyTarget.Parse(target);

        Assert.Equal(kind, parsed.Kind);
        Assert.Equal(token, parsed.Token);
        Assert.Equal(value, parsed.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nocolon")]
    [InlineData(":leadingcolon")]
    [InlineData("unknownkind:value")]
    public void AnythingElseIsUnknownRatherThanGuessedAt(string? target)
        => Assert.Equal(CustomHotkeyKind.Unknown, CustomHotkeyTarget.Parse(target).Kind);

    /// <summary>
    /// A prompt is free text and routinely contains a colon, so only the first
    /// one separates. Splitting on the last, or on all of them, would truncate
    /// exactly the prompts most worth binding to a key.
    /// </summary>
    [Fact]
    public void OnlyTheFirstColonSeparates()
    {
        var parsed = CustomHotkeyTarget.Parse("prompt:rewrite this as: a haiku");

        Assert.Equal(CustomHotkeyKind.InlinePrompt, parsed.Kind);
        Assert.Equal("rewrite this as: a haiku", parsed.Value);
    }

    [Theory]
    [InlineData("BUILTIN:UpperCase")]
    [InlineData("  BuiltIn  :UpperCase")]
    public void TheKindIsCaseAndSpaceInsensitive(string target)
    {
        var parsed = CustomHotkeyTarget.Parse(target);

        Assert.Equal(CustomHotkeyKind.BuiltIn, parsed.Kind);
        Assert.Equal("builtin", parsed.Token);
    }

    /// <summary>
    /// A transformed clip records its provenance as <c>token:value</c>, so the
    /// token has to survive parsing unchanged in the form it is written back.
    /// Deriving it from the enum instead would rename what is already in the
    /// database for every clip produced by a custom hotkey.
    /// </summary>
    [Fact]
    public void TheTokenIsPreservedForPersistence()
    {
        var parsed = CustomHotkeyTarget.Parse("BuiltIn:UpperCase");

        Assert.Equal("builtin:UpperCase", $"{parsed.Token}:{parsed.Value}");
    }

    /// <summary>
    /// The value keeps its own case: preset names and prompts are user text.
    /// </summary>
    [Fact]
    public void TheValueIsNotLowercased()
        => Assert.Equal("MyPreset", CustomHotkeyTarget.Parse("ai:MyPreset").Value);
}
