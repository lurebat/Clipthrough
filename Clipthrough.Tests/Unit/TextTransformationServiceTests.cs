using Clipthrough.Models;
using Clipthrough.Services;
using Xunit;

namespace Clipthrough.Tests.Unit;

public sealed class TextTransformationServiceTests
{
    [Theory]
    [InlineData("hello world", "HELLO WORLD")]
    [InlineData("Hello World", "HELLO WORLD")]
    [InlineData("", "")]
    public void UpperCase(string input, string expected)
        => Assert.Equal(expected, TextTransformationService.Apply(TextTransformation.UpperCase, input));

    [Theory]
    [InlineData("HELLO WORLD", "hello world")]
    [InlineData("Hello World", "hello world")]
    public void LowerCase(string input, string expected)
        => Assert.Equal(expected, TextTransformationService.Apply(TextTransformation.LowerCase, input));

    [Theory]
    [InlineData("hello world", "Hello World")]
    [InlineData("HELLO WORLD", "Hello World")]
    public void TitleCase(string input, string expected)
        => Assert.Equal(expected, TextTransformationService.Apply(TextTransformation.TitleCase, input));

    [Theory]
    [InlineData("hello world. goodbye world", "Hello world. Goodbye world")]
    public void SentenceCase(string input, string expected)
        => Assert.Equal(expected, TextTransformationService.Apply(TextTransformation.SentenceCase, input));

    [Theory]
    [InlineData("hello world", "HelloWorld")]
    [InlineData("hello-world", "HelloWorld")]
    [InlineData("hello_world_test", "HelloWorldTest")]
    public void UpperCamelCase(string input, string expected)
        => Assert.Equal(expected, TextTransformationService.Apply(TextTransformation.UpperCamelCase, input));

    [Theory]
    [InlineData("hello world", "helloWorld")]
    [InlineData("Hello World", "helloWorld")]
    [InlineData("some-thing", "someThing")]
    public void LowerCamelCase(string input, string expected)
        => Assert.Equal(expected, TextTransformationService.Apply(TextTransformation.LowerCamelCase, input));

    [Theory]
    [InlineData("helloWorld", "Hello World")]
    [InlineData("HelloWorld", "Hello World")]
    [InlineData("myURLParser", "My U R L Parser")]
    public void FromCamelCase(string input, string expected)
        => Assert.Equal(expected, TextTransformationService.Apply(TextTransformation.FromCamelCase, input));

    [Fact]
    public void TrimWhitespace_TrimsEachLine()
    {
        var input = "  hello  \n  world  ";
        var result = TextTransformationService.Apply(TextTransformation.TrimWhitespace, input);
        Assert.Equal("hello\nworld", result);
    }

    [Fact]
    public void CollapseWhitespace_CollapsesMultiple()
    {
        var input = "hello   world   test";
        var result = TextTransformationService.Apply(TextTransformation.CollapseWhitespace, input);
        Assert.Equal("hello world test", result);
    }

    [Fact]
    public void TabsToSpaces_ReplacesTabsWithSpaces()
    {
        var input = "\thello\n\t\tworld";
        var result = TextTransformationService.Apply(TextTransformation.TabsToSpaces, input);
        Assert.Equal("    hello\n        world", result);
    }

    [Fact]
    public void SpacesToTabs_ConvertsLeadingSpaces()
    {
        var input = "    hello\n        world";
        var result = TextTransformationService.Apply(TextTransformation.SpacesToTabs, input);
        Assert.Equal("\thello\n\t\tworld", result);
    }

    [Fact]
    public void NormalizeEol_ConvertsToUnixLineEndings()
    {
        var input = "hello\r\nworld\rfoo";
        var result = TextTransformationService.Apply(TextTransformation.NormalizeEol, input);
        Assert.Equal("hello\nworld\nfoo", result);
    }

    [Fact]
    public void LinesToJsonArray_ProducesValidJson()
    {
        var input = "hello\nworld\nfoo";
        var result = TextTransformationService.Apply(TextTransformation.LinesToJsonArray, input);
        Assert.Equal("[\"hello\",\"world\",\"foo\"]", result);
    }

