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
                    ActorTypeName = spec.ActorTypeName,
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

        var legacyClrTypeName = NormalizeLegacyClrTypeName(actorTypeName);
        if (_agentKindRegistry.TryResolveKindByClrTypeName(legacyClrTypeName, out var translatedKind))
            return translatedKind;

        throw new InvalidOperationException(
            $"Static legacy actor_type_name '{actorTypeName}' is not registered with IAgentKindRegistry.");
    }

    // Refactor (issue1044/static-service-agent-kind):
    //   Old pattern: static service preparation resolved actor_type_name through CLR-name reflection.
    //   New principle: static service activation persists agent_kind; actor_type_name only translates legacy boundary input.
    private static string NormalizeLegacyClrTypeName(string actorTypeName)
    {
        var commaIndex = actorTypeName.IndexOf(',', StringComparison.Ordinal);
        return commaIndex < 0
            ? actorTypeName
            : actorTypeName[..commaIndex].Trim();
    }
}
