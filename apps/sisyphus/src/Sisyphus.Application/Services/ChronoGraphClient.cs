using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Sisyphus.Application.Services;

/// <summary>
/// Accesses ChronoGraph via NyxId Service Proxy:
///   ANY /api/v1/proxy/{serviceId}/{*path}
/// </summary>
public sealed class ChronoGraphClient(
    HttpClient httpClient,
    NyxIdTokenService tokenService,
    IOptions<NyxIdOptions> options)
{
    private readonly string _serviceId = options.Value.ChronoGraphServiceId;
    private string ProxyPath(string path) => $"/api/v1/proxy/{_serviceId}/{path.TrimStart('/')}";

    public async Task<string> CreateGraphAsync(string name, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ProxyPath("api/graphs"));
        request.Content = JsonContent.Create(new { name });
        await tokenService.SetAuthHeaderAsync(request, ct);

        var response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return doc.RootElement.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Graph creation response missing id");
    }

    public async Task DeleteGraphAsync(string graphId, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, ProxyPath($"api/graphs/{graphId}"));
        await tokenService.SetAuthHeaderAsync(request, ct);

        var response = await httpClient.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return; // Idempotent
        response.EnsureSuccessStatusCode();
    }
}
