using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.ScopeWorkflows;
using Aevatar.GAgentService.Core.GAgents;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Hosting.Backfill;

internal sealed class ActorDispatchScopeWorkflowCatalogueRowCommandPort : IScopeWorkflowCatalogueRowCommandPort
{
    private const string PublisherId = "aevatar.scope-workflow-catalogue-source";

    private readonly IActorRuntime _actorRuntime;
    private readonly IActorDispatchPort _dispatchPort;

    public ActorDispatchScopeWorkflowCatalogueRowCommandPort(
        IActorRuntime actorRuntime,
        IActorDispatchPort dispatchPort)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
    }

    public async Task ObserveSourcesAsync(
        string scopeId,
        string workflowId,
        ScopeWorkflowCatalogueSourceSnapshot? draftSource,
        ScopeWorkflowCatalogueSourceSnapshot? serviceSource,
        string observationEventId,
        DateTimeOffset observedAt,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(scopeId) || string.IsNullOrWhiteSpace(workflowId))
            return;

        var actorId = ScopeWorkflowCatalogueActorIds.Row(scopeId, workflowId);
        _ = await _actorRuntime.GetAsync(actorId) ?? await _actorRuntime.CreateAsync<ScopeWorkflowCatalogueRowGAgent>(actorId, ct);
        await _dispatchPort.DispatchAsync(
            actorId,
            new EventEnvelope
            {
                Id = string.IsNullOrWhiteSpace(observationEventId)
                    ? Guid.NewGuid().ToString("N")
                    : observationEventId.Trim(),
                Timestamp = Timestamp.FromDateTimeOffset(observedAt),
                Payload = Any.Pack(new ObserveScopeWorkflowCatalogueSourcesCommand
                {
                    ScopeId = scopeId.Trim(),
                    WorkflowId = workflowId.Trim(),
                    DraftSource = draftSource?.Clone(),
                    ServiceSource = serviceSource?.Clone(),
                    ObservationEventId = observationEventId ?? string.Empty,
                    ObservedAt = Timestamp.FromDateTimeOffset(observedAt),
                }),
                Route = EnvelopeRouteSemantics.CreateDirect(PublisherId, actorId),
            },
            ct);
    }
}
