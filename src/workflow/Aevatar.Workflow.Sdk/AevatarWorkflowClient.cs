using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Aevatar.Workflow.Sdk.Contracts;
using Aevatar.Workflow.Sdk.Errors;
using Aevatar.Workflow.Sdk.Internal;
using Aevatar.Workflow.Sdk.Options;
using Aevatar.Workflow.Sdk.Streaming;
using Microsoft.Extensions.Options;

namespace Aevatar.Workflow.Sdk;

public sealed class AevatarWorkflowClient : IAevatarWorkflowClient
{
    private readonly HttpClient _httpClient;
    private readonly IWorkflowChatTransport _chatTransport;
    private readonly JsonSerializerOptions _jsonOptions;

    public AevatarWorkflowClient(
        HttpClient httpClient,
        IWorkflowChatTransport chatTransport,
        IOptions<AevatarWorkflowClientOptions>? options = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _chatTransport = chatTransport ?? throw new ArgumentNullException(nameof(chatTransport));

        var resolvedOptions = options?.Value ?? new AevatarWorkflowClientOptions();
        if (_httpClient.BaseAddress == null)
        {
            if (!Uri.TryCreate(resolvedOptions.BaseUrl, UriKind.Absolute, out var uri))
                throw new ArgumentException($"Invalid SDK base url: '{resolvedOptions.BaseUrl}'.", nameof(options));

            _httpClient.BaseAddress = uri;
        }

        _jsonOptions = resolvedOptions.JsonSerializerOptions ?? WorkflowSdkJson.CreateSerializerOptions();

        foreach (var (key, value) in resolvedOptions.DefaultHeaders)
        {
            if (_httpClient.DefaultRequestHeaders.Contains(key))
                continue;

            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation(key, value);
        }
    }

    public IAsyncEnumerable<WorkflowEvent> StartRunStreamAsync(
        ChatRunRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureNotBlank(request.Prompt, nameof(request.Prompt));
        EnsureNotBlank(request.ScopeId ?? string.Empty, nameof(request.ScopeId));
        EnsureNotBlank(request.Workflow ?? string.Empty, nameof(request.Workflow));

        return _chatTransport.StreamAsync(_httpClient, request, _jsonOptions, cancellationToken);
    }

    public async Task<WorkflowRunResult> RunToCompletionAsync(
        ChatRunRequest request,
        CancellationToken cancellationToken = default)
    {
        var events = new List<WorkflowEvent>();
        AevatarWorkflowException? runError = null;

        await foreach (var evt in StartRunStreamAsync(request, cancellationToken))
        {
            events.Add(evt);
            if (evt.IsRunError && runError == null)
                runError = AevatarWorkflowException.RunFailed(evt.Frame);
        }

        if (runError != null)
            throw runError;

        return new WorkflowRunResult(events);
    }

    public async Task<WorkflowResumeResponse> ResumeAsync(
        WorkflowResumeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureNotBlank(request.ScopeId, nameof(request.ScopeId));
        EnsureNotBlank(request.ServiceId, nameof(request.ServiceId));
        EnsureNotBlank(request.RunId, nameof(request.RunId));
        EnsureNotBlank(request.StepId, nameof(request.StepId));

        return await PostJsonAsync<object, WorkflowResumeResponse>(
            $"/api/scopes/{Uri.EscapeDataString(request.ScopeId)}/services/{Uri.EscapeDataString(request.ServiceId)}/runs/{Uri.EscapeDataString(request.RunId)}:resume",
            new
            {
                actorId = NormalizeOptional(request.ActorId),
                stepId = request.StepId,
                commandId = NormalizeOptional(request.CommandId),
                approved = request.Approved,
                userInput = NormalizeOptional(request.UserInput),
                metadata = request.Metadata,
            },
            cancellationToken);
    }

    public async Task<WorkflowSignalResponse> SignalAsync(
        WorkflowSignalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureNotBlank(request.ScopeId, nameof(request.ScopeId));
        EnsureNotBlank(request.ServiceId, nameof(request.ServiceId));
        EnsureNotBlank(request.RunId, nameof(request.RunId));
        EnsureNotBlank(request.SignalName, nameof(request.SignalName));

        return await PostJsonAsync<object, WorkflowSignalResponse>(
            $"/api/scopes/{Uri.EscapeDataString(request.ScopeId)}/services/{Uri.EscapeDataString(request.ServiceId)}/runs/{Uri.EscapeDataString(request.RunId)}:signal",
            new
            {
                actorId = NormalizeOptional(request.ActorId),
                signalName = request.SignalName,
                stepId = NormalizeOptional(request.StepId),
                commandId = NormalizeOptional(request.CommandId),
                payload = NormalizeOptional(request.Payload),
            },
            cancellationToken);
    }

    public async Task<IReadOnlyList<JsonElement>> GetWorkflowCatalogAsync(
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/workflow-catalog");
        using var response = await SendAsync(request, cancellationToken);
        var rawPayload = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw WorkflowSdkJson.BuildHttpException(
                response.StatusCode,
                rawPayload,
                $"Workflow catalog request failed with HTTP {(int)response.StatusCode}.");
        }

        if (string.IsNullOrWhiteSpace(rawPayload))
            return [];

