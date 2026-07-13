using Aevatar.Foundation.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.Scheduled;

internal sealed class UserAgentCatalogCredentialRepairPort : IUserAgentCatalogCredentialRepairPort
{
    private readonly IActorRuntime _actorRuntime;
    private readonly IActorDispatchPort _actorDispatchPort;

    public UserAgentCatalogCredentialRepairPort(
        IActorRuntime actorRuntime,
        IActorDispatchPort actorDispatchPort)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _actorDispatchPort = actorDispatchPort ?? throw new ArgumentNullException(nameof(actorDispatchPort));
    }

    public async Task<UserAgentCatalogCredentialRepairAcceptedReceipt> RepairMissingSecretReferenceAsync(
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
        _ = await _actorRuntime.GetAsync(UserAgentCatalogGAgent.WellKnownId)
            ?? await _actorRuntime.CreateAsync<UserAgentCatalogGAgent>(UserAgentCatalogGAgent.WellKnownId, ct);
        var admission = await _actorDispatchPort.DispatchAsync(
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
        return new UserAgentCatalogCredentialRepairAcceptedReceipt(requestId, admission);
    }
}
