using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace ModRelay.App;

internal enum UpdateStatus
{
    Current,
    Available,
    Unavailable,
    Failed
}

internal sealed record UpdateCheckResult(
    UpdateStatus Status,
    Version CurrentVersion,
    Version? LatestVersion = null,
    string? ReleaseUrl = null,
    string? Message = null);

internal sealed class UpdateChecker(HttpClient httpClient, string? repositoryUrl, Version currentVersion)
{
    private readonly HttpClient _httpClient = httpClient;
    private readonly string? _repository = ParseRepository(repositoryUrl);
    private readonly Version _currentVersion = Normalize(currentVersion);

    public static UpdateChecker ForCurrentApp(HttpClient httpClient)
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(UpdateChecker).Assembly;
        var repositoryUrl = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "RepositoryUrl")?.Value;
        return new UpdateChecker(httpClient, repositoryUrl, assembly.GetName().Version ?? new Version(1, 0));
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (_repository is null)
            return new UpdateCheckResult(UpdateStatus.Unavailable, _currentVersion,
                Message: "This development build is not connected to a release repository.");

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://api.github.com/repos/{_repository}/releases/latest");
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("ModRelay", _currentVersion.ToString(3)));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await _httpClient.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
                return new UpdateCheckResult(UpdateStatus.Failed, _currentVersion,
                    Message: $"GitHub returned {(int)response.StatusCode} {response.ReasonPhrase}.");

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
            var root = json.RootElement;
            var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
            var releaseUrl = root.TryGetProperty("html_url", out var urlElement) ? urlElement.GetString() : null;
            if (!TryParseVersion(tag, out var latest) || !IsSafeReleaseUrl(releaseUrl))
                return new UpdateCheckResult(UpdateStatus.Failed, _currentVersion,
                    Message: "The release information was incomplete or invalid.");

            return latest > _currentVersion
                ? new UpdateCheckResult(UpdateStatus.Available, _currentVersion, latest, releaseUrl)
                : new UpdateCheckResult(UpdateStatus.Current, _currentVersion, latest, releaseUrl);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new UpdateCheckResult(UpdateStatus.Failed, _currentVersion, Message: "The update check timed out.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return new UpdateCheckResult(UpdateStatus.Failed, _currentVersion, Message: ex.Message);
        }
    }

    private static string? ParseRepository(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            return null;

        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2)
            return null;

        return $"{segments[0]}/{segments[1].Replace(".git", string.Empty, StringComparison.OrdinalIgnoreCase)}";
    }

    private static bool TryParseVersion(string? tag, out Version version)
    {
        var clean = tag?.Trim().TrimStart('v', 'V').Split('-', 2)[0];
        if (Version.TryParse(clean, out var parsed))
        {
            version = Normalize(parsed);
            return true;
        }

        version = new Version();
        return false;
    }

    private static bool IsSafeReleaseUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);

    private static Version Normalize(Version version) => new(
        version.Major,
        version.Minor,
        Math.Max(0, version.Build),
        Math.Max(0, version.Revision));
}
