using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Models;
using Clipthrough.Services;
using Xunit;

namespace Clipthrough.Tests.Unit;

/// <summary>
/// Integration-style unit tests for <see cref="RemoteControlService"/> that start a real
/// Kestrel server on a free loopback port and exercise the API surface via <see cref="HttpClient"/>.
/// Each test class instance owns its own server + port so tests run in parallel safely.
/// </summary>
public sealed class RemoteControlServiceTests : IAsyncDisposable
{
    private static readonly HttpClient Http = new();

    // ---------------------------------------------------------------------------
    // ResolveBindAddress static behaviour — no server needed
    // ---------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("localhost")]
    [InlineData("loopback")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public void ResolveBindAddress_loopback_inputs_return_loopback(string? configured)
    {
        var result = RemoteControlService.ResolveBindAddress(configured);
        Assert.Equal(IPAddress.Loopback, result);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("0.0.0.0")]
    [InlineData("any")]
    [InlineData("192.168.1.100")]
    [InlineData("10.0.0.1")]
    [InlineData("not-an-ip")]
    public void ResolveBindAddress_non_loopback_refused_returns_loopback(string configured)
    {
        // Non-loopback addresses must be refused (no TLS → cleartext network exposure).
        var result = RemoteControlService.ResolveBindAddress(configured);
        Assert.Equal(IPAddress.Loopback, result);
    }

    // ---------------------------------------------------------------------------
    // Server helpers
    // ---------------------------------------------------------------------------

    private readonly List<RemoteControlService> _services = new();

    public async ValueTask DisposeAsync()
    {
        foreach (var svc in _services)
        {
            await svc.DisposeAsync();
        }
    }

    private static int GetFreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private async Task<(RemoteControlService Service, string BaseUrl)> StartServiceAsync(
        string token = "test-token",
        IClipStoreService? clipStore = null)
    {
        var settings = new TestSettingsService();
        settings.SetCurrent(new AppSettings
        {
            EnableRemoteApi = true,
            RemoteApiToken = token,
            RemoteApiPort = GetFreePort(),
            RemoteApiBindAddress = "127.0.0.1",
        });

        var svc = new RemoteControlService(
            settings,
            clipStore ?? new StubClipStoreService(),
            new StubScriptingService(),
            new TestAiTransformService());

        _services.Add(svc);
        await svc.ApplySettingsAsync();
        return (svc, svc.BaseUrl!);
    }

    // ---------------------------------------------------------------------------
    // /transform endpoint removed (KTD1 / U10)
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Post_transform_kind_script_returns_404_endpoint_removed()
    {
        var (_, baseUrl) = await StartServiceAsync();
        var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/clips/999/transform");
        req.Headers.Add("Authorization", "Bearer test-token");
        req.Content = JsonContent.Create(new { kind = "script", code = "while(true){}" });

        var resp = await Http.SendAsync(req);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Post_transform_kind_ai_returns_404_endpoint_removed()
    {
        var (_, baseUrl) = await StartServiceAsync();
        var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/clips/999/transform");
        req.Headers.Add("Authorization", "Bearer test-token");
        req.Content = JsonContent.Create(new { kind = "ai", prompt = "summarise" });

        var resp = await Http.SendAsync(req);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Post_transform_kind_builtin_returns_404_endpoint_removed()
    {
        // All transform kinds are removed; even the formerly-safe 'builtin' kind is gone.
        var (_, baseUrl) = await StartServiceAsync();
        var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/clips/999/transform");
        req.Headers.Add("Authorization", "Bearer test-token");
        req.Content = JsonContent.Create(new { kind = "builtin", name = "TrimWhitespace" });

        var resp = await Http.SendAsync(req);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ---------------------------------------------------------------------------
    // Sensitive-clip content withholding
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Get_clips_sensitive_clips_have_null_content()
    {
        var clipStore = new StubClipStoreService();
        clipStore.SetSearchResult(new[]
        {
            MakeClip(1, "password: hunter2", isSensitive: true),
            MakeClip(2, "safe text", isSensitive: false),
        });

        var (_, baseUrl) = await StartServiceAsync(clipStore: clipStore);
        var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/clips");
        req.Headers.Add("Authorization", "Bearer test-token");

        var resp = await Http.SendAsync(req);
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<ClipsListResponse>();
        Assert.NotNull(body);

        var sensitive = body!.Items.First(i => i.IsSensitive);
        var normal    = body!.Items.First(i => !i.IsSensitive);

        Assert.Null(sensitive.Content);       // withheld
        Assert.Equal("safe text", normal.Content); // present
    }

    [Fact]
    public async Task Get_clip_by_id_sensitive_clip_has_null_content()
    {
        var clipStore = new StubClipStoreService();
        clipStore.SetClip(1, MakeClip(1, "my secret token", isSensitive: true));

        var (_, baseUrl) = await StartServiceAsync(clipStore: clipStore);
        var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/clips/1");
        req.Headers.Add("Authorization", "Bearer test-token");

        var resp = await Http.SendAsync(req);
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<ClipDto>();
        Assert.NotNull(body);
        Assert.Null(body!.Content);   // withheld
        Assert.True(body.IsSensitive); // flag still surfaced
    }

    // ---------------------------------------------------------------------------
    // Bearer authentication: 401 for missing/empty/wrong token
    // ---------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bearer ")]
    [InlineData("Basic dXNlcjpwYXNz")]
    [InlineData("Bearer wrong-token")]
    public async Task Clips_routes_return_401_on_bad_or_missing_bearer(string? authHeader)
    {
        var (_, baseUrl) = await StartServiceAsync(token: "correct-token");
        var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/clips");
        if (!string.IsNullOrEmpty(authHeader))
        {
            req.Headers.TryAddWithoutValidation("Authorization", authHeader);
        }

        var resp = await Http.SendAsync(req);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Clips_route_returns_200_with_correct_bearer()
    {
        var (_, baseUrl) = await StartServiceAsync(token: "my-secret-token");
        var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/clips");
        req.Headers.Add("Authorization", "Bearer my-secret-token");

        var resp = await Http.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Openapi_endpoint_requires_auth()
    {
        var (_, baseUrl) = await StartServiceAsync();
        // No Authorization header — /openapi must not be exempt.
        var resp = await Http.GetAsync($"{baseUrl}/openapi/v1.json");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Docs_endpoint_requires_auth()
    {
        var (_, baseUrl) = await StartServiceAsync();
        var resp = await Http.GetAsync($"{baseUrl}/docs");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Health_endpoint_is_unauthenticated()
    {
        var (_, baseUrl) = await StartServiceAsync();
        var resp = await Http.GetAsync($"{baseUrl}/health");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static ClipEntry MakeClip(long id, string content, bool isSensitive = false) => new()
    {
        Id = id,
        Content = content,
        ContentType = ContentType.Text,
        ContentFormat = ClipContentFormat.PlainText,
        IsSensitive = isSensitive,
    };

    // Minimal JSON deserialization shapes matching the ToDto anonymous object.
    private sealed class ClipsListResponse
    {
        public int Total { get; set; }
        public List<ClipDto> Items { get; set; } = new();
    }

    private sealed class ClipDto
    {
        public long Id { get; set; }
        public string? Content { get; set; }
        public bool IsSensitive { get; set; }
    }

    // ---------------------------------------------------------------------------
    // Test doubles
    // ---------------------------------------------------------------------------

    private sealed class StubClipStoreService : IClipStoreService
    {
        private ClipSearchResult _searchResult = new() { Items = Array.Empty<ClipEntry>(), TotalMatchingCount = 0 };
        private readonly Dictionary<long, ClipEntry> _clips = new();

        public void SetSearchResult(IReadOnlyList<ClipEntry> items)
        {
            _searchResult = new ClipSearchResult { Items = items, TotalMatchingCount = items.Count };
            foreach (var c in items) _clips[c.Id] = c;
        }

        public void SetClip(long id, ClipEntry clip)
        {
            _clips[id] = clip;
        }

        public Task<ClipSearchResult> SearchAsync(ClipSearchFilters filters, CancellationToken cancellationToken = default)
            => Task.FromResult(_searchResult);

        public Task<ClipEntry?> GetByIdAsync(long clipId, CancellationToken cancellationToken = default)
            => Task.FromResult(_clips.TryGetValue(clipId, out var c) ? c : null);

        public Task<ClipEntry?> CaptureAsync(ClipCaptureRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult<ClipEntry?>(null);

        public Task DeleteAsync(long clipId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        // Remaining interface members — not exercised by these tests.
        public Task<ClipEntry?> CaptureFastAsync(ClipCaptureRequest request, CancellationToken cancellationToken = default) => Task.FromResult<ClipEntry?>(null);
        public Task<ClipEntry?> UpdateDeferredContentAsync(long clipId, ClipCaptureRequest request, CancellationToken cancellationToken = default) => Task.FromResult<ClipEntry?>(null);
        public Task<ClipEntry?> UpdateSourceAppIconAsync(long clipId, byte[] iconBytes, CancellationToken cancellationToken = default) => Task.FromResult<ClipEntry?>(null);
        public Task<ClipEntry?> ApplySensitivityAsync(long clipId, CancellationToken cancellationToken = default) => Task.FromResult<ClipEntry?>(null);
    public Task<int> ApplyPendingSensitivityAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<BulkCaptureResult> CaptureBatchAsync(IReadOnlyList<ClipCaptureRequest> requests, CancellationToken cancellationToken = default) => Task.FromResult(new BulkCaptureResult(0, 0));
        public Task SetFavoriteAsync(long clipId, bool isFavorite, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetPinnedAsync(long clipId, bool isPinned, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearSensitivityAsync(long clipId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetSensitiveAsync(long clipId, bool isSensitive, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task MarkPastedAsync(long clipId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> TryClaimForOcrAsync(long clipId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> SetOcrResultAsync(long clipId, string ocrText, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> SetOcrFailureAsync(long clipId, string? error, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<System.Collections.Generic.IReadOnlyList<long>> GetPendingOcrClipIdsAsync(CancellationToken cancellationToken = default) => Task.FromResult<System.Collections.Generic.IReadOnlyList<long>>(Array.Empty<long>());
        public Task<bool> MarkOcrForRerunAsync(long clipId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<System.Collections.Generic.IReadOnlyList<long>> MarkAllSucceededForRerunAsync(CancellationToken cancellationToken = default) => Task.FromResult<System.Collections.Generic.IReadOnlyList<long>>(Array.Empty<long>());
        public Task<OcrCoverage> GetOcrCoverageAsync(CancellationToken cancellationToken = default) => Task.FromResult(new OcrCoverage(0, 0, 0, 0, 0));
        public Task<ClipMaintenanceResult> ApplyMaintenanceAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ClipMaintenanceResult());
        public Task RebuildSensitivityMatchesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ClipEntry?> GetClipAtOffsetAsync(int offset, CancellationToken cancellationToken = default) => Task.FromResult<ClipEntry?>(null);
        public Task<System.Collections.Generic.IReadOnlyList<ClipEntry>> GetByIdsAsync(System.Collections.Generic.IReadOnlyList<long> clipIds, CancellationToken cancellationToken = default) => Task.FromResult<System.Collections.Generic.IReadOnlyList<ClipEntry>>(Array.Empty<ClipEntry>());
        public Task<System.Collections.Generic.IReadOnlyList<ClipEmbeddingCandidate>> ClaimPendingEmbeddingsAsync(int batchSize, CancellationToken cancellationToken = default) => Task.FromResult<System.Collections.Generic.IReadOnlyList<ClipEmbeddingCandidate>>(Array.Empty<ClipEmbeddingCandidate>());
        public Task SaveEmbeddingBatchAsync(System.Collections.Generic.IReadOnlyList<ClipEmbeddingRecord> records, string modelVersion, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> SetEmbeddingFailureAsync(long clipId, string? error, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<System.Collections.Generic.IReadOnlyList<long>> MarkAllEmbeddingsForRerunAsync(CancellationToken cancellationToken = default) => Task.FromResult<System.Collections.Generic.IReadOnlyList<long>>(Array.Empty<long>());
        public Task<EmbeddingCoverage> GetEmbeddingCoverageAsync(CancellationToken cancellationToken = default) => Task.FromResult(new EmbeddingCoverage(0, 0, 0, 0, 0));
        public Task<System.Collections.Generic.IReadOnlyList<ClipEmbedding>> LoadAllEmbeddingsAsync(CancellationToken cancellationToken = default) => Task.FromResult<System.Collections.Generic.IReadOnlyList<ClipEmbedding>>(Array.Empty<ClipEmbedding>());
        public Task PrewarmAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StubScriptingService : IScriptingService
    {
        public Task<string> EvaluateAsync(string code, string input, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("ScriptingService must not be called from the remote API after U10.");
    }
}
