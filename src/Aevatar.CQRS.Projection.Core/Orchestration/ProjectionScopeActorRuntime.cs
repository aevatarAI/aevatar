using Aevatar.Foundation.Abstractions.TypeSystem;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

internal sealed class ProjectionScopeActorRuntime<TScopeAgent>
    where TScopeAgent : IAgent
{
    private readonly IActorRuntime _runtime;
    private readonly IActorDispatchPort _dispatchPort;
    private readonly IAgentTypeVerifier? _agentTypeVerifier;

    public ProjectionScopeActorRuntime(
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort,
        IAgentTypeVerifier? agentTypeVerifier = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _agentTypeVerifier = agentTypeVerifier;
    }

    public async Task EnsureExistsAsync(ProjectionRuntimeScopeKey scopeKey, CancellationToken ct)
    {
        var actorId = ProjectionScopeActorId.Build(scopeKey);
        if (!await _runtime.ExistsAsync(actorId).ConfigureAwait(false))
        {
            _ = await _runtime.CreateAsync<TScopeAgent>(actorId, ct).ConfigureAwait(false);
            return;
        }

        if (_agentTypeVerifier != null &&
            !await _agentTypeVerifier.IsExpectedAsync(actorId, typeof(TScopeAgent), ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"Actor '{actorId}' is not a `{typeof(TScopeAgent).FullName}` projection scope actor.");
        }
    }

    public async Task<bool> ExistsAsync(ProjectionRuntimeScopeKey scopeKey, CancellationToken ct)
    {
        return await _runtime.ExistsAsync(ProjectionScopeActorId.Build(scopeKey)).ConfigureAwait(false);
    }

    public Task DispatchAsync(
        ProjectionRuntimeScopeKey scopeKey,
        Google.Protobuf.IMessage payload,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var actorId = ProjectionScopeActorId.Build(scopeKey);
        var envelope = ProjectionScopeCommandEnvelopeFactory.Create(payload, actorId);
        envelope.Route = EnvelopeRouteSemantics.CreateDirect("projection.scope.port", actorId);
        return _dispatchPort.DispatchAsync(actorId, envelope, ct);
    }
}
