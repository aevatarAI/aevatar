using System.Net.Http.Json;
using System.Text.Json;
using Aevatar.Tools.Cli.Studio.Application.Abstractions;
using Aevatar.Tools.Cli.Studio.Application.Contracts;

namespace Aevatar.Tools.Cli.Studio.Application.Services;

public sealed class ExecutionService
{
    private static readonly HttpClient RuntimeClient = new();

    private readonly IStudioWorkspaceStore _store;

    public ExecutionService(IStudioWorkspaceStore store)
    {
        _store = store;
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

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{runtimeBaseUrl}/api/chat");
        httpRequest.Headers.Accept.ParseAdd("text/event-stream");
        httpRequest.Content = JsonContent.Create(new
        {
            prompt = request.Prompt,
            workflow = record.WorkflowName,
            workflowYamls = request.WorkflowYamls,
        });

        try
        {
            using var response = await RuntimeClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                var failedRecord = record with
                {
                    Status = "failed",
                    CompletedAtUtc = DateTimeOffset.UtcNow,
                    Error = string.IsNullOrWhiteSpace(error) ? $"Runtime request failed: {(int)response.StatusCode}" : error,
                };

                await _store.SaveExecutionAsync(failedRecord, cancellationToken);
                return ToDetail(failedRecord);
            }

            var frames = new List<StoredExecutionFrame>();
            var pendingHumanSteps = new HashSet<string>(StringComparer.Ordinal);
            string? actorId = null;
            var runFinished = false;
            var runFailed = false;
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var payload = line["data:".Length..].Trim();
                if (payload.Length == 0)
                {
                    continue;
                }

                frames.Add(new StoredExecutionFrame(DateTimeOffset.UtcNow, payload));
                actorId ??= TryExtractActorId(payload);
                TrackExecutionState(payload, pendingHumanSteps, ref runFinished, ref runFailed);
            }

            var status = ResolveExecutionStatus(runFinished, runFailed, pendingHumanSteps.Count > 0);

            var completedRecord = record with
            {
                Status = status,
                CompletedAtUtc = status is "completed" or "failed" ? DateTimeOffset.UtcNow : null,
                ActorId = actorId,
                Frames = frames,
            };

            await _store.SaveExecutionAsync(completedRecord, cancellationToken);
            return ToDetail(completedRecord);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var failedRecord = record with
            {
                Status = "failed",
                CompletedAtUtc = DateTimeOffset.UtcNow,
                Error = exception.Message,
            };

            await _store.SaveExecutionAsync(failedRecord, cancellationToken);
            return ToDetail(failedRecord);
        }
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

        using var response = await RuntimeClient.SendAsync(httpRequest, cancellationToken);
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

    private static string ResolveExecutionStatus(bool runFinished, bool runFailed, bool waitingForHuman)
    {
        if (runFailed)
        {
            return "failed";
        }

        if (waitingForHuman)
        {
            return "waiting";
        }

        return "completed";
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
