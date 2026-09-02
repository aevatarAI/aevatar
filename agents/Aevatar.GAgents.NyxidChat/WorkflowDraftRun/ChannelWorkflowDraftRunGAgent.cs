using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.NyxidChat;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.NyxidChat.WorkflowDraftRun;

/// <summary>
/// Run-scoped owner for one channel workflow draft-run interaction.
/// </summary>
[GAgent("nyxid.chat.workflow-draft-run")]
public sealed class ChannelWorkflowDraftRunGAgent : GAgentBase<ChannelWorkflowDraftRunGAgentState>
{
    private const string RecoveryTimeoutErrorCode = "workflow_draft_run_recovery_timeout";
    private const string RecoveryContextMissingErrorCode = "workflow_draft_run_recovery_context_missing";
    private const string RelayReplyTokenSecretPurpose = "channel-relay-reply-token";
    private const string RelayUserAccessTokenSecretPurpose = "channel-relay-user-access-token";
    private static readonly TimeSpan RecoveryTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan TerminalHandoffSafetyWindow = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan TerminalHandoffRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ImmediateCallbackDelay = TimeSpan.FromMilliseconds(1);
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
            .On<ChannelWorkflowDraftRunTerminalProducedEvent>(ApplyTerminalProduced)
            .On<ChannelWorkflowDraftRunReplyHandedOffEvent>(ApplyReplyHandedOff)
            .On<ChannelWorkflowDraftRunFailedEvent>(ApplyFailed)
            .OrCurrent();

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        await base.OnActivateAsync(ct);
        if (State.Status is ChannelWorkflowDraftRunStatus.ReplyHandedOff or ChannelWorkflowDraftRunStatus.Failed)
        {
            await PurgeDurableCallbacksBestEffortAsync();
            return;
        }

