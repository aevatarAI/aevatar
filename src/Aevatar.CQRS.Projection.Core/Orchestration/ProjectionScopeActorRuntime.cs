using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

internal sealed class ProjectionScopeActorRuntime<TScopeAgent>
    where TScopeAgent : IAgent
{
    private readonly IActorRuntime _runtime;
    private readonly IActorDispatchPort _dispatchPort;
    private readonly IAgentTypeVerifier? _agentTypeVerifier;
    private readonly IStreamPubSubMaintenance? _streamPubSubMaintenance;
    private readonly ILogger<ProjectionScopeActorRuntime<TScopeAgent>> _logger;

    public ProjectionScopeActorRuntime(
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort,
        IAgentTypeVerifier? agentTypeVerifier = null,
        IStreamPubSubMaintenance? streamPubSubMaintenance = null,
        ILogger<ProjectionScopeActorRuntime<TScopeAgent>>? logger = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _agentTypeVerifier = agentTypeVerifier;
        _streamPubSubMaintenance = streamPubSubMaintenance;
        _logger = logger ?? NullLogger<ProjectionScopeActorRuntime<TScopeAgent>>.Instance;
    }

    public async Task EnsureExistsAsync(ProjectionRuntimeScopeKey scopeKey, CancellationToken ct)
    {
        var actorId = ProjectionScopeActorId.Build(scopeKey);
        if (!await _runtime.ExistsAsync(actorId).ConfigureAwait(false))
        {
            _ = await _runtime.CreateAsync<TScopeAgent>(actorId, ct).ConfigureAwait(false);
            return;
        }

        if (_agentTypeVerifier == null)
            return;

        if (await _agentTypeVerifier.IsExpectedAsync(actorId, typeof(TScopeAgent), ct).ConfigureAwait(false))
            return;

        // Stale runtime type at this scope key — most often after an actor type
        // migration where a retired-cleanup pass missed the new scope key.
        // Destroy the old actor (which also resets its event stream) and reset
        // the stream pub/sub rendezvous state so the recreated scope actor's
        // RegisterAsStreamProducer can succeed without an etag conflict, then
        // recreate as the expected type.
        _logger.LogWarning(
            "Projection scope actor {ActorId} has unexpected runtime type; destroying and recreating as {ExpectedType}.",
            actorId,
            typeof(TScopeAgent).FullName);

        await _runtime.DestroyAsync(actorId, ct).ConfigureAwait(false);
        if (_streamPubSubMaintenance != null)
            await _streamPubSubMaintenance.ResetActorStreamPubSubAsync(actorId, ct).ConfigureAwait(false);

        _ = await _runtime.CreateAsync<TScopeAgent>(actorId, ct).ConfigureAwait(false);
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
