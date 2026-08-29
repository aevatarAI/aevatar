using Aevatar.Foundation.Abstractions.Streaming;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

public sealed class ProjectionFailureReplayService : IProjectionFailureReplayService
{
    private readonly IActorRuntime _runtime;
    private readonly IActorDispatchPort _dispatchPort;
    private readonly IStreamForwardingBindingAuthority _bindingAuthority;
    private readonly IReadOnlyList<IProjectionScopeRecoveryAgentKindResolver> _recoveryAgentKindResolvers;
    private readonly ILogger<ProjectionFailureReplayService> _logger;

    public ProjectionFailureReplayService(
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort,
        IStreamForwardingBindingAuthority bindingAuthority,
        ILogger<ProjectionFailureReplayService>? logger = null,
        IEnumerable<IProjectionScopeRecoveryAgentKindResolver>? recoveryAgentKindResolvers = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _bindingAuthority = bindingAuthority ?? throw new ArgumentNullException(nameof(bindingAuthority));
        _recoveryAgentKindResolvers = recoveryAgentKindResolvers?.ToArray() ?? [];
        _logger = logger ?? NullLogger<ProjectionFailureReplayService>.Instance;
    }

    public async Task<bool> ReplayRetryExhaustedAsync(
        ProjectionRetryExhaustedFailuresRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRetryExhaustedRequest(request);
        ct.ThrowIfCancellationRequested();

        var actorId = ProjectionScopeActorId.Build(request.ScopeKey);
        if (!await EnsureActorExistsAsync(request.ScopeKey, actorId, ct).ConfigureAwait(false))
            return false;

        var envelope = ProjectionScopeCommandEnvelopeFactory.Create(
            new ReplayRetryExhaustedProjectionFailuresCommand
            {
                MaxItems = request.MaxItems,
                ExpectedScopeStateVersion = request.ExpectedScopeStateVersion,
                ExpectedUnresolvedFailureCount = request.ExpectedUnresolvedFailureCount,
                ExpectedRetryExhaustedFailureCount = request.ExpectedRetryExhaustedFailureCount,
                RequestId = request.RequestId.Trim(),
                Reason = request.Reason.Trim(),
                RequestedBySubjectId = request.RequestedBySubjectId.Trim(),
            },
            actorId);
        envelope.Route = EnvelopeRouteSemantics.CreateDirect(
            "projection.scope.operator.replay-retry-exhausted",
            actorId);
        await _dispatchPort.DispatchAsync(actorId, envelope, ct).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> ReplayAutomaticallyAsync(
        ProjectionRuntimeScopeKey scopeKey,
        long observedScopeStateVersion,
        int maxItems = 100,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var actorId = ProjectionScopeActorId.Build(scopeKey);
        if (!await EnsureActorExistsAsync(scopeKey, actorId, ct).ConfigureAwait(false))
            return false;

        var envelope = ProjectionScopeCommandEnvelopeFactory.Create(
            new ReplayProjectionFailuresCommand
            {
                MaxItems = Math.Max(1, maxItems),
                AutomaticRecovery = true,
                ObservedScopeStateVersion = Math.Max(1, observedScopeStateVersion),
            },
            actorId);
        envelope.Route = EnvelopeRouteSemantics.CreateDirect(
            "projection.scope.automatic-recovery",
            actorId);
        await _dispatchPort.DispatchAsync(actorId, envelope, ct).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> EnsureActorExistsAsync(
        ProjectionRuntimeScopeKey scopeKey,
        string actorId,
        CancellationToken ct)
    {
        if (!await _runtime.ExistsAsync(actorId).ConfigureAwait(false))
        {
            var binding = await _bindingAuthority
                .GetAsync(scopeKey.RootActorId, actorId, ct)
                .ConfigureAwait(false);
            if (!ProjectionScopeObservationRelayBinding.TryGetRecoveryTargetActorKind(
                    binding,
                    scopeKey.RootActorId,
                    actorId,
                    out var targetActorKind))
            {
                // A non-null binding is authoritative distributed state. If its shape or
                // identity is inconsistent, a module registration must not override that
                // conflict with a process-local capability mapping.
                if (binding != null)
                {
                    _logger.LogError(
                        "Projection failure replay found invalid durable relay evidence and refused registered Agent Kind recovery. actorId={ActorId} rootActorId={RootActorId} projectionKind={ProjectionKind}",
                        actorId,
                        scopeKey.RootActorId,
                        scopeKey.ProjectionKind);
                    return false;
                }

                if (!TryResolveRegisteredRecoveryAgentKind(scopeKey, out targetActorKind))
                    return false;

                _logger.LogWarning(
                    "Projection failure replay is using a registered recovery Agent Kind because the scope runtime identity and durable relay evidence are unavailable. actorId={ActorId} rootActorId={RootActorId} projectionKind={ProjectionKind} targetActorKind={TargetActorKind}",
                    actorId,
                    scopeKey.RootActorId,
                    scopeKey.ProjectionKind,
                    targetActorKind);
            }
            else
            {
                // The durable relay is actor-owned activation evidence and carries the
                // exact registered kind. Re-establishing by that typed fact repairs a
                // state row that the runtime deliberately reports as uninitialized;
                // the original stream delivery remains pending and is then redelivered.
                _logger.LogWarning(
                    "Projection failure replay is re-establishing an uninitialized scope actor from durable relay evidence. actorId={ActorId} rootActorId={RootActorId} projectionKind={ProjectionKind} targetActorKind={TargetActorKind}",
                    actorId,
                    scopeKey.RootActorId,
                    scopeKey.ProjectionKind,
                    targetActorKind);
            }

            _ = await _runtime
                .CreateByKindAsync(targetActorKind, actorId, ct)
                .ConfigureAwait(false);
        }

        return true;
    }

    private static void ValidateRetryExhaustedRequest(
        ProjectionRetryExhaustedFailuresRequest request)
    {
        if (request.ExpectedScopeStateVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(request.ExpectedScopeStateVersion));
        if (request.ExpectedUnresolvedFailureCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(request.ExpectedUnresolvedFailureCount));
        if (request.ExpectedRetryExhaustedFailureCount <= 0 ||
            request.ExpectedRetryExhaustedFailureCount > request.ExpectedUnresolvedFailureCount)
        {
            throw new ArgumentOutOfRangeException(nameof(request.ExpectedRetryExhaustedFailureCount));
        }
        if (request.MaxItems <= 0 || request.MaxItems > request.ExpectedRetryExhaustedFailureCount)
            throw new ArgumentOutOfRangeException(nameof(request.MaxItems));

        ArgumentException.ThrowIfNullOrWhiteSpace(request.RequestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Reason);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RequestedBySubjectId);
    }

    private bool TryResolveRegisteredRecoveryAgentKind(
        ProjectionRuntimeScopeKey scopeKey,
        out string agentKind)
    {
        string? resolvedAgentKind = null;
        foreach (var resolver in _recoveryAgentKindResolvers)
        {
            if (!resolver.TryResolve(scopeKey, out var candidateAgentKind))
                continue;

            if (string.IsNullOrWhiteSpace(candidateAgentKind))
            {
                _logger.LogError(
                    "Projection failure replay found a registered recovery resolver that matched without an Agent Kind. rootActorId={RootActorId} projectionKind={ProjectionKind} mode={Mode} resolverType={ResolverType}",
                    scopeKey.RootActorId,
                    scopeKey.ProjectionKind,
                    scopeKey.Mode,
                    resolver.GetType().FullName);
                agentKind = string.Empty;
                return false;
            }

            candidateAgentKind = candidateAgentKind.Trim();
            if (resolvedAgentKind == null)
            {
                resolvedAgentKind = candidateAgentKind;
                continue;
            }

            if (string.Equals(resolvedAgentKind, candidateAgentKind, StringComparison.Ordinal))
                continue;

            _logger.LogError(
                "Projection failure replay found conflicting registered recovery Agent Kinds. rootActorId={RootActorId} projectionKind={ProjectionKind} mode={Mode} firstAgentKind={FirstAgentKind} conflictingAgentKind={ConflictingAgentKind}",
                scopeKey.RootActorId,
                scopeKey.ProjectionKind,
                scopeKey.Mode,
                resolvedAgentKind,
                candidateAgentKind);
            agentKind = string.Empty;
            return false;
        }

        agentKind = resolvedAgentKind ?? string.Empty;
        return agentKind.Length > 0;
    }
}
