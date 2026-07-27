using System.Net;
using System.Text;
using ModRelay.Core;

namespace ModRelay.Tests;

public sealed class PenumbraClientTests
{
    [Fact]
    public async Task Pmp_IsHandedToPenumbraInstallEndpointWithoutLocalExtraction()
    {
        using var temp = new TestDirectory();
        var package = temp.File("native.pmp");
        File.WriteAllText(package, "package");
        var handler = new PenumbraHandler();
        using var http = new HttpClient(handler);
        var config = new AppConfig { PenumbraPort = 42069, PenumbraTimeoutSeconds = 2 };
        var client = new PenumbraClient(http, () => config);

        var result = await client.InstallAsync(package);

        Assert.Equal(InstallOutcome.Imported, result.Outcome);
        Assert.Equal("/api/installmod", handler.PostedUri?.AbsolutePath);
        Assert.Contains("\"Path\":", handler.PostedBody);
        Assert.DoesNotContain("\"path\":", handler.PostedBody);
        Assert.Contains(package.Replace("\\", "\\\\"), handler.PostedBody);
        Assert.True(File.Exists(package));
    }

    [Fact]
    public async Task MalformedPenumbraResponse_IsTreatedAsUnavailableInsteadOfCrashing()
    {
        using var http = new HttpClient(new StaticHandler("not json"));
        var config = new AppConfig { PenumbraPort = 42069 };
        var client = new PenumbraClient(http, () => config);

        var reachable = await client.IsReachableAsync();

        Assert.False(reachable);
    }

    private sealed class PenumbraHandler : HttpMessageHandler
    {
        private int _modRequests;

        public Uri? PostedUri { get; private set; }
        public string PostedBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/api/mods")
            {
                _modRequests++;
                var json = _modRequests == 1 ? "{}" : "{\"native\":\"Native Mod\"}";
                return Json(json);
            }

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/api/installmod")
            {
                PostedUri = request.RequestUri;
                PostedBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                return Json("{}");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(value, Encoding.UTF8, "application/json")
        };
    }

    private sealed class StaticHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }
}
