using System;
using System.IO;
using System.Text;
using Clipthrough.Models;
using Clipthrough.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Clipthrough.Tests.Unit;

/// <summary>
/// The root the resolver chose reaches every service that keeps a file, through
/// the container the app actually runs - not through the test seams, which take
/// a path and so prove nothing about where the production constructor looks.
/// The root is a temp folder: a service that ignored it would go to the real
/// %LOCALAPPDATA%, and the assertions below would find nothing in the root.
/// </summary>
public sealed class AppDataRootWiringTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ct-dataroot-wiring-" + Guid.NewGuid().ToString("N"));
    private readonly AppDataRoot _root;
    private readonly ServiceProvider _provider;

    public AppDataRootWiringTests()
    {
        _root = AppDataRoot.Chosen(_directory, AppDataRootSource.CommandLine);
        var services = App.CreateServiceCollection(_root);
        // A protector that works on every platform, so the token check below
        // is not skipped where DPAPI does not exist.
        services.AddSingleton<IDataProtectionService>(new FakeDataProtectionService());
        _provider = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _provider.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void TheRootItself_IsRegistered()
        => Assert.Same(_root, _provider.GetRequiredService<AppDataRoot>());

    [Fact]
    public void Storage_DefaultsTheLibraryIntoTheRoot()
    {
        var storage = _provider.GetRequiredService<IStorageOptionsService>();

        Assert.Equal(Path.Combine(_directory, "clipthrough.db"), storage.Current.DatabasePath);
        Assert.Same(_root, storage.DataRoot);
    }

    [Fact]
    public void Storage_ReadsStorageJsonFromTheRoot()
    {
        var library = Path.Combine(_directory, "elsewhere", "library.db");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "storage.json"), "{\"databasePath\":" + System.Text.Json.JsonSerializer.Serialize(library) + "}");

        Assert.Equal(library, _provider.GetRequiredService<IStorageOptionsService>().Current.DatabasePath);
    }

    [Fact]
    public void Settings_AreReadFromTheRoot()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "settings.json"), "{}");

        Assert.True(_provider.GetRequiredService<ISettingsService>().HasSavedSettings);
    }

    [Fact]
    public void WindowPlacement_IsSavedInTheRoot()
    {
        var store = _provider.GetRequiredService<IWindowPlacementStore>();

        store.Save(new SavedWindowPlacement(10, 20, 900, 700, Maximized: false));
        store.Flush();

        Assert.True(File.Exists(Path.Combine(_directory, "window.json")));
    }

    [Fact]
    public void CopilotToken_IsReadFromTheRoot()
    {
        Directory.CreateDirectory(_directory);
        var token = new FakeDataProtectionService().Protect(Encoding.UTF8.GetBytes("gho_NOT-A-REAL-TOKEN-for-tests"));
        File.WriteAllBytes(Path.Combine(_directory, "copilot-token.bin"), token);

        Assert.True(_provider.GetRequiredService<ICopilotAuthService>().IsSignedIn);
    }
}
