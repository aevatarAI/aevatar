using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;
using Aevatar.Tools.Cli.Studio.Application.Abstractions;
using Aevatar.Tools.Cli.Studio.Application.Contracts;

namespace Aevatar.Tools.Cli.Studio.Application.Services;

public sealed class ExecutionService
{
    private const string BackendClientName = "AppBridgeBackend";

    private readonly IStudioWorkspaceStore _store;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IStudioBackendRequestAuthSnapshotProvider? _authSnapshotProvider;

    public ExecutionService(
        IStudioWorkspaceStore store,
        IHttpClientFactory httpClientFactory,
        IStudioBackendRequestAuthSnapshotProvider? authSnapshotProvider = null)
    {
        _store = store;
        _httpClientFactory = httpClientFactory;
        _authSnapshotProvider = authSnapshotProvider;
    }

    public async Task<IReadOnlyList<ExecutionSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        var records = await _store.ListExecutionsAsync(cancellationToken);
        return records
            .OrderByDescending(record => record.StartedAtUtc)
            .Select(ToSummary)
            .ToList();
    }

    public async Task<ExecutionDetail?> GetAsync(string executionId, CancellationToken cancellationToken = default)
    {
        var record = await _store.GetExecutionAsync(executionId, cancellationToken);
        return record is null ? null : ToDetail(record);
    }

    public async Task<ExecutionDetail> StartAsync(StartExecutionRequest request, CancellationToken cancellationToken = default)
    {
        var settings = await _store.GetSettingsAsync(cancellationToken);
        var runtimeBaseUrl = string.IsNullOrWhiteSpace(request.RuntimeBaseUrl)
            ? settings.RuntimeBaseUrl
            : request.RuntimeBaseUrl.Trim().TrimEnd('/');

        var executionId = Guid.NewGuid().ToString("N");
        var record = new StoredExecutionRecord(
            ExecutionId: executionId,
            WorkflowName: string.IsNullOrWhiteSpace(request.WorkflowName) ? "workflow_editor" : request.WorkflowName.Trim(),
            Prompt: request.Prompt,
            RuntimeBaseUrl: runtimeBaseUrl,
            Status: "running",
            StartedAtUtc: DateTimeOffset.UtcNow,
            CompletedAtUtc: null,
            ActorId: null,
            Error: null,
            Frames: []);

        await _store.SaveExecutionAsync(record, cancellationToken);
        var authSnapshot = _authSnapshotProvider == null
            ? null
            : await _authSnapshotProvider.CaptureAsync(cancellationToken);
        _ = Task.Run(() => RunStartExecutionAsync(record, request, authSnapshot));
        return ToDetail(record);
    }

    public async Task<ExecutionDetail?> ResumeAsync(
        string executionId,
        ResumeExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        var record = await _store.GetExecutionAsync(executionId, cancellationToken);
        if (record is null)
        {
            return null;
        }

        var actorId = record.ActorId?.Trim();
        var runId = request.RunId?.Trim();
        var stepId = request.StepId?.Trim();
        if (string.IsNullOrWhiteSpace(actorId))
        {
            throw new InvalidOperationException("Execution is missing actorId and cannot be resumed.");
        }

        if (string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(stepId))
        {
            throw new InvalidOperationException("runId and stepId are required to resume this execution.");
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{record.RuntimeBaseUrl.TrimEnd('/')}/api/workflows/resume");
        httpRequest.Content = JsonContent.Create(new
        {
            actorId,
            runId,
            stepId,
            approved = request.Approved,
            userInput = string.IsNullOrWhiteSpace(request.UserInput) ? null : request.UserInput.Trim(),
            metadata = request.Metadata,
        });

        var runtimeClient = _httpClientFactory.CreateClient(BackendClientName);
        using var response = await runtimeClient.SendAsync(httpRequest, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? $"Runtime resume request failed: {(int)response.StatusCode}"
                : error);
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        var nextActorId = TryExtractActorIdFromResumeResponse(responseBody) ?? actorId;
        var frames = record.Frames.ToList();
        frames.Add(new StoredExecutionFrame(
            DateTimeOffset.UtcNow,
            BuildStudioResumeFrame(
                nextActorId,
                runId,
                stepId,
                request.Approved,
                request.UserInput,
                request.SuspensionType,
                request.Metadata)));

        var updatedRecord = record with
        {
            Status = "running",
            CompletedAtUtc = null,
            ActorId = nextActorId,
            Error = null,
            Frames = frames,
        };

        await _store.SaveExecutionAsync(updatedRecord, cancellationToken);
        return ToDetail(updatedRecord);
    }

    private static string? TryExtractActorId(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (!document.RootElement.TryGetProperty("custom", out var customElement))
            {
                return null;
            }

            if (!customElement.TryGetProperty("payload", out var payloadElement))
            {
                return null;
            }

            return payloadElement.TryGetProperty("actorId", out var actorIdElement)
                ? actorIdElement.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryExtractActorIdFromResumeResponse(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.TryGetProperty("actorId", out var actorIdElement)
                ? actorIdElement.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task RunStartExecutionAsync(
        StoredExecutionRecord record,
        StartExecutionRequest request,
        StudioBackendRequestAuthSnapshot? authSnapshot)
    {
        using var httpRequest = BuildStartExecutionRequest(record.RuntimeBaseUrl, request, record);
        ApplyAuthSnapshot(httpRequest, authSnapshot);
        var runtimeClient = _httpClientFactory.CreateClient(BackendClientName);

        try
        {
            using var response = await runtimeClient.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                CancellationToken.None);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(CancellationToken.None);
                var failure = ParseStartFailure(error, $"HTTP_{(int)response.StatusCode}");
                await SaveExecutionFailureAsync(record, failure);
                return;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(CancellationToken.None);
            await ConsumeExecutionStreamAsync(record, stream, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var failure = ParseStartFailure(exception.Message, "EXECUTION_START_FAILED");
            await SaveExecutionFailureAsync(record, failure);
        }
    }

    private async Task ConsumeExecutionStreamAsync(
        StoredExecutionRecord seedRecord,
        Stream stream,
        CancellationToken cancellationToken)
    {
        var frames = new List<StoredExecutionFrame>(seedRecord.Frames);
        var pendingHumanSteps = new HashSet<string>(StringComparer.Ordinal);
        var actorId = seedRecord.ActorId;
        var runFinished = false;
        var runFailed = false;
        string? error = seedRecord.Error;

        await foreach (var payload in ReadSsePayloadsAsync(stream, cancellationToken))
        {
            var receivedAtUtc = DateTimeOffset.UtcNow;
            frames.Add(new StoredExecutionFrame(receivedAtUtc, payload));
            actorId ??= TryExtractActorId(payload);
            TrackExecutionState(payload, pendingHumanSteps, ref runFinished, ref runFailed);
            error ??= TryExtractExecutionError(payload);

            var liveStatus = ResolveLiveExecutionStatus(runFinished, runFailed, pendingHumanSteps.Count > 0);
            var liveRecord = seedRecord with
            {
                Status = liveStatus,
                CompletedAtUtc = liveStatus is "completed" or "failed" ? receivedAtUtc : null,
                ActorId = actorId,
                Error = liveStatus == "failed" ? error : null,
                Frames = frames.ToArray(),
            };

            await _store.SaveExecutionAsync(liveRecord, cancellationToken);
        }

        var finalStatus = ResolveCompletedExecutionStatus(runFinished, runFailed, pendingHumanSteps.Count > 0);
        var finalRecord = seedRecord with
        {
            Status = finalStatus,
            CompletedAtUtc = finalStatus is "completed" or "failed" ? DateTimeOffset.UtcNow : null,
            ActorId = actorId,
            Error = finalStatus == "failed" ? error : null,
            Frames = frames.ToArray(),
        };

        await _store.SaveExecutionAsync(finalRecord, cancellationToken);
    }

    private async Task SaveExecutionFailureAsync(StoredExecutionRecord record, ExecutionStartFailure failure)
    {
        var failedRecord = record with
        {
            Status = "failed",
            CompletedAtUtc = DateTimeOffset.UtcNow,
            Error = failure.Message,
            Frames =
            [
                new StoredExecutionFrame(
                    DateTimeOffset.UtcNow,
                    BuildRunErrorFrame(failure.Message, failure.Code))
            ],
        };

        await _store.SaveExecutionAsync(failedRecord, CancellationToken.None);
    }

    private static void ApplyAuthSnapshot(
        HttpRequestMessage request,
        StudioBackendRequestAuthSnapshot? authSnapshot)
    {
        if (authSnapshot == null)
        {
            return;
        }

        if (request.Headers.Authorization == null &&
            !string.IsNullOrWhiteSpace(authSnapshot.BearerToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authSnapshot.BearerToken);
        }

        if (string.IsNullOrWhiteSpace(authSnapshot.InternalAuthHeaderName) ||
            string.IsNullOrWhiteSpace(authSnapshot.InternalAuthToken) ||
            !ShouldAttachInternalAuthHeader(request.RequestUri, authSnapshot.LocalOrigin) ||
            request.Headers.Contains(authSnapshot.InternalAuthHeaderName))
        {
            return;
        }

        request.Headers.TryAddWithoutValidation(
            authSnapshot.InternalAuthHeaderName,
            authSnapshot.InternalAuthToken);
    }

    private static bool ShouldAttachInternalAuthHeader(Uri? requestUri, string? localOrigin)
    {
        if (requestUri == null || string.IsNullOrWhiteSpace(localOrigin))
        {
            return false;
        }

        if (!Uri.TryCreate(localOrigin, UriKind.Absolute, out var localUri))
        {
            return false;
        }

        return Uri.Compare(
            requestUri,
            localUri,
            UriComponents.SchemeAndServer | UriComponents.Port,
            UriFormat.SafeUnescaped,
            StringComparison.OrdinalIgnoreCase) == 0;
    }

    private static async IAsyncEnumerable<string> ReadSsePayloadsAsync(
        Stream stream,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream);
        var dataLines = new List<string>();

        while (!reader.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            if (line.Length == 0)
            {
                if (dataLines.Count == 0)
                {
                    continue;
                }

                var payload = string.Join("\n", dataLines).Trim();
                dataLines.Clear();
                if (payload.Length > 0 && !string.Equals(payload, "[DONE]", StringComparison.Ordinal))
                {
                    yield return payload;
                }

                continue;
            }

            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                dataLines.Add(line["data:".Length..].Trim());
            }
        }

        if (dataLines.Count > 0)
        {
            var payload = string.Join("\n", dataLines).Trim();
            if (payload.Length > 0 && !string.Equals(payload, "[DONE]", StringComparison.Ordinal))
            {
                yield return payload;
            }
        }
    }

    private static void TrackExecutionState(
        string payload,
        HashSet<string> pendingHumanSteps,
        ref bool runFinished,
        ref bool runFailed)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var customName = root.TryGetProperty("custom", out var customElement) &&
                             customElement.TryGetProperty("name", out var customNameElement)
                ? customNameElement.GetString()
                : null;

            if (string.Equals(customName, "aevatar.human_input.request", StringComparison.Ordinal) &&
                customElement.TryGetProperty("payload", out var humanPayload) &&
                humanPayload.TryGetProperty("stepId", out var suspendedStepElement))
            {
                var suspendedStepId = suspendedStepElement.GetString();
                if (!string.IsNullOrWhiteSpace(suspendedStepId))
                {
                    pendingHumanSteps.Add(suspendedStepId);
                }
            }

            if (string.Equals(customName, "aevatar.step.completed", StringComparison.Ordinal) &&
                customElement.TryGetProperty("payload", out var completedPayload) &&
                completedPayload.TryGetProperty("stepId", out var completedStepElement))
            {
                var completedStepId = completedStepElement.GetString();
                if (!string.IsNullOrWhiteSpace(completedStepId))
                {
                    pendingHumanSteps.Remove(completedStepId);
                }

                if (completedPayload.TryGetProperty("success", out var successElement) &&
                    successElement.ValueKind == JsonValueKind.False)
                {
                    runFailed = true;
                }
            }

            if (root.TryGetProperty("runFinished", out _))
            {
                runFinished = true;
            }

            if (root.TryGetProperty("runError", out _))
            {
                runFailed = true;
            }
        }
        catch
        {
        }
    }

    private static string? TryExtractExecutionError(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.TryGetProperty("runError", out var runError) &&
                runError.TryGetProperty("message", out var messageElement) &&
                messageElement.ValueKind == JsonValueKind.String)
            {
                return messageElement.GetString();
            }

            if (root.TryGetProperty("custom", out var customElement) &&
                customElement.TryGetProperty("name", out var customNameElement) &&
                string.Equals(customNameElement.GetString(), "aevatar.step.completed", StringComparison.Ordinal) &&
                customElement.TryGetProperty("payload", out var payloadElement) &&
                payloadElement.TryGetProperty("error", out var errorElement) &&
                errorElement.ValueKind == JsonValueKind.String)
            {
                return errorElement.GetString();
            }
        }
        catch
        {
        }

        return null;
    }

    private static string ResolveLiveExecutionStatus(bool runFinished, bool runFailed, bool waitingForHuman)
    {
        if (runFailed)
        {
            return "failed";
        }

        if (waitingForHuman)
        {
            return "waiting";
        }

        return runFinished ? "completed" : "running";
    }

    private static string ResolveCompletedExecutionStatus(bool runFinished, bool runFailed, bool waitingForHuman)
    {
        if (runFailed)
        {
            return "failed";
        }

        if (waitingForHuman)
        {
            return "waiting";
        }

        return runFinished ? "completed" : "completed";
    }

    private static HttpRequestMessage BuildStartExecutionRequest(
        string runtimeBaseUrl,
        StartExecutionRequest request,
        StoredExecutionRecord record)
    {
        var normalizedBaseUrl = runtimeBaseUrl.TrimEnd('/');
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{normalizedBaseUrl}/api/chat");
        httpRequest.Headers.Accept.ParseAdd("text/event-stream");

        if (ShouldUsePublishedWorkflowRun(request))
        {
            var scopeId = request.ScopeId!.Trim();
            var workflowId = request.WorkflowId!.Trim();
            httpRequest.RequestUri = new Uri(
                $"{normalizedBaseUrl}/api/scopes/{Uri.EscapeDataString(scopeId)}/workflows/{Uri.EscapeDataString(workflowId)}/runs:stream",
                UriKind.Absolute);
            httpRequest.Content = JsonContent.Create(new
            {
                prompt = request.Prompt,
                eventFormat = string.IsNullOrWhiteSpace(request.EventFormat) ? "workflow" : request.EventFormat.Trim(),
            });
            return httpRequest;
        }

        httpRequest.Content = JsonContent.Create(new
        {
            prompt = request.Prompt,
            workflow = record.WorkflowName,
            workflowYamls = request.WorkflowYamls,
        });
        return httpRequest;
    }

    private static bool ShouldUsePublishedWorkflowRun(StartExecutionRequest request) =>
        !string.IsNullOrWhiteSpace(request.ScopeId) &&
        !string.IsNullOrWhiteSpace(request.WorkflowId);

    private static string BuildStudioResumeFrame(
        string actorId,
        string runId,
        string stepId,
        bool approved,
        string? userInput,
        string? suspensionType,
        IReadOnlyDictionary<string, string>? metadata)
    {
        var payload = new
        {
            custom = new
            {
                name = "studio.human.resume",
                payload = new
                {
                    actorId,
                    runId,
                    stepId,
                    approved,
                    userInput,
                    suspensionType,
                    metadata,
                },
            },
        };

        return JsonSerializer.Serialize(payload);
    }

    private static string BuildRunErrorFrame(string message, string? code)
    {
        var payload = new
        {
            runError = new
            {
                message,
                code,
            },
        };

        return JsonSerializer.Serialize(payload);
    }

    private static ExecutionStartFailure ParseStartFailure(string? rawError, string fallbackCode)
    {
        var fallbackMessage = string.IsNullOrWhiteSpace(rawError)
            ? "Runtime request failed before any execution events were received."
            : rawError.Trim();

        if (string.IsNullOrWhiteSpace(rawError))
            return new ExecutionStartFailure(fallbackMessage, fallbackCode);

        try
        {
            using var document = JsonDocument.Parse(rawError);
            var root = document.RootElement;
            var message = root.TryGetProperty("message", out var messageElement) &&
                          messageElement.ValueKind == JsonValueKind.String
                ? messageElement.GetString()
                : null;
            var code = root.TryGetProperty("code", out var codeElement) &&
                       codeElement.ValueKind == JsonValueKind.String
                ? codeElement.GetString()
                : null;

            return new ExecutionStartFailure(
                string.IsNullOrWhiteSpace(message) ? fallbackMessage : message.Trim(),
                string.IsNullOrWhiteSpace(code) ? fallbackCode : code.Trim());
        }
        catch
        {
            return new ExecutionStartFailure(fallbackMessage, fallbackCode);
        }
    }

    private sealed record ExecutionStartFailure(string Message, string? Code);

    private static ExecutionSummary ToSummary(StoredExecutionRecord record) =>
        new(
            record.ExecutionId,
            record.WorkflowName,
            record.Status,
            record.Prompt.Length <= 120 ? record.Prompt : $"{record.Prompt[..117]}...",
            record.StartedAtUtc,
            record.CompletedAtUtc,
            record.ActorId,
            record.Error);

    private static ExecutionDetail ToDetail(StoredExecutionRecord record) =>
        new(
            record.ExecutionId,
            record.WorkflowName,
            record.Prompt,
            record.RuntimeBaseUrl,
            record.Status,
            record.StartedAtUtc,
            record.CompletedAtUtc,
            record.ActorId,
            record.Error,
            record.Frames
                .Select(frame => new ExecutionFrameDto(frame.ReceivedAtUtc, frame.Payload))
                .ToList());
}
