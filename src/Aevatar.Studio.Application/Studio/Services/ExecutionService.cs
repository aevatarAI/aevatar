using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;

namespace Aevatar.Studio.Application.Studio.Services;

public sealed class ExecutionService
{
    private const string BackendClientName = "AppBridgeBackend";
    private const string ExecutionObserverLostFailureCode = "EXECUTION_OBSERVER_LOST";
    private const string ExecutionObserverLostFailureMessage = "Studio execution observer was lost before a terminal event was observed.";
    private const string ExecutionStreamClosedFailureCode = "EXECUTION_STREAM_TERMINATED";
    private const string ExecutionStreamClosedFailureMessage = "Execution stream ended before a terminal event was observed.";
    private const string ExecutionStreamFailedCode = "EXECUTION_STREAM_FAILED";

    private readonly IStudioWorkspaceStore _store;
    private readonly IUserConfigStore? _userConfigStore;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IStudioBackendRequestAuthSnapshotProvider? _authSnapshotProvider;
    private readonly string _observationSessionId = Guid.NewGuid().ToString("N");

    public ExecutionService(
        IStudioWorkspaceStore store,
        IHttpClientFactory httpClientFactory,
        IStudioBackendRequestAuthSnapshotProvider? authSnapshotProvider = null,
        IUserConfigStore? userConfigStore = null)
    {
        _store = store;
        _httpClientFactory = httpClientFactory;
        _authSnapshotProvider = authSnapshotProvider;
        _userConfigStore = userConfigStore;
    }

