using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.Scheduled;

internal interface IUserAgentCatalogCredentialRepairObservationPort
{
    Task<IUserAgentCatalogCredentialRepairObservationLease> BindAsync(
        string requestId,
        CancellationToken ct = default);
}

internal interface IUserAgentCatalogCredentialRepairObservationLease : IAsyncDisposable
{
    Task<UserAgentCatalogCredentialRepairOutcome> WaitAsync(CancellationToken ct = default);
}

internal sealed class UserAgentCatalogCredentialRepairPort : IUserAgentCatalogCredentialRepairPort
{
    private const string PublisherActorId = "scheduled-credential-repair";
    private readonly IActorRuntime _actorRuntime;
    private readonly IActorDispatchPort _actorDispatchPort;
    private readonly IUserAgentCatalogCredentialRepairObservationPort _observationPort;

    public UserAgentCatalogCredentialRepairPort(
        IActorRuntime actorRuntime,
        IActorDispatchPort actorDispatchPort,
        IUserAgentCatalogCredentialRepairObservationPort observationPort)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _actorDispatchPort = actorDispatchPort ?? throw new ArgumentNullException(nameof(actorDispatchPort));
        _observationPort = observationPort ?? throw new ArgumentNullException(nameof(observationPort));
    }

    public async Task<UserAgentCatalogCredentialRepairResult> RepairMissingSecretReferenceAsync(
        string agentId,
        string apiKeyId,
        Foundation.Abstractions.Credentials.SecretReference secretReference,
        string secretSubjectId,
        string repairReason,
        string requestedBySubjectId,
        long repairRequestedAtUnixMs,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(secretReference);

        var requestId = Guid.NewGuid().ToString("N");
        _ = await _actorRuntime.GetAsync(UserAgentCatalogGAgent.WellKnownId)
            ?? await _actorRuntime.CreateAsync<UserAgentCatalogGAgent>(UserAgentCatalogGAgent.WellKnownId, ct);

        await using var observation = await _observationPort.BindAsync(requestId, ct);
        var admission = await _actorDispatchPort.DispatchAsync(
            UserAgentCatalogGAgent.WellKnownId,
            new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                Payload = Any.Pack(new UserAgentCatalogRepairCredentialRevocationCommand
                {
                    RequestId = requestId,
                    AgentId = agentId?.Trim() ?? string.Empty,
                    ApiKeyId = apiKeyId?.Trim() ?? string.Empty,
                    SecretReference = secretReference.Clone(),
                    SecretSubjectId = secretSubjectId?.Trim() ?? string.Empty,
                    RepairReason = repairReason?.Trim() ?? string.Empty,
                    RequestedBySubjectId = requestedBySubjectId?.Trim() ?? string.Empty,
                    RepairRequestedAtUnixMs = repairRequestedAtUnixMs,
                }),
                Route = EnvelopeRouteSemantics.CreateDirect(PublisherActorId, UserAgentCatalogGAgent.WellKnownId),
            },
            ct);
        var outcome = await observation.WaitAsync(ct);
        return new UserAgentCatalogCredentialRepairResult(requestId, admission, outcome);
    }
}

