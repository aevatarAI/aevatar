using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgents.Registry;
using Aevatar.Studio.Projection.Orchestration;
using Aevatar.Studio.Projection.ReadModels;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Demos.Inspector.ReadModels;

public sealed class InspectorGAgentRegistryService
{
    public const string ScopeId = "inspector";
    public const string RegistryActorId = "gagent-registry-inspector";

    private readonly IActorRuntime _runtime;
    private readonly IActorDispatchPort _dispatchPort;
    private readonly StudioProjectionPort _projectionPort;
    private readonly IProjectionDocumentReader<GAgentRegistryCurrentStateDocument, string> _documentReader;

    public InspectorGAgentRegistryService(
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort,
        StudioProjectionPort projectionPort,
        IProjectionDocumentReader<GAgentRegistryCurrentStateDocument, string> documentReader)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
    }

    public async Task RegisterActorAsync(
        string agentType,
        string actorId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentType);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);

        var registryActor = await EnsureRegistryActorAsync(ct);
        await InspectorActorCommandDispatcher.SendAsync(
            _dispatchPort,
            registryActor,
            new ActorRegisteredEvent
            {
                GagentType = agentType.Trim(),
                ActorId = actorId.Trim(),
            },
            ct);
    }

    public async Task UnregisterActorAsync(
        string agentType,
        string actorId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentType);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);

        var registryActor = await EnsureRegistryActorAsync(ct);
        await InspectorActorCommandDispatcher.SendAsync(
            _dispatchPort,
            registryActor,
            new ActorUnregisteredEvent
            {
                GagentType = agentType.Trim(),
                ActorId = actorId.Trim(),
            },
            ct);
    }

    public async Task<InspectorActorsResponse> ListActorsAsync(CancellationToken ct = default)
    {
        var document = await _documentReader.GetAsync(RegistryActorId, ct);
        if (document?.StateRoot == null || !document.StateRoot.Is(GAgentRegistryState.Descriptor))
        {
            return new InspectorActorsResponse(
                ScopeId,
                0,
                DateTimeOffset.MinValue,
                DateTimeOffset.UtcNow,
                []);
        }

        var state = document.StateRoot.Unpack<GAgentRegistryState>();
        var groups = state.Groups
            .Select(group => new InspectorActorGroupDto(
                string.IsNullOrWhiteSpace(group.GagentType) ? "unknown" : group.GagentType,
                group.ActorIds.ToList(),
                group.ActorIds.Count))
            .OrderBy(group => group.Type, StringComparer.Ordinal)
            .ToList();

        return new InspectorActorsResponse(
            ScopeId,
            document.StateVersion,
            document.UpdatedAt?.ToDateTimeOffset() ?? DateTimeOffset.MinValue,
            DateTimeOffset.UtcNow,
            groups);
    }

    private async Task<IActor> EnsureRegistryActorAsync(CancellationToken ct)
    {
        var actor = await _runtime.GetAsync(RegistryActorId)
                    ?? await _runtime.CreateAsync<GAgentRegistryGAgent>(RegistryActorId, ct);
        await _projectionPort.EnsureProjectionAsync(RegistryActorId, GAgentRegistryGAgent.ProjectionKind, ct);
        return actor;
    }
}
