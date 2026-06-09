using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.NyxidChat;
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
    private readonly IChannelWorkflowDraftRunInteractionPort? _workflowInteractionPort;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ChannelWorkflowDraftRunGAgent> _logger;

    public ChannelWorkflowDraftRunGAgent(
        IActorDispatchPort actorDispatchPort,
        WorkflowDraftRunReplyRenderer renderer,
        ILogger<ChannelWorkflowDraftRunGAgent> logger,
        IChannelWorkflowDraftRunInteractionPort? workflowInteractionPort = null,
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
            .On<ChannelWorkflowDraftRunFrameRenderedEvent>(ApplyFrameRendered)
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

        if (_workflowInteractionPort is null)
        {
            await DispatchReadyAndPersistTerminalAsync(
                request,
                BuildFailure("workflow_interaction_port_unavailable", "Workflow interaction service is unavailable."),
                CancellationToken.None);
            return;
        }

        await _workflowInteractionPort.StartWorkflowInteractionAsync(Id, request, CancellationToken.None);
    }

    [EventHandler]
    public async Task HandleWorkflowFrameObservedAsync(ChannelWorkflowDraftRunFrameObserved evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (evt.Request is null || evt.Frame is null || !IsActiveContinuation(evt.Request))
            return;

        var rendered = _renderer.Render(evt.Frame, State.AccumulatedText);
        if (rendered is null)
            return;

        await PersistDomainEventAsync(new ChannelWorkflowDraftRunFrameRenderedEvent
        {
            RunId = evt.Request.RunId,
            CorrelationId = evt.Request.CorrelationId,
            TargetActorId = evt.Request.TargetActorId,
            AccumulatedText = rendered.Text,
            RenderedAtUnixMs = evt.ObservedAtUnixMs > 0
                ? evt.ObservedAtUnixMs
                : _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
        });

        if (rendered.IsTerminal)
        {
            await DispatchReadyAndPersistTerminalAsync(evt.Request, rendered, CancellationToken.None);
            return;
        }

        await DispatchChunkAsync(evt.Request, rendered.Text, CancellationToken.None);
    }

    [EventHandler]
    public async Task HandleWorkflowInteractionCompletedAsync(ChannelWorkflowDraftRunInteractionCompleted evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (evt.Request is null || !IsActiveContinuation(evt.Request))
            return;

        if (!evt.Succeeded)
        {
            await DispatchReadyAndPersistTerminalAsync(
                evt.Request,
                BuildFailure(
                    string.IsNullOrWhiteSpace(evt.ErrorCode) ? "workflow_draft_run_failed" : evt.ErrorCode,
                    string.IsNullOrWhiteSpace(evt.ErrorSummary) ? "Workflow draft-run failed." : evt.ErrorSummary),
                CancellationToken.None);
            return;
        }

        if (!evt.Completed)
        {
            await DispatchReadyAndPersistTerminalAsync(
                evt.Request,
                BuildFailure("workflow_completion_unknown", "Workflow ended without a terminal frame."),
                CancellationToken.None);
            return;
        }

        await DispatchReadyAndPersistTerminalAsync(
            evt.Request,
            new WorkflowDraftRunRenderedFrame(
                string.IsNullOrWhiteSpace(State.AccumulatedText) ? "Workflow 已完成。" : State.AccumulatedText,
                true,
                false),
            CancellationToken.None);
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

    private async Task DispatchReadyAndPersistTerminalAsync(
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
        next.AccumulatedText = string.Empty;
        next.CompletedAtUnixMs = 0;
        next.ErrorCode = string.Empty;
        next.ErrorSummary = string.Empty;
        return next;
    }

    private static ChannelWorkflowDraftRunGAgentState ApplyFrameRendered(
        ChannelWorkflowDraftRunGAgentState current,
        ChannelWorkflowDraftRunFrameRenderedEvent evt)
    {
        var next = current.Clone();
        next.RunId = evt.RunId;
        next.CorrelationId = evt.CorrelationId;
        next.TargetActorId = evt.TargetActorId;
        next.AccumulatedText = evt.AccumulatedText;
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

    private bool IsActiveContinuation(NeedsWorkflowDraftRunEvent request)
    {
        if (State.Status != ChannelWorkflowDraftRunStatus.Started)
        {
            _logger.LogInformation(
                "Ignoring workflow draft-run continuation for non-started run: runId={RunId} status={Status}",
                request.RunId,
                State.Status);
            return false;
        }

        if (!string.Equals(State.RunId, request.RunId, StringComparison.Ordinal) ||
            !string.Equals(State.CorrelationId, request.CorrelationId, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Ignoring stale workflow draft-run continuation: stateRunId={StateRunId} requestRunId={RequestRunId} stateCorrelation={StateCorrelationId} requestCorrelation={RequestCorrelationId}",
                State.RunId,
                request.RunId,
                State.CorrelationId,
                request.CorrelationId);
            return false;
        }

        return true;
    }
}
