using System;
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
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AppSettings _current = AppSettings.Default;
    private bool _isInitialized;

    public SettingsService(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public AppSettings Current => _current;

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
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO app_metadata (key, value)
                VALUES ($key, $value)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                """;
            command.Parameters.AddWithValue("$key", SettingsKey);
            command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(normalized, JsonOptions));
            await command.ExecuteNonQueryAsync(cancellationToken);

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

        try
        {
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)?.Normalize() ?? AppSettings.Default;
        }
        catch (JsonException)
        {
            return AppSettings.Default;
        }
    }
}


