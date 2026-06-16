using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Google.Protobuf;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.NyxidChat.WorkflowRunDelivery;

[GAgent("nyxid.chat.workflow-run-delivery")]
public sealed class WorkflowRunDeliveryGAgent : GAgentBase<WorkflowRunDeliveryGAgentState>
{
    private readonly IWorkflowExecutionProjectionPort _projectionPort;
    private readonly IActorDispatchPort _dispatchPort;
    private readonly NyxIdRelayOutboundPort _outboundPort;
    private readonly ICredentialProvider _credentialProvider;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WorkflowRunDeliveryGAgent> _logger;
    private IEventSink<WorkflowRunEventEnvelope>? _sink;
    private IAsyncDisposable? _liveSinkLease;

    public WorkflowRunDeliveryGAgent(
        IWorkflowExecutionProjectionPort projectionPort,
        IActorDispatchPort dispatchPort,
        NyxIdRelayOutboundPort outboundPort,
        ICredentialProvider credentialProvider,
        ILogger<WorkflowRunDeliveryGAgent> logger,
        TimeProvider? timeProvider = null)
    {
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _outboundPort = outboundPort ?? throw new ArgumentNullException(nameof(outboundPort));
        _credentialProvider = credentialProvider ?? throw new ArgumentNullException(nameof(credentialProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    protected override WorkflowRunDeliveryGAgentState TransitionState(
        WorkflowRunDeliveryGAgentState current,
        IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<WorkflowRunDeliveryStartedEvent>(ApplyStarted)
            .On<WorkflowRunDeliverySucceededEvent>(ApplySucceeded)
            .On<WorkflowRunDeliveryFailedEvent>(ApplyFailed)
            .OrCurrent();

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        if (State.Status == WorkflowRunDeliveryStatus.Started)
            await AttachProjectionAsync(ct).ConfigureAwait(false);
    }

    protected override async Task OnDeactivateAsync(CancellationToken ct)
    {
        await DetachProjectionAsync(ct).ConfigureAwait(false);
    }

    [EventHandler]
    public async Task HandleStartAsync(WorkflowRunDeliveryStartRequested command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (State.Status is WorkflowRunDeliveryStatus.Delivered or WorkflowRunDeliveryStatus.Failed)
            return;

        if (State.Status == WorkflowRunDeliveryStatus.Started)
        {
            await AttachProjectionAsync(CancellationToken.None).ConfigureAwait(false);
            return;
        }

        var validationError = ValidateStart(command);
        if (validationError is not null)
        {
            await PersistDomainEventAsync(new WorkflowRunDeliveryFailedEvent
            {
                DeliveryId = command.DeliveryId ?? string.Empty,
                WorkflowActorId = command.WorkflowActorId ?? string.Empty,
                WorkflowCommandId = command.WorkflowCommandId ?? string.Empty,
                ErrorCode = validationError.Value.Code,
                ErrorSummary = validationError.Value.Summary,
                Attempt = 0,
                FailedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            });
            return;
        }

        await PersistDomainEventAsync(new WorkflowRunDeliveryStartedEvent
        {
            DeliveryId = command.DeliveryId.Trim(),
            WorkflowActorId = command.WorkflowActorId.Trim(),
            WorkflowRunId = command.WorkflowRunId?.Trim() ?? string.Empty,
            WorkflowCommandId = command.WorkflowCommandId.Trim(),
            WorkflowCorrelationId = command.WorkflowCorrelationId?.Trim() ?? string.Empty,
            StreamTopic = command.StreamTopic?.Trim() ?? string.Empty,
            ChannelPlatform = command.ChannelPlatform.Trim(),
            ReplyMessageId = command.ReplyMessageId.Trim(),
            PlatformMessageId = command.PlatformMessageId?.Trim() ?? string.Empty,
            RegistrationScopeId = command.RegistrationScopeId?.Trim() ?? string.Empty,
            DurableReplyCredentialRef = command.DurableReplyCredentialRef.Trim(),
            StartedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
        });

        await AttachProjectionAsync(CancellationToken.None).ConfigureAwait(false);
    }

    [EventHandler]
    public async Task HandleTerminalFrameObservedAsync(WorkflowRunDeliveryTerminalFrameObserved command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!IsCurrentTerminalFrame(command))
            return;

        var attempt = Math.Max(1, State.DeliveryAttempt + 1);
        var observedAt = command.ObservedAtUnixMs > 0
            ? command.ObservedAtUnixMs
            : _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var text = BuildTerminalReplyText(command);
        var durableReplyCredential = await ResolveDurableReplyCredentialAsync(command, attempt, observedAt)
            .ConfigureAwait(false);
        if (durableReplyCredential is null)
            return;

        var result = await _outboundPort.SendWithAgentKeyAsync(
                State.ChannelPlatform,
                BuildConversationReference(),
                new MessageContent { Text = text },
                new OutboundDeliveryContext
                {
                    ReplyMessageId = State.ReplyMessageId,
                    CorrelationId = string.IsNullOrWhiteSpace(State.WorkflowCorrelationId)
                        ? State.WorkflowCommandId
                        : State.WorkflowCorrelationId,
                },
                durableReplyCredential,
                CancellationToken.None)
            .ConfigureAwait(false);

        if (result.Success)
        {
            await PersistDomainEventAsync(new WorkflowRunDeliverySucceededEvent
            {
                DeliveryId = State.DeliveryId,
                WorkflowActorId = State.WorkflowActorId,
                WorkflowCommandId = State.WorkflowCommandId,
                SentActivityId = result.SentActivityId,
                PlatformMessageId = result.PlatformMessageId,
                Attempt = attempt,
                DeliveredAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                TerminalStatus = command.Status,
                TerminalText = command.Text,
                TerminalErrorCode = command.ErrorCode,
                TerminalObservedAtUnixMs = observedAt,
            });
            await DetachProjectionAsync(CancellationToken.None).ConfigureAwait(false);
            return;
        }

        await PersistDomainEventAsync(new WorkflowRunDeliveryFailedEvent
        {
            DeliveryId = State.DeliveryId,
            WorkflowActorId = State.WorkflowActorId,
            WorkflowCommandId = State.WorkflowCommandId,
            ErrorCode = string.IsNullOrWhiteSpace(result.ErrorCode)
                ? "workflow_run_delivery_failed"
                : result.ErrorCode,
            ErrorSummary = string.IsNullOrWhiteSpace(result.ErrorMessage)
                ? "Workflow terminal reply delivery failed."
                : result.ErrorMessage,
            Attempt = attempt,
            FailedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            TerminalStatus = command.Status,
            TerminalText = command.Text,
            TerminalErrorCode = command.ErrorCode,
            TerminalObservedAtUnixMs = observedAt,
        });
        await DetachProjectionAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<string?> ResolveDurableReplyCredentialAsync(
        WorkflowRunDeliveryTerminalFrameObserved command,
        int attempt,
        long observedAt)
    {
        string? credential;
        try
        {
            credential = await _credentialProvider.ResolveAsync(
                    State.DurableReplyCredentialRef,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Workflow run delivery credential resolution failed: deliveryId={DeliveryId} credentialRef={CredentialRef}",
                State.DeliveryId,
                State.DurableReplyCredentialRef);
            await PersistDeliveryFailureAsync(
                    command,
                    attempt,
                    observedAt,
                    "durable_reply_credential_unavailable",
                    "Workflow terminal reply credential could not be resolved.")
                .ConfigureAwait(false);
            await DetachProjectionAsync(CancellationToken.None).ConfigureAwait(false);
            return null;
        }

        if (!string.IsNullOrWhiteSpace(credential))
            return credential.Trim();

        await PersistDeliveryFailureAsync(
                command,
                attempt,
                observedAt,
                "durable_reply_credential_missing",
                "Workflow terminal reply credential reference is not available.")
            .ConfigureAwait(false);
        await DetachProjectionAsync(CancellationToken.None).ConfigureAwait(false);
        return null;
    }

    private async Task PersistDeliveryFailureAsync(
        WorkflowRunDeliveryTerminalFrameObserved command,
        int attempt,
        long observedAt,
        string errorCode,
        string errorSummary)
    {
        await PersistDomainEventAsync(new WorkflowRunDeliveryFailedEvent
        {
            DeliveryId = State.DeliveryId,
            WorkflowActorId = State.WorkflowActorId,
            WorkflowCommandId = State.WorkflowCommandId,
            ErrorCode = errorCode,
            ErrorSummary = errorSummary,
            Attempt = attempt,
            FailedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            TerminalStatus = command.Status,
            TerminalText = command.Text,
            TerminalErrorCode = command.ErrorCode,
            TerminalObservedAtUnixMs = observedAt,
        });
    }

    private async Task AttachProjectionAsync(CancellationToken ct)
    {
        if (_liveSinkLease != null || State.Status != WorkflowRunDeliveryStatus.Started)
            return;

        if (!_projectionPort.ProjectionEnabled)
        {
            await PersistProjectionFailureAsync("projection_disabled", "Workflow execution projection is disabled.").ConfigureAwait(false);
            return;
        }

        _sink = new WorkflowRunDeliveryProjectionSink(
            _dispatchPort,
            Id,
            State.DeliveryId,
            State.WorkflowActorId,
            State.WorkflowCommandId,
            _timeProvider,
            _logger);

        var attachment = await _projectionPort.AttachExistingActorProjectionAsync(
                State.WorkflowActorId,
                State.WorkflowCommandId,
                _sink,
                ct)
            .ConfigureAwait(false);
        if (attachment == null)
        {
            await _sink.DisposeAsync().ConfigureAwait(false);
            _sink = null;
            await PersistProjectionFailureAsync(
                    "projection_unavailable",
                    "Workflow execution projection session is unavailable for background delivery.")
                .ConfigureAwait(false);
            return;
        }

        _liveSinkLease = attachment.LiveSinkLease;
    }

    private async Task DetachProjectionAsync(CancellationToken ct)
    {
        var liveSinkLease = _liveSinkLease;
        var sink = _sink;
        _liveSinkLease = null;
        _sink = null;

        if (liveSinkLease != null)
            await _projectionPort.DetachLiveSinkAsync(liveSinkLease, ct).ConfigureAwait(false);
        if (sink != null)
            await sink.DisposeAsync().ConfigureAwait(false);
    }

    private async Task PersistProjectionFailureAsync(string errorCode, string errorSummary)
    {
        if (State.Status != WorkflowRunDeliveryStatus.Started)
            return;

        await PersistDomainEventAsync(new WorkflowRunDeliveryFailedEvent
        {
            DeliveryId = State.DeliveryId,
            WorkflowActorId = State.WorkflowActorId,
            WorkflowCommandId = State.WorkflowCommandId,
            ErrorCode = errorCode,
            ErrorSummary = errorSummary,
            Attempt = State.DeliveryAttempt,
            FailedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
        });
    }

    private bool IsCurrentTerminalFrame(WorkflowRunDeliveryTerminalFrameObserved command)
    {
        if (State.Status != WorkflowRunDeliveryStatus.Started)
            return false;
        if (!string.Equals(State.DeliveryId, command.DeliveryId, StringComparison.Ordinal))
            return false;
        if (!string.Equals(State.WorkflowActorId, command.WorkflowActorId, StringComparison.Ordinal))
            return false;
        return string.Equals(State.WorkflowCommandId, command.WorkflowCommandId, StringComparison.Ordinal);
    }

    private ConversationReference BuildConversationReference()
    {
        if (string.IsNullOrWhiteSpace(State.ChannelPlatform))
            return new ConversationReference();

        var bot = string.IsNullOrWhiteSpace(State.DurableReplyCredentialRef)
            ? "nyx-relay-bot"
            : State.DurableReplyCredentialRef;
        var partition = string.IsNullOrWhiteSpace(State.RegistrationScopeId)
            ? State.ReplyMessageId
            : State.RegistrationScopeId;
        return ConversationReference.Create(
            ChannelId.From(State.ChannelPlatform),
            BotInstanceId.From(bot),
            ConversationScope.Unspecified,
            partition,
            string.IsNullOrWhiteSpace(partition) ? State.ReplyMessageId : partition,
            State.ReplyMessageId);
    }

    private static string BuildTerminalReplyText(WorkflowRunDeliveryTerminalFrameObserved command)
    {
        var text = string.IsNullOrWhiteSpace(command.Text)
            ? command.Status switch
            {
                "completed" => "Workflow completed.",
                "stopped" => "Workflow stopped.",
                _ => "Workflow failed.",
            }
            : command.Text.Trim();

        if (string.Equals(command.Status, "completed", StringComparison.Ordinal))
            return text;
        if (string.Equals(command.Status, "stopped", StringComparison.Ordinal))
            return $"Workflow stopped: {text}";

        var code = string.IsNullOrWhiteSpace(command.ErrorCode)
            ? "workflow_run_error"
            : command.ErrorCode.Trim();
        return $"Workflow failed ({code}): {text}";
    }

    private static (string Code, string Summary)? ValidateStart(WorkflowRunDeliveryStartRequested command)
    {
        if (string.IsNullOrWhiteSpace(command.DeliveryId))
            return ("delivery_id_required", "Workflow run delivery id is required.");
        if (string.IsNullOrWhiteSpace(command.WorkflowActorId))
            return ("workflow_actor_id_required", "Workflow run delivery requires a workflow actor id.");
        if (string.IsNullOrWhiteSpace(command.WorkflowCommandId))
            return ("workflow_command_id_required", "Workflow run delivery requires a workflow command id.");
        if (string.IsNullOrWhiteSpace(command.ChannelPlatform))
            return ("channel_platform_required", "Workflow run delivery requires a channel platform.");
        if (string.IsNullOrWhiteSpace(command.ReplyMessageId))
            return ("reply_message_id_required", "Workflow run delivery requires a reply message id.");
        if (string.IsNullOrWhiteSpace(command.DurableReplyCredentialRef))
            return ("durable_reply_credential_ref_required", "Workflow run delivery requires a durable reply credential reference.");

        return null;
    }

    private static WorkflowRunDeliveryGAgentState ApplyStarted(
        WorkflowRunDeliveryGAgentState current,
        WorkflowRunDeliveryStartedEvent evt)
    {
        var next = current.Clone();
        next.DeliveryId = evt.DeliveryId;
        next.WorkflowActorId = evt.WorkflowActorId;
        next.WorkflowRunId = evt.WorkflowRunId;
        next.WorkflowCommandId = evt.WorkflowCommandId;
        next.WorkflowCorrelationId = evt.WorkflowCorrelationId;
        next.StreamTopic = evt.StreamTopic;
        next.ChannelPlatform = evt.ChannelPlatform;
        next.ReplyMessageId = evt.ReplyMessageId;
        next.PlatformMessageId = evt.PlatformMessageId;
        next.RegistrationScopeId = evt.RegistrationScopeId;
        next.DurableReplyCredentialRef = evt.DurableReplyCredentialRef;
        next.Status = WorkflowRunDeliveryStatus.Started;
        next.StartedAtUnixMs = evt.StartedAtUnixMs;
        next.CompletedAtUnixMs = 0;
        next.ErrorCode = string.Empty;
        next.ErrorSummary = string.Empty;
        return next;
    }

    private static WorkflowRunDeliveryGAgentState ApplySucceeded(
        WorkflowRunDeliveryGAgentState current,
        WorkflowRunDeliverySucceededEvent evt)
    {
        var next = current.Clone();
        next.Status = WorkflowRunDeliveryStatus.Delivered;
        next.CompletedAtUnixMs = evt.DeliveredAtUnixMs;
        next.DeliveryAttempt = evt.Attempt;
        next.LastAttemptAtUnixMs = evt.DeliveredAtUnixMs;
        next.DeliveredActivityId = evt.SentActivityId;
        next.DeliveredPlatformMessageId = evt.PlatformMessageId;
        next.ErrorCode = string.Empty;
        next.ErrorSummary = string.Empty;
        next.TerminalStatus = evt.TerminalStatus;
        next.TerminalText = evt.TerminalText;
        next.TerminalErrorCode = evt.TerminalErrorCode;
        next.TerminalObservedAtUnixMs = evt.TerminalObservedAtUnixMs;
        return next;
    }

    private static WorkflowRunDeliveryGAgentState ApplyFailed(
        WorkflowRunDeliveryGAgentState current,
        WorkflowRunDeliveryFailedEvent evt)
    {
        var next = current.Clone();
        next.Status = WorkflowRunDeliveryStatus.Failed;
        next.CompletedAtUnixMs = evt.FailedAtUnixMs;
        next.DeliveryAttempt = evt.Attempt;
        next.LastAttemptAtUnixMs = evt.FailedAtUnixMs;
        next.ErrorCode = evt.ErrorCode;
        next.ErrorSummary = evt.ErrorSummary;
        next.TerminalStatus = evt.TerminalStatus;
        next.TerminalText = evt.TerminalText;
        next.TerminalErrorCode = evt.TerminalErrorCode;
        next.TerminalObservedAtUnixMs = evt.TerminalObservedAtUnixMs;
        return next;
    }
}
