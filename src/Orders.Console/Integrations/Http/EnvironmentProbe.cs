using System.Diagnostics;
using System.Text.Json;

namespace Orders.Console.Integrations.Http;

/// <summary>Calls /health/ready and /api/version on the prod and staging hosts.</summary>
public sealed class EnvironmentProbe(HttpClient http, ConsoleOptions options) : IEnvironmentProbe
{
    public async Task<EnvironmentHealth> CheckAsync(string environment, CancellationToken ct)
    {
        var baseUrl = environment == "prod" ? options.ProdUrl : options.StagingUrl;
        string? version = null, commit = null;
        var watch = Stopwatch.StartNew();
        try
        {
            using var ready = await http.GetAsync($"{baseUrl}/health/ready", ct);
            watch.Stop();
            var latency = watch.Elapsed;
            try
            {
                using var response = await http.GetAsync($"{baseUrl}/api/version", ct);
                if (response.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                    version = doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
                    commit = doc.RootElement.TryGetProperty("commit", out var c) ? c.GetString() : null;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
            {
                // Version is nice to have; readiness is what matters.
            }

            var detail = ready.IsSuccessStatusCode ? "Ready" : $"Not ready ({(int)ready.StatusCode})";
            return new EnvironmentHealth(environment, baseUrl, ready.IsSuccessStatusCode, detail, version, commit, latency);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new EnvironmentHealth(environment, baseUrl, false, $"Unreachable: {ex.Message}", null, null, null);
        }
    }
}