    public async Task<IReadOnlyList<ExecutionSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        var records = await LoadReconciledExecutionRecordsAsync(cancellationToken);
        return records
            .OrderByDescending(record => record.StartedAtUtc)
            .Select(ToSummary)
            .ToList();
    }

    public async Task<ExecutionDetail?> GetAsync(string executionId, CancellationToken cancellationToken = default)
    {
        var record = await LoadReconciledExecutionRecordAsync(executionId, cancellationToken);
        return record is null ? null : ToDetail(record);
    }

    public async Task<ExecutionDetail> StartAsync(StartExecutionRequest request, CancellationToken cancellationToken = default)
    {
        if (!ShouldUsePublishedWorkflowRun(request))
            throw new InvalidOperationException("scopeId and workflowId are required. Executions must target a registered scope service.");

        var runtimeBaseUrl = request.RuntimeBaseUrl?.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(runtimeBaseUrl))
            runtimeBaseUrl = await ResolveRuntimeBaseUrlAsync(cancellationToken);

        var executionId = Guid.NewGuid().ToString("N");
        var startedAtUtc = DateTimeOffset.UtcNow;
        var record = new StoredExecutionRecord(
            ExecutionId: executionId,
            WorkflowName: string.IsNullOrWhiteSpace(request.WorkflowName) ? "workflow_editor" : request.WorkflowName.Trim(),
            Prompt: request.Prompt,
            RuntimeBaseUrl: runtimeBaseUrl,
            Status: "running",
            StartedAtUtc: startedAtUtc,
            CompletedAtUtc: null,
            ActorId: null,
            Error: null,
            Frames: [],
            ObservationSessionId: _observationSessionId,
            ObservationActive: true,
            LastObservedAtUtc: startedAtUtc,
            ScopeId: string.IsNullOrWhiteSpace(request.ScopeId) ? null : request.ScopeId.Trim(),
            WorkflowId: string.IsNullOrWhiteSpace(request.WorkflowId) ? null : request.WorkflowId.Trim());

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
        var record = await LoadReconciledExecutionRecordAsync(executionId, cancellationToken);
        if (record is null)
        {
            return null;
        }

        if (IsTerminalExecutionStatus(record.Status))
        {
            throw new InvalidOperationException(
                $"Execution is already in terminal status '{record.Status}' and cannot be resumed.");
        }

        var runId = request.RunId?.Trim();
        var stepId = request.StepId?.Trim();
        if (string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(stepId))
        {
            throw new InvalidOperationException("runId and stepId are required to resume this execution.");
        }

        var actorId = record.ActorId?.Trim();
        using var httpRequest = BuildResumeExecutionRequest(record, runId);
        httpRequest.Content = JsonContent.Create(new
        {
            actorId,
            stepId,
            approved = request.Approved,
            userInput = string.IsNullOrWhiteSpace(request.UserInput) ? null : request.UserInput.Trim(),
            metadata = request.Metadata,
        });
        var authSnapshot = _authSnapshotProvider == null
            ? null
            : await _authSnapshotProvider.CaptureAsync(cancellationToken);
        ApplyAuthSnapshot(httpRequest, authSnapshot);

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
        var nextActorId = TryExtractActorIdFromResumeResponse(responseBody) ?? actorId ?? string.Empty;
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

    public async Task<ExecutionDetail?> StopAsync(
        string executionId,
        StopExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        var record = await LoadReconciledExecutionRecordAsync(executionId, cancellationToken);
        if (record is null)
        {
            return null;
        }

        if (IsTerminalExecutionStatus(record.Status))
            return ToDetail(record);

        var runId = TryExtractRunIdFromRecord(record);
        if (string.IsNullOrWhiteSpace(runId))
        {
            throw new InvalidOperationException("Execution is missing runId and cannot be stopped.");
        }

        var actorId = record.ActorId?.Trim();
        var reason = string.IsNullOrWhiteSpace(request.Reason)
            ? "user requested stop"
            : request.Reason.Trim();

        using var httpRequest = BuildStopExecutionRequest(record, runId);
        httpRequest.Content = JsonContent.Create(new
        {
            actorId,
            reason,
        });
        var authSnapshot = _authSnapshotProvider == null
            ? null
            : await _authSnapshotProvider.CaptureAsync(cancellationToken);
        ApplyAuthSnapshot(httpRequest, authSnapshot);

        var runtimeClient = _httpClientFactory.CreateClient(BackendClientName);
        using var response = await runtimeClient.SendAsync(httpRequest, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            if (ShouldIgnoreStopFailure(response.StatusCode, error))
            {
                return await GetReconciledDetailAsync(executionId, record, cancellationToken);
            }

            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? $"Runtime stop request failed: {(int)response.StatusCode}"
                : error);
        }

        var frames = record.Frames.ToList();
        frames.Add(new StoredExecutionFrame(
            DateTimeOffset.UtcNow,
            BuildStudioStopRequestedFrame(actorId ?? string.Empty, runId, reason)));

        var updatedRecord = record with
        {
            Frames = frames,
        };

        await _store.SaveExecutionAsync(updatedRecord, cancellationToken);
        return ToDetail(updatedRecord);
    }

    private async Task<ExecutionDetail> GetReconciledDetailAsync(
        string executionId,
        StoredExecutionRecord fallbackRecord,
        CancellationToken cancellationToken)
    {
        var latestRecord = await LoadReconciledExecutionRecordAsync(executionId, cancellationToken) ?? fallbackRecord;
        return ToDetail(latestRecord);
    }

    private async Task<IReadOnlyList<StoredExecutionRecord>> LoadReconciledExecutionRecordsAsync(
        CancellationToken cancellationToken)
    {
        var records = await _store.ListExecutionsAsync(cancellationToken);
        var reconciled = new List<StoredExecutionRecord>(records.Count);
        foreach (var record in records)
        {
            reconciled.Add(await ReconcileExecutionRecordAsync(record, cancellationToken));
        }

        return reconciled;
    }

    private async Task<StoredExecutionRecord?> LoadReconciledExecutionRecordAsync(
        string executionId,
        CancellationToken cancellationToken)
    {
        var record = await _store.GetExecutionAsync(executionId, cancellationToken);
        return record is null
            ? null
            : await ReconcileExecutionRecordAsync(record, cancellationToken);
    }

    private async Task<StoredExecutionRecord> ReconcileExecutionRecordAsync(
        StoredExecutionRecord record,
        CancellationToken cancellationToken)
    {
        var reconciledRecord = ReconcileExecutionRecord(record);
        if (ShouldFailLostObservation(reconciledRecord))
        {
            reconciledRecord = MarkExecutionObserverLost(reconciledRecord);
        }

        if (!Equals(reconciledRecord, record))
        {
            await _store.SaveExecutionAsync(reconciledRecord, cancellationToken);
        }

        return reconciledRecord;
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

    private static string? TryExtractRunId(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            if (root.TryGetProperty("runStarted", out var runStarted) &&
                runStarted.TryGetProperty("runId", out var runIdElement) &&
                runIdElement.ValueKind == JsonValueKind.String)
            {
                return runIdElement.GetString();
            }

            if (root.TryGetProperty("runStopped", out var runStopped) &&
                runStopped.TryGetProperty("runId", out var stoppedRunIdElement) &&
                stoppedRunIdElement.ValueKind == JsonValueKind.String)
            {
                return stoppedRunIdElement.GetString();
            }

            if (root.TryGetProperty("custom", out var customElement) &&
                customElement.TryGetProperty("payload", out var payloadElement) &&
                payloadElement.TryGetProperty("runId", out var customRunIdElement) &&
                customRunIdElement.ValueKind == JsonValueKind.String)
            {
                return customRunIdElement.GetString();
            }
        }
        catch
        {
        }

        return null;
    }

    private static string? TryExtractRunIdFromRecord(StoredExecutionRecord record)
    {
        foreach (var frame in record.Frames)
        {
            var runId = TryExtractRunId(frame.Payload);
            if (!string.IsNullOrWhiteSpace(runId))
                return runId.Trim();
        }

        return null;
    }

    private static bool ShouldIgnoreStopFailure(
        HttpStatusCode statusCode,
        string? errorPayload)
    {
        if (statusCode != HttpStatusCode.Conflict &&
            statusCode != HttpStatusCode.NotFound)
        {
            return false;
        }

        var message = TryExtractApiErrorMessage(errorPayload) ?? errorPayload ?? string.Empty;
        return message.Contains("does not have a bound run id", StringComparison.OrdinalIgnoreCase) ||
               (message.Contains("Actor '", StringComparison.OrdinalIgnoreCase) &&
                message.Contains("not found", StringComparison.OrdinalIgnoreCase));
    }

    private static string? TryExtractApiErrorMessage(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return null;

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var errorElement) &&
                errorElement.ValueKind == JsonValueKind.String)
            {
                return errorElement.GetString();
            }

            if (root.TryGetProperty("message", out var messageElement) &&
                messageElement.ValueKind == JsonValueKind.String)
            {
                return messageElement.GetString();
            }
        }
        catch
        {
        }

        return null;
    }

    private static StoredExecutionRecord ReconcileExecutionRecord(StoredExecutionRecord record)
    {
        if (record.Frames.Count == 0)
            return record;

        var pendingHumanSteps = new HashSet<string>(StringComparer.Ordinal);
        var actorId = record.ActorId;
        var runFinished = false;
        var runFailed = false;
        var runStopped = false;
        var error = record.Error;
        DateTimeOffset? completedAtUtc = null;

        foreach (var frame in record.Frames)
        {
            actorId ??= TryExtractActorId(frame.Payload);
            TrackExecutionState(frame.Payload, pendingHumanSteps, ref runFinished, ref runFailed, ref runStopped);

            if (string.IsNullOrWhiteSpace(error))
            {
                error = TryExtractExecutionError(frame.Payload);
            }

            var liveStatus = ResolveLiveExecutionStatus(runFinished, runFailed, runStopped, pendingHumanSteps.Count > 0);
            if (IsTerminalExecutionStatus(liveStatus))
            {
                completedAtUtc = frame.ReceivedAtUtc;
            }
        }

        var status = ResolveLiveExecutionStatus(runFinished, runFailed, runStopped, pendingHumanSteps.Count > 0);
        var terminalError = status is "failed" or "stopped"
            ? error
            : null;
        var nextCompletedAtUtc = IsTerminalExecutionStatus(status)
            ? completedAtUtc ?? record.CompletedAtUtc
            : null;
        var lastObservedAtUtc = record.Frames[^1].ReceivedAtUtc;

        return record with
        {
            Status = status,
            CompletedAtUtc = nextCompletedAtUtc,
            ActorId = actorId,
            Error = terminalError,
            ObservationActive = record.ObservationActive && !IsTerminalExecutionStatus(status),
            LastObservedAtUtc = lastObservedAtUtc,
        };
    }

    private bool ShouldFailLostObservation(StoredExecutionRecord record)
    {
        if (IsTerminalExecutionStatus(record.Status) || !record.ObservationActive)
            return false;

        var observationSessionId = record.ObservationSessionId?.Trim();
        return !string.IsNullOrWhiteSpace(observationSessionId) &&
               !string.Equals(observationSessionId, _observationSessionId, StringComparison.Ordinal);
    }

    private StoredExecutionRecord MarkExecutionObserverLost(StoredExecutionRecord record)
    {
        var failedAtUtc = DateTimeOffset.UtcNow;
        var frames = record.Frames.ToList();
        frames.Add(new StoredExecutionFrame(
            failedAtUtc,
            BuildRunErrorFrame(ExecutionObserverLostFailureMessage, ExecutionObserverLostFailureCode)));

        return record with
        {
            Status = "failed",
            CompletedAtUtc = record.CompletedAtUtc ?? failedAtUtc,
            Error = ExecutionObserverLostFailureMessage,
            Frames = frames,
            ObservationSessionId = _observationSessionId,
            ObservationActive = false,
            LastObservedAtUtc = failedAtUtc,
        };
    }

    private async Task RunStartExecutionAsync(
        StoredExecutionRecord record,
        StartExecutionRequest request,
        StudioBackendRequestAuthSnapshot? authSnapshot)
    {
        using var httpRequest = BuildStartExecutionRequest(record.RuntimeBaseUrl, request);
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
        var runStopped = false;
        string? error = seedRecord.Error;
        var latestRecord = seedRecord;

        try
        {
            await foreach (var payload in ReadSsePayloadsAsync(stream, cancellationToken))
            {
                var receivedAtUtc = DateTimeOffset.UtcNow;
                frames.Add(new StoredExecutionFrame(receivedAtUtc, payload));
                actorId ??= TryExtractActorId(payload);
                TrackExecutionState(payload, pendingHumanSteps, ref runFinished, ref runFailed, ref runStopped);
                error ??= TryExtractExecutionError(payload);

                var liveStatus = ResolveLiveExecutionStatus(runFinished, runFailed, runStopped, pendingHumanSteps.Count > 0);
                latestRecord = seedRecord with
                {
                    Status = liveStatus,
                    CompletedAtUtc = liveStatus is "completed" or "failed" or "stopped" ? receivedAtUtc : null,
                    ActorId = actorId,
                    Error = liveStatus is "failed" or "stopped" ? error : null,
                    Frames = frames.ToArray(),
                    ObservationSessionId = _observationSessionId,
                    ObservationActive = !IsTerminalExecutionStatus(liveStatus),
                    LastObservedAtUtc = receivedAtUtc,
                };

                await _store.SaveExecutionAsync(latestRecord, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await SaveExecutionFailureAsync(
                latestRecord with
                {
                    ActorId = actorId,
                    Error = error,
                    Frames = frames.ToArray(),
                },
                new ExecutionStartFailure(
                    string.IsNullOrWhiteSpace(exception.Message)
                        ? "Execution stream failed before a terminal event was observed."
                        : exception.Message.Trim(),
                    ExecutionStreamFailedCode));
            return;
        }

        var finalStatus = ResolveCompletedExecutionStatus(runFinished, runFailed, runStopped, pendingHumanSteps.Count > 0);
        if (string.Equals(finalStatus, "failed", StringComparison.Ordinal))
        {
            await SaveExecutionFailureAsync(
                latestRecord with
                {
                    ActorId = actorId,
                    Error = error,
                    Frames = frames.ToArray(),
                },
                new ExecutionStartFailure(
                    string.IsNullOrWhiteSpace(error) ? ExecutionStreamClosedFailureMessage : error,
                    string.IsNullOrWhiteSpace(error) ? ExecutionStreamClosedFailureCode : null));
            return;
        }

        var finalRecord = latestRecord with
        {
            Status = finalStatus,
            CompletedAtUtc = finalStatus is "completed" or "failed" or "stopped"
                ? latestRecord.CompletedAtUtc ?? DateTimeOffset.UtcNow
                : null,
            ActorId = actorId,
            Error = finalStatus is "failed" or "stopped" ? error : null,
            Frames = frames.ToArray(),
            ObservationSessionId = _observationSessionId,
            ObservationActive = !IsTerminalExecutionStatus(finalStatus),
            LastObservedAtUtc = frames.Count > 0 ? frames[^1].ReceivedAtUtc : latestRecord.LastObservedAtUtc,
        };

        await _store.SaveExecutionAsync(finalRecord, cancellationToken);
    }

    private async Task SaveExecutionFailureAsync(StoredExecutionRecord record, ExecutionStartFailure failure)
    {
        var failedAtUtc = DateTimeOffset.UtcNow;
        var frames = record.Frames.ToList();
        frames.Add(new StoredExecutionFrame(
            failedAtUtc,
            BuildRunErrorFrame(failure.Message, failure.Code)));
        var failedRecord = record with
        {
            Status = "failed",
            CompletedAtUtc = record.CompletedAtUtc ?? failedAtUtc,
            Error = failure.Message,
            Frames = frames,
            ObservationSessionId = _observationSessionId,
            ObservationActive = false,
            LastObservedAtUtc = failedAtUtc,
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

        while (true)
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
        ref bool runFailed,
        ref bool runStopped)
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

            if (string.Equals(customName, "studio.human.resume", StringComparison.Ordinal) &&
                customElement.TryGetProperty("payload", out var resumePayload) &&
                resumePayload.TryGetProperty("stepId", out var resumedStepElement))
            {
                var resumedStepId = resumedStepElement.GetString();
                if (!string.IsNullOrWhiteSpace(resumedStepId))
                {
                    pendingHumanSteps.Remove(resumedStepId);
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

            if (string.Equals(customName, "aevatar.run.stopped", StringComparison.Ordinal))
            {
                runStopped = true;
            }

            if (root.TryGetProperty("runFinished", out _))
            {
                runFinished = true;
            }

            if (root.TryGetProperty("runError", out _))
            {
                runFailed = true;
            }

            if (root.TryGetProperty("runStopped", out _))
            {
                runStopped = true;
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

            if (root.TryGetProperty("runStopped", out var runStopped) &&
                runStopped.TryGetProperty("reason", out var reasonElement) &&
                reasonElement.ValueKind == JsonValueKind.String)
            {
                return reasonElement.GetString();
            }

            if (root.TryGetProperty("custom", out var customElement) &&
                customElement.TryGetProperty("name", out var customNameElement) &&
                customElement.TryGetProperty("payload", out var payloadElement))
            {
                if (string.Equals(customNameElement.GetString(), "aevatar.step.completed", StringComparison.Ordinal) &&
                    payloadElement.TryGetProperty("error", out var errorElement) &&
                    errorElement.ValueKind == JsonValueKind.String)
                {
                    return errorElement.GetString();
                }

                if (string.Equals(customNameElement.GetString(), "aevatar.run.stopped", StringComparison.Ordinal) &&
                    payloadElement.TryGetProperty("reason", out var stoppedReasonElement) &&
                    stoppedReasonElement.ValueKind == JsonValueKind.String)
                {
                    return stoppedReasonElement.GetString();
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static string ResolveLiveExecutionStatus(
        bool runFinished,
        bool runFailed,
        bool runStopped,
        bool waitingForHuman)
    {
        if (runFailed)
        {
            return "failed";
        }

        if (runStopped)
        {
            return "stopped";
        }

        if (waitingForHuman)
        {
            return "waiting";
        }

        return runFinished ? "completed" : "running";
    }

    private static string ResolveCompletedExecutionStatus(
        bool runFinished,
        bool runFailed,
        bool runStopped,
        bool waitingForHuman)
    {
        if (runFailed)
        {
            return "failed";
        }

        if (runStopped)
        {
            return "stopped";
        }

        if (waitingForHuman)
        {
            return "waiting";
        }

        return runFinished ? "completed" : "failed";
    }

    private static HttpRequestMessage BuildStartExecutionRequest(
        string runtimeBaseUrl,
        StartExecutionRequest request)
    {
        var normalizedBaseUrl = runtimeBaseUrl.TrimEnd('/');
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, normalizedBaseUrl);
        httpRequest.Headers.Accept.ParseAdd("text/event-stream");

        if (ShouldUsePublishedWorkflowRun(request))
        {
            var scopeId = request.ScopeId!.Trim();
            var workflowId = request.WorkflowId!.Trim();
            var requestPath = $"{normalizedBaseUrl}/api/scopes/{Uri.EscapeDataString(scopeId)}/services/{Uri.EscapeDataString(workflowId)}/invoke/chat:stream";
            httpRequest.RequestUri = new Uri(requestPath, UriKind.Absolute);
            httpRequest.Content = JsonContent.Create(new
            {
                prompt = request.Prompt,
            });
            return httpRequest;
        }

        throw new InvalidOperationException("scopeId and workflowId are required. Executions must target a registered scope service.");
    }

    private static bool ShouldUsePublishedWorkflowRun(StartExecutionRequest request) =>
        !string.IsNullOrWhiteSpace(request.ScopeId) &&
        !string.IsNullOrWhiteSpace(request.WorkflowId);

    private static HttpRequestMessage BuildResumeExecutionRequest(
        StoredExecutionRecord record,
        string runId)
    {
        return new HttpRequestMessage(HttpMethod.Post, BuildScopeServiceRunControlUri(record, runId, "resume"));
    }

    private static HttpRequestMessage BuildStopExecutionRequest(
        StoredExecutionRecord record,
        string runId)
    {
        return new HttpRequestMessage(HttpMethod.Post, BuildScopeServiceRunControlUri(record, runId, "stop"));
    }

    private static string BuildScopeServiceRunControlUri(
        StoredExecutionRecord record,
        string runId,
        string action)
    {
        var baseUrl = record.RuntimeBaseUrl.TrimEnd('/');
        var scopeId = record.ScopeId?.Trim();
        var serviceId = record.WorkflowId?.Trim();
        if (string.IsNullOrWhiteSpace(scopeId) || string.IsNullOrWhiteSpace(serviceId))
        {
            throw new InvalidOperationException("Execution is missing scopeId or workflowId and cannot call service run control.");
        }

        return $"{baseUrl}/api/scopes/{Uri.EscapeDataString(scopeId)}/services/{Uri.EscapeDataString(serviceId)}/runs/{Uri.EscapeDataString(runId)}:{action}";
    }

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

    private static string BuildStudioStopRequestedFrame(
        string actorId,
        string runId,
        string reason)
    {
        var payload = new
        {
            custom = new
            {
                name = "studio.run.stop.requested",
                payload = new
                {
                    actorId,
                    runId,
                    reason,
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

    private static bool IsTerminalExecutionStatus(string? status) =>
        string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "stopped", StringComparison.OrdinalIgnoreCase);

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

    /// <summary>
    /// Resolve runtime base URL: user config (runtimeMode + local/remote URLs) takes priority;
    /// falls back to workspace settings if user config is unavailable.
    /// </summary>
    private async Task<string> ResolveRuntimeBaseUrlAsync(CancellationToken ct)
    {
        if (_userConfigStore != null)
        {
            try
            {
                var userConfig = await _userConfigStore.GetAsync(ct);
                var resolved = UserConfigRuntime.ResolveActiveRuntimeBaseUrl(userConfig);
                if (!string.IsNullOrWhiteSpace(resolved))
                    return resolved;
            }
            catch
            {
                // Fall through to workspace settings
            }
        }

        var settings = await _store.GetSettingsAsync(ct);
        return settings.RuntimeBaseUrl;
    }
}
