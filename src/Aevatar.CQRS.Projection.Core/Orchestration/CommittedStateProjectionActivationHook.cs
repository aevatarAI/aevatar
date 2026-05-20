using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Core.EventSourcing;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

/// <summary>
/// Activates existing projection scopes at the committed-state publication boundary.
/// </summary>
public sealed class CommittedStateProjectionActivationHook : ICommittedStatePublicationHook
{
    private readonly IEnumerable<IProjectionActivationPlanProvider> _providers;
    private readonly ProjectionActivationPlanDispatcher _dispatcher;

    public CommittedStateProjectionActivationHook(
        IEnumerable<IProjectionActivationPlanProvider> providers,
        ProjectionActivationPlanDispatcher dispatcher)
    {
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    // Refactor (iter18/cluster-006):
    //   Old pattern: command-path projection activation facade with new actor/lifecycle phase
    //   New principle: committed-state publication hook activates existing projection scopes; no new actor/lifecycle phase
    public async Task BeforePublishAsync(CommittedStatePublicationContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var planned = new HashSet<PlanKey>();
        foreach (var provider in _providers)
        {
            IEnumerable<ProjectionActivationPlan> plans;
            try
            {
                plans = provider.GetPlans(context) ?? [];
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Projection activation plan provider '{provider.GetType().FullName}' failed for actor '{context.ActorId}'.",
                    ex);
            }

            foreach (var plan in plans)
            {
                var key = PlanKey.From(plan);
                if (!planned.Add(key))
                    continue;

                try
                {
                    await _dispatcher.DispatchAsync(plan, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Projection activation failed for actor '{context.ActorId}', projection '{plan.StartRequest.ProjectionKind}'.",
                        ex);
                }
            }
        }
    }

    private readonly record struct PlanKey(
        string RootActorId,
        string ProjectionKind,
        ProjectionRuntimeMode Mode,
        string SessionId,
        Type LeaseType)
    {
        public static PlanKey From(ProjectionActivationPlan plan) =>
            new(
                plan.StartRequest.RootActorId,
                plan.StartRequest.ProjectionKind,
                plan.StartRequest.Mode,
                plan.StartRequest.SessionId,
                plan.LeaseType);
    }
}
