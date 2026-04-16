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
    private readonly IScriptingService _scripting;
    private readonly IAiTransformService _ai;
    private readonly SemaphoreSlim _sync = new(1, 1);
    private WebApplication? _app;
    private string? _baseUrl;

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
        _ = ApplySettingsAsync();
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
        builder.WebHost.ConfigureKestrel(o =>
        {
            o.ListenLocalhost(settings.RemoteApiPort);
        });

        var app = builder.Build();

        var token = settings.RemoteApiToken;
        var tokenBytes = Encoding.UTF8.GetBytes(token);
        app.Use(async (ctx, next) =>
        {
            if (!ctx.Request.Headers.TryGetValue("Authorization", out var header))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await ctx.Response.WriteAsJsonAsync(new { error = "unauthorized" }).ConfigureAwait(false);
                return;
            }
            var raw = header.ToString();
            const string prefix = "Bearer ";
            if (!raw.StartsWith(prefix, StringComparison.Ordinal))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await ctx.Response.WriteAsJsonAsync(new { error = "unauthorized" }).ConfigureAwait(false);
                return;
            }
            var presented = Encoding.UTF8.GetBytes(raw.Substring(prefix.Length));
            if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(presented, tokenBytes))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await ctx.Response.WriteAsJsonAsync(new { error = "unauthorized" }).ConfigureAwait(false);
                return;
            }
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

        app.MapPost("/clips/{id:long}/transform", async (long id, TransformRequest body, CancellationToken ct) =>
        {
            if (body is null || string.IsNullOrEmpty(body.Kind))
            {
                return Results.BadRequest(new { error = "kind required" });
            }
            var item = await _clipStore.GetByIdAsync(id, ct).ConfigureAwait(false);
            if (item is null)
            {
                return Results.NotFound();
            }
            var source = item.Content ?? string.Empty;
            string transformed;
            try
            {
                transformed = body.Kind.ToLowerInvariant() switch
                {
                    "builtin" => TextTransformationService.Apply(ParseBuiltin(body.Name), source),
                    "script" => await _scripting.EvaluateAsync(body.Code ?? string.Empty, source, ct).ConfigureAwait(false),
                    "ai" => await _ai.TransformAsync(body.Prompt ?? string.Empty, source, ct).ConfigureAwait(false),
                    _ => source,
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }

            var captured = await _clipStore.CaptureAsync(new ClipCaptureRequest
            {
                ContentBytes = Encoding.UTF8.GetBytes(transformed),
                ContentText = transformed,
                ContentType = ContentType.Text,
                ContentFormat = ClipContentFormat.PlainText,
                SourceApp = item.SourceApp,
                IncrementExistingCopyCount = false,
            }, ct).ConfigureAwait(false);
            return captured is null ? Results.Problem("capture failed") : Results.Ok(ToDto(captured));
        });

        await app.StartAsync().ConfigureAwait(false);
        _app = app;
        _baseUrl = $"http://127.0.0.1:{settings.RemoteApiPort}";
    }

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
        catch { }
        await _app.DisposeAsync().ConfigureAwait(false);
        _app = null;
        _baseUrl = null;
    }

    private static TextTransformation ParseBuiltin(string? name)
        => Enum.TryParse<TextTransformation>(name, ignoreCase: true, out var t) ? t : TextTransformation.None;

    private static object ToDto(ClipEntry c) => new
    {
        id = c.Id,
        content = c.ContentType == ContentType.Image ? null : c.Content,
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

    private sealed class TransformRequest
    {
        public string Kind { get; set; } = string.Empty;
        public string? Name { get; set; }
        public string? Code { get; set; }
        public string? Prompt { get; set; }
    }
}
