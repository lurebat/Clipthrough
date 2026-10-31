using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Services;
using Xunit;

namespace Clipthrough.Tests.Unit;

/// <summary>
/// F-31. The GitHub token behind Copilot is persisted with DPAPI and removed on
/// sign-out, but the only constructor that persisted wrote to the real user
/// profile, so nothing tested it: mutants that wrote it unprotected, or left it
/// on disk after sign-out, survived every test.
/// </summary>
public sealed class CopilotAuthServicePersistenceTests : IDisposable
{
    private const string GitHubToken = "gho_NOT-A-REAL-TOKEN-for-tests";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ct-copilot-" + Guid.NewGuid().ToString("N"));
    private string TokenPath => Path.Combine(_dir, "copilot-token.bin");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* test cleanup */ }
    }

    private sealed class Responder(Func<HttpRequestMessage, string> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(respond(request)) });
        }
    }

    private void SeedProtectedToken(string token)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(TokenPath, new FakeDataProtectionService().Protect(Encoding.UTF8.GetBytes(token)));
    }

    [Fact]
    public async Task SigningIn_PersistsTheTokenProtected_AndANewInstanceIsSignedIn()
    {
        var protector = new FakeDataProtectionService();
        using var http = new HttpClient(new Responder(_ => $"{{\"access_token\":\"{GitHubToken}\"}}"));
        var auth = new CopilotAuthService(http, protector, TokenPath);

        var signedIn = await auth.PollForAuthorizationAsync(new DeviceCodeResult("dc", "UC", "https://github.com/login/device", 60, 0));

        Assert.True(signedIn);
        var bytes = File.ReadAllBytes(TokenPath);
        Assert.NotEqual(Encoding.UTF8.GetBytes(GitHubToken), bytes);
        Assert.DoesNotContain(GitHubToken, Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.Equal(GitHubToken, Encoding.UTF8.GetString(protector.Unprotect(bytes)));

        using var http2 = new HttpClient(new Responder(_ => "{}"));
        Assert.True(new CopilotAuthService(http2, protector, TokenPath).IsSignedIn);
    }

    [Fact]
    public async Task APersistedToken_IsTheOneSentForTheCopilotToken()
    {
        SeedProtectedToken(GitHubToken);
        var handler = new Responder(_ => "{\"token\":\"cop-session\",\"expires_at\":4102444800}");
        using var http = new HttpClient(handler);
        var auth = new CopilotAuthService(http, new FakeDataProtectionService(), TokenPath);

        Assert.Equal("cop-session", await auth.GetTokenAsync());
        Assert.Equal("token", handler.Last!.Headers.Authorization!.Scheme);
        Assert.Equal(GitHubToken, handler.Last.Headers.Authorization.Parameter);
    }

    [Fact]
    public void SigningOut_DeletesTheToken_AndANewInstanceIsSignedOut()
    {
        SeedProtectedToken(GitHubToken);
        using var http = new HttpClient(new Responder(_ => "{}"));
        var auth = new CopilotAuthService(http, new FakeDataProtectionService(), TokenPath);
        Assert.True(auth.IsSignedIn);

        auth.SignOut();

        Assert.False(File.Exists(TokenPath));
        Assert.False(new CopilotAuthService(http, new FakeDataProtectionService(), TokenPath).IsSignedIn);
    }

    [Fact]
    public void ATokenThatCannotBeUnprotected_SignsOutAndIsDeleted()
    {
        SeedProtectedToken(GitHubToken);
        using var http = new HttpClient(new Responder(_ => "{}"));

        var auth = new CopilotAuthService(http, new FailingUnprotectDataProtectionService(), TokenPath);

        Assert.False(auth.IsSignedIn);
        Assert.False(File.Exists(TokenPath));
    }
}