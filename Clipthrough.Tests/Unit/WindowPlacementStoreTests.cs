using System;
using System.IO;
using System.Threading.Tasks;
using Clipthrough.Models;
using Clipthrough.Services;
using Xunit;

namespace Clipthrough.Tests.Unit;

public sealed class WindowPlacementStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Clipthrough.Tests", Guid.NewGuid().ToString("N"));

    private string StorePath => Path.Combine(_directory, "window.json");

    [Fact]
    public async Task ASavedPlacement_LoadsBack()
    {
        var placement = new SavedWindowPlacement(-1200, 40, 900.5, 700, Maximized: true);
        var store = new WindowPlacementStore(StorePath);
        store.Save(placement);
        await store.FlushAsync();

        Assert.Equal(placement, new WindowPlacementStore(StorePath).Load());
    }

    [Fact]
    public void NoFile_LoadsNothing()
    {
        Assert.Null(new WindowPlacementStore(StorePath).Load());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"x\":0,\"y\":0,\"width\":0,\"height\":700,\"maximized\":false}")]
    [InlineData("{\"x\":0,\"y\":0,\"width\":900,\"height\":-1,\"maximized\":false}")]
    public void AnUnusablePlacement_LoadsNothing(string json)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(StorePath, json);

        Assert.Null(new WindowPlacementStore(StorePath).Load());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
