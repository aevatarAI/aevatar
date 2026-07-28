using System.Threading.Channels;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.Channel.Runtime;

public interface IChannelWorkflowResultDeliveryRepairObservationPort
{
    Task<IChannelWorkflowResultDeliveryRepairObservationLease> BindAsync(
        string requestId,
        CancellationToken ct = default);
}

public interface IChannelWorkflowResultDeliveryRepairObservationLease : IAsyncDisposable
{
    Task<ChannelBotWorkflowResultDeliveryRepairOutcome> WaitAsync(
        ChannelBotWorkflowResultDeliveryRepairOutcome.OutcomeOneofCase expected,
        CancellationToken ct = default);
}

internal sealed class ChannelWorkflowResultDeliveryRepairObservationPort
    : IChannelWorkflowResultDeliveryRepairObservationPort
{
    internal const string ProjectionKind = "channel-workflow-result-delivery-repair";

    private readonly IProjectionScopeActivationService<ChannelWorkflowResultDeliveryRepairRuntimeLease>
        _activationService;
    private readonly IProjectionScopeReleaseService<ChannelWorkflowResultDeliveryRepairRuntimeLease>
        _releaseService;
    private readonly IProjectionSessionEventHub<ChannelBotWorkflowResultDeliveryRepairOutcome> _eventHub;

    public ChannelWorkflowResultDeliveryRepairObservationPort(
        IProjectionScopeActivationService<ChannelWorkflowResultDeliveryRepairRuntimeLease> activationService,
        IProjectionScopeReleaseService<ChannelWorkflowResultDeliveryRepairRuntimeLease> releaseService,
        IProjectionSessionEventHub<ChannelBotWorkflowResultDeliveryRepairOutcome> eventHub)
    {
        _activationService = activationService ?? throw new ArgumentNullException(nameof(activationService));
        _releaseService = releaseService ?? throw new ArgumentNullException(nameof(releaseService));
        _eventHub = eventHub ?? throw new ArgumentNullException(nameof(eventHub));
    }

    public async Task<IChannelWorkflowResultDeliveryRepairObservationLease> BindAsync(
        string requestId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);

        var normalizedRequestId = requestId.Trim();
        var runtimeLease = await _activationService.EnsureAsync(
            new ProjectionScopeStartRequest
            {
                RootActorId = ChannelBotRegistrationGAgent.WellKnownId,
                ProjectionKind = ProjectionKind,
                Mode = ProjectionRuntimeMode.SessionObservation,
                SessionId = normalizedRequestId,
            },
            ct);
        var outcomes = System.Threading.Channels.Channel.CreateUnbounded<
            ChannelBotWorkflowResultDeliveryRepairOutcome>(
            new UnboundedChannelOptions
            {
                SingleReader = false,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });
        try
        {
            var subscription = await _eventHub.SubscribeAsync(
                ChannelBotRegistrationGAgent.WellKnownId,
                normalizedRequestId,
                outcome =>
                {
                    outcomes.Writer.TryWrite(outcome.Clone());
                    return ValueTask.CompletedTask;
                },
                ct);
            return new ObservationLease(runtimeLease, subscription, outcomes, _releaseService);
        }
        catch
        {
            outcomes.Writer.TryComplete();
            await _releaseService.ReleaseIfIdleAsync(runtimeLease, CancellationToken.None);
            throw;
        }
    }

    private sealed class ObservationLease : IChannelWorkflowResultDeliveryRepairObservationLease
    {
        private readonly ChannelWorkflowResultDeliveryRepairRuntimeLease _runtimeLease;
        private readonly IAsyncDisposable _subscription;
        private readonly Channel<ChannelBotWorkflowResultDeliveryRepairOutcome> _outcomes;
        private readonly IProjectionScopeReleaseService<ChannelWorkflowResultDeliveryRepairRuntimeLease>
            _releaseService;
        private int _disposed;

        public ObservationLease(
            ChannelWorkflowResultDeliveryRepairRuntimeLease runtimeLease,
            IAsyncDisposable subscription,
            Channel<ChannelBotWorkflowResultDeliveryRepairOutcome> outcomes,
            IProjectionScopeReleaseService<ChannelWorkflowResultDeliveryRepairRuntimeLease> releaseService)
        {
            _runtimeLease = runtimeLease;
            _subscription = subscription;
            _outcomes = outcomes;
            _releaseService = releaseService;
        }

        public async Task<ChannelBotWorkflowResultDeliveryRepairOutcome> WaitAsync(
            ChannelBotWorkflowResultDeliveryRepairOutcome.OutcomeOneofCase expected,
            CancellationToken ct = default)
        {
            if (expected == ChannelBotWorkflowResultDeliveryRepairOutcome.OutcomeOneofCase.None)
                throw new ArgumentOutOfRangeException(nameof(expected));

            while (await _outcomes.Reader.WaitToReadAsync(ct))
            {
                while (_outcomes.Reader.TryRead(out var outcome))
                {
                    if (outcome.OutcomeCase == expected ||
                        outcome.OutcomeCase ==
                        ChannelBotWorkflowResultDeliveryRepairOutcome.OutcomeOneofCase.Rejected)
                    {
                        return outcome.Clone();
                    }
                }
            }

            throw new InvalidOperationException("Channel workflow result delivery repair observation ended before the expected outcome.");
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _outcomes.Writer.TryComplete();
            try
            {
                await _subscription.DisposeAsync();
            }
            finally
            {
                await _releaseService.ReleaseIfIdleAsync(_runtimeLease, CancellationToken.None);
            }
        }
    }
}

internal sealed class ChannelWorkflowResultDeliveryRepairProjectionContext : IProjectionSessionContext
{
    public required string SessionId { get; init; }
    public required string RootActorId { get; init; }
    public required string ProjectionKind { get; init; }
}