    [Fact]
    public void LinesToJsonArray_EscapesSpecialCharacters()
    {
        var input = "hello \"world\"\nfoo\\bar";
        var result = TextTransformationService.Apply(TextTransformation.LinesToJsonArray, input);
        // System.Text.Json uses \u0022 for double quotes by default
        Assert.Contains("world", result);
        Assert.Contains("foo", result);
        Assert.StartsWith("[", result);
        Assert.EndsWith("]", result);
    }

    [Theory]
    [InlineData("hello\nworld\nfoo", ", ", "hello, world, foo")]
    [InlineData("hello\nworld\nfoo", "|", "hello|world|foo")]
    public void JoinWithDelimiter(string input, string delimiter, string expected)
        => Assert.Equal(expected, TextTransformationService.Apply(TextTransformation.JoinWithDelimiter, input, delimiter));

    [Fact]
    public void SortLines_SortsAlphabetically()
    {
        var input = "charlie\nalpha\nbravo";
        var result = TextTransformationService.Apply(TextTransformation.SortLines, input);
        Assert.Equal("alpha\nbravo\ncharlie", result);
    }

    [Fact]
    public void ReverseLines_ReversesOrder()
    {
        var input = "first\nsecond\nthird";
        var result = TextTransformationService.Apply(TextTransformation.ReverseLines, input);
        Assert.Equal("third\nsecond\nfirst", result);
    }

    [Fact]
    public void RemoveEmptyLines_FiltersBlankLines()
    {
        var input = "hello\n\nworld\n  \nfoo";
        var result = TextTransformationService.Apply(TextTransformation.RemoveEmptyLines, input);
        Assert.Equal("hello\nworld\nfoo", result);
    }

    [Fact]
    public void RemoveDuplicateLines_KeepsFirstOccurrence()
    {
        var input = "hello\nworld\nhello\nfoo\nworld";
        var result = TextTransformationService.Apply(TextTransformation.RemoveDuplicateLines, input);
        Assert.Equal("hello\nworld\nfoo", result);
    }

