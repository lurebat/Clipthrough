using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Models;
using Clipthrough.Services;
using Xunit;

namespace Clipthrough.Tests.Unit;

public class AiTransformServiceTests
{
    [Fact]
    public async Task ThrowsWhenNoApiKeyConfigured()
    {
        var oldEnv = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
        try
        {
            var settings = new StubSettingsService(AppSettings.Default with { EnableAi = true });
            using var http = new HttpClient(new StubHandler((_, _) => throw new InvalidOperationException("should not send")));
            var service = new AiTransformService(settings, http);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.TransformAsync("sys", "input"));
            Assert.Contains("not configured", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", oldEnv);
        }
    }

    [Fact]
    public async Task SendsChatCompletionAndReturnsAssistantContent()
    {
        var settings = new StubSettingsService(AppSettings.Default with
        {
            EnableAi = true,
            AiApiKey = "sk-test",
            AiBaseUrl = "https://example.test/v1",
            AiModel = "gpt-4o-mini",
        });

        HttpRequestMessage? captured = null;
        var handler = new StubHandler((req, _) =>
        {
            captured = req;
            var body = @"{""choices"":[{""message"":{""role"":""assistant"",""content"":""HELLO""}}]}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        });

        using var http = new HttpClient(handler);
        var service = new AiTransformService(settings, http);

        var result = await service.TransformAsync("uppercase", "hello");
        Assert.Equal("HELLO", result);
        Assert.NotNull(captured);
        Assert.Equal("https://example.test/v1/chat/completions", captured!.RequestUri!.ToString());
        Assert.Equal("Bearer", captured.Headers.Authorization!.Scheme);
        Assert.Equal("sk-test", captured.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task SurfacesHttpErrorsWithBody()
    {
        var settings = new StubSettingsService(AppSettings.Default with
        {
            EnableAi = true,
            AiApiKey = "sk-test",
        });
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("invalid key"),
        }));

        using var http = new HttpClient(handler);
        var service = new AiTransformService(settings, http);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => service.TransformAsync("sys", "input"));
        Assert.Contains("401", ex.Message);
        Assert.Contains("invalid key", ex.Message);
    }

    private sealed class StubSettingsService : ISettingsService
    {
        public StubSettingsService(AppSettings current) => Current = current;
        public AppSettings Current { get; private set; }
        public bool HasSavedSettings => true;
        public event EventHandler<AppSettings>? SettingsChanged { add { } remove { } }
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            Current = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;
        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => _send = send;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _send(request, cancellationToken);
    }
}
