using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Helpers;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.Studio.Application.Studio.Abstractions;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgentService.Application.ScopeGAgents;

internal sealed class GAgentDraftRunActorPreparationService : IGAgentDraftRunActorPreparationPort
{
    private readonly IActorRuntime _actorRuntime;
    private readonly IGAgentActorStore _actorStore;
    private readonly ILogger<GAgentDraftRunActorPreparationService>? _logger;

    public GAgentDraftRunActorPreparationService(
        IActorRuntime actorRuntime,
        IGAgentActorStore actorStore,
        ILogger<GAgentDraftRunActorPreparationService>? logger = null)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _actorStore = actorStore ?? throw new ArgumentNullException(nameof(actorStore));
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
            return GAgentDraftRunPreparationResult.Success(
                new GAgentDraftRunPreparedActor(
                    scopeId,
                    actorTypeName,
                    actorId,
                    RequiresRollbackOnFailure: false));
        }

        await _actorStore.AddActorAsync(scopeId, actorTypeName, actorId, ct);
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

        try
        {
            await _actorRuntime.DestroyAsync(preparedActor.ActorId, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to destroy draft-run actor {ActorId} during rollback", preparedActor.ActorId);
        }

        try
        {
            await _actorStore.RemoveActorAsync(
                preparedActor.ScopeId,
                preparedActor.ActorTypeName,
                preparedActor.ActorId,
                ct);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to remove draft-run actor {ActorId} from registry during rollback", preparedActor.ActorId);
        }
    }
}
