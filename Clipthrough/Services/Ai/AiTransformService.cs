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
    private const string CopilotBaseUrl = "https://api.githubcopilot.com";
    private const string DefaultModel = "gpt-4o-mini";
    private const string DefaultCopilotModel = "gpt-4o";
    private const string DefaultImageModel = "gpt-image-1";

    // Headers Copilot's chat-completions API expects from IDE integrations.
    // Values mirror what the public Copilot CLI/Neovim clients send.
    private const string CopilotEditorVersion = "Clipthrough/1.0";
    private const string CopilotEditorPluginVersion = "Clipthrough/1.0";
    private const string CopilotIntegrationId = "vscode-chat";
    private const string CopilotUserAgent = "GitHubCopilotChat/0.26.7";

    private readonly ISettingsService _settings;
    private readonly ICopilotAuthService? _copilotAuth;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;

    public AiTransformService(ISettingsService settings, ICopilotAuthService? copilotAuth = null)
        : this(settings, copilotAuth, new HttpClient { Timeout = TimeSpan.FromSeconds(60) }, ownsHttpClient: true)
    {
    }

    internal AiTransformService(ISettingsService settings, ICopilotAuthService? copilotAuth, HttpClient http, bool ownsHttpClient = false)
    {
        _settings = settings;
        _copilotAuth = copilotAuth;
        _http = http;
        _ownsHttpClient = ownsHttpClient;
    }

    // Test-only constructor (public to avoid InternalsVisibleTo just for tests)
    public AiTransformService(ISettingsService settings, HttpClient http)
        : this(settings, null, http, ownsHttpClient: false)
    {
    }

    public bool IsConfigured
    {
        get
        {
            var s = _settings.Current;
            if (!s.EnableAi) return false;
            if (s.AiProvider == Models.AiProvider.Copilot)
                return _copilotAuth?.IsSignedIn == true;
            var (_, apiKey, _, _) = ResolveOpenAiConfig();
            return !string.IsNullOrWhiteSpace(apiKey);
        }
    }

    public async Task<string> TransformAsync(string systemPrompt, string input, CancellationToken cancellationToken = default)
    {
        var (baseUrl, apiKey, model, _, isCopilot) = await ResolveConfigAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("AI is not configured. Set an API key in settings or the OPENAI_API_KEY environment variable.");
        }

        var endpoint = baseUrl.TrimEnd('/') + "/chat/completions";
        var reasoning = (_settings.Current.AiReasoningEffort ?? string.Empty).Trim();
        var payload = new System.Collections.Generic.Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = new[]
            {
                new { role = "system", content = systemPrompt ?? string.Empty },
                new { role = "user", content = input ?? string.Empty },
            },
            ["temperature"] = 0.2,
        };
        if (!string.IsNullOrEmpty(reasoning))
        {
            payload["reasoning_effort"] = reasoning;
        }

        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        ApplyProviderHeaders(request, isCopilot);

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

    public async Task<string> DescribeImageAsync(string systemPrompt, byte[] imageBytes, string mediaType, CancellationToken cancellationToken = default)
    {
        if (imageBytes is null || imageBytes.Length == 0)
        {
            throw new ArgumentException("Image bytes are required.", nameof(imageBytes));
        }

        var (baseUrl, apiKey, model, _, isCopilot) = await ResolveConfigAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("AI is not configured. Set an API key in settings or the OPENAI_API_KEY environment variable.");
        }

        var endpoint = baseUrl.TrimEnd('/') + "/chat/completions";
        var mime = string.IsNullOrWhiteSpace(mediaType) ? "image/png" : mediaType;
        var dataUrl = $"data:{mime};base64,{Convert.ToBase64String(imageBytes)}";
        var reasoning = (_settings.Current.AiReasoningEffort ?? string.Empty).Trim();

        var payload = new System.Collections.Generic.Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = new object[]
            {
                new { role = "system", content = systemPrompt ?? string.Empty },
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = string.IsNullOrWhiteSpace(systemPrompt) ? "Describe this image." : systemPrompt },
                        new { type = "image_url", image_url = new { url = dataUrl } },
                    },
                },
            },
            ["temperature"] = 0.2,
        };
        if (!string.IsNullOrEmpty(reasoning))
        {
            payload["reasoning_effort"] = reasoning;
        }

        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        ApplyProviderHeaders(request, isCopilot);

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
            return content.ValueKind switch
            {
                JsonValueKind.String => content.GetString() ?? string.Empty,
                JsonValueKind.Array => ExtractTextFromContentArray(content),
                _ => string.Empty,
            };
        }

        throw new InvalidOperationException($"AI response had no content: {Truncate(body, 500)}");
    }

    public async Task<byte[]> EditImageAsync(string prompt, byte[] imageBytes, string mediaType, CancellationToken cancellationToken = default)
    {
        if (imageBytes is null || imageBytes.Length == 0)
        {
            throw new ArgumentException("Image bytes are required.", nameof(imageBytes));
        }
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new ArgumentException("Prompt is required.", nameof(prompt));
        }

        var (baseUrl, apiKey, _, imageModel, isCopilot) = await ResolveConfigAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("AI is not configured. Set an API key in settings or the OPENAI_API_KEY environment variable.");
        }

        var endpoint = baseUrl.TrimEnd('/') + "/images/edits";
        var mime = string.IsNullOrWhiteSpace(mediaType) ? "image/png" : mediaType;
        var extension = mime.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase) ? "jpg"
            : mime.Equals("image/webp", StringComparison.OrdinalIgnoreCase) ? "webp"
            : "png";

        using var form = new MultipartFormDataContent
        {
            { new StringContent(imageModel), "model" },
            { new StringContent(prompt), "prompt" },
            { new StringContent("1"), "n" },
            { new StringContent("b64_json"), "response_format" },
        };
        var imageContent = new ByteArrayContent(imageBytes);
        imageContent.Headers.ContentType = new MediaTypeHeaderValue(mime);
        form.Add(imageContent, "image", $"image.{extension}");

        var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        ApplyProviderHeaders(request, isCopilot);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"AI image edit failed: {(int)response.StatusCode} {response.ReasonPhrase}. {Truncate(body, 500)}");
        }

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Array
            && data.GetArrayLength() > 0)
        {
            var first = data[0];
            if (first.TryGetProperty("b64_json", out var b64) && b64.ValueKind == JsonValueKind.String)
            {
                var s = b64.GetString();
                if (!string.IsNullOrEmpty(s))
                {
                    return Convert.FromBase64String(s);
                }
            }
            if (first.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String)
            {
                var src = url.GetString();
                if (!string.IsNullOrEmpty(src))
                {
                    using var imgResponse = await _http.GetAsync(src, cancellationToken).ConfigureAwait(false);
                    imgResponse.EnsureSuccessStatusCode();
                    return await imgResponse.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }

        throw new InvalidOperationException($"AI image edit returned no data: {Truncate(body, 500)}");
    }

    private static string ExtractTextFromContentArray(JsonElement array)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var part in array.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.Object
                && part.TryGetProperty("text", out var t)
                && t.ValueKind == JsonValueKind.String)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(t.GetString());
            }
        }
        return sb.ToString();
    }

    private async Task<(string BaseUrl, string ApiKey, string Model, string ImageModel, bool IsCopilot)> ResolveConfigAsync(CancellationToken cancellationToken)
    {
        var s = _settings.Current;
        if (s.AiProvider == Models.AiProvider.Copilot && _copilotAuth is not null && _copilotAuth.IsSignedIn)
        {
            var token = await _copilotAuth.GetTokenAsync(cancellationToken).ConfigureAwait(false);
            var model = !string.IsNullOrWhiteSpace(s.AiModel) ? s.AiModel : DefaultCopilotModel;
            var imageModel = !string.IsNullOrWhiteSpace(s.AiImageModel) ? s.AiImageModel : DefaultImageModel;
            return (CopilotBaseUrl, token, model, imageModel, true);
        }

        var (baseUrl, apiKey, openAiModel, openAiImageModel) = ResolveOpenAiConfig();
        return (baseUrl, apiKey, openAiModel, openAiImageModel, false);
    }

    private static void ApplyProviderHeaders(HttpRequestMessage request, bool isCopilot)
    {
        if (!isCopilot)
        {
            return;
        }

        request.Headers.TryAddWithoutValidation("Editor-Version", CopilotEditorVersion);
        request.Headers.TryAddWithoutValidation("Editor-Plugin-Version", CopilotEditorPluginVersion);
        request.Headers.TryAddWithoutValidation("Copilot-Integration-Id", CopilotIntegrationId);
        request.Headers.TryAddWithoutValidation("User-Agent", CopilotUserAgent);
        request.Headers.TryAddWithoutValidation("Openai-Intent", "conversation-panel");
    }

    private (string BaseUrl, string ApiKey, string Model, string ImageModel) ResolveOpenAiConfig()
    {
        var s = _settings.Current;
        var baseUrl = !string.IsNullOrWhiteSpace(s.AiBaseUrl)
            ? s.AiBaseUrl
            : Environment.GetEnvironmentVariable("OPENAI_BASE_URL") ?? DefaultBaseUrl;
        var apiKey = !string.IsNullOrWhiteSpace(s.AiApiKey)
            ? s.AiApiKey
            : Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? string.Empty;
        var model = !string.IsNullOrWhiteSpace(s.AiModel) ? s.AiModel : DefaultModel;
        var imageModel = !string.IsNullOrWhiteSpace(s.AiImageModel) ? s.AiImageModel : DefaultImageModel;
        return (baseUrl, apiKey, model, imageModel);
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
