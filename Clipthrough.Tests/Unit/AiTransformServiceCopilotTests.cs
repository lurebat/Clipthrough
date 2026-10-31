using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Models;
using Clipthrough.Services;
using Xunit;

namespace Clipthrough.Tests.Unit;

/// <summary>
/// F-32. The Copilot branch of AiTransformService was untested: a mutant that
/// sent the Copilot session token to the user-configured OpenAI base URL - any
/// host the settings name - survived every test.
/// </summary>
public partial class AiTransformServiceTests
{
    private const string EvilBase = "https://evil.test/v1";

    private sealed class SignedInCopilot : ICopilotAuthService
    {
        public bool IsSignedIn => true;
        public event Action? SignedInChanged { add { } remove { } }
        public Task<DeviceCodeResult> StartDeviceCodeFlowAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> PollForAuthorizationAsync(DeviceCodeResult deviceCode, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<string> GetTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult("cop-session-token");
        public void SignOut() { }
    }

    private static (AiTransformService Service, List<HttpRequestMessage> Requests, HttpClient Http) Copilot(AiProvider provider, string responseBody)
    {
        var settings = new StubSettingsService(AppSettings.Default with
        {
            EnableAi = true,
            AiProvider = provider,
            AiApiKey = "sk-user-key",
            AiBaseUrl = EvilBase,
            AiModel = "gpt-4o-mini",
        });
        var requests = new List<HttpRequestMessage>();
        var http = new HttpClient(new StubHandler((req, _) =>
        {
            requests.Add(req);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            });
        }));
        return (new AiTransformService(settings, new SignedInCopilot(), http), requests, http);
    }

    private const string ChatReply = @"{""choices"":[{""message"":{""role"":""assistant"",""content"":""DONE""}}]}";

    private static void AssertCopilotRequest(HttpRequestMessage request, string path)
    {
        Assert.Equal("https://api.githubcopilot.com" + path, request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("cop-session-token", request.Headers.Authorization.Parameter);
        Assert.True(request.Headers.Contains("Copilot-Integration-Id"));
        Assert.True(request.Headers.Contains("Editor-Version"));
    }

    [Fact]
    public async Task WithCopilot_TheSessionTokenGoesOnlyToCopilot_NotToTheConfiguredBaseUrl()
    {
        var (service, requests, http) = Copilot(AiProvider.Copilot, ChatReply);
        using (http)
        {
            Assert.Equal("DONE", await service.TransformAsync("sys", "input"));

            var request = Assert.Single(requests);
            AssertCopilotRequest(request, "/chat/completions");
        }
    }

    [Fact]
    public async Task WithOpenAi_NoCopilotTokenOrHeadersAreSent_EvenWhenSignedIn()
    {
        var (service, requests, http) = Copilot(AiProvider.OpenAi, ChatReply);
        using (http)
        {
            await service.TransformAsync("sys", "input");

            var request = Assert.Single(requests);
            Assert.Equal(EvilBase + "/chat/completions", request.RequestUri!.ToString());
            Assert.Equal("sk-user-key", request.Headers.Authorization!.Parameter);
            Assert.False(request.Headers.Contains("Copilot-Integration-Id"));
        }
    }

    [Fact]
    public async Task WithCopilot_DescribingAnImage_GoesToCopilot()
    {
        var (service, requests, http) = Copilot(AiProvider.Copilot, ChatReply);
        using (http)
        {
            Assert.Equal("DONE", await service.DescribeImageAsync("describe", [1, 2, 3], "image/png"));

            AssertCopilotRequest(Assert.Single(requests), "/chat/completions");
        }
    }

    [Fact]
    public async Task WithCopilot_EditingAnImage_GoesToCopilot_AndReturnsTheDecodedImage()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 9, 9 };
        var (service, requests, http) = Copilot(AiProvider.Copilot, $@"{{""data"":[{{""b64_json"":""{Convert.ToBase64String(png)}""}}]}}");
        using (http)
        {
            Assert.Equal(png, await service.EditImageAsync("make it blue", [1, 2, 3], "image/png"));

            AssertCopilotRequest(Assert.Single(requests), "/images/edits");
        }
    }
}