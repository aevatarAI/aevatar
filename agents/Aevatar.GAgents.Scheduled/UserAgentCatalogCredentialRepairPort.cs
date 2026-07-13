using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.Scheduled;

internal sealed class UserAgentCatalogCredentialRepairPort : IUserAgentCatalogCredentialRepairPort
{
    private readonly IActorRuntime _actorRuntime;
    private readonly IActorDispatchPort _actorDispatchPort;
    private readonly IActorEventSubscriptionProvider _subscriptionProvider;

    public UserAgentCatalogCredentialRepairPort(
        IActorRuntime actorRuntime,
        IActorDispatchPort actorDispatchPort,
        IActorEventSubscriptionProvider subscriptionProvider)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _actorDispatchPort = actorDispatchPort ?? throw new ArgumentNullException(nameof(actorDispatchPort));
        _subscriptionProvider = subscriptionProvider ?? throw new ArgumentNullException(nameof(subscriptionProvider));
    }

    public async Task<UserAgentCatalogCredentialRepairResult> RepairMissingSecretReferenceAsync(
        string agentId,
        string apiKeyId,
        Foundation.Abstractions.Credentials.SecretReference secretReference,
        string secretSubjectId,
        string repairReason,
        string requestedBySubjectId,
        long requestedAtUnixMs,
        CancellationToken ct = default)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var resultSource = new TaskCompletionSource<UserAgentCatalogCredentialRepairResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = await _subscriptionProvider.SubscribeAsync<CommittedStateEventPublished>(
            UserAgentCatalogGAgent.WellKnownId,
            published => HandleCommittedResultAsync(published, requestId, resultSource),
            ct);

        _ = await _actorRuntime.GetAsync(UserAgentCatalogGAgent.WellKnownId)
            ?? await _actorRuntime.CreateAsync<UserAgentCatalogGAgent>(UserAgentCatalogGAgent.WellKnownId, ct);
        await _actorDispatchPort.DispatchAsync(
            UserAgentCatalogGAgent.WellKnownId,
            new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                Payload = Google.Protobuf.WellKnownTypes.Any.Pack(new UserAgentCatalogRepairCredentialRevocationCommand
                {
                    RequestId = requestId,
                    AgentId = agentId,
                    ApiKeyId = apiKeyId,
                    SecretReference = secretReference.Clone(),
                    SecretSubjectId = secretSubjectId,
                    RepairReason = repairReason,
                    RequestedBySubjectId = requestedBySubjectId,
                    RequestedAtUnixMs = requestedAtUnixMs,
                }),
                Route = EnvelopeRouteSemantics.CreateDirect("scheduled-credential-repair", UserAgentCatalogGAgent.WellKnownId),
            },
            ct);
        return await resultSource.Task.WaitAsync(ct);
    }

    private static Task HandleCommittedResultAsync(
        CommittedStateEventPublished published,
        string requestId,
        TaskCompletionSource<UserAgentCatalogCredentialRepairResult> resultSource)
    {
        var eventData = published.StateEvent?.EventData;
        if (eventData?.Is(UserAgentCatalogCredentialRevocationRepairedEvent.Descriptor) == true)
        {
            var repaired = eventData.Unpack<UserAgentCatalogCredentialRevocationRepairedEvent>();
            if (string.Equals(repaired.RequestId, requestId, StringComparison.Ordinal))
            {
                resultSource.TrySetResult(new UserAgentCatalogCredentialRepairResult(
                    true,
                    UserAgentCatalogCredentialRevocationRepairRejectionReason.Unspecified));
            }
        }
        else if (eventData?.Is(UserAgentCatalogCredentialRevocationRepairRejectedEvent.Descriptor) == true)
        {
            var rejected = eventData.Unpack<UserAgentCatalogCredentialRevocationRepairRejectedEvent>();
            if (string.Equals(rejected.RequestId, requestId, StringComparison.Ordinal))
                resultSource.TrySetResult(new UserAgentCatalogCredentialRepairResult(false, rejected.Reason));
        }

        return Task.CompletedTask;
    }
}
