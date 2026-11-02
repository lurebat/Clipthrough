using Clipthrough.ViewModels;
using Xunit;

namespace Clipthrough.Tests.Unit;

/// <summary>
/// The refresh after undoing a single delete animates only when it is exactly
/// that one row coming back; anything more is a list change, not an arrival.
/// </summary>
public sealed class SingleInsertionTests
{
    [Theory]
    [InlineData(new long[] { 1, 2, 3 }, new long[] { 9, 1, 2, 3 }, true)]    // back at the top
    [InlineData(new long[] { 1, 2, 3 }, new long[] { 1, 9, 2, 3 }, true)]    // back in the middle
    [InlineData(new long[] { 1, 2, 3 }, new long[] { 1, 2, 3, 9 }, true)]    // back at the end
    [InlineData(new long[] { }, new long[] { 9 }, true)]
    [InlineData(new long[] { 1, 2, 3 }, new long[] { 1, 2, 3 }, false)]      // nothing arrived
    [InlineData(new long[] { 1, 2, 3 }, new long[] { 9, 8, 1, 2, 3 }, false)] // two arrived
    [InlineData(new long[] { 1, 2, 3 }, new long[] { 9, 2, 1, 3 }, false)]   // one arrived, two swapped
    [InlineData(new long[] { 1, 2, 3 }, new long[] { 9, 1, 2, 4 }, false)]   // one arrived, one replaced
    public void OnlyExactlyOneArrival(long[] shown, long[] next, bool expected)
        => Assert.Equal(expected, MainWindowViewModel.IsSingleInsertion(shown, next));
}