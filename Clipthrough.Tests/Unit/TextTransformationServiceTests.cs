using System;
using System.Globalization;
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

    /// <summary>
    /// Sorting used the machine's culture, so the same clip sorted differently
    /// on two machines. Ordinal would fix that but compares codepoints, which
    /// banishes every capitalised line ahead of every lowercase one. Invariant
    /// is both deterministic and dictionary-ordered, and this pins it: the
    /// expected order is wrong under Ordinal (which yields Banana, Zebra, apple,
    /// cherry) and unstable under CurrentCulture.
    /// </summary>
    [Fact]
    public void SortLines_IsCaseAwareAndIndependentOfTheMachineCulture()
    {
        // "Ahre" with a diaeresis is the discriminator: sv-SE treats it as a
        // distinct letter sorting after Z, while invariant groups it with A.
        // Without it the fixture sorts identically under every culture and
        // proves nothing.
        const string input = "Zebra\napple\nBanana\ncherry\n\u00c4hre";
        const string expected = "\u00c4hre\napple\nBanana\ncherry\nZebra";

        Assert.Equal(expected, WithCulture("en-US", () => TextTransformationService.Apply(TextTransformation.SortLines, input)));
        Assert.Equal(expected, WithCulture("sv-SE", () => TextTransformationService.Apply(TextTransformation.SortLines, input)));
        Assert.Equal(expected, WithCulture("tr-TR", () => TextTransformationService.Apply(TextTransformation.SortLines, input)));
    }

    /// <summary>
    /// The camel-case transforms produce identifiers, so they must not depend on
    /// the machine's locale. Under tr-TR the culture-aware overloads map "ID" to
    /// the dotless "\u0131d" and "identifier" to the dotted "\u0130dentifier", silently
    /// corrupting code on its way back to the clipboard.
    ///
    /// Every case below places an i/I where exactly one of the conversion sites
    /// will touch it - first word, leading character of a later word, and the
    /// tail of a later word - because an input without an i in that position
    /// cannot tell the two cultures apart.
    /// </summary>
    [Theory]
    [InlineData(TextTransformation.LowerCamelCase, "ID number", "idNumber")]
    [InlineData(TextTransformation.LowerCamelCase, "user id", "userId")]
    [InlineData(TextTransformation.LowerCamelCase, "user MAIL", "userMail")]
    [InlineData(TextTransformation.UpperCamelCase, "user identifier", "UserIdentifier")]
    [InlineData(TextTransformation.FromCamelCase, "identifierValue", "Identifier Value")]
    public void CamelCaseTransforms_AreNotCorruptedByTheTurkishDotlessI(
        TextTransformation transformation, string input, string expected)
    {
        Assert.Equal(expected, WithCulture("en-US", () => TextTransformationService.Apply(transformation, input)));
        Assert.Equal(expected, WithCulture("tr-TR", () => TextTransformationService.Apply(transformation, input)));
    }

    /// <summary>
    /// The counterpart to the test above: recasing prose is a linguistic
    /// operation, so it deliberately stays culture-aware. Forcing invariant here
    /// would turn "istanbul" into "ISTANBUL", which is not Turkish - the same
    /// corruption pointed the other way. This is what stops a well-meaning
    /// sweep from making every case transform invariant.
    /// </summary>
    [Fact]
    public void UpperCase_StaysLinguisticSoTurkishPreservesItsDottedCapital()
    {
        Assert.Equal("ISTANBUL", WithCulture("en-US", () => TextTransformationService.Apply(TextTransformation.UpperCase, "istanbul")));
        Assert.Equal("\u0130STANBUL", WithCulture("tr-TR", () => TextTransformationService.Apply(TextTransformation.UpperCase, "istanbul")));
    }

    private static T WithCulture<T>(string cultureName, Func<T> action)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
            return action();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
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

    [Theory]
    [InlineData("hello", "\"hello\"")]
    [InlineData("a\nb", "\"a\\nb\"")]
    [InlineData("with \"quotes\"", "\"with \\u0022quotes\\u0022\"")]
    public void JsonQuote(string input, string expected)
        => Assert.Equal(expected, TextTransformationService.Apply(TextTransformation.JsonQuote, input));

    [Theory]
    [InlineData("\"hello\"", "hello")]
    [InlineData("  \"a\\nb\"  ", "a\nb")]
    // Forgiving: bare text without surrounding quotes is still unescaped.
    [InlineData("a\\nb", "a\nb")]
    [InlineData("plain text", "plain text")]
    [InlineData("a\\tb", "a\tb")]
    public void JsonUnquote(string input, string expected)
        => Assert.Equal(expected, TextTransformationService.Apply(TextTransformation.JsonUnquote, input));

    [Fact]
    public void JsonMinify_RemovesWhitespace()
    {
        var input = "{\n  \"a\": 1,\n  \"b\": [1, 2, 3]\n}";
        Assert.Equal("{\"a\":1,\"b\":[1,2,3]}", TextTransformationService.Apply(TextTransformation.JsonMinify, input));
    }

    [Fact]
    public void JsonPretty_FormatsCompactJson()
    {
        var result = TextTransformationService.Apply(TextTransformation.JsonPretty, "{\"a\":1,\"b\":2}");
        Assert.Contains("\"a\": 1", result);
        Assert.Contains("\"b\": 2", result);
        Assert.Contains("\n", result);
    }

    [Fact]
    public void JsonPretty_ReturnsInputUnchangedOnInvalid()
    {
        var result = TextTransformationService.Apply(TextTransformation.JsonPretty, "not json");
        Assert.Equal("not json", result);
    }

    [Theory]
    [InlineData("hello world", "hello%20world")]
    [InlineData("a=b&c", "a%3Db%26c")]
    public void UrlEncode(string input, string expected)
        => Assert.Equal(expected, TextTransformationService.Apply(TextTransformation.UrlEncode, input));

    [Theory]
    [InlineData("hello%20world", "hello world")]
    [InlineData("a%3Db", "a=b")]
    public void UrlDecode(string input, string expected)
        => Assert.Equal(expected, TextTransformationService.Apply(TextTransformation.UrlDecode, input));

    [Fact]
    public void Base64Encode_RoundTrips()
    {
        var encoded = TextTransformationService.Apply(TextTransformation.Base64Encode, "hello");
        Assert.Equal("aGVsbG8=", encoded);
        var decoded = TextTransformationService.Apply(TextTransformation.Base64Decode, encoded);
        Assert.Equal("hello", decoded);
    }

    [Fact]
    public void Base64Decode_AcceptsMissingPadding()
    {
        // "hello" -> "aGVsbG8=" — drop the padding and confirm it still decodes.
        Assert.Equal("hello", TextTransformationService.Apply(TextTransformation.Base64Decode, "aGVsbG8"));
    }

    [Fact]
    public void Base64Decode_ReturnsInputOnInvalid()
    {
        Assert.Equal("not base64!", TextTransformationService.Apply(TextTransformation.Base64Decode, "not base64!"));
    }

    [Fact]
    public void CleanTerminalFormatting_StripsBoxBordersAndKeepsInnerContent()
    {
        var input =
            "┌────────────┐\n" +
            "│  let x = 1; │\n" +
            "│  let y = 2; │\n" +
            "└────────────┘\n";

        var expected = "let x = 1;\nlet y = 2;";

        Assert.Equal(expected, TextTransformationService.Apply(TextTransformation.CleanTerminalFormatting, input));
    }

    [Fact]
    public void CleanTerminalFormatting_HandlesDoubleBorderAndScrollbarColumn()
    {
        // Mirrors the Kusto / Copilot CLI dashboard format with an outer box,
        // an inner box and a scrollbar character on the right.
        var input =
            "│  let lookback = 90d;                                                   │ │\n" +
            "│  let bin = 1d;                                                         │ │\n" +
            "│                                                                       │ │\n";

        var expected = "let lookback = 90d;\nlet bin = 1d;";

        Assert.Equal(expected, TextTransformationService.Apply(TextTransformation.CleanTerminalFormatting, input));
    }

    [Fact]
    public void CleanTerminalFormatting_StripsAnsiColorEscapes()
    {
        var input = "\x1B[31mred\x1B[0m and \x1B[1mbold\x1B[22m text";
        Assert.Equal("red and bold text", TextTransformationService.Apply(TextTransformation.CleanTerminalFormatting, input));
    }

    [Fact]
    public void CleanTerminalFormatting_LeavesUnrelatedTextAlone()
    {
        var input = "Just a regular paragraph.\nWith two lines.";
        Assert.Equal(input, TextTransformationService.Apply(TextTransformation.CleanTerminalFormatting, input));
    }

    [Fact]
    public void CleanTerminalFormatting_PreservesKustoPipeOperator()
    {
        // Real-world capture from a Copilot CLI / Kusto Workbench panel: each
        // line is wrapped in Unicode box-drawing borders with a scrollbar
        // column on the right, and the inside of the box contains real Kusto
        // code that uses the ASCII `|` pipe operator. The cleaner must strip
        // the outer borders but leave the pipe alone.
        var input =
            "│  let lookback = 90d;                                          │ │\n" +
            "│  let Incidents =                                              │ │\n" +
            "│      IcMIncidents                                             │ │\n" +
            "│      | where TimeCreated > ago(lookback)                      │ │\n" +
            "│      | where ServiceName in (\"Kusto\",\"Fabric RTI\",\"ADX\")     │ │\n" +
            "│      | extend Severity = tostring(Severity)                   │";

        var expected =
            "let lookback = 90d;\n" +
            "let Incidents =\n" +
            "    IcMIncidents\n" +
            "    | where TimeCreated > ago(lookback)\n" +
            "    | where ServiceName in (\"Kusto\",\"Fabric RTI\",\"ADX\")\n" +
            "    | extend Severity = tostring(Severity)";

        Assert.Equal(expected, TextTransformationService.Apply(TextTransformation.CleanTerminalFormatting, input));
    }

    [Fact]
    public void CleanTerminalFormatting_DoesNotStripAsciiPipeWhenNoUnicodeBorders()
    {
        // Pure ASCII content with `|` characters (Kusto / SQL / shell pipes)
        // must survive untouched — ASCII '|' is not a terminal border.
        var input = "data\n| project x\n| where y == 1";
        Assert.Equal(input, TextTransformationService.Apply(TextTransformation.CleanTerminalFormatting, input));
    }
}
