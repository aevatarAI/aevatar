using System.Reflection;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

/// <summary>
/// Dispatches activation plans to existing projection scope activation services.
/// </summary>
// Refactor (iter18/cluster-006):
//   Old pattern: command-path projection activation facade with new actor/lifecycle phase
//   New principle: committed-state publication hook activates existing projection scopes; no new actor/lifecycle phase
public sealed class ProjectionActivationPlanDispatcher
{
    private static readonly MethodInfo DispatchCoreMethod = typeof(ProjectionActivationPlanDispatcher)
        .GetMethod(nameof(DispatchCoreAsync), BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(nameof(ProjectionActivationPlanDispatcher), nameof(DispatchCoreAsync));

    private readonly IServiceProvider _services;
    private readonly IActorDispatchPort? _dispatchPort;

    public ProjectionActivationPlanDispatcher(IServiceProvider services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _dispatchPort = services.GetService<IActorDispatchPort>();
    }

    public Task DispatchAsync(ProjectionActivationPlan plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(plan.StartRequest);
        ArgumentNullException.ThrowIfNull(plan.LeaseType);

        return (Task)DispatchCoreMethod
            .MakeGenericMethod(plan.LeaseType)
            .Invoke(this, [plan.StartRequest, ct])!;
    }

    public async Task DispatchAsync(
        ProjectionActivationPlan plan,
        CommittedStatePublicationContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(context);

        await DispatchAsync(plan, ct).ConfigureAwait(false);
        if (_dispatchPort is null)
            return;

        var scopeKey = new ProjectionRuntimeScopeKey(
            plan.StartRequest.RootActorId,
            plan.StartRequest.ProjectionKind,
            plan.StartRequest.Mode,
            plan.StartRequest.SessionId);
        var targetScopeActorId = ProjectionScopeActorId.Build(scopeKey);
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(context.Published.Clone()),
            Route = EnvelopeRouteSemantics.CreateObserverPublication(context.ActorId, context.Audience),
        };
        envelope.EnsureRuntime().SourceActorId = context.ActorId;
        var forwardedEnvelope = StreamForwardingRules.BuildForwardedEnvelope(
            envelope,
            context.ActorId,
            targetScopeActorId,
            StreamForwardingMode.HandleThenForward);

        await _dispatchPort.DispatchAsync(targetScopeActorId, forwardedEnvelope, ct).ConfigureAwait(false);
    }

    // Refactor (iter18/cluster-006):
    //   Old pattern: command-path projection activation facade with new actor/lifecycle phase
    //   New principle: committed-state publication hook activates existing projection scopes; no new actor/lifecycle phase
    private async Task DispatchCoreAsync<TLease>(
        ProjectionScopeStartRequest request,
        CancellationToken ct)
        where TLease : class, IProjectionRuntimeLease
    {
        var activationService = _services.GetService(typeof(IProjectionScopeActivationService<TLease>))
            as IProjectionScopeActivationService<TLease>;
        if (activationService == null)
        {
            throw new InvalidOperationException(
                $"Projection activation service for lease '{typeof(TLease).FullName}' is not registered.");
        }

        _ = await activationService.EnsureAsync(request, ct).ConfigureAwait(false);
    }
}
