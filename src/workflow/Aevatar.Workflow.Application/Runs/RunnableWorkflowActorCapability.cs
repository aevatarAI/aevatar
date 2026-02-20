using System.Text;
using System.Text.Json;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Abstractions.Workflows;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Application.Runs;

/// <summary>
/// Workflow capability facade for sibling capabilities that need a single-run workflow execution contract.
/// </summary>
public sealed class RunnableWorkflowActorCapability : IRunnableWorkflowActorCapability
{
    private readonly IWorkflowRunCommandService _runService;
    private readonly IWorkflowDefinitionRegistry _workflowRegistry;
    private readonly IWorkflowRunActorPort _actorPort;
    private readonly ILogger<RunnableWorkflowActorCapability> _logger;

    public RunnableWorkflowActorCapability(
        IWorkflowRunCommandService runService,
        IWorkflowDefinitionRegistry workflowRegistry,
        IWorkflowRunActorPort actorPort,
        ILogger<RunnableWorkflowActorCapability> logger)
    {
        _runService = runService;
        _workflowRegistry = workflowRegistry;
        _actorPort = actorPort;
        _logger = logger;
    }

    public async Task<RunnableWorkflowActorResult> RunAsync(
        RunnableWorkflowActorRequest request,
        Func<WorkflowOutputFrame, CancellationToken, ValueTask>? emitAsync = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Input))
            throw new ArgumentException("Input is required.", nameof(request));

        var workflowName = request.WorkflowName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(workflowName))
            throw new ArgumentException("WorkflowName is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.WorkflowYaml))
            throw new ArgumentException("WorkflowYaml is required.", nameof(request));

        // Maker-style execution supports explicit YAML by registering/overwriting the named workflow definition.
        _workflowRegistry.Register(workflowName, request.WorkflowYaml);

        var runStartedAt = DateTimeOffset.UtcNow;
        var actorId = request.ActorId?.Trim() ?? string.Empty;
        var commandId = string.Empty;
        var outputBuffer = new StringBuilder();
        var output = string.Empty;
        var success = false;
        var timedOut = false;
        string? error = null;
        var shouldDestroyActor = request.DestroyActorAfterRun || string.IsNullOrWhiteSpace(request.ActorId);

        using var timeoutCts = CreateTimeoutTokenSource(request.Timeout, ct);
        var executionToken = timeoutCts?.Token ?? ct;

        try
        {
            WorkflowChatRunExecutionResult runResult;
            try
            {
                runResult = await _runService.ExecuteAsync(
                    new WorkflowChatRunRequest(request.Input, workflowName, request.ActorId),
                    async (frame, token) =>
                    {
                        await CaptureFrameAsync(frame, emitAsync, outputBuffer, token);
                        if (string.Equals(frame.Type, "RUN_FINISHED", StringComparison.Ordinal))
                        {
                            output = ResolveOutput(frame.Result);
                            success = true;
                            error = null;
                        }
                        else if (string.Equals(frame.Type, "RUN_ERROR", StringComparison.Ordinal))
                        {
                            success = false;
                            error = string.IsNullOrWhiteSpace(frame.Message)
                                ? "Workflow run failed."
                                : frame.Message;
                        }
                    },
                    (started, token) =>
                    {
                        actorId = started.ActorId;
                        commandId = started.CommandId;
                        workflowName = started.WorkflowName;
                        runStartedAt = DateTimeOffset.UtcNow;
                        return ValueTask.CompletedTask;
                    },
                    executionToken);
            }
            catch (OperationCanceledException) when (timeoutCts is { IsCancellationRequested: true } && !ct.IsCancellationRequested)
            {
                timedOut = true;
                success = false;
                error = "Timed out";
                return BuildResult();
            }

            if (!runResult.Succeeded)
            {
                success = false;
                error = MapStartError(runResult.Error);
                return BuildResult();
            }

            var finalize = runResult.FinalizeResult;
            if (finalize != null)
            {
                if (finalize.ProjectionCompletionStatus == WorkflowProjectionCompletionStatus.TimedOut)
                {
                    timedOut = true;
                    success = false;
                    error ??= "Timed out";
                }
                else if (finalize.ProjectionCompletionStatus is WorkflowProjectionCompletionStatus.Failed
                    or WorkflowProjectionCompletionStatus.Stopped
                    or WorkflowProjectionCompletionStatus.NotFound
                    or WorkflowProjectionCompletionStatus.Disabled
                    or WorkflowProjectionCompletionStatus.Unknown)
                {
                    success = false;
                    error ??= $"Run failed ({finalize.ProjectionCompletionStatus}).";
                }
            }

            if (success && string.IsNullOrWhiteSpace(output))
                output = outputBuffer.ToString();
            if (!success)
                output = string.Empty;

            return BuildResult();
        }
        finally
        {
            var destroyActorId = !string.IsNullOrWhiteSpace(actorId)
                ? actorId
                : request.ActorId;
            if (shouldDestroyActor && !string.IsNullOrWhiteSpace(destroyActorId))
            {
                try
                {
                    await _actorPort.DestroyAsync(destroyActorId, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to destroy workflow actor after capability run. actor={ActorId}", destroyActorId);
                }
            }
        }

        RunnableWorkflowActorResult BuildResult() =>
            new(
                actorId,
                workflowName,
                commandId,
                runStartedAt,
                output,
                success,
                timedOut,
                error);
    }

    private static async ValueTask CaptureFrameAsync(
        WorkflowOutputFrame frame,
        Func<WorkflowOutputFrame, CancellationToken, ValueTask>? emitAsync,
        StringBuilder outputBuffer,
        CancellationToken ct)
    {
        if (emitAsync != null)
            await emitAsync(frame, ct);

        if (string.Equals(frame.Type, "TEXT_MESSAGE_CONTENT", StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(frame.Delta))
        {
            outputBuffer.Append(frame.Delta);
        }
    }

    private static CancellationTokenSource? CreateTimeoutTokenSource(
        TimeSpan? timeout,
        CancellationToken ct)
    {
        if (!timeout.HasValue || timeout.Value <= TimeSpan.Zero)
            return null;

        var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout.Value);
        return timeoutCts;
    }

    private static string MapStartError(WorkflowChatRunStartError error)
    {
        return error switch
        {
            WorkflowChatRunStartError.AgentNotFound => "Agent not found.",
            WorkflowChatRunStartError.WorkflowNotFound => "Workflow not found.",
            WorkflowChatRunStartError.AgentTypeNotSupported => "Agent type is not supported.",
            WorkflowChatRunStartError.ProjectionDisabled => "Projection pipeline is disabled.",
            WorkflowChatRunStartError.WorkflowBindingMismatch => "Actor is bound to a different workflow.",
            WorkflowChatRunStartError.AgentWorkflowNotConfigured => "Actor has no bound workflow.",
            _ => "Failed to start workflow run.",
        };
    }

    private static string ResolveOutput(object? result)
    {
        if (result == null)
            return string.Empty;

        if (result is string outputText)
            return outputText;

        if (result is JsonElement json)
        {
            if (json.ValueKind == JsonValueKind.String)
                return json.GetString() ?? string.Empty;
            if (json.ValueKind == JsonValueKind.Object &&
                json.TryGetProperty("output", out var outputProp))
            {
                return outputProp.ValueKind == JsonValueKind.String
                    ? outputProp.GetString() ?? string.Empty
                    : outputProp.ToString();
            }

            return json.ToString();
        }

        var outputProperty = result.GetType().GetProperty("output")
                            ?? result.GetType().GetProperty("Output");
        if (outputProperty?.GetValue(result) is { } value)
            return value.ToString() ?? string.Empty;

        return result.ToString() ?? string.Empty;
    }
}
