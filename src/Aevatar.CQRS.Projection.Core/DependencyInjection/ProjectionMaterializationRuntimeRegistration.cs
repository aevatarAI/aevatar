using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
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
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            ICommittedStatePublicationHook,
            ProjectionScopeCommittedStateRedactionHook>());
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
                sp.GetRequiredService<IStreamForwardingBindingAuthority>(),
                sp.GetRequiredService<IStreamForwardingRegistry>());

            return materializeScopeStatus
                ? new ProjectionScopeStatusActivationService<TRuntimeLease>(
                    activationService,
                    sp.GetService<IProjectionScopeActivationService<ProjectionScopeStatusRuntimeLease>>(),
                    sp.GetService<IStreamForwardingBindingAuthority>())
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
                sp.GetRequiredService<Aevatar.Foundation.Abstractions.TypeSystem.IAgentKindRegistry>(),
                sp.GetRequiredService<IStreamForwardingBindingAuthority>(),
                sp.GetRequiredService<IStreamForwardingRegistry>()));
        return services;
    }

    /// <summary>
    /// Ensures the status writer of a durable materialization scope alongside the scope itself.
    /// The source scope actor owns the status write route: once it has adopted the terminal
    /// materializer (durable relay evidence on its stream), the legacy event-sourced status
    /// shadow is no longer ensured for it; until then the legacy shadow is ensured exactly as
    /// before. Steady state after adoption costs one relay-evidence read and no legacy work.
    /// </summary>
    private sealed class ProjectionScopeStatusActivationService<TRuntimeLease>
        : IProjectionScopeActivationService<TRuntimeLease>
        where TRuntimeLease : class, IProjectionRuntimeLease
    {
        private readonly IProjectionScopeActivationService<TRuntimeLease> _inner;
        private readonly IProjectionScopeActivationService<ProjectionScopeStatusRuntimeLease>? _statusActivationService;
        private readonly IStreamForwardingBindingAuthority? _bindingAuthority;

        public ProjectionScopeStatusActivationService(
            IProjectionScopeActivationService<TRuntimeLease> inner,
            IProjectionScopeActivationService<ProjectionScopeStatusRuntimeLease>? statusActivationService,
            IStreamForwardingBindingAuthority? bindingAuthority)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _statusActivationService = statusActivationService;
            _bindingAuthority = bindingAuthority;
        }

        public async Task<TRuntimeLease> EnsureAsync(
            ProjectionScopeStartRequest request,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            var lease = await _inner.EnsureAsync(request, ct);
            if (_statusActivationService == null ||
                ProjectionScopeStatusRuntimeRegistration.IsProjectionScopeStatusKind(request.ProjectionKind))
            {
                return lease;
            }

            if (await HasTerminalStatusRouteAsync(request, ct))
                return lease;

            await _statusActivationService.EnsureAsync(
                ProjectionScopeStatusRuntimeRegistration.BuildStatusScopeStartRequest(request),
                ct);
            return lease;
        }

        private async Task<bool> HasTerminalStatusRouteAsync(ProjectionScopeStartRequest request, CancellationToken ct)
        {
            if (_bindingAuthority == null)
                return false;

            var sourceScopeActorId = ProjectionScopeActorId.Build(new ProjectionRuntimeScopeKey(
                request.RootActorId ?? string.Empty,
                request.ProjectionKind ?? string.Empty,
                ProjectionRuntimeMode.DurableMaterialization,
                request.SessionId));
            var terminalActorId = ProjectionScopeStatusRoutes.BuildTerminalActorId(sourceScopeActorId);
            var binding = await _bindingAuthority.GetAsync(sourceScopeActorId, terminalActorId, ct);
            return ProjectionScopeObservationRelayBinding.IsExactActivationEvidence(
                binding,
                sourceScopeActorId,
                terminalActorId,
                ProjectionScopeStatusGAgent.AgentKind);
        }
    }
}
