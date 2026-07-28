using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace ModRelay.Core;

public enum InstallOutcome
{
    Imported,
    Accepted,

    /// <summary>Penumbra did not answer - game not running, or the HTTP API is switched off.</summary>
    PenumbraUnreachable,

    Failed
}

public sealed record InstallResult(InstallOutcome Outcome, string ModName, string? Message = null);

/// <summary>
/// Hands packages to Penumbra's supported external HTTP API. Penumbra performs the
/// actual import for every format, including .pmp and .pcp.
/// </summary>
public sealed class PenumbraClient(HttpClient httpClient, Func<AppConfig> configProvider) : IDisposable
{
    private const string BaseUrl = "http://localhost:42069/api";
    private static readonly JsonSerializerOptions PenumbraJsonOptions = new()
    {
        // Penumbra's EmbedIO request binding expects this contract's exact "Path" casing.
        PropertyNamingPolicy = null
    };
    private readonly HttpClient _httpClient = httpClient;
    private readonly Func<AppConfig> _config = configProvider;
    private readonly SemaphoreSlim _installGate = new(1, 1);

    public async Task<bool> IsReachableAsync(CancellationToken cancellationToken = default) =>
        await GetModListAsync(cancellationToken) is not null;

    public async Task<InstallResult> InstallAsync(string modPath, CancellationToken cancellationToken = default)
    {
        await _installGate.WaitAsync(cancellationToken);
        try
        {
            var fallbackName = Path.GetFileNameWithoutExtension(modPath);
            if (!File.Exists(modPath))
                return new InstallResult(InstallOutcome.Failed, fallbackName, "The source package no longer exists.");

            if (await GetModListAsync(cancellationToken) is null)
            {
                Log.Warn($"Penumbra is not reachable on port 42069; {fallbackName} stays queued.");
                return new InstallResult(InstallOutcome.PenumbraUnreachable, fallbackName);
            }

            try
            {
                using var response = await PostAsync("installmod", new { Path = modPath }, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    Log.Error($"Penumbra rejected {modPath}: {(int)response.StatusCode} {response.ReasonPhrase}. {body}");
                    return new InstallResult(InstallOutcome.Failed, fallbackName,
                        $"{(int)response.StatusCode} {response.ReasonPhrase}");
                }

                Log.Info($"Penumbra accepted {modPath} for import.");
                return new InstallResult(InstallOutcome.Accepted, fallbackName,
                    "Penumbra accepted the package. Its HTTP API does not expose package-specific completion status.");
            }
            catch (HttpRequestException ex)
            {
                Log.Warn($"Could not reach Penumbra while importing {modPath}.", ex);
                return new InstallResult(InstallOutcome.PenumbraUnreachable, fallbackName);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                Log.Warn($"Penumbra timed out while importing {modPath}.", ex);
                return new InstallResult(InstallOutcome.PenumbraUnreachable, fallbackName, "The request timed out.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Error($"Unexpected error importing {modPath}.", ex);
                return new InstallResult(InstallOutcome.Failed, fallbackName, ex.Message);
            }
        }
        finally
        {
            _installGate.Release();
        }
    }

    private async Task<Dictionary<string, string>?> GetModListAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            return await _httpClient.GetFromJsonAsync<Dictionary<string, string>>($"{BaseUrl}/mods", timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            Log.Warn("Penumbra's mod list could not be read.", ex);
            return null;
        }
    }

    private async Task<HttpResponseMessage> PostAsync(string route, object payload, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_config().PenumbraTimeoutSeconds));
        var json = JsonSerializer.Serialize(payload, payload.GetType(), PenumbraJsonOptions);
        Log.Debug($"Posting Penumbra /{route} with case-sensitive Path contract.");
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await _httpClient.PostAsync($"{BaseUrl}/{route}", content, timeout.Token);
    }

    public void Dispose() => _installGate.Dispose();
}