        try
        {
            using var document = JsonDocument.Parse(rawPayload);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw AevatarWorkflowException.StreamPayload(
                    "Workflow catalog response is not a JSON array.",
                    rawPayload);
            }

            return document.RootElement.EnumerateArray().Select(x => x.Clone()).ToArray();
        }
        catch (JsonException ex)
        {
            throw AevatarWorkflowException.StreamPayload(
                "Failed to parse workflow catalog response payload.",
                rawPayload,
                ex);
        }
    }

    public async Task<JsonElement?> GetCapabilitiesAsync(
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/capabilities");
        using var response = await SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        var rawPayload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw WorkflowSdkJson.BuildHttpException(
                response.StatusCode,
                rawPayload,
                $"Capabilities request failed with HTTP {(int)response.StatusCode}.");
        }

        if (string.IsNullOrWhiteSpace(rawPayload))
            return null;

        try
        {
            using var document = JsonDocument.Parse(rawPayload);
            return document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw AevatarWorkflowException.StreamPayload(
                "Failed to parse capabilities response payload.",
                rawPayload,
                ex);
        }
    }

    public async Task<JsonElement?> GetWorkflowDetailAsync(
        string workflowName,
        CancellationToken cancellationToken = default)
    {
        EnsureNotBlank(workflowName, nameof(workflowName));

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/workflows/{Uri.EscapeDataString(workflowName)}");
        using var response = await SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        var rawPayload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw WorkflowSdkJson.BuildHttpException(
                response.StatusCode,
                rawPayload,
                $"Workflow detail request failed with HTTP {(int)response.StatusCode}.");
        }

        if (string.IsNullOrWhiteSpace(rawPayload))
            return null;

        try
        {
            using var document = JsonDocument.Parse(rawPayload);
            return document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw AevatarWorkflowException.StreamPayload(
                "Failed to parse workflow detail response payload.",
                rawPayload,
                ex);
        }
    }

    public async Task<JsonElement?> GetActorSnapshotAsync(
        string actorId,
        CancellationToken cancellationToken = default)
    {
        EnsureNotBlank(actorId, nameof(actorId));

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/actors/{Uri.EscapeDataString(actorId)}");
        using var response = await SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        var rawPayload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw WorkflowSdkJson.BuildHttpException(
                response.StatusCode,
                rawPayload,
                $"Actor snapshot request failed with HTTP {(int)response.StatusCode}.");
        }

        if (string.IsNullOrWhiteSpace(rawPayload))
            return null;

        try
        {
            using var document = JsonDocument.Parse(rawPayload);
            return document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw AevatarWorkflowException.StreamPayload(
                "Failed to parse actor snapshot response payload.",
                rawPayload,
                ex);
        }
    }

    public async Task<IReadOnlyList<JsonElement>> GetActorTimelineAsync(
        string actorId,
        int take = 200,
        CancellationToken cancellationToken = default)
    {
        EnsureNotBlank(actorId, nameof(actorId));
        if (take <= 0)
            throw AevatarWorkflowException.InvalidRequest("Parameter 'take' must be greater than zero.");

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/actors/{Uri.EscapeDataString(actorId)}/timeline?take={take}");
        using var response = await SendAsync(request, cancellationToken);
        var rawPayload = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw WorkflowSdkJson.BuildHttpException(
                response.StatusCode,
                rawPayload,
                $"Actor timeline request failed with HTTP {(int)response.StatusCode}.");
        }

        if (string.IsNullOrWhiteSpace(rawPayload))
            return [];

        try
        {
            using var document = JsonDocument.Parse(rawPayload);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw AevatarWorkflowException.StreamPayload(
                    "Timeline response is not a JSON array.",
                    rawPayload);
            }

            return document.RootElement.EnumerateArray().Select(x => x.Clone()).ToArray();
        }
        catch (JsonException ex)
        {
            throw AevatarWorkflowException.StreamPayload(
                "Failed to parse actor timeline response payload.",
                rawPayload,
                ex);
        }
    }

    private async Task<TResponse> PostJsonAsync<TRequest, TResponse>(
        string path,
        TRequest requestPayload,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(requestPayload, options: _jsonOptions),
        };
        using var response = await SendAsync(request, cancellationToken);
        var rawPayload = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw WorkflowSdkJson.BuildHttpException(
                response.StatusCode,
                rawPayload,
                $"Request to '{path}' failed with HTTP {(int)response.StatusCode}.");
        }

        try
        {
            var result = JsonSerializer.Deserialize<TResponse>(rawPayload, _jsonOptions);
            if (result == null)
            {
                throw AevatarWorkflowException.StreamPayload(
                    $"Response payload from '{path}' is empty or invalid.",
                    rawPayload);
            }

            return result;
        }
        catch (JsonException ex)
        {
            throw AevatarWorkflowException.StreamPayload(
                $"Failed to parse response payload from '{path}'.",
                rawPayload,
                ex);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw AevatarWorkflowException.Transport(
                $"Transport failure while calling '{request.RequestUri}'.",
                ex);
        }
    }

    private static void EnsureNotBlank(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw AevatarWorkflowException.InvalidRequest($"Parameter '{fieldName}' is required.");
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
