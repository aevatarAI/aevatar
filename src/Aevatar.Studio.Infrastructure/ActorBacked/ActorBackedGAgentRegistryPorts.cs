using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
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

    private readonly IStudioActorBootstrap _bootstrap;
    private readonly IActorRuntime _actorRuntime;
    private readonly IActorDispatchPort _dispatchPort;
    private readonly IAppScopeResolver _scopeResolver;
    private readonly IProjectionDocumentReader<GAgentRegistryCurrentStateDocument, string> _documentReader;
    private readonly ILogger<ActorBackedGAgentRegistryPorts> _logger;

    public ActorBackedGAgentRegistryPorts(
        IStudioActorBootstrap bootstrap,
        IActorRuntime actorRuntime,
        IActorDispatchPort dispatchPort,
        IAppScopeResolver scopeResolver,
        IProjectionDocumentReader<GAgentRegistryCurrentStateDocument, string> documentReader,
        ILogger<ActorBackedGAgentRegistryPorts> logger)
    {
        _bootstrap = bootstrap ?? throw new ArgumentNullException(nameof(bootstrap));
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _scopeResolver = scopeResolver ?? throw new ArgumentNullException(nameof(scopeResolver));
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<GAgentActorRegistryCommandReceipt> RegisterActorAsync(
        GAgentActorRegistration registration,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeRegistration(registration);
        var actor = await EnsureWriteActorAsync(normalized.ScopeId, cancellationToken);
        await ActorCommandDispatcher.SendAsync(_dispatchPort, actor, new ActorRegisteredEvent
        {
            GagentType = normalized.GAgentType,
            ActorId = normalized.ActorId,
        }, cancellationToken);
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
        await ActorCommandDispatcher.SendAsync(_dispatchPort, actor, new ActorUnregisteredEvent
        {
            GagentType = normalized.GAgentType,
            ActorId = normalized.ActorId,
        }, cancellationToken);
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
            GAgentType = NormalizeRequired(target.GAgentType, nameof(target.GAgentType)),
            ActorId = NormalizeRequired(target.ActorId, nameof(target.ActorId)),
        };
        try
        {
            var actorId = ResolveWriteActorId(normalized.ScopeId);
            var actor = await _actorRuntime.GetAsync(actorId);
            if (actor is null)
                return ScopeResourceAdmissionResult.NotFound();

            await ActorCommandDispatcher.SendAsync(_dispatchPort, actor, new ScopeResourceAdmissionRequested
            {
                GagentType = normalized.GAgentType,
                ActorId = normalized.ActorId,
                Operation = ToRegistryOperation(normalized.Operation),
            }, cancellationToken);
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
            var groups = state.Groups
                .Select(g => new GAgentActorGroup(
                    g.GagentType,
                    g.ActorIds.ToList().AsReadOnly()))
                .ToList()
                .AsReadOnly();
            return new RegistryReadModelSnapshot(
                groups,
                document.StateVersion,
                document.UpdatedAt?.ToDateTimeOffset() ?? DateTimeOffset.MinValue);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to read registry snapshot for scope {ScopeId}", scopeId);
            throw;
        }
    }

    private string ResolveWriteActorId(string? scopeId = null) =>
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
            await ActorCommandDispatcher.SendAsync(_dispatchPort, registryActor, new ScopeResourceAdmissionRequested
            {
                GagentType = registration.GAgentType,
                ActorId = registration.ActorId,
                Operation = GAgentRegistryOperation.Use,
            }, ct);
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

    private string NormalizeScopeId(string? scopeId) =>
        string.IsNullOrWhiteSpace(scopeId)
            ? _scopeResolver.ResolveScopeIdOrDefault()
            : scopeId.Trim();

    private GAgentActorRegistration NormalizeRegistration(GAgentActorRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        return registration with
        {
            ScopeId = NormalizeScopeId(registration.ScopeId),
            GAgentType = NormalizeRequired(registration.GAgentType, nameof(registration.GAgentType)),
            ActorId = NormalizeRequired(registration.ActorId, nameof(registration.ActorId)),
        };
    }

    private static string NormalizeRequired(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{parameterName} is required.", parameterName);

        return value.Trim();
    }

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
            _ => GAgentRegistryOperation.Unknown,
        };

    private sealed record RegistryReadModelSnapshot(
        IReadOnlyList<GAgentActorGroup> Groups,
        long StateVersion,
        DateTimeOffset UpdatedAt);
}
