using System.Net;
using System.Text;
using lumibelle.Services.AI;

namespace Lumibelle.Tests;

public sealed class ComfyAccessDetectionTests
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(301)] [InlineData(302)] [InlineData(303)] [InlineData(307)] [InlineData(308)]
    public void AccessLoginRedirectsAreRecognizedWithoutEchoingTheRedirect(int status)
    {
        using var response = new HttpResponseMessage((HttpStatusCode)status);
        response.Headers.Location = new("https://myteam.cloudflareaccess.com/cdn-cgi/access/login/comfy.example?token=do-not-echo");
        var error = ComfyAccessResponses.Classify(response, new("https://comfy.example/object_info?private=do-not-echo"), false);
        Assert.NotNull(error); Assert.True(error.NeedsCredentials);
        Assert.Equal(ComfyAccessFailureKind.AccessLogin, error.Kind);
        Assert.False(error.CredentialsWereSent); Assert.False(error.DefinitelyNotSubmitted);
        Assert.Contains("/object_info", error.Message);
        Assert.DoesNotContain("do-not-echo", error.ToString()); Assert.DoesNotContain("myteam", error.ToString());
    }

    [Theory]
    [InlineData("https://team.cloudflareaccess.com.evil.test/cdn-cgi/access/login/app")]
    [InlineData("https://evil.test/cdn-cgi/access/login/app")]
    [InlineData("https://team.cloudflareaccess.com/not-access/login")]
    [InlineData("https://team.cloudflareaccess.com/cdn-cgi/access/login-spoof")]
    [InlineData("https://team.cloudflareaccess.com/other?next=/cdn-cgi/access/login/app")]
    [InlineData("http://team.cloudflareaccess.com/cdn-cgi/access/login/app")]
    [InlineData("https://user:password@team.cloudflareaccess.com/cdn-cgi/access/login/app")]
    [InlineData("https://comfy.example/new-path")]
    public void UnrelatedRedirectsNeverRequestAnAccessToken(string location)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.Redirect);
        response.Headers.Location = new(location);
        response.Headers.TryAddWithoutValidation("CF-Ray", "cdn-signal-only");
        var error = ComfyAccessResponses.Classify(response, new("https://comfy.example/object_info"), false);
        Assert.NotNull(error); Assert.Equal(ComfyAccessFailureKind.Redirect, error.Kind);
        Assert.False(error.NeedsCredentials);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, true)]
    public void SameOriginLoginPathNeedsACorroboratingCloudflareSignal(bool cloudflare, bool expected)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.Redirect);
        response.Headers.Location = new("/cdn-cgi/access/login/app", UriKind.Relative);
        if (cloudflare) response.Headers.TryAddWithoutValidation("CF-Ray", "example");
        Assert.Equal(expected, ComfyAccessResponses.Classify(response, new("https://comfy.example/object_info"), false)!.NeedsCredentials);
    }

    [Theory]
    [InlineData(401, false, false)] [InlineData(403, false, false)]
    [InlineData(401, true, true)] [InlineData(403, true, true)]
    public void BareDenialDoesNotPretendThatOrdinaryCloudflareHostingIsAccess(int status, bool savedToken, bool prompt)
    {
        using var response = new HttpResponseMessage((HttpStatusCode)status);
        response.Headers.TryAddWithoutValidation("Server", "cloudflare");
        response.Headers.TryAddWithoutValidation("CF-Ray", "example");
        var error = ComfyAccessResponses.Classify(response, new("https://comfy.example/system_stats"), savedToken)!;
        Assert.Equal(prompt, error.NeedsCredentials);
        Assert.Equal(savedToken, error.CredentialsWereSent);
        Assert.True(error.DefinitelyNotSubmitted);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void BrowserChallengesAreNotConfusedWithAccessEvenWithSavedCredentials(bool savedToken)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
        response.Headers.TryAddWithoutValidation("cf-mitigated", "challenge");
        response.Headers.Location = new("https://team.cloudflareaccess.com/cdn-cgi/access/login/app");
        var error = ComfyAccessResponses.Classify(response, new("https://comfy.example/view"), savedToken)!;
        Assert.Equal(ComfyAccessFailureKind.BrowserChallenge, error.Kind);
        Assert.False(error.NeedsCredentials); Assert.Contains("WAF", error.Message);
    }

    [Fact]
    public void SuccessfulCloudflareResponsesAndUnrelatedApiErrorsStayUnchanged()
    {
        foreach (var status in new[] { 101, 200, 206, 304, 400, 404, 500 })
        {
            using var response = new HttpResponseMessage((HttpStatusCode)status);
            response.Headers.TryAddWithoutValidation("Server", "cloudflare");
            response.Headers.TryAddWithoutValidation("CF-Ray", "example");
            Assert.Null(ComfyAccessResponses.Classify(response, new("https://comfy.example/view"), false));
        }
    }

    [Fact]
    public void HtmlIsDiagnosedButNeitherClassifiedAsAccessNorSafeToResubmit()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("login form", Encoding.UTF8, "text/html") };
        var error = ComfyAccessResponses.Classify(response, new("https://comfy.example/prompt"), true)!;
        Assert.Equal(ComfyAccessFailureKind.UnexpectedHtml, error.Kind);
        Assert.False(error.NeedsCredentials); Assert.False(error.DefinitelyNotSubmitted);
    }

    [Fact]
    public async Task RealHandlerPropagatesDetectionAndRemovesSecretHeaders()
    {
        using var f = new ComfyAccessFixture();
        await f.Store.SaveAsync("https://comfy.example", "id", "private-secret", 0, Ct);
        using var http = ComfyAccessTestHttp.Client(f.Store, (request, _) =>
        {
            ComfyAccessTestHttp.AssertPair(request, "id", "private-secret");
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new("https://team.cloudflareaccess.com/cdn-cgi/access/login/app?secret=private-secret");
            return Task.FromResult(response);
        });
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://comfy.example/object_info");
        var error = await Assert.ThrowsAsync<ComfyAccessException>(() => http.SendAsync(request, Ct));
        Assert.True(error.NeedsCredentials); Assert.True(error.CredentialsWereSent);
        ComfyAccessTestHttp.AssertNoPair(request);
        Assert.DoesNotContain("private-secret", error.ToString());
    }

    [Fact]
    public async Task CredentialFailureBeforeDispatchCarriesNonSubmissionEvidence()
    {
        using var f = new ComfyAccessFixture();
        await f.Store.SaveAsync("https://comfy.example", "id", "secret", 0, Ct);
        f.Protector.FailUnprotect = true;
        var calls = 0;
        using var http = ComfyAccessTestHttp.Client(f.Store, (_, _) =>
        { calls++; return Task.FromResult(ComfyAccessTestHttp.Json()); });
        var error = await Assert.ThrowsAsync<ComfyAccessException>(() => http.PostAsync("https://comfy.example/prompt", new StringContent("{}"), Ct));
        Assert.True(error.RequestWasNotSent); Assert.True(error.DefinitelyNotSubmitted);
        Assert.True(error.NeedsCredentials); Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ProbePreservesTheTypedChallengeForItsCaller()
    {
        using var f = new ComfyAccessFixture();
        var clients = new ComfyAccessTestClients(() => ComfyAccessTestHttp.Client(f.Store, (_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new("https://team.cloudflareaccess.com/cdn-cgi/access/login/app");
            return Task.FromResult(response);
        }));
        var result = await new ComfyAccessProbe(clients, new UnusedSockets()).CheckAsync("https://comfy.example", Ct);
        Assert.False(result.HttpSucceeded); Assert.True(result.NeedsCredentials);
        Assert.Equal(ComfyAccessFailureKind.AccessLogin, result.FailureKind);
    }

    [Fact]
    public async Task WebSocketOnlyAccessChallengeSurvivesTheRealWebSocketExceptionWrapper()
    {
        using var f = new ComfyAccessFixture();
        var clients = new ComfyAccessTestClients(() => ComfyAccessTestHttp.Client(f.Store, (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/system_stats")
                return Task.FromResult(ComfyAccessTestHttp.Json());
            Assert.Equal("/ws", request.RequestUri.AbsolutePath);
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new("https://team.cloudflareaccess.com/cdn-cgi/access/login/app");
            return Task.FromResult(response);
        }));
        var probe = new ComfyAccessProbe(clients, new AccessComfyWebSocketFactory(clients));
        var result = await probe.CheckAsync("https://comfy.example", Ct);
        Assert.True(result.HttpSucceeded); Assert.False(result.WebSocketSucceeded);
        Assert.True(result.NeedsCredentials); Assert.Equal(ComfyAccessFailureKind.AccessLogin, result.FailureKind);
    }

    private sealed class UnusedSockets : IComfyWebSocketFactory
    {
        public IComfyWebSocket Create() => throw new InvalidOperationException("HTTP challenge must stop this probe before WebSocket creation.");
    }
}
