using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Aevatar.Tools.Cli.Hosting;

internal sealed class AppApiClient : IDisposable
{
    private readonly HttpClient _http;

    public AppApiClient(string baseUrl)
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };
    }

    public async Task<HttpResponseMessage> StreamDraftRunAsync(
        string scopeId,
        string prompt,
        string[]? workflowYamls,
        CancellationToken ct)
    {
        var body = new Dictionary<string, object> { ["prompt"] = prompt };
        if (workflowYamls is { Length: > 0 })
            body["workflowYamls"] = workflowYamls;

        var path = $"api/scopes/{Uri.EscapeDataString(scopeId)}/workflow/draft-run";
        return await PostSseAsync(path, body, ct);
    }

    public async Task<HttpResponseMessage> StreamChatAsync(
        string scopeId,
        string prompt,
        string? serviceId,
        string? sessionId,
        CancellationToken ct)
    {
        var body = new Dictionary<string, object> { ["prompt"] = prompt };
        if (!string.IsNullOrWhiteSpace(sessionId))
            body["sessionId"] = sessionId;

        var path = string.IsNullOrWhiteSpace(serviceId)
            ? $"api/scopes/{Uri.EscapeDataString(scopeId)}/invoke/chat:stream"
            : $"api/scopes/{Uri.EscapeDataString(scopeId)}/services/{Uri.EscapeDataString(serviceId)}/invoke/chat:stream";

        return await PostSseAsync(path, body, ct);
    }

    public async Task<JsonElement> ListServicesAsync(
        string scopeId,
        int take,
        CancellationToken ct)
    {
        var path = $"api/services?tenantId={Uri.EscapeDataString(scopeId)}&appId=default&namespace=default&take={take}";
        return await GetJsonAsync(path, ct);
    }

    public async Task<JsonElement> GetBindingsAsync(
        string serviceId,
        string? tenantId,
        CancellationToken ct)
    {
        var path = $"api/services/{Uri.EscapeDataString(serviceId)}/bindings";
        if (!string.IsNullOrWhiteSpace(tenantId))
            path += $"?tenantId={Uri.EscapeDataString(tenantId)}";
        return await GetJsonAsync(path, ct);
    }

    public async Task<JsonElement> GetActorTimelineAsync(
        string actorId,
        int take,
        CancellationToken ct)
    {
        var path = $"api/actors/{Uri.EscapeDataString(actorId)}/timeline?take={take}";
        return await GetJsonAsync(path, ct);
    }

    public async Task<bool> ProbeHealthAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            using var response = await _http.GetAsync("", cts.Token);
            if (!response.IsSuccessStatusCode)
                return false;
            var payload = await response.Content.ReadAsStringAsync(cts.Token);
            if (string.IsNullOrWhiteSpace(payload))
                return false;
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.TryGetProperty("status", out var s) &&
                   string.Equals(s.GetString(), "running", StringComparison.Ordinal);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task<HttpResponseMessage> PostSseAsync(
        string path,
        object body,
        CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(body);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"HTTP {(int)response.StatusCode}: {errorBody}");
        }
        return response;
    }

    private async Task<JsonElement> GetJsonAsync(string path, CancellationToken ct)
    {
        using var response = await _http.GetAsync(path, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {body}");
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }

    public void Dispose() => _http.Dispose();
}
