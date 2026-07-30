using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Core.TypeSystem;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Aevatar.CQRS.Projection.Core.DependencyInjection;

/// <summary>
/// Shared registration helpers for actorized durable materialization components.
/// </summary>
public static class ProjectionMaterializationRuntimeRegistration
{
    public static IServiceCollection AddProjectionMaterializationRuntimeCore<TContext, TRuntimeLease, TScopeAgent>(
        this IServiceCollection services,
        Func<ProjectionRuntimeScopeKey, TContext> contextFactory,
        Func<TContext, TRuntimeLease> leaseFactory,
        bool materializeScopeStatus = true)
        where TContext : class, IProjectionMaterializationContext
        where TRuntimeLease : class, IProjectionRuntimeLease, IProjectionContextRuntimeLease<TContext>
        where TScopeAgent : IAgent
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(leaseFactory);

        services.AddAevatarAgentKindRegistry(builder =>
            builder.Register(ProjectionScopeAgentRegistration.Create<TScopeAgent>()));
        services.TryAddSingleton<IProjectionFailureReplayService, ProjectionFailureReplayService>();
        services.TryAddSingleton<IProjectionFailureAlertSink, LoggingProjectionFailureAlertSink>();
        services.TryAddSingleton<Func<ProjectionRuntimeScopeKey, TContext>>(_ => contextFactory);
        services.TryAddSingleton<IProjectionScopeAttachExistingLeaseLookup<TRuntimeLease>>(sp =>
            new ProjectionScopeAttachExistingLeaseLookup<
                TRuntimeLease,
                TContext>(
                sp.GetRequiredService<IActorRuntime>(),
                request => contextFactory(new ProjectionRuntimeScopeKey(
                    request.RootActorId,
                    request.ProjectionKind,
                    ProjectionRuntimeMode.DurableMaterialization,
                    request.SessionId)),
                (_, context) => leaseFactory(context)));
        services.TryAddSingleton<IProjectionScopeActivationService<TRuntimeLease>>(sp =>
        {
            IProjectionScopeActivationService<TRuntimeLease> activationService =
                new ProjectionScopeActivationService<
                    TRuntimeLease,
                    TContext,
                    TScopeAgent>(
                sp.GetRequiredService<IActorRuntime>(),
                sp.GetRequiredService<IActorDispatchPort>(),
                request => contextFactory(new ProjectionRuntimeScopeKey(
                    request.RootActorId,
                    request.ProjectionKind,
                    ProjectionRuntimeMode.DurableMaterialization,
                    request.SessionId)),
                (_, context) => leaseFactory(context),
                sp.GetService<Aevatar.Foundation.Abstractions.TypeSystem.IAgentKindVerifier>(),
                sp.GetRequiredService<Aevatar.Foundation.Abstractions.TypeSystem.IAgentKindRegistry>(),
                sp.GetService<IStreamPubSubMaintenance>(),
                sp.GetService<ILoggerFactory>(),
                sp.GetService<IStreamForwardingRegistry>());

            return materializeScopeStatus
                ? new ProjectionScopeStatusActivationService<TRuntimeLease>(
                    activationService,
                    sp.GetService<IProjectionScopeActivationService<ProjectionScopeStatusRuntimeLease>>())
                : activationService;
        });
        services.TryAddSingleton<IProjectionScopeReleaseService<TRuntimeLease>>(sp =>
            new ProjectionScopeReleaseService<
                TRuntimeLease,
                TScopeAgent>(
                sp.GetRequiredService<IActorRuntime>(),
                sp.GetRequiredService<IActorDispatchPort>(),
                lease => new ProjectionRuntimeScopeKey(
                    lease.Context.RootActorId,
                    lease.Context.ProjectionKind,
                    ProjectionRuntimeMode.DurableMaterialization,
                    lease.Context is IProjectionSessionScopedMaterializationContext scopedContext
                        ? scopedContext.SessionId
                        : string.Empty),
                sp.GetService<Aevatar.Foundation.Abstractions.TypeSystem.IAgentKindVerifier>(),
                sp.GetRequiredService<Aevatar.Foundation.Abstractions.TypeSystem.IAgentKindRegistry>()));
        return services;
    }

    // Refactor (iter17/cluster-034):
    //   Old pattern: Replay-based projection scope watermark query via IEventStore (EventStoreProjectionScopeWatermarkQueryPort).
    //   New principle: Materialized ProjectionScopeStatusDocument readmodel; ProjectionScopeStatusQueryPort reads document only; never replays IEventStore.
    //   refactor helper, no behavior change beyond ensuring the existing status materialization scope.
    private sealed class ProjectionScopeStatusActivationService<TRuntimeLease>
        : IProjectionScopeActivationService<TRuntimeLease>
        where TRuntimeLease : class, IProjectionRuntimeLease
    {
        private readonly IProjectionScopeActivationService<TRuntimeLease> _inner;
        private readonly IProjectionScopeActivationService<ProjectionScopeStatusRuntimeLease>? _statusActivationService;

        public ProjectionScopeStatusActivationService(
            IProjectionScopeActivationService<TRuntimeLease> inner,
            IProjectionScopeActivationService<ProjectionScopeStatusRuntimeLease>? statusActivationService)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _statusActivationService = statusActivationService;
        }

        public async Task<TRuntimeLease> EnsureAsync(
            ProjectionScopeStartRequest request,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            var lease = await _inner.EnsureAsync(request, ct);
            if (_statusActivationService != null &&
                !ProjectionScopeStatusRuntimeRegistration.IsProjectionScopeStatusKind(request.ProjectionKind))
            {
                await _statusActivationService.EnsureAsync(
                    ProjectionScopeStatusRuntimeRegistration.BuildStatusScopeStartRequest(request),
                    ct);
            }

            return lease;
        }
    }
}
