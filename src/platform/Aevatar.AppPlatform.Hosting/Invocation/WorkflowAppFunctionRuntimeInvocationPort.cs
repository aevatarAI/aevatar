using Aevatar.AI.Abstractions;
using Aevatar.AppPlatform.Abstractions;
using Aevatar.AppPlatform.Abstractions.Ports;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aevatar.AppPlatform.Hosting.Invocation;

internal sealed class WorkflowAppFunctionRuntimeInvocationPort : IAppFunctionRuntimeInvocationPort
{
    private readonly ICommandInteractionService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus>? _chatRunService;
    private readonly IOperationCommandPort _operationCommandPort;
    private readonly ILogger<WorkflowAppFunctionRuntimeInvocationPort> _logger;
    private readonly CancellationToken _shutdownToken;

    public WorkflowAppFunctionRuntimeInvocationPort(
        ICommandInteractionService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus>? chatRunService,
        IOperationCommandPort operationCommandPort,
        ILogger<WorkflowAppFunctionRuntimeInvocationPort> logger,
        IHostApplicationLifetime? hostApplicationLifetime = null)
    {
        _chatRunService = chatRunService;
        _operationCommandPort = operationCommandPort ?? throw new ArgumentNullException(nameof(operationCommandPort));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _shutdownToken = hostApplicationLifetime?.ApplicationStopping ?? CancellationToken.None;
    }

    public async Task<AppFunctionRuntimeInvokeAccepted?> TryInvokeAsync(
        AppFunctionExecutionTarget target,
        AppFunctionInvokeRequest request,
        Func<AppFunctionRuntimeInvokeAccepted, CancellationToken, ValueTask<string>> onAcceptedAsync,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(onAcceptedAsync);

        if (target.ServiceRef.ImplementationKind != AppImplementationKind.Workflow)
            return null;

        if (_chatRunService == null)
            return null;

        if (request.Payload == null)
            throw new InvalidOperationException("payload is required.");

        if (!request.Payload.Is(ChatRequestEvent.Descriptor))
            throw new InvalidOperationException("Workflow-backed functions require ChatRequestEvent payload.");

        if (string.IsNullOrWhiteSpace(target.PrimaryActorId))
            throw new InvalidOperationException("Function target is not activated.");

        var chatRequest = request.Payload.Unpack<ChatRequestEvent>().Clone();
        var acceptedSource = new TaskCompletionSource<AppFunctionRuntimeInvokeAccepted>(TaskCreationOptions.RunContinuationsAsynchronously);

        _ = Task.Run(
            () => ExecuteAsync(target, chatRequest, onAcceptedAsync, acceptedSource),
            CancellationToken.None);

        return await acceptedSource.Task.WaitAsync(ct);
    }

