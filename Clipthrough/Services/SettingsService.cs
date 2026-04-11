using System;
using System.Diagnostics;
using System.IO;
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
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _settingsPath;
    private AppSettings _current = AppSettings.Default;
    private bool _isInitialized;

    public SettingsService(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
        _settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Clipthrough",
            "settings.json");
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

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await SaveToDiskAsync(normalized, cancellationToken);
            await TrySaveLegacyCopyToDatabaseAsync(normalized, cancellationToken);
            _current = normalized;
            _isInitialized = true;
        }
        finally
        {
            _gate.Release();
        }

        SettingsChanged?.Invoke(this, _current);
    }

    private async Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(_settingsPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(_settingsPath, cancellationToken);
                return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)?.Normalize() ?? AppSettings.Default;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                Trace.TraceWarning($"Settings file load failed: {ex.Message}");
            }
        }

        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM app_metadata WHERE key = $key LIMIT 1;";
            command.Parameters.AddWithValue("$key", SettingsKey);
            var scalar = await command.ExecuteScalarAsync(cancellationToken);
            if (scalar is not string json || string.IsNullOrWhiteSpace(json))
            {
                return AppSettings.Default;
            }

            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)?.Normalize() ?? AppSettings.Default;
        }
        catch (Exception ex) when (ex is JsonException or SqliteException)
        {
            Trace.TraceWarning($"Legacy settings load failed: {ex.Message}");
            return AppSettings.Default;
        }
    }

    private async Task SaveToDiskAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        await File.WriteAllTextAsync(_settingsPath, json, cancellationToken);
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
            command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(settings, JsonOptions));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException ex)
        {
            Trace.TraceWarning($"Legacy settings mirror save failed: {ex.Message}");
        }
    }
}


