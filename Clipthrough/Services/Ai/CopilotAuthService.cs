using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Clipthrough.Services;

public sealed class CopilotAuthService : ICopilotAuthService, IDisposable
{
    // Well-known Copilot CLI OAuth app client_id.
    private const string ClientId = "Iv1.b507a08c87ecfe98";
    private const string DeviceCodeUrl = "https://github.com/login/device/code";
    private const string AccessTokenUrl = "https://github.com/login/oauth/access_token";
    private const string CopilotTokenUrl = "https://api.github.com/copilot_internal/v2/token";
    private const string RequiredScope = "read:user";

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly IDataProtectionService? _dataProtection;
    private readonly string? _tokenPath;

    private string? _gitHubToken;
    private string? _copilotToken;
    private DateTimeOffset _copilotTokenExpiry;

    public CopilotAuthService(IDataProtectionService dataProtection)
        : this(new HttpClient(), ownsHttpClient: true, dataProtection)
    {
    }

    // Parameterless ctor kept for legacy callers / DI fallback paths.
    public CopilotAuthService()
        : this(new HttpClient(), ownsHttpClient: true, dataProtection: null)
    {
    }

    public CopilotAuthService(HttpClient http)
        : this(http, ownsHttpClient: false, dataProtection: null)
    {
    }

    private CopilotAuthService(HttpClient http, bool ownsHttpClient, IDataProtectionService? dataProtection)
    {
        _http = http;
        _ownsHttpClient = ownsHttpClient;
        _http.DefaultRequestHeaders.Accept.Clear();
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        _dataProtection = dataProtection;
        if (dataProtection is not null)
        {
            _tokenPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Clipthrough",
                "copilot-token.bin");
            TryLoadGitHubToken();
        }
    }

    private void TryLoadGitHubToken()
    {
        if (_dataProtection is null || _tokenPath is null) return;
        try
        {
            if (!File.Exists(_tokenPath)) return;
            var protectedBytes = File.ReadAllBytes(_tokenPath);
            if (protectedBytes.Length == 0) return;
            var raw = _dataProtection.Unprotect(protectedBytes);
            var token = System.Text.Encoding.UTF8.GetString(raw);
            if (!string.IsNullOrEmpty(token))
            {
                _gitHubToken = token;
            }
        }
        catch (Exception ex)
        {
            // Don't crash startup if the token file is corrupt or unprotect
            // fails (e.g., user profile changed). Just sign the user out.
            System.Diagnostics.Trace.TraceWarning($"Failed to load persisted Copilot token: {ex.Message}");
            try { File.Delete(_tokenPath); } catch { /* best-effort */ }
        }
    }

    private void TryPersistGitHubToken()
    {
        if (_dataProtection is null || _tokenPath is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_tokenPath)!);
            if (string.IsNullOrEmpty(_gitHubToken))
            {
                if (File.Exists(_tokenPath)) File.Delete(_tokenPath);
                return;
            }
            var raw = System.Text.Encoding.UTF8.GetBytes(_gitHubToken);
            var protectedBytes = _dataProtection.Protect(raw);
            File.WriteAllBytes(_tokenPath, protectedBytes);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"Failed to persist Copilot token: {ex.Message}");
        }
    }

    public bool IsSignedIn => !string.IsNullOrEmpty(_gitHubToken);

    public event Action? SignedInChanged;

    public async Task<DeviceCodeResult> StartDeviceCodeFlowAsync(CancellationToken cancellationToken = default)
    {
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["scope"] = RequiredScope,
        });

        using var response = await _http.PostAsync(DeviceCodeUrl, content, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        return new DeviceCodeResult(
            DeviceCode: root.GetProperty("device_code").GetString()!,
            UserCode: root.GetProperty("user_code").GetString()!,
            VerificationUri: root.GetProperty("verification_uri").GetString()!,
            ExpiresInSeconds: root.GetProperty("expires_in").GetInt32(),
            IntervalSeconds: root.GetProperty("interval").GetInt32());
    }

    public async Task<bool> PollForAuthorizationAsync(DeviceCodeResult deviceCode, CancellationToken cancellationToken = default)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(deviceCode.IntervalSeconds, 5));
        var deadline = DateTimeOffset.UtcNow.AddSeconds(deviceCode.ExpiresInSeconds);

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);

            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["device_code"] = deviceCode.DeviceCode,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            });

            using var response = await _http.PostAsync(AccessTokenUrl, content, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("access_token", out var token))
            {
                _gitHubToken = token.GetString();
                _copilotToken = null;
                _copilotTokenExpiry = default;
                TryPersistGitHubToken();
                SignedInChanged?.Invoke();
                return true;
            }

            if (root.TryGetProperty("error", out var error))
            {
                var errorCode = error.GetString();
                if (errorCode == "authorization_pending")
                {
                    continue;
                }
                if (errorCode == "slow_down")
                {
                    interval = interval.Add(TimeSpan.FromSeconds(5));
                    continue;
                }
                // expired_token, access_denied, or other terminal error
                return false;
            }
        }

        return false;
    }

    public async Task<string> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(_gitHubToken))
        {
            throw new InvalidOperationException("Not signed in to GitHub Copilot.");
        }

        if (!string.IsNullOrEmpty(_copilotToken) && DateTimeOffset.UtcNow < _copilotTokenExpiry.AddMinutes(-2))
        {
            return _copilotToken;
        }

        var request = new HttpRequestMessage(HttpMethod.Get, CopilotTokenUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("token", _gitHubToken);
        request.Headers.UserAgent.ParseAdd("Clipthrough/1.0");

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        _copilotToken = root.GetProperty("token").GetString()
            ?? throw new InvalidOperationException("Copilot token response missing 'token' field.");

        if (root.TryGetProperty("expires_at", out var exp) && exp.ValueKind == JsonValueKind.Number)
        {
            _copilotTokenExpiry = DateTimeOffset.FromUnixTimeSeconds(exp.GetInt64());
        }
        else
        {
            _copilotTokenExpiry = DateTimeOffset.UtcNow.AddMinutes(25);
        }

        return _copilotToken;
    }

    public void SignOut()
    {
        _gitHubToken = null;
        _copilotToken = null;
        _copilotTokenExpiry = default;
        TryPersistGitHubToken();
        SignedInChanged?.Invoke();
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }
}
