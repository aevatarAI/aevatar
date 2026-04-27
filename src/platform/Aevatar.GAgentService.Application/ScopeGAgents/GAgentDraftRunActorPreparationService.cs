using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Helpers;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.Studio.Application.Studio.Abstractions;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgentService.Application.ScopeGAgents;

internal sealed class GAgentDraftRunActorPreparationService : IGAgentDraftRunActorPreparationPort
{
    private readonly IActorRuntime _actorRuntime;
    private readonly IGAgentActorRegistryCommandPort _registryCommandPort;
    private readonly IScopeResourceAdmissionPort _admissionPort;
    private readonly ILogger<GAgentDraftRunActorPreparationService>? _logger;

    public GAgentDraftRunActorPreparationService(
        IActorRuntime actorRuntime,
        IGAgentActorRegistryCommandPort registryCommandPort,
        IScopeResourceAdmissionPort admissionPort,
        ILogger<GAgentDraftRunActorPreparationService>? logger = null)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _registryCommandPort = registryCommandPort ?? throw new ArgumentNullException(nameof(registryCommandPort));
        _admissionPort = admissionPort ?? throw new ArgumentNullException(nameof(admissionPort));
        _logger = logger;
    }

    public async Task<GAgentDraftRunPreparationResult> PrepareAsync(
        GAgentDraftRunPreparationRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var scopeId = request.ScopeId.Trim();
        var actorTypeName = request.ActorTypeName.Trim();
        var actorType = ScopeGAgentActorTypeResolver.Resolve(actorTypeName);
        if (actorType is null)
            return GAgentDraftRunPreparationResult.Failure(GAgentDraftRunStartError.UnknownActorType);

        var actorId = string.IsNullOrWhiteSpace(request.PreferredActorId)
            ? AgentId.New(actorType)
            : request.PreferredActorId.Trim();
        var existingActor = await _actorRuntime.GetAsync(actorId);
        if (existingActor is not null)
        {
            var admission = await _admissionPort.AuthorizeTargetAsync(
                new ScopeResourceTarget(
                    scopeId,
                    ScopeResourceKind.GAgentActor,
                    actorTypeName,
                    actorId,
                    ScopeResourceOperation.DraftRunReuse),
                ct);
            if (!admission.IsAllowed)
                return GAgentDraftRunPreparationResult.Failure(GAgentDraftRunStartError.ActorTypeMismatch);

            return GAgentDraftRunPreparationResult.Success(
                new GAgentDraftRunPreparedActor(
                    scopeId,
                    actorTypeName,
                    actorId,
                    RequiresRollbackOnFailure: false));
        }

        var registrationAttempted = false;
        IActor? createdActor = null;
        try
        {
            createdActor = await _actorRuntime.CreateAsync(actorType, actorId, ct);
            registrationAttempted = true;
            var receipt = await _registryCommandPort.RegisterActorAsync(
                new GAgentActorRegistration(scopeId, actorTypeName, actorId),
                ct);
            if (!receipt.IsAdmissionVisible)
            {
                await RollbackCreatedActorAsync(scopeId, actorTypeName, actorId, registrationAttempted, CancellationToken.None);
                return GAgentDraftRunPreparationResult.Failure(GAgentDraftRunStartError.ActorTypeMismatch);
            }
        }
        catch
        {
            if (createdActor is not null)
                await RollbackCreatedActorAsync(scopeId, actorTypeName, actorId, registrationAttempted, CancellationToken.None);
            throw;
        }

        return GAgentDraftRunPreparationResult.Success(
            new GAgentDraftRunPreparedActor(
                scopeId,
                actorTypeName,
                actorId,
                RequiresRollbackOnFailure: true));
    }

    public async Task RollbackAsync(
        GAgentDraftRunPreparedActor preparedActor,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(preparedActor);

        if (!preparedActor.RequiresRollbackOnFailure)
            return;

        if (!await TryUnregisterDraftRunActorAsync(
                preparedActor.ScopeId,
                preparedActor.ActorTypeName,
                preparedActor.ActorId,
                ct))
            return;

        try
        {
            await _actorRuntime.DestroyAsync(preparedActor.ActorId, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to destroy draft-run actor {ActorId} during rollback", preparedActor.ActorId);
        }

    }

    private async Task RollbackCreatedActorAsync(
        string scopeId,
        string actorTypeName,
        string actorId,
        bool unregisterFromRegistry,
        CancellationToken ct)
    {
        if (unregisterFromRegistry &&
            !await TryUnregisterDraftRunActorAsync(scopeId, actorTypeName, actorId, ct))
            return;

        try
        {
            await _actorRuntime.DestroyAsync(actorId, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to destroy draft-run actor {ActorId} during rollback", actorId);
        }

    }

    private async Task<bool> TryUnregisterDraftRunActorAsync(
        string scopeId,
        string actorTypeName,
        string actorId,
        CancellationToken ct)
    {
        try
        {
            await _registryCommandPort.UnregisterActorAsync(
                new GAgentActorRegistration(scopeId, actorTypeName, actorId),
                ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to remove draft-run actor {ActorId} from registry during rollback", actorId);
            return false;
        }
    }

}
