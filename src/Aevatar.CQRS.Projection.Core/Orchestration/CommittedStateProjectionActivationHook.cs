using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

/// <summary>
/// Activates existing projection scopes at the committed-state publication boundary.
/// </summary>
// Refactor (iter18/cluster-006):
//   Old pattern: command-path projection activation facade with new actor/lifecycle phase
//   New principle: committed-state publication hook activates existing projection scopes; no new actor/lifecycle phase
public sealed class CommittedStateProjectionActivationHook : ICommittedStatePublicationHook
{
    private readonly IEnumerable<IProjectionActivationPlanProvider> _providers;
    private readonly ProjectionActivationPlanDispatcher _dispatcher;
    private readonly ILogger<CommittedStateProjectionActivationHook> _logger;

    public CommittedStateProjectionActivationHook(
        IEnumerable<IProjectionActivationPlanProvider> providers,
        ProjectionActivationPlanDispatcher dispatcher,
        ILogger<CommittedStateProjectionActivationHook>? logger = null)
    {
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _logger = logger ?? NullLogger<CommittedStateProjectionActivationHook>.Instance;
    }

    // Refactor (iter18/cluster-006):
    //   Old pattern: command-path projection activation facade with new actor/lifecycle phase
    //   New principle: committed-state publication hook activates existing projection scopes; no new actor/lifecycle phase
    public async Task BeforePublishAsync(CommittedStatePublicationContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var planned = new HashSet<PlanKey>();
        var failures = new List<Exception>();
        foreach (var provider in _providers)
        {
            IEnumerable<ProjectionActivationPlan> plans;
            try
            {
                plans = provider.GetPlans(context) ?? [];
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Projection activation plan provider {ProviderType} failed for actor {ActorId}; remaining providers will be attempted before committed event publication fails.",
                    provider.GetType().FullName,
                    context.ActorId);
                failures.Add(ex);
                continue;
            }

            try
            {
                foreach (var plan in plans)
                {
                    var key = PlanKey.From(plan);
                    if (!planned.Add(key))
                        continue;

                    try
                    {
                        await _dispatcher.DispatchAsync(plan, context, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(
                            ex,
                            "Projection activation failed for actor {ActorId}, projection {ProjectionKind}; remaining plans will be attempted before committed event publication fails.",
                            context.ActorId,
                            plan.StartRequest.ProjectionKind);
                        failures.Add(ex);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Projection activation plan provider {ProviderType} failed while enumerating plans for actor {ActorId}; remaining providers will be attempted before committed event publication fails.",
                    provider.GetType().FullName,
                    context.ActorId);
                failures.Add(ex);
            }
        }

        if (failures.Count > 0)
        {
            throw new AggregateException(
                $"Projection activation failed for actor '{context.ActorId}'.",
                failures);
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