internal sealed class UserAgentCatalogCredentialRepairObservationPort
    : IUserAgentCatalogCredentialRepairObservationPort
{
    public const string ProjectionKind = "user-agent-catalog-credential-repair";

    private readonly IProjectionScopeActivationService<UserAgentCatalogCredentialRepairRuntimeLease> _activationService;
    private readonly IProjectionScopeReleaseService<UserAgentCatalogCredentialRepairRuntimeLease> _releaseService;
    private readonly IProjectionSessionEventHub<UserAgentCatalogCredentialRepairOutcome> _eventHub;

    public UserAgentCatalogCredentialRepairObservationPort(
        IProjectionScopeActivationService<UserAgentCatalogCredentialRepairRuntimeLease> activationService,
        IProjectionScopeReleaseService<UserAgentCatalogCredentialRepairRuntimeLease> releaseService,
        IProjectionSessionEventHub<UserAgentCatalogCredentialRepairOutcome> eventHub)
    {
        _activationService = activationService ?? throw new ArgumentNullException(nameof(activationService));
        _releaseService = releaseService ?? throw new ArgumentNullException(nameof(releaseService));
        _eventHub = eventHub ?? throw new ArgumentNullException(nameof(eventHub));
    }

    public async Task<IUserAgentCatalogCredentialRepairObservationLease> BindAsync(
        string requestId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);

        var normalizedRequestId = requestId.Trim();
        var runtimeLease = await _activationService.EnsureAsync(
            new ProjectionScopeStartRequest
            {
                RootActorId = UserAgentCatalogGAgent.WellKnownId,
                ProjectionKind = ProjectionKind,
                Mode = ProjectionRuntimeMode.SessionObservation,
                SessionId = normalizedRequestId,
            },
            ct);
        var completion = new TaskCompletionSource<UserAgentCatalogCredentialRepairOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var subscription = await _eventHub.SubscribeAsync(
                UserAgentCatalogGAgent.WellKnownId,
                normalizedRequestId,
                outcome =>
                {
                    completion.TrySetResult(outcome.Clone());
                    return ValueTask.CompletedTask;
                },
                ct);
            return new ObservationLease(runtimeLease, subscription, completion, _releaseService);
        }
        catch
        {
            await _releaseService.ReleaseIfIdleAsync(runtimeLease, CancellationToken.None);
            throw;
        }
    }

    private sealed class ObservationLease : IUserAgentCatalogCredentialRepairObservationLease
    {
        private readonly UserAgentCatalogCredentialRepairRuntimeLease _runtimeLease;
        private readonly IAsyncDisposable _subscription;
        private readonly TaskCompletionSource<UserAgentCatalogCredentialRepairOutcome> _completion;
        private readonly IProjectionScopeReleaseService<UserAgentCatalogCredentialRepairRuntimeLease> _releaseService;
        private int _disposed;

        public ObservationLease(
            UserAgentCatalogCredentialRepairRuntimeLease runtimeLease,
            IAsyncDisposable subscription,
            TaskCompletionSource<UserAgentCatalogCredentialRepairOutcome> completion,
            IProjectionScopeReleaseService<UserAgentCatalogCredentialRepairRuntimeLease> releaseService)
        {
            _runtimeLease = runtimeLease;
            _subscription = subscription;
            _completion = completion;
            _releaseService = releaseService;
        }

        public async Task<UserAgentCatalogCredentialRepairOutcome> WaitAsync(CancellationToken ct = default) =>
            await _completion.Task.WaitAsync(ct);

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

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

internal sealed class UserAgentCatalogCredentialRepairProjectionContext : IProjectionSessionContext
{
    public required string SessionId { get; init; }
    public required string RootActorId { get; init; }
    public required string ProjectionKind { get; init; }
}

internal sealed class UserAgentCatalogCredentialRepairRuntimeLease
    : EventSinkProjectionRuntimeLeaseBase<UserAgentCatalogCredentialRepairOutcome>,
      IProjectionContextRuntimeLease<UserAgentCatalogCredentialRepairProjectionContext>
{
    public UserAgentCatalogCredentialRepairRuntimeLease(UserAgentCatalogCredentialRepairProjectionContext context)
        : base(context?.RootActorId ?? throw new ArgumentNullException(nameof(context)))
    {
        Context = context;
        SessionId = context.SessionId;
    }

    public string SessionId { get; }
    public UserAgentCatalogCredentialRepairProjectionContext Context { get; }
}

internal sealed class UserAgentCatalogCredentialRepairOutcomeProjector
    : ProjectionSessionEventProjectorBase<
        UserAgentCatalogCredentialRepairProjectionContext,
        UserAgentCatalogCredentialRepairOutcome>
{
    public UserAgentCatalogCredentialRepairOutcomeProjector(
        IProjectionSessionEventHub<UserAgentCatalogCredentialRepairOutcome> eventHub)
        : base(eventHub)
    {
    }

    protected override IReadOnlyList<ProjectionSessionEventEntry<UserAgentCatalogCredentialRepairOutcome>>
        ResolveSessionEventEntries(
            UserAgentCatalogCredentialRepairProjectionContext context,
            EventEnvelope envelope)
    {
        if (!CommittedStateEventEnvelope.TryGetObservedPayload(envelope, out var payload, out _, out _) ||
            payload is null)
        {
            return EmptyEntries;
        }

        UserAgentCatalogCredentialRepairOutcome? outcome = null;
        if (payload.Is(UserAgentCatalogCredentialRevocationRepairedEvent.Descriptor))
        {
            var repaired = payload.Unpack<UserAgentCatalogCredentialRevocationRepairedEvent>();
            if (string.Equals(repaired.RequestId, context.SessionId, StringComparison.Ordinal))
                outcome = new UserAgentCatalogCredentialRepairOutcome { Repaired = repaired };
        }
        else if (payload.Is(UserAgentCatalogCredentialRevocationRepairRejectedEvent.Descriptor))
        {
            var rejected = payload.Unpack<UserAgentCatalogCredentialRevocationRepairRejectedEvent>();
            if (string.Equals(rejected.RequestId, context.SessionId, StringComparison.Ordinal))
                outcome = new UserAgentCatalogCredentialRepairOutcome { Rejected = rejected };
        }

        return outcome is null
            ? EmptyEntries
            : [new ProjectionSessionEventEntry<UserAgentCatalogCredentialRepairOutcome>(
                context.RootActorId,
                context.SessionId,
                outcome)];
    }
}

internal sealed class UserAgentCatalogCredentialRepairOutcomeCodec
    : IProjectionSessionEventCodec<UserAgentCatalogCredentialRepairOutcome>
{
    public string Channel => "user-agent-catalog-credential-repair";

    public string GetEventType(UserAgentCatalogCredentialRepairOutcome evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return evt.OutcomeCase.ToString();
    }

    public ByteString Serialize(UserAgentCatalogCredentialRepairOutcome evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return evt.ToByteString();
    }

    public UserAgentCatalogCredentialRepairOutcome? Deserialize(string eventType, ByteString payload)
    {
        if (string.IsNullOrWhiteSpace(eventType) || payload is null || payload.IsEmpty)
            return null;

        try
        {
            var outcome = UserAgentCatalogCredentialRepairOutcome.Parser.ParseFrom(payload);
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
