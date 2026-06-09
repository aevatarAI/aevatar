using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.NyxidChat;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.NyxidChat.WorkflowDraftRun;

public sealed class ChannelWorkflowDraftRunInteractionPort : IChannelWorkflowDraftRunInteractionPort
{
    private readonly IActorRuntime _actorRuntime;
    private readonly IActorDispatchPort _actorDispatchPort;
    private readonly IWorkflowChatRunInteractionPort? _workflowInteractionPort;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ChannelWorkflowDraftRunInteractionPort> _logger;

    public ChannelWorkflowDraftRunInteractionPort(
        IActorRuntime actorRuntime,
        IActorDispatchPort actorDispatchPort,
        ILogger<ChannelWorkflowDraftRunInteractionPort> logger,
        IWorkflowChatRunInteractionPort? workflowInteractionPort = null,
        TimeProvider? timeProvider = null)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _actorDispatchPort = actorDispatchPort ?? throw new ArgumentNullException(nameof(actorDispatchPort));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _workflowInteractionPort = workflowInteractionPort;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task DispatchAsync(NeedsWorkflowDraftRunEvent request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var runId = ChannelWorkflowDraftRunId.Parse(request.RunId, nameof(request.RunId));
        var actorId = ChannelWorkflowDraftRunActorIds.ForRun(runId);
        var actor = await _actorRuntime.CreateAsync<ChannelWorkflowDraftRunGAgent>(actorId, ct).ConfigureAwait(false);

        var commandId = BuildStartCommandId(runId);
        var command = new ChannelWorkflowDraftRunStartRequested
        {
            Request = request.Clone(),
            RunId = runId.Value,
        };
        command.Request.RunId = runId.Value;

        var envelope = new EventEnvelope
        {
            Id = commandId,
            Timestamp = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
            Payload = Any.Pack(command),
            Route = EnvelopeRouteSemantics.CreateDirect("channel-workflow-draft-run-dispatcher", actor.Id),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = string.IsNullOrWhiteSpace(request.CorrelationId)
                    ? runId.Value
                    : request.CorrelationId.Trim(),
            },
            Runtime = new EnvelopeRuntime
            {
                Deduplication = new DeliveryDeduplication
                {
                    OperationId = commandId,
                },
            },
        };

