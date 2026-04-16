using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Models;

namespace Clipthrough.Services;

/// <summary>
/// Minimal OpenAI-compatible chat-completions client. Reads base URL, API key and
/// model from <see cref="AppSettings"/>, falling back to the <c>OPENAI_BASE_URL</c>
/// and <c>OPENAI_API_KEY</c> environment variables when settings are blank.
/// </summary>
public sealed class AiTransformService : IAiTransformService, IDisposable
{
    private const string DefaultBaseUrl = "https://api.openai.com/v1";
    private const string DefaultModel = "gpt-4o-mini";

    private readonly ISettingsService _settings;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;

    public AiTransformService(ISettingsService settings)
        : this(settings, new HttpClient { Timeout = TimeSpan.FromSeconds(60) }, ownsHttpClient: true)
    {
    }

    internal AiTransformService(ISettingsService settings, HttpClient http, bool ownsHttpClient = false)
    {
        _settings = settings;
        _http = http;
        _ownsHttpClient = ownsHttpClient;
    }

    // Test-only constructor (public to avoid InternalsVisibleTo just for tests)
    public AiTransformService(ISettingsService settings, HttpClient http)
        : this(settings, http, ownsHttpClient: false)
    {
    }

    public bool IsConfigured
    {
        get
        {
            var (_, apiKey, _) = ResolveConfig();
            return _settings.Current.EnableAi && !string.IsNullOrWhiteSpace(apiKey);
        }
    }

    public async Task<string> TransformAsync(string systemPrompt, string input, CancellationToken cancellationToken = default)
    {
        var (baseUrl, apiKey, model) = ResolveConfig();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("AI is not configured. Set an API key in settings or the OPENAI_API_KEY environment variable.");
        }

        var endpoint = baseUrl.TrimEnd('/') + "/chat/completions";
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new ChatRequest(
                model,
                new[]
                {
                    new ChatMessage("system", systemPrompt ?? string.Empty),
                    new ChatMessage("user", input ?? string.Empty),
                },
                Temperature: 0.2)),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"AI request failed: {(int)response.StatusCode} {response.ReasonPhrase}. {Truncate(body, 500)}");
        }

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("choices", out var choices)
            && choices.ValueKind == JsonValueKind.Array
            && choices.GetArrayLength() > 0
            && choices[0].TryGetProperty("message", out var message)
            && message.TryGetProperty("content", out var content))
        {
            return content.GetString() ?? string.Empty;
        }

        throw new InvalidOperationException($"AI response had no content: {Truncate(body, 500)}");
    }

    private (string BaseUrl, string ApiKey, string Model) ResolveConfig()
    {
        var s = _settings.Current;
        var baseUrl = !string.IsNullOrWhiteSpace(s.AiBaseUrl)
            ? s.AiBaseUrl
            : Environment.GetEnvironmentVariable("OPENAI_BASE_URL") ?? DefaultBaseUrl;
        var apiKey = !string.IsNullOrWhiteSpace(s.AiApiKey)
            ? s.AiApiKey
            : Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? string.Empty;
        var model = !string.IsNullOrWhiteSpace(s.AiModel) ? s.AiModel : DefaultModel;
        return (baseUrl, apiKey, model);
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }

    private sealed record ChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] ChatMessage[] Messages,
        [property: JsonPropertyName("temperature")] double Temperature);

    private sealed record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);
}
