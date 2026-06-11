using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Clipthrough.Services;

public sealed class RemoteControlService : IRemoteControlService
{
    private readonly ISettingsService _settings;
    private readonly IClipStoreService _clipStore;
    // retained: DI registered in App.axaml.cs; cleanup deferred
    private readonly IScriptingService _scripting;
    // retained: DI registered in App.axaml.cs; cleanup deferred
    private readonly IAiTransformService _ai;
    private readonly SemaphoreSlim _sync = new(1, 1);
    private WebApplication? _app;
    private string? _baseUrl;

    // Per-IP auth-failure backoff. Tracks consecutive failures to slow brute-force
    // probes. State persists across server restarts (intentional — attacks restart too).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int Failures, DateTimeOffset LastFailAt)> _authFailures = new();

    private const int AuthBackoffThreshold = 5;
    private static readonly TimeSpan AuthBackoffDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan AuthFailureTtl = TimeSpan.FromMinutes(10);

    public RemoteControlService(
        ISettingsService settings,
        IClipStoreService clipStore,
        IScriptingService scripting,
        IAiTransformService ai)
    {
        _settings = settings;
        _clipStore = clipStore;
        _scripting = scripting;
        _ai = ai;
        _settings.SettingsChanged += OnSettingsChanged;
    }

    private void OnSettingsChanged(object? sender, AppSettings e)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await ApplySettingsAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError($"Remote API restart failed: {ex.Message}");
            }
        });
    }

    public bool IsRunning => _app is not null;

    public string? BaseUrl => _baseUrl;

    public async Task ApplySettingsAsync(CancellationToken cancellationToken = default)
    {
        await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = _settings.Current;
            var shouldRun = current.EnableRemoteApi && !string.IsNullOrWhiteSpace(current.RemoteApiToken);
            if (!shouldRun)
            {
                await StopCoreAsync().ConfigureAwait(false);
                return;
            }

            if (_app is not null)
            {
                await StopCoreAsync().ConfigureAwait(false);
            }

            await StartCoreAsync(current).ConfigureAwait(false);
        }
        finally
        {
            _sync.Release();
        }
    }

    private async Task StartCoreAsync(AppSettings settings)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();

        // ResolveBindAddress validates the configured address and logs a warning if a
        // non-loopback address was supplied (non-loopback is refused; see method comment).
        var bind = ResolveBindAddress(settings.RemoteApiBindAddress);

        builder.WebHost.ConfigureKestrel(o =>
        {
            // bind is always loopback after ResolveBindAddress enforces the restriction.
            o.ListenLocalhost(settings.RemoteApiPort);
        });

        builder.Services.AddOpenApi();

        var app = builder.Build();

        // /openapi and /docs require a valid bearer token (they expose API surface details
        // that should not be publicly readable on a shared machine).
        app.MapOpenApi();
        app.MapGet("/docs", () => Results.Content(BuildDocsHtml(), "text/html"));

        var token = settings.RemoteApiToken;
        var tokenBytes = Encoding.UTF8.GetBytes(token);
        var authFailures = _authFailures;

        app.Use(async (ctx, next) =>
        {
            // Only the health probe is exempt from authentication.
            var path = ctx.Request.Path.Value ?? string.Empty;
            if (path.Equals("/health", StringComparison.OrdinalIgnoreCase))
            {
                await next().ConfigureAwait(false);
                return;
            }

            var clientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";

            // Apply per-IP backoff before processing the credential to slow brute-force.
            if (authFailures.TryGetValue(clientIp, out var fs))
            {
                if (fs.Failures >= AuthBackoffThreshold && DateTimeOffset.UtcNow - fs.LastFailAt < AuthFailureTtl)
                {
                    await Task.Delay(AuthBackoffDelay, ctx.RequestAborted).ConfigureAwait(false);
                }
                else if (DateTimeOffset.UtcNow - fs.LastFailAt >= AuthFailureTtl)
                {
                    authFailures.TryRemove(clientIp, out _); // stale — reset
                }
            }

            if (string.IsNullOrWhiteSpace(token))
            {
                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await ctx.Response.WriteAsJsonAsync(new { error = "remote_api_token_not_configured" }).ConfigureAwait(false);
                return;
            }

            void RecordFail() => authFailures.AddOrUpdate(clientIp,
                _ => (1, DateTimeOffset.UtcNow),
                (_, prev) => (prev.Failures + 1, DateTimeOffset.UtcNow));

            if (!ctx.Request.Headers.TryGetValue("Authorization", out var header))
            {
                RecordFail();
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await ctx.Response.WriteAsJsonAsync(new { error = "unauthorized" }).ConfigureAwait(false);
                return;
            }

            var raw = header.ToString();
            const string prefix = "Bearer ";
            if (!raw.StartsWith(prefix, StringComparison.Ordinal))
            {
                RecordFail();
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await ctx.Response.WriteAsJsonAsync(new { error = "unauthorized" }).ConfigureAwait(false);
                return;
            }

            var presented = Encoding.UTF8.GetBytes(raw.Substring(prefix.Length));
            if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(presented, tokenBytes))
            {
                RecordFail();
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await ctx.Response.WriteAsJsonAsync(new { error = "unauthorized" }).ConfigureAwait(false);
                return;
            }

            authFailures.TryRemove(clientIp, out _); // clear failures on successful auth
            await next().ConfigureAwait(false);
        });

        app.MapGet("/health", () => Results.Ok(new { ok = true, version = typeof(RemoteControlService).Assembly.GetName().Version?.ToString() }));

        app.MapGet("/clips", async (string? query, int? limit, int? offset, CancellationToken ct) =>
        {
            var res = await _clipStore.SearchAsync(new ClipSearchFilters
            {
                SearchText = query ?? string.Empty,
                Limit = limit.GetValueOrDefault(100),
                Offset = offset.GetValueOrDefault(0),
            }, ct).ConfigureAwait(false);
            return Results.Ok(new
            {
                total = res.TotalMatchingCount,
                items = res.Items.Select(ToDto),
            });
        });

        app.MapGet("/clips/{id:long}", async (long id, CancellationToken ct) =>
        {
            var item = await _clipStore.GetByIdAsync(id, ct).ConfigureAwait(false);
            return item is null ? Results.NotFound() : Results.Ok(ToDto(item));
        });

        app.MapPost("/clips", async (CaptureRequest body, CancellationToken ct) =>
        {
            if (body is null || string.IsNullOrEmpty(body.Text))
            {
                return Results.BadRequest(new { error = "text required" });
            }
            var bytes = Encoding.UTF8.GetBytes(body.Text);
            var clip = await _clipStore.CaptureAsync(new ClipCaptureRequest
            {
                ContentBytes = bytes,
                ContentText = body.Text,
                ContentType = ContentType.Text,
                ContentFormat = ClipContentFormat.PlainText,
                SourceApp = body.SourceApp,
                IncrementExistingCopyCount = false,
            }, ct).ConfigureAwait(false);
            return clip is null ? Results.Problem("capture failed") : Results.Ok(ToDto(clip));
        });

        app.MapDelete("/clips/{id:long}", async (long id, CancellationToken ct) =>
        {
            await _clipStore.DeleteAsync(id, ct).ConfigureAwait(false);
            return Results.NoContent();
        });

        // POST /clips/{id}/transform is intentionally removed (KTD1 / U10).
        // The kind=script and kind=ai branches were remote code-execution surfaces;
        // kind=builtin was deterministic but the endpoint is eliminated wholesale
        // per the R3 requirement that the remote API exposes only read + capture.

        await app.StartAsync().ConfigureAwait(false);
        _app = app;
        _baseUrl = $"http://{bind}:{settings.RemoteApiPort}";
    }

    /// <summary>
    /// Resolves the configured bind address. Non-loopback addresses are refused because
    /// transport protection (TLS) is not implemented: binding to a non-loopback address
    /// would expose the API to the local network in cleartext, enabling credential theft
    /// and MITM. If non-loopback binding is required in a future release, add TLS support
    /// before relaxing this restriction.
    /// </summary>
    internal static System.Net.IPAddress ResolveBindAddress(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return System.Net.IPAddress.Loopback;
        }

        var trimmed = configured.Trim();

        if (trimmed.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("loopback", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("127.0.0.1", StringComparison.Ordinal)
            || trimmed.Equals("::1", StringComparison.Ordinal))
        {
            return System.Net.IPAddress.Loopback;
        }

        // Non-loopback addresses (*, 0.0.0.0, any routable IP) are refused.
        System.Diagnostics.Trace.TraceWarning(
            $"[RemoteControlService] Bind address '{trimmed}' is non-loopback; " +
            "defaulting to 127.0.0.1. Configure TLS before enabling non-loopback binds.");
        return System.Net.IPAddress.Loopback;
    }

    private static string BuildDocsHtml() => """
<!doctype html>
<html>
  <head>
    <title>Clipthrough API</title>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1" />
    <link rel="stylesheet" href="https://unpkg.com/swagger-ui-dist/swagger-ui.css" />
  </head>
  <body>
    <div id="swagger"></div>
    <script src="https://unpkg.com/swagger-ui-dist/swagger-ui-bundle.js"></script>
    <script>
      window.ui = SwaggerUIBundle({
        url: '/openapi/v1.json',
        dom_id: '#swagger',
        deepLinking: true,
        presets: [SwaggerUIBundle.presets.apis],
      });
    </script>
  </body>
</html>
""";

    private async Task StopCoreAsync()
    {
        if (_app is null)
        {
            return;
        }
        try
        {
            await _app.StopAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"Kestrel stop failed: {ex.Message}");
        }
        await _app.DisposeAsync().ConfigureAwait(false);
        _app = null;
        _baseUrl = null;
    }

    /// <summary>
    /// Maps a <see cref="ClipEntry"/> to a safe API response object.
    /// Sensitive clips have their <c>content</c> withheld to prevent exfiltration
    /// of passwords, tokens, and other PII over the remote API.
    /// </summary>
    private static object ToDto(ClipEntry c) => new
    {
        id = c.Id,
        // Withhold content for image clips (not text-serialisable) and for sensitive
        // clips (passwords, tokens, PII — must not be exposed over the remote API).
        content = (c.ContentType == ContentType.Image || c.IsSensitive) ? null : c.Content,
        contentType = c.ContentType.ToString(),
        format = c.ContentFormat.ToString(),
        sourceApp = c.SourceApp,
        sourceWindowTitle = c.SourceWindowTitle,
        sourceUrl = c.SourceUrl,
        isFavorite = c.IsFavorite,
        isSensitive = c.IsSensitive,
        isPinned = c.IsPinned,
        isPasted = c.IsPasted,
        copyCount = c.CopyCount,
        byteSize = c.ByteSize,
        capturedAt = c.CapturedAt,
    };

    public async ValueTask DisposeAsync()
    {
        _settings.SettingsChanged -= OnSettingsChanged;
        await StopCoreAsync().ConfigureAwait(false);
        _sync.Dispose();
    }

    private sealed class CaptureRequest
    {
        public string Text { get; set; } = string.Empty;
        public string? SourceApp { get; set; }
    }
}
