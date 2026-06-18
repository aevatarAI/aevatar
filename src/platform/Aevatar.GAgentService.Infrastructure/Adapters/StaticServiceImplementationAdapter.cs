using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Core.Ports;

namespace Aevatar.GAgentService.Infrastructure.Adapters;

public sealed class StaticServiceImplementationAdapter : IServiceImplementationAdapter
{
    private readonly IAgentKindRegistry _agentKindRegistry;

    public StaticServiceImplementationAdapter(IAgentKindRegistry agentKindRegistry)
    {
        _agentKindRegistry = agentKindRegistry ?? throw new ArgumentNullException(nameof(agentKindRegistry));
    }

    public ServiceImplementationKind ImplementationKind => ServiceImplementationKind.Static;

    public Task<PreparedServiceRevisionArtifact> PrepareRevisionAsync(
        PrepareServiceRevisionRequest request,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        var spec = request.Spec?.StaticSpec
            ?? throw new InvalidOperationException("static implementation_spec is required.");
        var agentKind = ResolveAgentKind(spec);
        if (spec.Endpoints.Count == 0)
            throw new InvalidOperationException("static endpoints are required.");

        return Task.FromResult(new PreparedServiceRevisionArtifact
        {
            Identity = request.Spec.Identity.Clone(),
            RevisionId = request.Spec.RevisionId,
            ImplementationKind = ServiceImplementationKind.Static,
            Endpoints = { spec.Endpoints.Select(x => x.Clone()) },
            DeploymentPlan = new ServiceDeploymentPlan
            {
                StaticPlan = new StaticServiceDeploymentPlan
                {
                    AgentKind = agentKind,
                    ActorTypeName = spec.ActorTypeName ?? string.Empty,
                    PreferredActorId = spec.PreferredActorId ?? string.Empty,
                },
            },
        });
    }

    private string ResolveAgentKind(StaticServiceRevisionSpec spec)
    {
        var agentKind = spec.AgentKind?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(agentKind))
        {
            _agentKindRegistry.Resolve(agentKind);
            return agentKind;
        }

        var actorTypeName = spec.ActorTypeName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(actorTypeName))
            throw new InvalidOperationException("static agent_kind is required.");

        throw new InvalidOperationException(
            $"Static actor_type_name '{actorTypeName}' is deprecated and cannot be used for identity. Provide static agent_kind.");
    }
}