        await _actorDispatchPort.DispatchAsync(actor.Id, envelope, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Accepted workflow draft run for actor dispatch: runId={RunId} actorId={ActorId} commandId={CommandId} target={TargetActorId}",
            runId.Value,
            actor.Id,
            commandId,
            request.TargetActorId);
    }

    public Task StartWorkflowInteractionAsync(
        string runActorId,
        NeedsWorkflowDraftRunEvent request,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runActorId);
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        if (_workflowInteractionPort is null)
        {
            return DispatchToRunActorAsync(
                runActorId,
                BuildCompletion(
                    request,
                    false,
                    false,
                    "workflow_interaction_port_unavailable",
                    "Workflow interaction service is unavailable."),
                ct);
        }

        _ = Task.Run(
            () => ExecuteWorkflowInteractionAsync(runActorId, request.Clone(), CancellationToken.None),
            CancellationToken.None);
        return Task.CompletedTask;
    }

    private static string BuildStartCommandId(ChannelWorkflowDraftRunId runId) =>
        $"workflow-draft-run-start:{runId.Value}";

    private async Task ExecuteWorkflowInteractionAsync(
        string runActorId,
        NeedsWorkflowDraftRunEvent request,
        CancellationToken ct)
    {
        try
        {
            var result = await _workflowInteractionPort!.ExecuteAsync(
                    BuildCommand(request),
                    async (frame, token) =>
                    {
                        var continuationFrame = TryMapFrame(frame);
                        if (continuationFrame is null)
                            return;

                        await DispatchToRunActorAsync(
                                runActorId,
                                new ChannelWorkflowDraftRunFrameObserved
                                {
                                    Request = request.Clone(),
                                    Frame = continuationFrame,
                                    ObservedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                                },
                                token)
                            .ConfigureAwait(false);
                    },
                    onAcceptedAsync: null,
                    ct)
                .ConfigureAwait(false);

            if (!result.Succeeded)
            {
                await DispatchToRunActorAsync(
                        runActorId,
                        BuildCompletion(
                            request,
                            false,
                            false,
                            $"workflow_start_failed:{result.Error}",
                            $"Workflow start failed: {result.Error}"),
                        CancellationToken.None)
                    .ConfigureAwait(false);
                return;
            }

            if (result.FinalizeResult is null || !result.FinalizeResult.Completed)
            {
                await DispatchToRunActorAsync(
                        runActorId,
                        BuildCompletion(
                            request,
                            true,
                            false,
                            "workflow_completion_unknown",
                            "Workflow ended without a terminal frame."),
                        CancellationToken.None)
                    .ConfigureAwait(false);
                return;
            }

            await DispatchToRunActorAsync(
                    runActorId,
                    BuildCompletion(request, true, true, string.Empty, string.Empty),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Workflow draft-run interaction failed: runId={RunId} correlation={CorrelationId}",
                request.RunId,
                request.CorrelationId);
            await DispatchFailureSafelyAsync(
                    runActorId,
                    request,
                    "workflow_draft_run_exception",
                    "Workflow draft-run failed.")
                .ConfigureAwait(false);
        }
    }

    private static WorkflowChatRunRequest BuildCommand(NeedsWorkflowDraftRunEvent request)
    {
        var source = request.WorkflowSource ?? new ChannelWorkflowDraftRunSource();
        var headers = request.Headers.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        return new WorkflowChatRunRequest(
            Prompt: request.Prompt ?? string.Empty,
            Source: WorkflowChatSource.DefinitionActor(source.DefinitionActorId, source.WorkflowName),
            SessionId: request.RunId,
            Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["channel.registration_id"] = request.RegistrationId ?? string.Empty,
                ["channel.correlation_id"] = request.CorrelationId ?? string.Empty,
            },
            ScopeId: source.ScopeId,
            CallerCredential: new WorkflowCallerCredential(request.NyxUserAccessToken),
            Headers: headers,
            CommandIdSeed: request.RunId,
            CorrelationIdSeed: request.CorrelationId);
    }

    private static ChannelWorkflowDraftRunFrame? TryMapFrame(WorkflowRunEventEnvelope frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        return frame.EventCase switch
        {
            WorkflowRunEventEnvelope.EventOneofCase.TextMessageContent => new ChannelWorkflowDraftRunFrame
            {
                TextMessageContent = new ChannelWorkflowDraftRunTextMessageContentFrame
                {
                    Delta = frame.TextMessageContent?.Delta ?? string.Empty,
                },
            },
            WorkflowRunEventEnvelope.EventOneofCase.RunFinished => new ChannelWorkflowDraftRunFrame
            {
                RunFinished = new ChannelWorkflowDraftRunFinishedFrame
                {
                    ResultOutput = TryUnpackResultOutput(frame),
                },
            },
            WorkflowRunEventEnvelope.EventOneofCase.RunError => new ChannelWorkflowDraftRunFrame
            {
                RunError = new ChannelWorkflowDraftRunErrorFrame
                {
                    Message = frame.RunError?.Message ?? string.Empty,
                    Code = frame.RunError?.Code ?? string.Empty,
                },
            },
            WorkflowRunEventEnvelope.EventOneofCase.RunStopped => new ChannelWorkflowDraftRunFrame
            {
                RunStopped = new ChannelWorkflowDraftRunStoppedFrame
                {
                    Reason = frame.RunStopped?.Reason ?? string.Empty,
                },
            },
            _ => null,
        };
    }

    private static string TryUnpackResultOutput(WorkflowRunEventEnvelope frame)
    {
        if (frame.RunFinished?.Result?.Is(WorkflowRunResultPayload.Descriptor) != true)
            return string.Empty;

        return frame.RunFinished.Result.Unpack<WorkflowRunResultPayload>().Output;
    }

    private ChannelWorkflowDraftRunInteractionCompleted BuildCompletion(
        NeedsWorkflowDraftRunEvent request,
        bool succeeded,
        bool completed,
        string errorCode,
        string errorSummary) =>
        new()
        {
            Request = request.Clone(),
            Succeeded = succeeded,
            Completed = completed,
            ErrorCode = errorCode,
            ErrorSummary = errorSummary,
            CompletedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
        };

    private async Task DispatchToRunActorAsync(
        string runActorId,
        IMessage payload,
        CancellationToken ct)
    {
        await _actorDispatchPort.DispatchAsync(
                runActorId,
                new EventEnvelope
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Timestamp = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
                    Payload = Any.Pack(payload),
                    Route = EnvelopeRouteSemantics.CreateDirect("channel-workflow-draft-run-interaction", runActorId),
                    Propagation = new EnvelopePropagation
                    {
                        CorrelationId = payload switch
                        {
                            ChannelWorkflowDraftRunFrameObserved observed => observed.Request?.CorrelationId ?? string.Empty,
                            ChannelWorkflowDraftRunInteractionCompleted completed => completed.Request?.CorrelationId ?? string.Empty,
                            _ => string.Empty,
                        },
                    },
                },
                ct)
            .ConfigureAwait(false);
    }

    private async Task DispatchFailureSafelyAsync(
        string runActorId,
        NeedsWorkflowDraftRunEvent request,
        string errorCode,
        string errorSummary)
    {
        try
        {
            await DispatchToRunActorAsync(
                    runActorId,
                    BuildCompletion(request, false, false, errorCode, errorSummary),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception dispatchEx)
        {
            _logger.LogError(
                dispatchEx,
                "Failed to dispatch workflow draft-run failure continuation: runId={RunId} actorId={ActorId}",
                request.RunId,
                runActorId);
        }
    }
}
