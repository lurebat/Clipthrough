using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Database;
using Clipthrough.Models;
using Microsoft.Data.Sqlite;

namespace Clipthrough.Services;

public sealed class SettingsService : ISettingsService
{
    private const string SettingsKey = "settings:app:v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly IDataProtectionService _dataProtection;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _settingsPath;
    private readonly string _aiKeyPath;
    private readonly string _legacyRemoteTokenPath;
    private AppSettings _current = AppSettings.Default;
    private bool _isInitialized;

    public SettingsService(SqliteConnectionFactory connectionFactory, IDataProtectionService dataProtection)
        : this(connectionFactory, dataProtection, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Clipthrough",
            "settings.json"))
    {
    }

    // Test-only seam: allow the settings path to be overridden so tests don't
    // pollute the user's real settings.json.
    public SettingsService(SqliteConnectionFactory connectionFactory, IDataProtectionService dataProtection, string settingsPath)
    {
        _connectionFactory = connectionFactory;
        _dataProtection = dataProtection;
        _settingsPath = settingsPath;
        // Sidecar files live alongside settings.json; each holds one DPAPI blob.
        var stem = Path.Combine(Path.GetDirectoryName(settingsPath)!, Path.GetFileNameWithoutExtension(settingsPath));
        _aiKeyPath = stem + "-ai-key.bin";
        _legacyRemoteTokenPath = stem + "-remote-token.bin";
    }

    public AppSettings Current => _current;

    public bool HasSavedSettings => File.Exists(_settingsPath);

    public event EventHandler<AppSettings>? SettingsChanged;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_isInitialized)
        {
            return;
        }

        var shouldNotify = false;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_isInitialized)
            {
                return;
            }

            _current = await LoadSettingsAsync(cancellationToken);
            _isInitialized = true;
            shouldNotify = true;
        }
        finally
        {
            _gate.Release();
        }

        if (shouldNotify)
        {
            SettingsChanged?.Invoke(this, _current);
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var normalized = settings.Normalize();

        SecretPersistenceException? secretFailure = null;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            try
            {
                await SaveToDiskAsync(normalized, cancellationToken);
            }
            catch (SecretPersistenceException ex)
            {
                // settings.json was still written; only the credential sidecars
                // failed. Finish publishing the in-memory state so the secret
                // stays usable for this session, then report the failure.
                secretFailure = ex;
            }

            await TrySaveLegacyCopyToDatabaseAsync(normalized, cancellationToken);
            _current = normalized;
            _isInitialized = true;
        }
        finally
        {
            _gate.Release();
        }

        SettingsChanged?.Invoke(this, _current);

        if (secretFailure is not null)
        {
            throw secretFailure;
        }
    }

    private async Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken)
    {
        AppSettings? loaded = null;

        if (File.Exists(_settingsPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(_settingsPath, cancellationToken);
                loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)?.Normalize();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                Trace.TraceWarning($"Settings file load failed: {ex.Message}");
            }
        }

        if (loaded is null)
        {
            try
            {
                await using var connection = _connectionFactory.CreateConnection();
                await connection.OpenAsync(cancellationToken);

                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT value FROM app_metadata WHERE key = $key LIMIT 1;";
                command.Parameters.AddWithValue("$key", SettingsKey);
                var scalar = await command.ExecuteScalarAsync(cancellationToken);
                if (scalar is string legacyJson && !string.IsNullOrWhiteSpace(legacyJson))
                {
                    loaded = JsonSerializer.Deserialize<AppSettings>(legacyJson, JsonOptions)?.Normalize();
                }
            }
            catch (Exception ex) when (ex is JsonException or SqliteException)
            {
                Trace.TraceWarning($"Legacy settings load failed: {ex.Message}");
            }
        }

        loaded ??= AppSettings.Default;

        // Merge protected secrets from sidecar files into the loaded settings.
        // If the settings.json still carries plaintext credentials (legacy), use
        // them in-memory and migrate to sidecars (auto-migration, KTD2).
        var (aiApiKey, needsMigration) = LoadAndMergeSecrets(loaded);
        var merged = loaded with { AiApiKey = aiApiKey };

        if (needsMigration)
        {
            // Best-effort migration: write sidecars and strip plaintext from settings.json.
            try
            {
                await SaveToDiskAsync(merged, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecretPersistenceException)
            {
                // A migration that cannot re-protect the credential is not fatal:
                // the value is still live in memory for this session.
                Trace.TraceWarning($"Settings secrets migration failed: {ex.Message}");
            }
        }

        return merged;
    }

    /// <summary>
    /// Loads the protected AI credential from its sidecar file, falling back to
    /// the plaintext field in <paramref name="settings"/> (legacy). Returns the
    /// resolved value and a flag indicating whether the caller should re-save to
    /// complete the migration away from plaintext.
    /// </summary>
    private (string aiApiKey, bool needsMigration) LoadAndMergeSecrets(AppSettings settings)
    {
        var needsMigration = false;

        // The remote-control API was removed; drop any credential it left behind
        // rather than leaving a protected secret orphaned on disk.
        try { File.Delete(_legacyRemoteTokenPath); } catch { /* best-effort */ }

        // Try to load the secret from its sidecar first.
        var aiApiKey = TryLoadSecret(_aiKeyPath) ?? string.Empty;

        // Fall back to legacy plaintext if no sidecar exists.
        if (string.IsNullOrEmpty(aiApiKey) && !string.IsNullOrEmpty(settings.AiApiKey))
        {
            aiApiKey = settings.AiApiKey;
            needsMigration = true;
        }

        return (aiApiKey, needsMigration);
    }

    /// <summary>
    /// Reads and unprotects a secret from a sidecar <c>.bin</c> file.
    /// Returns null when the file is absent or the blob is corrupt/unprotectable.
    /// </summary>
    private string? TryLoadSecret(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var protectedBytes = File.ReadAllBytes(path);
            if (protectedBytes.Length == 0) return null;
            var raw = _dataProtection.Unprotect(protectedBytes);
            return Encoding.UTF8.GetString(raw);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Secret load failed for '{Path.GetFileName(path)}'; dropping: {ex.Message}");
            try { File.Delete(path); } catch { /* best-effort */ }
            return null;
        }
    }

    private async Task SaveToDiskAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);

        // Persist AiApiKey via a protected sidecar rather than as plaintext in
        // settings.json. The field is stripped from the JSON so settings.json
        // never carries a readable credential.
        var failedSecrets = new List<string>();
        if (!TrySaveSecret(_aiKeyPath, settings.AiApiKey)) failedSecrets.Add("AI API key");

        // Serialize without the secret field.
        var stripped = settings with { AiApiKey = string.Empty };
        var json = JsonSerializer.Serialize(stripped, JsonOptions);
        await File.WriteAllTextAsync(_settingsPath, json, cancellationToken);

        // Everything except the named credentials is now on disk. Report the
        // credentials that are not, so the caller never shows an unqualified
        // "saved" for a secret that will be gone after a restart.
        if (failedSecrets.Count > 0)
        {
            throw new SecretPersistenceException(failedSecrets);
        }
    }

    /// <summary>
    /// Protects <paramref name="secret"/> and writes it to <paramref name="path"/>.
    /// When the value is empty the sidecar file is deleted (no credential → no file).
    /// When <see cref="IDataProtectionService.CanPersistSecrets"/> is false the
    /// secret is kept in-memory only and no file is written.
    /// </summary>
    /// <returns>
    /// <c>true</c> when the sidecar now reflects <paramref name="secret"/>, or when
    /// persisting secrets is intentionally unsupported on this platform;
    /// <c>false</c> when the write or delete failed and disk state is wrong.
    /// </returns>
    private bool TrySaveSecret(string path, string secret)
    {
        try
        {
            if (string.IsNullOrEmpty(secret))
            {
                if (File.Exists(path)) File.Delete(path);
                return true;
            }

            if (!_dataProtection.CanPersistSecrets)
            {
                // No-op protector (non-Windows): keep secret in-memory only.
                return true;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var raw = Encoding.UTF8.GetBytes(secret);
            var protectedBytes = _dataProtection.Protect(raw);
            File.WriteAllBytes(path, protectedBytes);
            return true;
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Secret save failed for '{Path.GetFileName(path)}': {ex.Message}");
            return false;
        }
    }

    private async Task TrySaveLegacyCopyToDatabaseAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO app_metadata (key, value)
                VALUES ($key, $value)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                """;
            command.Parameters.AddWithValue("$key", SettingsKey);
            // Legacy DB copy also strips secrets — never mirror credentials to SQLite.
            var stripped = settings with { AiApiKey = string.Empty };
            command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(stripped, JsonOptions));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException ex)
        {
            Trace.TraceWarning($"Legacy settings mirror save failed: {ex.Message}");
        }
    }
}
