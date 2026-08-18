using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Aevatar.CQRS.Projection.Core.Observability;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

internal sealed class ProjectionScopeActorRuntime<TScopeAgent>
    where TScopeAgent : IAgent
{
    private readonly IActorRuntime _runtime;
    private readonly IActorDispatchPort _dispatchPort;
    private readonly IAgentKindVerifier? _agentKindVerifier;
    private readonly string _scopeAgentKind;
    private readonly IStreamPubSubMaintenance? _streamPubSubMaintenance;
    private readonly ILogger<ProjectionScopeActorRuntime<TScopeAgent>> _logger;

    public ProjectionScopeActorRuntime(
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort,
        IAgentKindVerifier? agentKindVerifier = null,
        IAgentKindRegistry? agentKindRegistry = null,
        IStreamPubSubMaintenance? streamPubSubMaintenance = null,
        ILogger<ProjectionScopeActorRuntime<TScopeAgent>>? logger = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _agentKindVerifier = agentKindVerifier;
        _scopeAgentKind = ResolveScopeAgentKind(agentKindRegistry);
        _streamPubSubMaintenance = streamPubSubMaintenance;
        _logger = logger ?? NullLogger<ProjectionScopeActorRuntime<TScopeAgent>>.Instance;
    }

    public async Task<ProjectionScopeActorEnsureResult> EnsureExistsAsync(
        ProjectionRuntimeScopeKey scopeKey,
        CancellationToken ct)
    {
        var actorId = ProjectionScopeActorId.Build(scopeKey);
        var existenceStartedAt = ProjectionActivationMetrics.StartTimestamp();
        bool exists;
        try
        {
            exists = await _runtime.ExistsAsync(actorId).ConfigureAwait(false);
            ProjectionActivationMetrics.RecordStage(
                ProjectionActivationMetrics.ExistenceLookupStage,
                existenceStartedAt,
                scopeKey.Mode,
                "success");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            ProjectionActivationMetrics.RecordStage(
                ProjectionActivationMetrics.ExistenceLookupStage,
                existenceStartedAt,
                scopeKey.Mode,
                "cancelled");
            throw;
        }
        catch
        {
            ProjectionActivationMetrics.RecordStage(
                ProjectionActivationMetrics.ExistenceLookupStage,
                existenceStartedAt,
                scopeKey.Mode,
                "failure");
            throw;
        }

        if (!exists)
        {
            _ = await _runtime.CreateByKindAsync(_scopeAgentKind, actorId, ct).ConfigureAwait(false);
            return ProjectionScopeActorEnsureResult.Proven;
        }

        if (_agentKindVerifier == null)
            return ProjectionScopeActorEnsureResult.Unverified;

        var kindStartedAt = ProjectionActivationMetrics.StartTimestamp();
        try
        {
            if (await _agentKindVerifier.IsExpectedKindAsync(actorId, _scopeAgentKind, ct).ConfigureAwait(false))
            {
                ProjectionActivationMetrics.RecordStage(
                    ProjectionActivationMetrics.KindVerificationStage,
                    kindStartedAt,
                    scopeKey.Mode,
                    "success");
                return ProjectionScopeActorEnsureResult.Proven;
            }

            ProjectionActivationMetrics.RecordStage(
                ProjectionActivationMetrics.KindVerificationStage,
                kindStartedAt,
                scopeKey.Mode,
                "mismatch");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            ProjectionActivationMetrics.RecordStage(
                ProjectionActivationMetrics.KindVerificationStage,
                kindStartedAt,
                scopeKey.Mode,
                "cancelled");
            throw;
        }
        catch
        {
            ProjectionActivationMetrics.RecordStage(
                ProjectionActivationMetrics.KindVerificationStage,
                kindStartedAt,
                scopeKey.Mode,
                "failure");
            throw;
        }

        // Stale runtime kind at this scope key — most often after an actor kind
        // migration where a retired-cleanup pass missed the new scope key.
        // Destroy the old actor (which also resets its event stream) and reset
        // the stream pub/sub rendezvous state so the recreated scope actor's
        // RegisterAsStreamProducer can succeed without an etag conflict, then
        // recreate as the expected kind.
        _logger.LogWarning(
            "Projection scope actor {ActorId} has unexpected runtime kind; destroying and recreating as {ExpectedKind}.",
            actorId,
            _scopeAgentKind);

        await _runtime.DestroyAsync(actorId, ct).ConfigureAwait(false);

        // Pub/sub reset is best-effort: at this point the old actor is already
        // destroyed, so a future IStreamPubSubMaintenance impl that throws must
        // not block the recreate — failing here would leave us strictly worse
        // than the pre-self-heal state (the type mismatch at least had an actor).
        // Matches RetiredActorCleanupHostedService.CleanupStreamPubSubBestEffortAsync.
        if (_streamPubSubMaintenance != null)
        {
            try
            {
                await _streamPubSubMaintenance
                    .ResetActorStreamPubSubAsync(actorId, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Stream pub/sub state reset failed during projection scope self-heal for {ActorId}; proceeding with recreate.",
                    actorId);
            }
        }

        _ = await _runtime.CreateByKindAsync(_scopeAgentKind, actorId, ct).ConfigureAwait(false);
        return ProjectionScopeActorEnsureResult.Proven;
    }

    public async Task<bool> ExistsAsync(ProjectionRuntimeScopeKey scopeKey, CancellationToken ct)
    {
        return await _runtime.ExistsAsync(ProjectionScopeActorId.Build(scopeKey)).ConfigureAwait(false);
    }

    public string ScopeAgentKind => _scopeAgentKind;

    public async Task DispatchAsync(
        ProjectionRuntimeScopeKey scopeKey,
        Google.Protobuf.IMessage payload,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var actorId = ProjectionScopeActorId.Build(scopeKey);
        var envelope = ProjectionScopeCommandEnvelopeFactory.Create(payload, actorId);
        envelope.Route = EnvelopeRouteSemantics.CreateDirect("projection.scope.port", actorId);
        _ = await _dispatchPort.DispatchAsync(actorId, envelope, ct).ConfigureAwait(false);
    }
    private static string ResolveScopeAgentKind(IAgentKindRegistry? agentKindRegistry)
    {
        if (agentKindRegistry == null)
            throw new InvalidOperationException("IAgentKindRegistry is required for projection scope actor runtime.");

        if (!agentKindRegistry.TryGetKindForAgentType(typeof(TScopeAgent), out var kind))
        {
            throw new InvalidOperationException(
                $"Projection scope agent type {typeof(TScopeAgent).FullName} is not registered with a primary [GAgent] kind.");
        }

        return kind;
    }
}

internal readonly record struct ProjectionScopeActorEnsureResult(bool ExpectedKindProven)
{
    public static ProjectionScopeActorEnsureResult Proven { get; } = new(true);

    public static ProjectionScopeActorEnsureResult Unverified { get; } = new(false);
}
