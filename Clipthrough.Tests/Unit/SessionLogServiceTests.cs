using System;
using System.Reflection;
using Clipthrough.Services;
using Xunit;

namespace Clipthrough.Tests.Unit;

public sealed class SessionLogServiceTests
{
    private static readonly MethodInfo s_shouldIgnoreMessage = typeof(SessionLogService)
        .GetMethod("ShouldIgnoreMessage", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("SessionLogService.ShouldIgnoreMessage was not found.");

    [Theory]
    [InlineData("[Layout]Layout cycle detected. Item 'Avalonia.Controls.Primitives.DataGridRowsPresenter' was enqueued '10' times. (LayoutQueue`1 #1)")]
    [InlineData("[Layout]Layout cycle detected. Item 'Avalonia.Controls.Primitives.DataGridColumnHeadersPresenter' was enqueued '10' times. (LayoutQueue`1 #1)")]
    [InlineData("[Layout]Layout cycle detected. Item 'Avalonia.Controls.DataGrid' was enqueued '10' times. (LayoutQueue`1 #1)")]
    [InlineData("[Layout]Layout cycle detected. Item 'Avalonia.Controls.Grid' was enqueued '10' times. (LayoutQueue`1 #1)")]
    public void ShouldIgnoreMessage_IgnoresBenignDataGridLayoutCycles(string message)
    {
        Assert.True(ShouldIgnoreMessage(message));
    }

    [Fact]
    public void ShouldIgnoreMessage_DoesNotIgnoreRealApplicationErrors()
    {
        const string message = "Embedding persist failed: Microsoft.Data.Sqlite.SqliteException (0x80004005): SQLite Error 19: 'FOREIGN KEY constraint failed'.";

        Assert.False(ShouldIgnoreMessage(message));
    }

    private static bool ShouldIgnoreMessage(string message)
        => (bool)s_shouldIgnoreMessage.Invoke(null, [message])!;
}
