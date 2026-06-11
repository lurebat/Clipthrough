using System;
using System.IO;
using System.Threading.Tasks;
using Clipthrough.Database;
using Clipthrough.Models;
using Clipthrough.Services;
using Clipthrough.Services.Platform;
using Xunit;

namespace Clipthrough.Tests.Unit;

/// <summary>
/// Unit tests for U2 (DPAPI protection of AiApiKey + RemoteApiToken in SettingsService).
/// Verifies: no plaintext in settings.json; legacy plaintext migrates on load;
/// Unprotect failure drops key; no-op protector keeps secrets in-memory only.
/// </summary>
public sealed class SettingsServiceSecretsTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _settingsPath;
    private readonly string _aiKeyPath;
    private readonly string _remoteTokenPath;

    public SettingsServiceSecretsTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "clipthrough-settings-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _settingsPath = Path.Combine(_tempRoot, "settings.json");
        _aiKeyPath = Path.Combine(_tempRoot, "settings-ai-key.bin");
        _remoteTokenPath = Path.Combine(_tempRoot, "settings-remote-token.bin");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* test cleanup */ }
    }

    /// <summary>
    /// Creates a <see cref="SettingsService"/> backed by a temporary, uninitialized
    /// SQLite DB. The legacy-mirror save (<c>TrySaveLegacyCopyToDatabaseAsync</c>)
    /// will silently fail because <c>app_metadata</c> doesn't exist — that's fine for
    /// these tests, which only verify file-based credential persistence.
    /// </summary>
    private SettingsService NewService(IDataProtectionService protection)
    {
        var dbPath = Path.Combine(_tempRoot, "test.db");
        var storageOptions = new TestStorageOptionsService(dbPath);
        var factory = new SqliteConnectionFactory(storageOptions);
        return new SettingsService(factory, protection, _settingsPath);
    }

    // ─── Save path: no plaintext in settings.json ────────────────────────────

    [Fact]
    public async Task SaveAsync_RealProtector_NoPlaintextKeyInSettingsJson()
    {
        var service = NewService(new FakeDataProtectionService());
        await service.InitializeAsync();

        await service.SaveAsync(AppSettings.Default with
        {
            AiApiKey = "sk-secret-key",
            RemoteApiToken = "my-remote-token",
        });

        var json = await File.ReadAllTextAsync(_settingsPath);
        Assert.DoesNotContain("sk-secret-key", json);
        Assert.DoesNotContain("my-remote-token", json);
    }

    [Fact]
    public async Task SaveAsync_RealProtector_WritesSidecarFiles()
    {
        var service = NewService(new FakeDataProtectionService());
        await service.InitializeAsync();

        await service.SaveAsync(AppSettings.Default with
        {
            EnableRemoteApi = true,
            AiApiKey = "sk-secret-key",
            RemoteApiToken = "remote-tok",
        });

        Assert.True(File.Exists(_aiKeyPath), "AI key sidecar should be written");
        Assert.True(File.Exists(_remoteTokenPath), "Remote token sidecar should be written");

        // Sidecar must not contain plaintext.
        var aiBytes = await File.ReadAllBytesAsync(_aiKeyPath);
        Assert.DoesNotContain("sk-secret-key", System.Text.Encoding.UTF8.GetString(aiBytes));
    }

    [Fact]
    public async Task SaveAsync_RealProtector_RoundTrips_Secrets()
    {
        var service = NewService(new FakeDataProtectionService());
        await service.InitializeAsync();

        await service.SaveAsync(AppSettings.Default with
        {
            EnableRemoteApi = true,
            AiApiKey = "my-ai-key",
            RemoteApiToken = "my-token",
        });

        // Reload: fresh service with same protector.
        var reloaded = NewService(new FakeDataProtectionService());
        await reloaded.InitializeAsync();

        Assert.Equal("my-ai-key", reloaded.Current.AiApiKey);
        Assert.Equal("my-token", reloaded.Current.RemoteApiToken);
    }

    // ─── NoOp protector: in-memory only ──────────────────────────────────────

    [Fact]
    public async Task SaveAsync_NoOpProtector_DoesNotWriteSidecarFiles()
    {
        var service = NewService(new NoOpDataProtectionService());
        await service.InitializeAsync();

        await service.SaveAsync(AppSettings.Default with
        {
            EnableRemoteApi = true,
            AiApiKey = "sk-in-memory-only",
            RemoteApiToken = "tok-in-memory-only",
        });

        // In-memory state should carry the secrets.
        Assert.Equal("sk-in-memory-only", service.Current.AiApiKey);
        Assert.Equal("tok-in-memory-only", service.Current.RemoteApiToken);

        // But no sidecar files should exist.
        Assert.False(File.Exists(_aiKeyPath), "No AI key sidecar should be written for no-op protector");
        Assert.False(File.Exists(_remoteTokenPath), "No remote token sidecar should be written for no-op protector");

        // settings.json must not contain the plaintext.
        var json = await File.ReadAllTextAsync(_settingsPath);
        Assert.DoesNotContain("sk-in-memory-only", json);
        Assert.DoesNotContain("tok-in-memory-only", json);
    }

    [Fact]
    public async Task SaveAsync_NoOpProtector_FreshLoad_YieldsEmptySecrets()
    {
        var service = NewService(new NoOpDataProtectionService());
        await service.InitializeAsync();

        await service.SaveAsync(AppSettings.Default with { AiApiKey = "transient" });

        // A fresh load can't recover the secret — it was never written.
        var reloaded = NewService(new NoOpDataProtectionService());
        await reloaded.InitializeAsync();

        Assert.Equal(string.Empty, reloaded.Current.AiApiKey);
    }

    // ─── Legacy plaintext auto-migration ────────────────────────────────────

    [Fact]
    public async Task InitializeAsync_LegacyPlaintext_MigratesOnLoad_RealProtector()
    {
        // Simulate settings.json written by an older version with plaintext secrets.
        var legacy = AppSettings.Default with { EnableRemoteApi = true, AiApiKey = "legacy-key", RemoteApiToken = "legacy-tok" };
        await File.WriteAllTextAsync(_settingsPath,
            System.Text.Json.JsonSerializer.Serialize(legacy,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));

        // Load with a real protector — migration should fire.
        var service = NewService(new FakeDataProtectionService());
        await service.InitializeAsync();

        // In-memory secrets should be restored.
        Assert.Equal("legacy-key", service.Current.AiApiKey);
        Assert.Equal("legacy-tok", service.Current.RemoteApiToken);

        // settings.json must no longer contain the plaintext.
        var json = await File.ReadAllTextAsync(_settingsPath);
        Assert.DoesNotContain("legacy-key", json);
        Assert.DoesNotContain("legacy-tok", json);

        // Sidecar files must now exist.
        Assert.True(File.Exists(_aiKeyPath));
        Assert.True(File.Exists(_remoteTokenPath));

        // A fresh load must still return the secrets.
        var reloaded = NewService(new FakeDataProtectionService());
        await reloaded.InitializeAsync();
        Assert.Equal("legacy-key", reloaded.Current.AiApiKey);
        Assert.Equal("legacy-tok", reloaded.Current.RemoteApiToken);
    }

    // ─── Unprotect failure: drop key ─────────────────────────────────────────

    [Fact]
    public async Task InitializeAsync_UnprotectFailure_DropsKeyNocrash()
    {
        // First, write a real protected sidecar with the fake protector.
        var setup = NewService(new FakeDataProtectionService());
        await setup.InitializeAsync();
        await setup.SaveAsync(AppSettings.Default with { AiApiKey = "protected-key" });

        Assert.True(File.Exists(_aiKeyPath));

        // Now load with a service whose Unprotect always throws.
        var service = NewService(new FailingUnprotectDataProtectionService());
        await service.InitializeAsync(); // must not throw

        // Key must be dropped.
        Assert.Equal(string.Empty, service.Current.AiApiKey);
        // Sidecar should be deleted by the failure handler.
        Assert.False(File.Exists(_aiKeyPath));
    }

    // ─── Empty secrets: sidecar files deleted ────────────────────────────────

    [Fact]
    public async Task SaveAsync_EmptySecrets_DeletesSidecarFiles()
    {
        // Write non-empty secrets first.
        var service = NewService(new FakeDataProtectionService());
        await service.InitializeAsync();
        await service.SaveAsync(AppSettings.Default with { AiApiKey = "key", RemoteApiToken = "tok" });
        Assert.True(File.Exists(_aiKeyPath));

        // Now save with empty secrets — sidecars should be deleted.
        await service.SaveAsync(AppSettings.Default with { AiApiKey = string.Empty, RemoteApiToken = string.Empty });
        Assert.False(File.Exists(_aiKeyPath), "AI key sidecar should be removed when secret is empty");
        Assert.False(File.Exists(_remoteTokenPath), "Remote token sidecar should be removed when secret is empty");
    }
}
