using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.GAgents.Registry;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.Studio.Projection.ReadModels;
using Microsoft.Extensions.Logging;

namespace Aevatar.Studio.Infrastructure.ActorBacked;

internal sealed class ActorBackedGAgentRegistryPorts :
    IGAgentActorRegistryCommandPort,
    IGAgentActorRegistryQueryPort,
    IScopeResourceAdmissionPort
{
    private const string WriteActorIdPrefix = "gagent-registry-";
    private const string PublisherId = "aevatar.studio.infrastructure.gagent-registry";

    private readonly IStudioActorBootstrap _bootstrap;
    private readonly IActorRuntime _actorRuntime;
    private readonly StudioActorCommandDispatch _commandDispatch;
    private readonly IProjectionDocumentReader<GAgentRegistryCurrentStateDocument, string> _documentReader;
    private readonly IAgentKindRegistry _agentKindRegistry;
    private readonly ILogger<ActorBackedGAgentRegistryPorts> _logger;

    public ActorBackedGAgentRegistryPorts(
        IStudioActorBootstrap bootstrap,
        IActorRuntime actorRuntime,
        StudioActorCommandDispatch commandDispatch,
        IAppScopeResolver scopeResolver,
        IProjectionDocumentReader<GAgentRegistryCurrentStateDocument, string> documentReader,
        IAgentKindRegistry agentKindRegistry,
        ILogger<ActorBackedGAgentRegistryPorts> logger)
    {
        _bootstrap = bootstrap ?? throw new ArgumentNullException(nameof(bootstrap));
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _commandDispatch = commandDispatch ?? throw new ArgumentNullException(nameof(commandDispatch));
        _ = scopeResolver ?? throw new ArgumentNullException(nameof(scopeResolver));
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
        _agentKindRegistry = agentKindRegistry ?? throw new ArgumentNullException(nameof(agentKindRegistry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<GAgentActorRegistryCommandReceipt> RegisterActorAsync(
        GAgentActorRegistration registration,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeRegistration(registration);
        var actor = await EnsureWriteActorAsync(normalized.ScopeId, cancellationToken);
        await _commandDispatch.DispatchAsync(actor, new ActorRegisteredEvent
        {
            AgentKind = normalized.AgentKind,
            ActorId = normalized.ActorId,
        }, PublisherId, cancellationToken);
        var stage = await VerifyAdmissionVisibleAsync(actor, normalized, cancellationToken)
            ? GAgentActorRegistryCommandStage.AdmissionVisible
            : GAgentActorRegistryCommandStage.AcceptedForDispatch;
        return new GAgentActorRegistryCommandReceipt(
            normalized,
            stage);
    }

    public async Task<GAgentActorRegistryCommandReceipt> UnregisterActorAsync(
        GAgentActorRegistration registration,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeRegistration(registration);
        var actor = await EnsureWriteActorAsync(normalized.ScopeId, cancellationToken);
        await _commandDispatch.DispatchAsync(actor, new ActorUnregisteredEvent
        {
            AgentKind = normalized.AgentKind,
            ActorId = normalized.ActorId,
        }, PublisherId, cancellationToken);
        return new GAgentActorRegistryCommandReceipt(
            normalized,
            GAgentActorRegistryCommandStage.AdmissionRemoved);
    }

    public async Task<GAgentActorRegistrySnapshot> ListActorsAsync(
        string scopeId,
        CancellationToken cancellationToken = default)
    {
        var normalizedScopeId = NormalizeScopeId(scopeId);
        var snapshot = await ReadSnapshotAsync(normalizedScopeId, cancellationToken);
        return new GAgentActorRegistrySnapshot(
            normalizedScopeId,
            snapshot.Groups,
            snapshot.StateVersion,
            snapshot.UpdatedAt,
            DateTimeOffset.UtcNow);
    }

    public async Task<ScopeResourceAdmissionResult> AuthorizeTargetAsync(
        ScopeResourceTarget target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (target.ResourceKind != ScopeResourceKind.GAgentActor)
            return ScopeResourceAdmissionResult.Denied();

        var normalized = target with
        {
            ScopeId = NormalizeScopeId(target.ScopeId),
            AgentKind = NormalizeRequired(target.AgentKind, nameof(target.AgentKind)),
            ActorId = NormalizeRequired(target.ActorId, nameof(target.ActorId)),
        };
        if (!IsRegisteredAgentKind(normalized.AgentKind))
            return ScopeResourceAdmissionResult.NotFound();

        try
        {
            var actorId = ResolveWriteActorId(normalized.ScopeId);
            var actor = await _actorRuntime.GetAsync(actorId);
            if (actor is null)
                return ScopeResourceAdmissionResult.NotFound();

            await _commandDispatch.DispatchAsync(actor, new ScopeResourceAdmissionRequested
            {
                ScopeId = normalized.ScopeId,
                AgentKind = normalized.AgentKind,
                ActorId = normalized.ActorId,
                Operation = ToRegistryOperation(normalized.Operation),
            }, PublisherId, cancellationToken);
            return ScopeResourceAdmissionResult.Allowed();
        }
        catch (GAgentRegistryAdmissionNotFoundException)
        {
            return ScopeResourceAdmissionResult.NotFound();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Registry admission was unavailable for scope {ScopeId}, actor {ActorId}",
                normalized.ScopeId,
                normalized.ActorId);
            return ScopeResourceAdmissionResult.Unavailable();
        }
    }

    private async Task<RegistryReadModelSnapshot> ReadSnapshotAsync(
        string scopeId,
        CancellationToken cancellationToken)
    {
        var actorId = ResolveWriteActorId(scopeId);
        try
        {
            var document = await _documentReader.GetAsync(actorId, cancellationToken);
            if (document?.StateRoot == null ||
                !document.StateRoot.Is(GAgentRegistryState.Descriptor))
                return new RegistryReadModelSnapshot([], 0, DateTimeOffset.MinValue);

            var state = document.StateRoot.Unpack<GAgentRegistryState>();
            var groups = new List<GAgentActorGroup>();
            foreach (var group in state.Groups)
            {
                if (!IsRegisteredAgentKind(group.AgentKind))
                {
                    foreach (var registeredActorId in group.ActorIds)
                    {
                        _logger.LogWarning(
                            "GAgent registry legacy row is quarantined for scope {ScopeId}, actor {ActorId}, previous key {PreviousRegistryKey}",
                            scopeId,
                            registeredActorId,
                            group.AgentKind);
                    }

                    continue;
                }

                groups.Add(new GAgentActorGroup(
                    group.AgentKind,
                    group.ActorIds.ToList().AsReadOnly()));
            }

            var canonicalGroups = groups
                .ToList()
                .AsReadOnly();
            return new RegistryReadModelSnapshot(
                canonicalGroups,
                document.StateVersion,
                document.UpdatedAt?.ToDateTimeOffset() ?? DateTimeOffset.MinValue);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to read registry snapshot for scope {ScopeId}", scopeId);
            throw;
        }
    }

    private static string ResolveWriteActorId(string? scopeId) =>
        WriteActorIdPrefix + NormalizeScopeId(scopeId);

    private Task<IActor> EnsureWriteActorAsync(string? scopeId, CancellationToken ct) =>
        _bootstrap.EnsureAsync<GAgentRegistryGAgent>(ResolveWriteActorId(scopeId), ct);

    private async Task<bool> VerifyAdmissionVisibleAsync(
        IActor registryActor,
        GAgentActorRegistration registration,
        CancellationToken ct)
    {
        try
        {
            await _commandDispatch.DispatchAsync(registryActor, new ScopeResourceAdmissionRequested
            {
                ScopeId = registration.ScopeId,
                AgentKind = registration.AgentKind,
                ActorId = registration.ActorId,
                Operation = GAgentRegistryOperation.Use,
            }, PublisherId, ct);
            return true;
        }
        catch (GAgentRegistryAdmissionNotFoundException)
        {
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Registry registration was dispatched but admission visibility could not be verified for scope {ScopeId}, actor {ActorId}",
                registration.ScopeId,
                registration.ActorId);
            return false;
        }
    }

    private static string NormalizeScopeId(string? scopeId) =>
        NormalizeRequired(scopeId ?? string.Empty, "ScopeId");

    private GAgentActorRegistration NormalizeRegistration(GAgentActorRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        return registration with
        {
            ScopeId = NormalizeScopeId(registration.ScopeId),
            AgentKind = NormalizeAgentKind(registration.AgentKind, nameof(registration.AgentKind)),
            ActorId = NormalizeRequired(registration.ActorId, nameof(registration.ActorId)),
        };
    }

    private string NormalizeAgentKind(string value, string parameterName)
    {
        var normalized = NormalizeRequired(value, parameterName);
        if (!IsRegisteredAgentKind(normalized))
            throw new ArgumentException($"AgentKind '{normalized}' is not registered.", parameterName);

        return normalized;
    }

    private static string NormalizeRequired(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{parameterName} is required.", parameterName);

        return value.Trim();
    }

    private bool IsRegisteredAgentKind(string? agentKind) =>
        !string.IsNullOrWhiteSpace(agentKind) &&
        _agentKindRegistry.TryResolve(agentKind.Trim(), out _);

    private static GAgentRegistryOperation ToRegistryOperation(ScopeResourceOperation operation) =>
        operation switch
        {
            ScopeResourceOperation.Use => GAgentRegistryOperation.Use,
            ScopeResourceOperation.Delete => GAgentRegistryOperation.Delete,
            ScopeResourceOperation.Chat => GAgentRegistryOperation.Chat,
            ScopeResourceOperation.Stream => GAgentRegistryOperation.Stream,
            ScopeResourceOperation.Approve => GAgentRegistryOperation.Approve,
            ScopeResourceOperation.Join => GAgentRegistryOperation.Join,
            ScopeResourceOperation.ListParticipants => GAgentRegistryOperation.ListParticipants,
            ScopeResourceOperation.DraftRunReuse => GAgentRegistryOperation.DraftRunReuse,
            ScopeResourceOperation.Control => GAgentRegistryOperation.Control,
            _ => GAgentRegistryOperation.Unknown,
        };

    private sealed record RegistryReadModelSnapshot(
        IReadOnlyList<GAgentActorGroup> Groups,
        long StateVersion,
        DateTimeOffset UpdatedAt);
}
