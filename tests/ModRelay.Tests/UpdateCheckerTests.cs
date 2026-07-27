using System.Net;
using System.Text;
using ModRelay.App;

namespace ModRelay.Tests;

public sealed class UpdateCheckerTests
{
    [Fact]
    public async Task NewerOfficialRelease_IsReportedAsAvailable()
    {
        using var http = new HttpClient(new JsonHandler("""
            { "tag_name": "v1.3.0", "html_url": "https://github.com/example/ModRelay/releases/tag/v1.3.0" }
            """));
        var checker = new UpdateChecker(http, "https://github.com/example/ModRelay", new Version(1, 2, 0));

        var result = await checker.CheckAsync();

        Assert.Equal(UpdateStatus.Available, result.Status);
        Assert.Equal(new Version(1, 3, 0, 0), result.LatestVersion);
    }

    [Fact]
    public async Task SameRelease_IsReportedAsCurrent()
    {
        using var http = new HttpClient(new JsonHandler("""
            { "tag_name": "v1.2.0", "html_url": "https://github.com/example/ModRelay/releases/tag/v1.2.0" }
            """));
        var checker = new UpdateChecker(http, "https://github.com/example/ModRelay", new Version(1, 2, 0));

        var result = await checker.CheckAsync();

        Assert.Equal(UpdateStatus.Current, result.Status);
    }

    [Fact]
    public async Task DevelopmentBuildWithoutRepository_DoesNotContactNetwork()
    {
        var handler = new JsonHandler("{}", failIfCalled: true);
        using var http = new HttpClient(handler);
        var checker = new UpdateChecker(http, null, new Version(1, 0));

        var result = await checker.CheckAsync();

        Assert.Equal(UpdateStatus.Unavailable, result.Status);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task NonGithubReleaseLink_IsRejected()
    {
        using var http = new HttpClient(new JsonHandler("""
            { "tag_name": "v9.0.0", "html_url": "https://example.com/untrusted.exe" }
            """));
        var checker = new UpdateChecker(http, "https://github.com/example/ModRelay", new Version(1, 0));

        var result = await checker.CheckAsync();

        Assert.Equal(UpdateStatus.Failed, result.Status);
    }

    private sealed class JsonHandler(string json, bool failIfCalled = false) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            if (failIfCalled)
                throw new InvalidOperationException("Network should not be contacted.");

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