internal sealed class ChannelWorkflowResultDeliveryRepairRuntimeLease
    : EventSinkProjectionRuntimeLeaseBase<ChannelBotWorkflowResultDeliveryRepairOutcome>,
      IProjectionContextRuntimeLease<ChannelWorkflowResultDeliveryRepairProjectionContext>
{
    public ChannelWorkflowResultDeliveryRepairRuntimeLease(
        ChannelWorkflowResultDeliveryRepairProjectionContext context)
        : base(context?.RootActorId ?? throw new ArgumentNullException(nameof(context)))
    {
        Context = context;
        SessionId = context.SessionId;
    }

    public string SessionId { get; }
    public ChannelWorkflowResultDeliveryRepairProjectionContext Context { get; }
}

internal sealed class ChannelWorkflowResultDeliveryRepairOutcomeProjector
    : ProjectionSessionEventProjectorBase<
        ChannelWorkflowResultDeliveryRepairProjectionContext,
        ChannelBotWorkflowResultDeliveryRepairOutcome>
{
    public ChannelWorkflowResultDeliveryRepairOutcomeProjector(
        IProjectionSessionEventHub<ChannelBotWorkflowResultDeliveryRepairOutcome> eventHub)
        : base(eventHub)
    {
    }

    protected override IReadOnlyList<ProjectionSessionEventEntry<ChannelBotWorkflowResultDeliveryRepairOutcome>>
        ResolveSessionEventEntries(
            ChannelWorkflowResultDeliveryRepairProjectionContext context,
            EventEnvelope envelope)
    {
        if (!CommittedStateEventEnvelope.TryGetObservedPayload(envelope, out var payload, out _, out _) ||
            payload is null)
        {
            return EmptyEntries;
        }

        var outcome = ResolveOutcome(payload);
        return outcome is null || !string.Equals(RequestId(outcome), context.SessionId, StringComparison.Ordinal)
            ? EmptyEntries
            : [new ProjectionSessionEventEntry<ChannelBotWorkflowResultDeliveryRepairOutcome>(
                context.RootActorId,
                context.SessionId,
                outcome)];
    }

    private static ChannelBotWorkflowResultDeliveryRepairOutcome? ResolveOutcome(Any payload)
    {
        if (payload.Is(ChannelBotWorkflowResultDeliveryRepairRequestedEvent.Descriptor))
            return new() { Requested = payload.Unpack<ChannelBotWorkflowResultDeliveryRepairRequestedEvent>() };
        if (payload.Is(ChannelBotWorkflowResultDeliveryRepairPreparedEvent.Descriptor))
            return new() { Prepared = payload.Unpack<ChannelBotWorkflowResultDeliveryRepairPreparedEvent>() };
        if (payload.Is(ChannelBotWorkflowResultDeliveryRepairCompletedEvent.Descriptor))
            return new() { Completed = payload.Unpack<ChannelBotWorkflowResultDeliveryRepairCompletedEvent>() };
        if (payload.Is(ChannelBotWorkflowResultDeliveryRepairFailedEvent.Descriptor))
            return new() { Failed = payload.Unpack<ChannelBotWorkflowResultDeliveryRepairFailedEvent>() };
        if (payload.Is(ChannelBotWorkflowResultDeliveryRepairRejectedEvent.Descriptor))
            return new() { Rejected = payload.Unpack<ChannelBotWorkflowResultDeliveryRepairRejectedEvent>() };
        return null;
    }

    private static string RequestId(ChannelBotWorkflowResultDeliveryRepairOutcome outcome) =>
        outcome.OutcomeCase switch
        {
            ChannelBotWorkflowResultDeliveryRepairOutcome.OutcomeOneofCase.Requested =>
                outcome.Requested.Repair?.RequestId ?? string.Empty,
            ChannelBotWorkflowResultDeliveryRepairOutcome.OutcomeOneofCase.Prepared =>
                outcome.Prepared.Repair?.RequestId ?? string.Empty,
            ChannelBotWorkflowResultDeliveryRepairOutcome.OutcomeOneofCase.Completed =>
                outcome.Completed.RequestId,
            ChannelBotWorkflowResultDeliveryRepairOutcome.OutcomeOneofCase.Failed =>
                outcome.Failed.Repair?.RequestId ?? string.Empty,
            ChannelBotWorkflowResultDeliveryRepairOutcome.OutcomeOneofCase.Rejected =>
                outcome.Rejected.RequestId,
            _ => string.Empty,
        };
}

internal sealed class ChannelWorkflowResultDeliveryRepairOutcomeCodec
    : IProjectionSessionEventCodec<ChannelBotWorkflowResultDeliveryRepairOutcome>
{
    public string Channel => "channel-workflow-result-delivery-repair";

    public string GetEventType(ChannelBotWorkflowResultDeliveryRepairOutcome evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return evt.OutcomeCase.ToString();
    }

    public ByteString Serialize(ChannelBotWorkflowResultDeliveryRepairOutcome evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return evt.ToByteString();
    }

    public ChannelBotWorkflowResultDeliveryRepairOutcome? Deserialize(
        string eventType,
        ByteString payload)
    {
        if (string.IsNullOrWhiteSpace(eventType) || payload is null || payload.IsEmpty)
            return null;

        try
        {
            var outcome = ChannelBotWorkflowResultDeliveryRepairOutcome.Parser.ParseFrom(payload);
            return string.Equals(GetEventType(outcome), eventType, StringComparison.Ordinal)
                ? outcome
                : null;
        }
        catch (InvalidProtocolBufferException)
        {
            return null;
        }
    }
}