    [Fact]
    public void BoxTableToHtml_ConvertsBoxDrawingTable()
    {
        var input = string.Join('\n',
            "┌────────┬─────────┐",
            "│ Name   │ Status  │",
            "├────────┼─────────┤",
            "│ alpha  │ ✅ ok   │",
            "├────────┼─────────┤",
            "│ bravo  │ ⚠️ miss │",
            "└────────┴─────────┘");

        var result = TextTransformationService.Apply(TextTransformation.BoxTableToHtml, input);

        Assert.StartsWith("<table", result);
        Assert.EndsWith("</table>", result);
        Assert.Contains("<th>Name</th>", result);
        Assert.Contains("<th>Status</th>", result);
        Assert.Contains("<td>alpha</td>", result);
        Assert.Contains("<td>\u2705 ok</td>", result);
        Assert.Contains("<td>bravo</td>", result);
        // Border-only rows are dropped, leaving header + 2 data rows.
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(result, "<tr>").Count);
    }

    [Fact]
    public void BoxTableToHtml_ConvertsCollapsedSingleLineBoxDrawingTable()
    {
        var input = "┌──────────────────────────┬────────────────────────────┬─────────────────┬──────────────┐ │ Source                   │ May 5-7 schema-skip alerts │ Distinct tables │ Distinct DBs │ ├──────────────────────────┼────────────────────────────┼─────────────────┼──────────────┤ │ INGEST-REFLEXPRDCUC000   │ ~445K                      │ 235             │ 155          │ ├──────────────────────────┼────────────────────────────┼─────────────────┼──────────────┤ │ INGEST-REFLEXPRDE2C000   │ ~253K                      │ 141             │ 77           │ ├──────────────────────────┼────────────────────────────┼─────────────────┼──────────────┤ │ INGEST-REFLEXPRDWUC000   │ ~208K                      │ 55              │ 51           │ ├──────────────────────────┼────────────────────────────┼─────────────────┼──────────────┤ │ INGEST-REFLEXPRDNEU000   │ ~139K                      │ 44              │ 32           │ └──────────────────────────┴────────────────────────────┴─────────────────┴──────────────┘";

        var result = TextTransformationService.Apply(TextTransformation.BoxTableToHtml, input);

        Assert.StartsWith("<table", result);
        Assert.Contains("<th>Source</th>", result);
        Assert.Contains("<th>May 5-7 schema-skip alerts</th>", result);
        Assert.Contains("<td>INGEST-REFLEXPRDCUC000</td>", result);
        Assert.Contains("<td>~445K</td>", result);
        Assert.Contains("<td>155</td>", result);
        Assert.Equal(5, System.Text.RegularExpressions.Regex.Matches(result, "<tr>").Count);
    }

    [Fact]
    public void BoxTableToHtml_EscapesHtmlInCells()
    {
        var input = string.Join('\n',
            "┌──────────────┐",
            "│ a<b>&\"c\"      │",
            "└──────────────┘");

        var result = TextTransformationService.Apply(TextTransformation.BoxTableToHtml, input);

        Assert.Contains("a&lt;b&gt;&amp;&quot;c&quot;", result);
    }

    [Fact]
    public void BoxTableToHtml_ReturnsInputWhenNoTableRows()
    {
        var input = "just some plain text\nwith no box drawing";
        var result = TextTransformationService.Apply(TextTransformation.BoxTableToHtml, input);
        Assert.Equal(input, result);
    }

    [Fact]
    public void BoxTableToHtml_ConvertsMarkdownPipeTable()
    {
        var input = string.Join('\n',
            "| Name  | Status |",
            "|-------|:------:|",
            "| alpha | ok     |",
            "| bravo | miss   |");

        var result = TextTransformationService.Apply(TextTransformation.BoxTableToHtml, input);

        Assert.StartsWith("<table", result);
        Assert.Contains("<th>Name</th>", result);
        Assert.Contains("<th>Status</th>", result);
        Assert.Contains("<td>alpha</td>", result);
        Assert.Contains("<td>bravo</td>", result);
        // The |---|:---:| separator must be dropped — only header + 2 data rows.
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(result, "<tr>").Count);
    }

    [Fact]
    public void BoxTableToHtml_ConvertsAsciiPlusBorderedTable()
    {
        var input = string.Join('\n',
            "+-------+--------+",
            "| Name  | Status |",
            "+-------+--------+",
            "| alpha | ok     |",
            "| bravo | miss   |",
            "+-------+--------+");

        var result = TextTransformationService.Apply(TextTransformation.BoxTableToHtml, input);

        Assert.Contains("<th>Name</th>", result);
        Assert.Contains("<td>alpha</td>", result);
        Assert.Contains("<td>bravo</td>", result);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(result, "<tr>").Count);
    }

    [Fact]
    public void BoxTableToHtml_PreservesSurroundingTextAndHandlesMultipleTables()
    {
        var input = string.Join("\n", new[]
        {
            "Intro paragraph with <special> chars.",
            "",
            "┌─────────┬─────────┐",
            "│ Cluster │ Status  │",
            "├─────────┼─────────┤",
            "│ A       │ ok      │",
            "└─────────┴─────────┘",
            "",
            "Some words between tables.",
            "",
            "| Check | Result |",
            "|-------|--------|",
            "| One   | ✅     |",
            "",
            "Trailing line.",
        });

        var result = TextTransformationService.Apply(TextTransformation.BoxTableToHtml, input);

        // Two distinct tables produced.
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(result, "<table").Count);
        // Headers from each table present.
        Assert.Contains("<th>Cluster</th>", result);
        Assert.Contains("<th>Check</th>", result);
        // Surrounding text preserved and HTML-escaped.
        Assert.Contains("Intro paragraph with &lt;special&gt; chars.", result);
        Assert.Contains("Some words between tables.", result);
        Assert.Contains("Trailing line.", result);
    }

    [Fact]
    public void None_ReturnsInputUnchanged()
    {
        var input = "hello world";
        Assert.Equal(input, TextTransformationService.Apply(TextTransformation.None, input));
    }

    [Fact]
    public void NullInput_ReturnsNull()
        => Assert.Null(TextTransformationService.Apply(TextTransformation.UpperCase, null!));

    [Fact]
    public void EmptyInput_ReturnsEmpty()
        => Assert.Equal(string.Empty, TextTransformationService.Apply(TextTransformation.UpperCase, string.Empty));
}
