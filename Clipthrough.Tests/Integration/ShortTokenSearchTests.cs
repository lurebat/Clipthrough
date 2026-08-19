using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Clipthrough.Models;
using Xunit;

namespace Clipthrough.Tests.Integration;

/// <summary>
/// Every word typed into the search box has to be required, including words
/// shorter than three characters.
/// </summary>
/// <remarks>
/// Asaf reported that spaces behave as OR. The token combination is in fact AND
/// on all three paths - the FTS expression joins with AND, the plain-text path
/// fails a row on the first token it misses, and the regex path requires every
/// matcher - but the *effect* he described was real, by a different mechanism.
///
/// The trigram index stores 3-character shingles, so a 1- or 2-character token
/// cannot be looked up in it and BuildFtsExpression drops it. The path was
/// chosen by HasFtsCompatibleSearchTerm, which asked whether ANY token was long
/// enough. So "go home" took the FTS path and searched for "home" alone: every
/// clip containing "home" came back, whether or not it contained "go". That is
/// indistinguishable from OR at the search box, and two-word queries where one
/// word is short are ordinary - "is null", "to do", "on off", "a bug".
///
/// The path is now chosen on whether EVERY token can be looked up. A query with
/// a short token falls back to the substring path, which requires all of them.
/// </remarks>
public sealed class ShortTokenSearchTests
{
    private static async Task CaptureAsync(TemporaryDatabaseScope scope, string text)
    {
        var clip = await scope.ClipStoreService.CaptureFastAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = text,
            ContentBytes = Encoding.UTF8.GetBytes(text),
        });

        Assert.NotNull(clip);
    }

    private static async Task<string[]> SearchAsync(TemporaryDatabaseScope scope, string query)
    {
        var result = await scope.ClipStoreService.SearchAsync(new ClipSearchFilters { SearchText = query });
        return result.Items.Select(i => i.Content).OrderBy(c => c, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// The reported behaviour: a short word must not be ignored.
    /// </summary>
    [Fact]
    public async Task AShortWordIsStillRequired()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();

        await CaptureAsync(scope, "go home now");
        await CaptureAsync(scope, "a home without the short word");

        var hits = await SearchAsync(scope, "go home");

        Assert.Equal(["go home now"], hits);
    }

    /// <summary>
    /// The control. Without it, "never return anything" would satisfy the test
    /// above, and so would a fix that broke short-token queries entirely.
    /// </summary>
    [Fact]
    public async Task LongWordsStillAndTogetherAndStillMatch()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();

        await CaptureAsync(scope, "alpha beta gamma");
        await CaptureAsync(scope, "alpha only here");

        Assert.Equal(["alpha beta gamma"], await SearchAsync(scope, "alpha beta"));
        Assert.Equal(2, (await SearchAsync(scope, "alpha")).Length);
    }

    /// <summary>
    /// A query that is nothing but short tokens has to work too. This already
    /// went down the substring path, and must keep doing so.
    /// </summary>
    [Fact]
    public async Task AQueryOfOnlyShortWordsStillMatches()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();

        await CaptureAsync(scope, "on off switch");
        await CaptureAsync(scope, "on its own");

        Assert.Equal(["on off switch"], await SearchAsync(scope, "on off"));
    }
}