        if (State.Status == ChannelWorkflowDraftRunStatus.Started)
            await EnsureRecoveryTimeoutAsync(ct);
        else if (State.Status == ChannelWorkflowDraftRunStatus.TerminalProduced)
            await EnsureTerminalHandoffRetryAsync(ct);
    }

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

        if (State.Status == ChannelWorkflowDraftRunStatus.TerminalProduced)
        {
            _logger.LogInformation(
                "Reconciling duplicate workflow draft-run start from durable terminal outbox: runId={RunId} correlation={CorrelationId}",
                State.RunId,
                State.CorrelationId);
            await EnsureTerminalHandoffRetryAsync(CancellationToken.None);
            await TryHandoffProducedTerminalAsync(CancellationToken.None);
            return;
        }

        if (State.Status == ChannelWorkflowDraftRunStatus.Started)
        {
            _logger.LogInformation(
                "Reconciling duplicate in-flight workflow draft-run start without redispatch: runId={RunId} correlation={CorrelationId}",
                State.RunId,
                State.CorrelationId);
            await EnsureRecoveryTimeoutAsync(CancellationToken.None);
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
        await EnsureRuntimeSecretReferencesAsync(request, CancellationToken.None);
        var startedAt = _timeProvider.GetUtcNow();
        await PersistDomainEventAsync(new ChannelWorkflowDraftRunStartedEvent
        {
            RunId = request.RunId,
            CorrelationId = request.CorrelationId,
            TargetActorId = request.TargetActorId,
            StartedAtUnixMs = startedAt.ToUnixTimeMilliseconds(),
            RecoveryRequest = BuildRecoveryRequest(request),
            RecoveryDeadlineUnixMs = ResolveRecoveryDeadline(request, startedAt).ToUnixTimeMilliseconds(),
        });
        await EnsureRecoveryTimeoutAsync(CancellationToken.None);

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

    private static DateTimeOffset ResolveRecoveryDeadline(
        NeedsWorkflowDraftRunEvent request,
        DateTimeOffset startedAt)
    {
        var deadline = startedAt.Add(RecoveryTimeout);
        if (request.ReplyTokenExpiresAtUnixMs <= 0)
            return deadline;

        var credentialDeadline = DateTimeOffset
            .FromUnixTimeMilliseconds(request.ReplyTokenExpiresAtUnixMs)
            .Subtract(TerminalHandoffSafetyWindow);
        if (credentialDeadline <= startedAt)
            return startedAt;

        return credentialDeadline < deadline ? credentialDeadline : deadline;
    }

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public async Task HandleRecoveryTimeoutAsync(ChannelWorkflowDraftRunRecoveryTimeoutElapsed signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (State.Status != ChannelWorkflowDraftRunStatus.Started ||
            !string.Equals(State.RunId, signal.RunId, StringComparison.Ordinal) ||
            !string.Equals(State.CorrelationId, signal.CorrelationId, StringComparison.Ordinal) ||
            State.RecoveryDeadlineUnixMs != signal.RecoveryDeadlineUnixMs)
        {
            return;
        }

        if (State.RecoveryRequest is null || State.RecoveryDeadlineUnixMs <= 0)
        {
            await DispatchReadyAndPersistTerminalAsync(
                BuildLegacyRecoveryRequest(),
                BuildFailure(
                    RecoveryContextMissingErrorCode,
                    "Workflow draft-run recovery context is unavailable after restart."),
                CancellationToken.None);
            return;
        }

        if (_timeProvider.GetUtcNow().ToUnixTimeMilliseconds() < State.RecoveryDeadlineUnixMs)
        {
            await EnsureRecoveryTimeoutAsync(CancellationToken.None);
            return;
        }

        await DispatchReadyAndPersistTerminalAsync(
            State.RecoveryRequest.Clone(),
            BuildFailure(
                RecoveryTimeoutErrorCode,
                "Workflow draft-run did not complete before its durable recovery deadline."),
            CancellationToken.None);
    }

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public async Task HandleTerminalHandoffRetryAsync(ChannelWorkflowDraftRunTerminalHandoffRetryElapsed signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (State.Status != ChannelWorkflowDraftRunStatus.TerminalProduced ||
            !string.Equals(State.RunId, signal.RunId, StringComparison.Ordinal) ||
            !string.Equals(State.CorrelationId, signal.CorrelationId, StringComparison.Ordinal) ||
            !string.Equals(State.TerminalOperationId, signal.OperationId, StringComparison.Ordinal))
        {
            return;
        }

        await EnsureTerminalHandoffRetryAsync(CancellationToken.None);
        await TryHandoffProducedTerminalAsync(CancellationToken.None);
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
        await EnsureRuntimeSecretReferencesAsync(request, ct);
        var activity = request.Activity?.Clone() ?? new ChatActivity();
        if (activity.TransportExtras is not null)
            activity.TransportExtras.NyxUserAccessToken = string.Empty;
        var ready = new LlmReplyReadyEvent
        {
            CorrelationId = request.CorrelationId,
            RunId = request.RunId,
            RegistrationId = request.RegistrationId,
            SourceActorId = Id,
            Activity = activity,
            Outbound = new MessageContent { Text = rendered.Text },
            TerminalState = rendered.IsFailure ? LlmReplyTerminalState.Failed : LlmReplyTerminalState.Completed,
            ErrorCode = rendered.ErrorCode,
            ErrorSummary = rendered.IsFailure ? rendered.Text : string.Empty,
            ReadyAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            RelayReplyTokenRef = request.RelayReplyTokenRef?.Clone(),
            RelayUserAccessTokenRef = request.RelayUserAccessTokenRef?.Clone(),
        };

        if (!rendered.IsFailure)
        {
            ready.AppendedHistory.Add(new ConversationHistoryEntry
            {
                Role = "assistant",
                Content = rendered.Text,
            });
        }

        var operationId = BuildTerminalOperationId(request.RunId);
        await PersistDomainEventAsync(new ChannelWorkflowDraftRunTerminalProducedEvent
        {
            RunId = request.RunId,
            CorrelationId = request.CorrelationId,
            TargetActorId = request.TargetActorId,
            TerminalReply = ready,
            OperationId = operationId,
            ProducedAtUnixMs = ready.ReadyAtUnixMs,
        });
        await EnsureTerminalHandoffRetryAsync(ct);
        await TryHandoffProducedTerminalAsync(ct);
    }

    private async Task TryHandoffProducedTerminalAsync(CancellationToken ct)
    {
        if (State.Status != ChannelWorkflowDraftRunStatus.TerminalProduced ||
            State.ProducedTerminalReply is null ||
            string.IsNullOrWhiteSpace(State.TerminalOperationId))
        {
            return;
        }

        try
        {
            await DispatchEnvelopeAsync(
                State.TargetActorId,
                State.CorrelationId,
                State.ProducedTerminalReply,
                ct,
                State.TerminalOperationId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Workflow draft-run terminal admission failed; durable outbox remains pending: runId={RunId} correlation={CorrelationId}",
                State.RunId,
                State.CorrelationId);
            return;
        }

        try
        {
            await PersistTerminalAsync(State.ProducedTerminalReply);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Workflow draft-run terminal admission succeeded but final state append failed; durable outbox will replay: runId={RunId} correlation={CorrelationId}",
                State.RunId,
                State.CorrelationId);
            return;
        }

        await PurgeDurableCallbacksBestEffortAsync();
    }

    private async Task DispatchToConversationAsync(
        NeedsWorkflowDraftRunEvent request,
        IMessage payload,
        CancellationToken ct,
        string? operationId = null)
    {
        await DispatchEnvelopeAsync(request.TargetActorId, request.CorrelationId, payload, ct, operationId);
    }

    private async Task DispatchEnvelopeAsync(
        string targetActorId,
        string correlationId,
        IMessage payload,
        CancellationToken ct,
        string? operationId = null)
    {
        if (string.IsNullOrWhiteSpace(targetActorId))
            throw new InvalidOperationException("Workflow draft-run request target actor id is required.");

        var envelopeId = string.IsNullOrWhiteSpace(operationId)
            ? Guid.NewGuid().ToString("N")
            : operationId;
        var envelope = new EventEnvelope
        {
            Id = envelopeId,
            Timestamp = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateDirect(
                "channel-workflow-draft-run-runner",
                targetActorId),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = correlationId,
            },
        };
        if (!string.IsNullOrWhiteSpace(operationId))
        {
            envelope.Runtime = new EnvelopeRuntime
            {
                DeliveryIdentity = new DeliveryIdentity
                {
                    OperationId = operationId,
                },
            };
        }

        var admission = await _actorDispatchPort.DispatchAsync(
            targetActorId,
            envelope,
            ct);
        if (!admission.Accepted)
            throw new InvalidOperationException($"Workflow draft-run terminal envelope '{envelopeId}' was not accepted.");
    }

    private async Task EnsureRecoveryTimeoutAsync(CancellationToken ct)
    {
        if (State.Status != ChannelWorkflowDraftRunStatus.Started)
            return;

        var remaining = State.RecoveryDeadlineUnixMs <= 0
            ? ImmediateCallbackDelay
            : DateTimeOffset.FromUnixTimeMilliseconds(State.RecoveryDeadlineUnixMs) - _timeProvider.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
            remaining = ImmediateCallbackDelay;

        await ScheduleSelfDurableTimeoutAsync(
            BuildRecoveryCallbackId(State.RunId),
            remaining,
            new ChannelWorkflowDraftRunRecoveryTimeoutElapsed
            {
                RunId = State.RunId,
                CorrelationId = State.CorrelationId,
                RecoveryDeadlineUnixMs = State.RecoveryDeadlineUnixMs,
            },
            ct: ct);
    }

    private async Task EnsureTerminalHandoffRetryAsync(CancellationToken ct)
    {
        if (State.Status != ChannelWorkflowDraftRunStatus.TerminalProduced ||
            string.IsNullOrWhiteSpace(State.TerminalOperationId))
        {
            return;
        }

        await ScheduleSelfDurableTimeoutAsync(
            BuildTerminalHandoffRetryCallbackId(State.RunId),
            TerminalHandoffRetryDelay,
            new ChannelWorkflowDraftRunTerminalHandoffRetryElapsed
            {
                RunId = State.RunId,
                CorrelationId = State.CorrelationId,
                OperationId = State.TerminalOperationId,
            },
            ct: ct);
    }

    private async Task EnsureRuntimeSecretReferencesAsync(
        NeedsWorkflowDraftRunEvent request,
        CancellationToken ct)
    {
        var secretStore = Services.GetService<IRuntimeSecretStore>();
        if (secretStore is null)
            return;

        var expiresAt = request.ReplyTokenExpiresAtUnixMs > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(request.ReplyTokenExpiresAtUnixMs)
            : _timeProvider.GetUtcNow().Add(RecoveryTimeout);
        var timeToLive = expiresAt - _timeProvider.GetUtcNow();
        if (timeToLive <= TimeSpan.Zero)
            return;

        if (string.IsNullOrWhiteSpace(request.RelayReplyTokenRef?.Ref) &&
            !string.IsNullOrWhiteSpace(request.ReplyToken))
        {
            request.RelayReplyTokenRef = (await secretStore.PutAsync(
                new StoreRuntimeSecretRequest(
                    RelayReplyTokenSecretPurpose,
                    request.RunId,
                    request.CorrelationId,
                    request.ReplyToken,
                    timeToLive,
                    ConsumeOnce: false,
                    AuditReason: "Preserve workflow draft-run reply credential for durable terminal handoff."),
                ct)).Reference;
        }

        if (string.IsNullOrWhiteSpace(request.RelayUserAccessTokenRef?.Ref) &&
            !string.IsNullOrWhiteSpace(request.NyxUserAccessToken))
        {
            request.RelayUserAccessTokenRef = (await secretStore.PutAsync(
                new StoreRuntimeSecretRequest(
                    RelayUserAccessTokenSecretPurpose,
                    request.RunId,
                    request.CorrelationId,
                    request.NyxUserAccessToken,
                    timeToLive,
                    ConsumeOnce: false,
                    AuditReason: "Preserve workflow draft-run user credential for durable recovery."),
                ct)).Reference;
        }
    }

    private async Task PurgeDurableCallbacksBestEffortAsync()
    {
        try
        {
            await Services.GetRequiredService<IActorRuntimeCallbackScheduler>()
                .PurgeActorAsync(Id, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Workflow draft-run callback cleanup failed: actorId={ActorId} runId={RunId} status={Status}",
                Id,
                State.RunId,
                State.Status);
        }
    }

    private static NeedsWorkflowDraftRunEvent BuildRecoveryRequest(NeedsWorkflowDraftRunEvent request)
    {
        var recovery = request.Clone();
        recovery.ReplyToken = string.Empty;
        recovery.ReplyTokenExpiresAtUnixMs = 0;
        recovery.NyxUserAccessToken = string.Empty;
        if (recovery.Activity?.TransportExtras is not null)
            recovery.Activity.TransportExtras.NyxUserAccessToken = string.Empty;
        return recovery;
    }

    private NeedsWorkflowDraftRunEvent BuildLegacyRecoveryRequest() =>
        new()
        {
            RunId = State.RunId,
            CorrelationId = State.CorrelationId,
            TargetActorId = State.TargetActorId,
        };

    private static string BuildRecoveryCallbackId(string runId) =>
        $"workflow-draft-run-recovery:{runId}";

    private static string BuildTerminalHandoffRetryCallbackId(string runId) =>
        $"workflow-draft-run-terminal-handoff:{runId}";

    private static string BuildTerminalOperationId(string runId) =>
        $"workflow-draft-run-terminal:{runId}";

    private async Task PersistTerminalAsync(LlmReplyReadyEvent ready)
    {
        if (ready.TerminalState == LlmReplyTerminalState.Failed)
        {
            await PersistDomainEventAsync(new ChannelWorkflowDraftRunFailedEvent
            {
                RunId = ready.RunId,
                CorrelationId = ready.CorrelationId,
                TargetActorId = State.TargetActorId,
                ErrorCode = ready.ErrorCode,
                ErrorSummary = ready.ErrorSummary,
                FailedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            });
            return;
        }

        await PersistDomainEventAsync(new ChannelWorkflowDraftRunReplyHandedOffEvent
        {
            RunId = ready.RunId,
            CorrelationId = ready.CorrelationId,
            TargetActorId = State.TargetActorId,
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
        next.RecoveryRequest = evt.RecoveryRequest?.Clone();
        next.RecoveryDeadlineUnixMs = evt.RecoveryDeadlineUnixMs;
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

    private static ChannelWorkflowDraftRunGAgentState ApplyTerminalProduced(
        ChannelWorkflowDraftRunGAgentState current,
        ChannelWorkflowDraftRunTerminalProducedEvent evt)
    {
        var next = current.Clone();
        next.RunId = evt.RunId;
        next.CorrelationId = evt.CorrelationId;
        next.TargetActorId = evt.TargetActorId;
        next.Status = ChannelWorkflowDraftRunStatus.TerminalProduced;
        next.ProducedTerminalReply = evt.TerminalReply?.Clone();
        next.TerminalOperationId = evt.OperationId;
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
        next.RecoveryRequest = null;
        next.RecoveryDeadlineUnixMs = 0;
        next.ProducedTerminalReply = null;
        next.TerminalOperationId = string.Empty;
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
        next.RecoveryRequest = null;
        next.RecoveryDeadlineUnixMs = 0;
        next.ProducedTerminalReply = null;
        next.TerminalOperationId = string.Empty;
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
