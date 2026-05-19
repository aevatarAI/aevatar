using Aevatar.Demos.Inspector.Messages;
using Aevatar.Demos.Inspector.ReadModels;

namespace Aevatar.Demos.Inspector.Demo;

public sealed class InspectorDemoScenarioService
{
    private readonly IActorRuntime _runtime;
    private readonly InspectorGAgentRegistryService _registry;

    public InspectorDemoScenarioService(
        IActorRuntime runtime,
        InspectorGAgentRegistryService registry)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public async Task<InspectorDemoRunResponse> RunHierarchyAsync(CancellationToken ct = default)
    {
        var parent = await _runtime.CreateAsync<InspectorTransformerAgent>("inspector-parent", ct);
        var child = await _runtime.CreateAsync<InspectorCollectorAgent>("inspector-child", ct);
        await _registry.RegisterActorAsync(nameof(InspectorTransformerAgent), parent.Id, ct);
        await _registry.RegisterActorAsync(nameof(InspectorCollectorAgent), child.Id, ct);
        await _runtime.LinkAsync(parent.Id, child.Id);

        await ((GAgentBase)parent.Agent).EventPublisher.PublishAsync(
            new InspectorPingEvent { Message = "hello-inspector" },
            TopologyAudience.Self,
            ct);

        return new InspectorDemoRunResponse(
            "hierarchy",
            [
                new InspectorDemoActor(parent.Id, nameof(InspectorTransformerAgent)),
                new InspectorDemoActor(child.Id, nameof(InspectorCollectorAgent)),
            ],
            [
                new InspectorDemoLink(parent.Id, child.Id),
            ]);
    }
}

public sealed record InspectorDemoRunResponse(
    string Scenario,
    IReadOnlyList<InspectorDemoActor> Actors,
    IReadOnlyList<InspectorDemoLink> Links);

public sealed record InspectorDemoActor(string ActorId, string AgentType);

public sealed record InspectorDemoLink(string ParentId, string ChildId);
