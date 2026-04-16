using Clipthrough.Services;
using Xunit;

namespace Clipthrough.Tests.Unit;

public class FuzzyMatcherTests
{
    [Theory]
    [InlineData("hotkey shortcut local", "hotkey", true)]
    [InlineData("hotkey shortcut local", "hot key", true)] // synonym expansion
    [InlineData("hotkey shortcut local", "keybind", true)] // synonym expansion
    [InlineData("theme dark light", "dark mode", true)] // multiword synonym
    [InlineData("theme dark light", "color", true)]
    [InlineData("storage database path", "db", true)]
    [InlineData("storage database path", "sqlite", true)]
    [InlineData("capacity size library entries", "max", true)]
    [InlineData("sensitivity rules pattern regex", "secret", true)]
    [InlineData("tools external editor diff winmerge", "compare", true)]
    [InlineData("a b c compare d", "compar", true)] // prefix/fuzzy match on the word
    [InlineData("storage database path", "completely unrelated term", false)]
    public void SettingsMatch_applies_synonyms_and_fuzzy(string haystack, string query, bool expected)
    {
        Assert.Equal(expected, FuzzyMatcher.SettingsMatch(haystack, query));
    }

    [Fact]
    public void SettingsMatch_empty_query_returns_true()
    {
        Assert.True(FuzzyMatcher.SettingsMatch("anything", ""));
    }

    [Fact]
    public void Score_returns_one_for_exact_substring()
    {
        Assert.Equal(1.0, FuzzyMatcher.Score("the quick brown fox", "brown"));
    }

    [Fact]
    public void Score_returns_zero_for_empty_inputs()
    {
        Assert.Equal(0.0, FuzzyMatcher.Score("", "x"));
        Assert.Equal(0.0, FuzzyMatcher.Score("x", ""));
    }
}
