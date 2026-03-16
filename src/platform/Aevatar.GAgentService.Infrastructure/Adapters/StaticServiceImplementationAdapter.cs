using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Core.Ports;

namespace Aevatar.GAgentService.Infrastructure.Adapters;

public sealed class StaticServiceImplementationAdapter : IServiceImplementationAdapter
{
    public ServiceImplementationKind ImplementationKind => ServiceImplementationKind.Static;

    public Task<PreparedServiceRevisionArtifact> PrepareRevisionAsync(
        PrepareServiceRevisionRequest request,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        var spec = request.Spec?.StaticSpec
            ?? throw new InvalidOperationException("static implementation_spec is required.");
        if (string.IsNullOrWhiteSpace(spec.ActorTypeName))
            throw new InvalidOperationException("static actor_type_name is required.");
        if (spec.Endpoints.Count == 0)
            throw new InvalidOperationException("static endpoints are required.");

        var actorType = Type.GetType(spec.ActorTypeName, throwOnError: false);
        if (actorType == null)
            throw new InvalidOperationException($"Static actor type '{spec.ActorTypeName}' was not found.");
        if (!typeof(IAgent).IsAssignableFrom(actorType))
            throw new InvalidOperationException($"Static actor type '{spec.ActorTypeName}' does not implement IAgent.");

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
                    ActorTypeName = spec.ActorTypeName,
                    PreferredActorId = spec.PreferredActorId ?? string.Empty,
                },
            },
        });
    }
}
