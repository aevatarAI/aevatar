using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.NyxidChat;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.NyxidChat.WorkflowDraftRun;

/// <summary>
/// Run-scoped owner for one channel workflow draft-run interaction.
/// </summary>
[GAgent("nyxid.chat.workflow-draft-run")]
public sealed class ChannelWorkflowDraftRunGAgent : GAgentBase<ChannelWorkflowDraftRunGAgentState>
{
    private readonly IActorDispatchPort _actorDispatchPort;
    private readonly WorkflowDraftRunReplyRenderer _renderer;
    private readonly IWorkflowChatRunInteractionPort? _workflowInteractionPort;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ChannelWorkflowDraftRunGAgent> _logger;

    public ChannelWorkflowDraftRunGAgent(
        IActorDispatchPort actorDispatchPort,
        WorkflowDraftRunReplyRenderer renderer,
        ILogger<ChannelWorkflowDraftRunGAgent> logger,
        IWorkflowChatRunInteractionPort? workflowInteractionPort = null,
        TimeProvider? timeProvider = null)
    {
        _actorDispatchPort = actorDispatchPort ?? throw new ArgumentNullException(nameof(actorDispatchPort));
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _workflowInteractionPort = workflowInteractionPort;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    protected override ChannelWorkflowDraftRunGAgentState TransitionState(
        ChannelWorkflowDraftRunGAgentState current,
        IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ChannelWorkflowDraftRunStartedEvent>(ApplyStarted)
            .On<ChannelWorkflowDraftRunReplyHandedOffEvent>(ApplyReplyHandedOff)
            .On<ChannelWorkflowDraftRunFailedEvent>(ApplyFailed)
            .OrCurrent();

    [EventHandler]
    public async Task HandleStartAsync(ChannelWorkflowDraftRunStartRequested command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Request is null)
        {
            _logger.LogWarning("Dropping malformed workflow draft-run start command without request: actor={ActorId}", Id);
            return;
        }

        if (!ChannelWorkflowDraftRunId.TryParse(command.RunId, out var typedCommandRunId))
        {
            _logger.LogWarning(
                "Dropping malformed workflow draft-run start command without run_id: actor={ActorId} correlation={CorrelationId}",
                Id,
                command.Request.CorrelationId);
            return;
        }

        if (State.Status is ChannelWorkflowDraftRunStatus.ReplyHandedOff or ChannelWorkflowDraftRunStatus.Failed)
        {
            _logger.LogInformation(
                "Ignoring terminal workflow draft-run start: runId={RunId} status={Status}",
                State.RunId,
                State.Status);
            return;
        }

        if (State.Status == ChannelWorkflowDraftRunStatus.Started)
        {
            _logger.LogInformation(
                "Ignoring duplicate in-flight workflow draft-run start: runId={RunId} correlation={CorrelationId}",
                State.RunId,
                State.CorrelationId);
            return;
        }

        var request = command.Request.Clone();
        if (!string.IsNullOrWhiteSpace(request.RunId) &&
            !string.Equals(request.RunId.Trim(), typedCommandRunId.Value, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Dropping workflow draft-run start with mismatched run_id: commandRunId={CommandRunId} requestRunId={RequestRunId} actor={ActorId}",
                typedCommandRunId.Value,
                request.RunId,
                Id);
            return;
        }

        request.RunId = typedCommandRunId.Value;
        await PersistDomainEventAsync(new ChannelWorkflowDraftRunStartedEvent
        {
            RunId = request.RunId,
            CorrelationId = request.CorrelationId,
            TargetActorId = request.TargetActorId,
            StartedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
        });

        await ExecuteWorkflowAsync(request);
    }

    private async Task ExecuteWorkflowAsync(NeedsWorkflowDraftRunEvent request)
    {
        if (_workflowInteractionPort is null)
        {
            await DispatchReadyAsync(
                request,
                BuildFailure("workflow_interaction_port_unavailable", "Workflow interaction service is unavailable."),
                CancellationToken.None);
            return;
        }

        var accumulatedText = string.Empty;
        try
        {
            var result = await _workflowInteractionPort.ExecuteAsync(
                    BuildCommand(request),
                    async (frame, token) =>
                    {
                        var rendered = _renderer.Render(frame, accumulatedText);
                        if (rendered is null)
                            return;

                        accumulatedText = rendered.Text;
                        if (rendered.IsTerminal)
                        {
                            await DispatchReadyAsync(request, rendered, token).ConfigureAwait(false);
                            return;
                        }

                        await DispatchChunkAsync(request, accumulatedText, token).ConfigureAwait(false);
                    },
                    onAcceptedAsync: null,
                    CancellationToken.None)
                .ConfigureAwait(false);

            if (!result.Succeeded)
            {
                await DispatchReadyAsync(
                    request,
                    BuildFailure(
                        $"workflow_start_failed:{result.Error}",
                        $"Workflow start failed: {result.Error}"),
                    CancellationToken.None);
                return;
            }

            if (result.FinalizeResult is null || !result.FinalizeResult.Completed)
            {
                await DispatchReadyAsync(
                    request,
                    BuildFailure("workflow_completion_unknown", "Workflow ended without a terminal frame."),
                    CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Workflow draft-run interaction failed: runId={RunId} correlation={CorrelationId}",
                request.RunId,
                request.CorrelationId);
            await DispatchReadyAsync(
                request,
                BuildFailure("workflow_draft_run_exception", "Workflow draft-run failed."),
                CancellationToken.None);
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

    private async Task DispatchChunkAsync(
        NeedsWorkflowDraftRunEvent request,
        string accumulatedText,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(accumulatedText))
            return;

        await DispatchToConversationAsync(
            request,
            new LlmReplyStreamChunkEvent
            {
                CorrelationId = request.CorrelationId,
                RegistrationId = request.RegistrationId,
                Activity = request.Activity?.Clone() ?? new ChatActivity(),
                AccumulatedText = accumulatedText,
                ChunkAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                ReplyToken = request.ReplyToken,
                ReplyTokenExpiresAtUnixMs = request.ReplyTokenExpiresAtUnixMs,
            },
            ct);
    }

    private async Task DispatchReadyAsync(
        NeedsWorkflowDraftRunEvent request,
        WorkflowDraftRunRenderedFrame rendered,
        CancellationToken ct)
    {
        var ready = new LlmReplyReadyEvent
        {
            CorrelationId = request.CorrelationId,
            RunId = request.RunId,
            RegistrationId = request.RegistrationId,
            Activity = request.Activity?.Clone() ?? new ChatActivity(),
            Outbound = new MessageContent { Text = rendered.Text },
            TerminalState = rendered.IsFailure ? LlmReplyTerminalState.Failed : LlmReplyTerminalState.Completed,
            ErrorCode = rendered.ErrorCode,
            ErrorSummary = rendered.IsFailure ? rendered.Text : string.Empty,
            ReadyAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            ReplyToken = request.ReplyToken,
            ReplyTokenExpiresAtUnixMs = request.ReplyTokenExpiresAtUnixMs,
        };

        if (!rendered.IsFailure)
        {
            ready.AppendedHistory.Add(new ConversationHistoryEntry
            {
                Role = "assistant",
                Content = rendered.Text,
            });
        }

        await DispatchToConversationAsync(request, ready, ct);
        await PersistTerminalAsync(request, rendered);
    }

    private async Task DispatchToConversationAsync(
        NeedsWorkflowDraftRunEvent request,
        IMessage payload,
        CancellationToken ct)
    {
        var targetActorId = request.TargetActorId;
        if (string.IsNullOrWhiteSpace(targetActorId))
            throw new InvalidOperationException("Workflow draft-run request target actor id is required.");

        await _actorDispatchPort.DispatchAsync(
            targetActorId,
            new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Timestamp = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
                Payload = Any.Pack(payload),
                Route = EnvelopeRouteSemantics.CreateDirect(
                    "channel-workflow-draft-run-runner",
                    targetActorId),
                Propagation = new EnvelopePropagation
                {
                    CorrelationId = request.CorrelationId,
                },
            },
            ct);
    }

    private async Task PersistTerminalAsync(
        NeedsWorkflowDraftRunEvent request,
        WorkflowDraftRunRenderedFrame rendered)
    {
        if (rendered.IsFailure)
        {
            await PersistDomainEventAsync(new ChannelWorkflowDraftRunFailedEvent
            {
                RunId = request.RunId,
                CorrelationId = request.CorrelationId,
                TargetActorId = request.TargetActorId,
                ErrorCode = rendered.ErrorCode,
                ErrorSummary = rendered.Text,
                FailedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            });
            return;
        }

        await PersistDomainEventAsync(new ChannelWorkflowDraftRunReplyHandedOffEvent
        {
            RunId = request.RunId,
            CorrelationId = request.CorrelationId,
            TargetActorId = request.TargetActorId,
            HandedOffAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
        });
    }

    private static WorkflowDraftRunRenderedFrame BuildFailure(string errorCode, string text) =>
        new(text, true, true, errorCode);

    private static ChannelWorkflowDraftRunGAgentState ApplyStarted(
        ChannelWorkflowDraftRunGAgentState current,
        ChannelWorkflowDraftRunStartedEvent evt)
    {
        var next = current.Clone();
        next.RunId = evt.RunId;
        next.CorrelationId = evt.CorrelationId;
        next.TargetActorId = evt.TargetActorId;
        next.Status = ChannelWorkflowDraftRunStatus.Started;
        next.StartedAtUnixMs = evt.StartedAtUnixMs;
        return next;
    }

    private static ChannelWorkflowDraftRunGAgentState ApplyReplyHandedOff(
        ChannelWorkflowDraftRunGAgentState current,
        ChannelWorkflowDraftRunReplyHandedOffEvent evt)
    {
        var next = current.Clone();
        next.RunId = evt.RunId;
        next.CorrelationId = evt.CorrelationId;
        next.TargetActorId = evt.TargetActorId;
        next.Status = ChannelWorkflowDraftRunStatus.ReplyHandedOff;
        next.CompletedAtUnixMs = evt.HandedOffAtUnixMs;
        next.ErrorCode = string.Empty;
        next.ErrorSummary = string.Empty;
        return next;
    }

    private static ChannelWorkflowDraftRunGAgentState ApplyFailed(
        ChannelWorkflowDraftRunGAgentState current,
        ChannelWorkflowDraftRunFailedEvent evt)
    {
        var next = current.Clone();
        next.RunId = evt.RunId;
        next.CorrelationId = evt.CorrelationId;
        next.TargetActorId = evt.TargetActorId;
        next.Status = ChannelWorkflowDraftRunStatus.Failed;
        next.CompletedAtUnixMs = evt.FailedAtUnixMs;
        next.ErrorCode = evt.ErrorCode;
        next.ErrorSummary = evt.ErrorSummary;
        return next;
    }
}