    private async Task ExecuteAsync(
        AppFunctionExecutionTarget target,
        ChatRequestEvent chatRequest,
        Func<AppFunctionRuntimeInvokeAccepted, CancellationToken, ValueTask<string>> onAcceptedAsync,
        TaskCompletionSource<AppFunctionRuntimeInvokeAccepted> acceptedSource)
    {
        string? operationId = null;
        var terminalObserved = false;

        try
        {
            var result = await _chatRunService!.ExecuteAsync(
                BuildWorkflowRequest(target, chatRequest),
                async (frame, token) =>
                {
                    if (string.IsNullOrWhiteSpace(operationId))
                        return;

                    var update = BuildFrameUpdate(operationId, frame);
                    if (update == null)
                        return;

                    await _operationCommandPort.AdvanceAsync(update, token);
                    if (IsTerminal(update.Status))
                        terminalObserved = true;
                },
                async (receipt, token) =>
                {
                    var accepted = new AppFunctionRuntimeInvokeAccepted(
                        RequestId: receipt.CommandId,
                        TargetActorId: receipt.ActorId,
                        CommandId: receipt.CommandId,
                        CorrelationId: receipt.CorrelationId);
                    operationId = await onAcceptedAsync(accepted, token);
                    acceptedSource.TrySetResult(accepted);
                },
                _shutdownToken);

            if (!result.Succeeded)
            {
                acceptedSource.TrySetException(new InvalidOperationException(MapStartErrorMessage(result.Error)));
                return;
            }

            if (string.IsNullOrWhiteSpace(operationId))
            {
                acceptedSource.TrySetException(new InvalidOperationException("Workflow runtime did not acknowledge function invocation."));
                return;
            }

            if (!terminalObserved && result.FinalizeResult?.Completed == true)
            {
                var finalizeUpdate = BuildFinalizeUpdate(operationId, result.FinalizeResult.Completion);
                if (finalizeUpdate != null)
                    await _operationCommandPort.AdvanceAsync(finalizeUpdate, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            if (!acceptedSource.Task.IsCompleted)
            {
                acceptedSource.TrySetException(ex);
                return;
            }

            if (!string.IsNullOrWhiteSpace(operationId))
            {
                try
                {
                    await _operationCommandPort.AdvanceAsync(
                        new AppOperationUpdate
                        {
                            OperationId = operationId,
                            Status = AppOperationStatus.Failed,
                            EventCode = "workflow.runtime_failed",
                            Message = ex.Message,
                            OccurredAt = Timestamp.FromDateTime(DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc)),
                            Result = BuildTerminalResult(
                                operationId,
                                AppOperationStatus.Failed,
                                "workflow.runtime_failed",
                                ex.Message,
                                Any.Pack(new StringValue { Value = ex.Message })),
                        },
                        CancellationToken.None);
                }
                catch (Exception advanceEx)
                {
                    _logger.LogWarning(
                        advanceEx,
                        "Failed to publish workflow runtime failure to operation store. operationId={OperationId}",
                        operationId);
                }
            }

            _logger.LogWarning(
                ex,
                "Workflow-backed function invocation bridge failed. appId={AppId} releaseId={ReleaseId} functionId={FunctionId}",
                target.Release.AppId,
                target.Release.ReleaseId,
                target.Entry.EntryId);
        }
    }

    private static WorkflowChatRunRequest BuildWorkflowRequest(
        AppFunctionExecutionTarget target,
        ChatRequestEvent request)
    {
        var metadata = BuildWorkflowMetadata(request.Metadata);
        return new WorkflowChatRunRequest(
            request.Prompt ?? string.Empty,
            WorkflowName: null,
            ActorId: target.PrimaryActorId,
            SessionId: NormalizeOptional(request.SessionId),
            WorkflowYamls: null,
            Metadata: metadata.Count == 0 ? null : metadata,
            ScopeId: ResolveScopeId(request, target.App.OwnerScopeId));
    }

    private static Dictionary<string, string> BuildWorkflowMetadata(
        IDictionary<string, string> metadata)
    {
        var output = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in metadata)
        {
            if (string.IsNullOrWhiteSpace(key))
                continue;

            if (string.Equals(key, WorkflowRunCommandMetadataKeys.ScopeId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "scope_id", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            output[key.Trim()] = value?.Trim() ?? string.Empty;
        }

        return output;
    }

    private static string ResolveScopeId(ChatRequestEvent request, string? ownerScopeId)
    {
        if (!string.IsNullOrWhiteSpace(request.ScopeId))
            return request.ScopeId.Trim();

        if (request.Metadata.TryGetValue(WorkflowRunCommandMetadataKeys.ScopeId, out var workflowScopeId) &&
            !string.IsNullOrWhiteSpace(workflowScopeId))
        {
            return workflowScopeId.Trim();
        }

        if (request.Metadata.TryGetValue("scope_id", out var scopeId) &&
            !string.IsNullOrWhiteSpace(scopeId))
        {
            return scopeId.Trim();
        }

        return ownerScopeId?.Trim() ?? string.Empty;
    }

    private static AppOperationUpdate? BuildFrameUpdate(
        string operationId,
        WorkflowRunEventEnvelope frame)
    {
        var occurredAt = ResolveOccurredAt(frame);

        return frame.EventCase switch
        {
            WorkflowRunEventEnvelope.EventOneofCase.RunStarted => new AppOperationUpdate
            {
                OperationId = operationId,
                Status = AppOperationStatus.Running,
                EventCode = "workflow.run_started",
                Message = "Workflow run started.",
                OccurredAt = occurredAt,
            },
            WorkflowRunEventEnvelope.EventOneofCase.RunFinished => new AppOperationUpdate
            {
                OperationId = operationId,
                Status = AppOperationStatus.Completed,
                EventCode = "workflow.completed",
                Message = "Workflow run completed.",
                OccurredAt = occurredAt,
                Result = BuildTerminalResult(
                    operationId,
                    AppOperationStatus.Completed,
                    "workflow.completed",
                    "Workflow run completed.",
                    frame.RunFinished.Result),
            },
            WorkflowRunEventEnvelope.EventOneofCase.RunError => new AppOperationUpdate
            {
                OperationId = operationId,
                Status = AppOperationStatus.Failed,
                EventCode = NormalizeOptional(frame.RunError.Code) ?? "workflow.failed",
                Message = NormalizeOptional(frame.RunError.Message) ?? "Workflow run failed.",
                OccurredAt = occurredAt,
                Result = BuildTerminalResult(
                    operationId,
                    AppOperationStatus.Failed,
                    NormalizeOptional(frame.RunError.Code) ?? "workflow.failed",
                    NormalizeOptional(frame.RunError.Message) ?? "Workflow run failed.",
                    Any.Pack(new StringValue { Value = NormalizeOptional(frame.RunError.Message) ?? string.Empty })),
            },
            WorkflowRunEventEnvelope.EventOneofCase.RunStopped => new AppOperationUpdate
            {
                OperationId = operationId,
                Status = AppOperationStatus.Cancelled,
                EventCode = "workflow.cancelled",
                Message = NormalizeOptional(frame.RunStopped.Reason) ?? "Workflow run stopped.",
                OccurredAt = occurredAt,
                Result = BuildTerminalResult(
                    operationId,
                    AppOperationStatus.Cancelled,
                    "workflow.cancelled",
                    NormalizeOptional(frame.RunStopped.Reason) ?? "Workflow run stopped.",
                    Any.Pack(new StringValue { Value = NormalizeOptional(frame.RunStopped.Reason) ?? string.Empty })),
            },
            _ => null,
        };
    }

    private static AppOperationUpdate? BuildFinalizeUpdate(
        string operationId,
        WorkflowProjectionCompletionStatus completion)
    {
        var now = Timestamp.FromDateTime(DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc));
        return completion switch
        {
            WorkflowProjectionCompletionStatus.Completed => new AppOperationUpdate
            {
                OperationId = operationId,
                Status = AppOperationStatus.Completed,
                EventCode = "workflow.completed",
                Message = "Workflow run completed.",
                OccurredAt = now,
                Result = BuildTerminalResult(
                    operationId,
                    AppOperationStatus.Completed,
                    "workflow.completed",
                    "Workflow run completed.",
                    null),
            },
            WorkflowProjectionCompletionStatus.Stopped => new AppOperationUpdate
            {
                OperationId = operationId,
                Status = AppOperationStatus.Cancelled,
                EventCode = "workflow.cancelled",
                Message = "Workflow run stopped.",
                OccurredAt = now,
                Result = BuildTerminalResult(
                    operationId,
                    AppOperationStatus.Cancelled,
                    "workflow.cancelled",
                    "Workflow run stopped.",
                    null),
            },
            WorkflowProjectionCompletionStatus.Failed or
            WorkflowProjectionCompletionStatus.TimedOut or
            WorkflowProjectionCompletionStatus.NotFound or
            WorkflowProjectionCompletionStatus.Disabled => new AppOperationUpdate
            {
                OperationId = operationId,
                Status = AppOperationStatus.Failed,
                EventCode = MapFinalizeCode(completion),
                Message = MapFinalizeMessage(completion),
                OccurredAt = now,
                Result = BuildTerminalResult(
                    operationId,
                    AppOperationStatus.Failed,
                    MapFinalizeCode(completion),
                    MapFinalizeMessage(completion),
                    null),
            },
            _ => null,
        };
    }

    private static AppOperationResult BuildTerminalResult(
        string operationId,
        AppOperationStatus status,
        string resultCode,
        string message,
        Any? payload) =>
        new()
        {
            OperationId = operationId,
            Status = status,
            ResultCode = resultCode,
            Message = message,
            Payload = payload?.Clone(),
            CompletedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc)),
        };

    private static Timestamp ResolveOccurredAt(WorkflowRunEventEnvelope frame)
    {
        var unixTimeMs = frame.Timestamp.GetValueOrDefault();
        if (unixTimeMs > 0)
        {
            var instant = DateTimeOffset.FromUnixTimeMilliseconds(unixTimeMs);
            return Timestamp.FromDateTime(instant.UtcDateTime);
        }

        return Timestamp.FromDateTime(DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc));
    }

    private static bool IsTerminal(AppOperationStatus status) =>
        status is AppOperationStatus.Completed or AppOperationStatus.Failed or AppOperationStatus.Cancelled;

    private static string MapFinalizeCode(WorkflowProjectionCompletionStatus completion) =>
        completion switch
        {
            WorkflowProjectionCompletionStatus.TimedOut => "workflow.timed_out",
            WorkflowProjectionCompletionStatus.NotFound => "workflow.not_found",
            WorkflowProjectionCompletionStatus.Disabled => "workflow.disabled",
            _ => "workflow.failed",
        };

    private static string MapFinalizeMessage(WorkflowProjectionCompletionStatus completion) =>
        completion switch
        {
            WorkflowProjectionCompletionStatus.TimedOut => "Workflow run timed out.",
            WorkflowProjectionCompletionStatus.NotFound => "Workflow run was not found.",
            WorkflowProjectionCompletionStatus.Disabled => "Workflow projection is disabled.",
            _ => "Workflow run failed.",
        };

    private static string MapStartErrorMessage(WorkflowChatRunStartError error) =>
        error switch
        {
            WorkflowChatRunStartError.AgentNotFound => "Agent not found.",
            WorkflowChatRunStartError.WorkflowNotFound => "Workflow not found.",
            WorkflowChatRunStartError.AgentTypeNotSupported => "Actor is not workflow-capable.",
            WorkflowChatRunStartError.ProjectionDisabled => "Projection pipeline is disabled.",
            WorkflowChatRunStartError.WorkflowBindingMismatch => "Actor is bound to a different workflow.",
            WorkflowChatRunStartError.AgentWorkflowNotConfigured => "Actor has no bound workflow.",
            WorkflowChatRunStartError.InvalidWorkflowYaml => "Workflow YAML is invalid.",
            WorkflowChatRunStartError.WorkflowNameMismatch => "Workflow name does not match workflow YAML.",
            WorkflowChatRunStartError.PromptRequired => "Prompt is required.",
            WorkflowChatRunStartError.ConflictingScopeId => "Conflicting scope_id values were provided.",
            _ => "Failed to invoke workflow-backed function.",
        };

    private static string? NormalizeOptional(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Length == 0 ? null : normalized;
    }
}
