namespace Aevatar.GAgentService.Abstractions.ScopeGAgents;

public sealed record GAgentDraftRunPreparationRequest(
    string ScopeId,
    string ActorTypeName,
    string? PreferredActorId = null);

public sealed record GAgentDraftRunPreparedActor(
    string ScopeId,
    string ActorTypeName,
    string ActorId,
    bool RequiresRollbackOnFailure);

public sealed record GAgentDraftRunPreparationResult(
    GAgentDraftRunPreparedActor? PreparedActor,
    GAgentDraftRunStartError Error)
{
    public bool Succeeded => Error == GAgentDraftRunStartError.None && PreparedActor is not null;

    public static GAgentDraftRunPreparationResult Success(GAgentDraftRunPreparedActor preparedActor)
    {
        ArgumentNullException.ThrowIfNull(preparedActor);
        return new GAgentDraftRunPreparationResult(preparedActor, GAgentDraftRunStartError.None);
    }

    public static GAgentDraftRunPreparationResult Failure(GAgentDraftRunStartError error) =>
        new(null, error);
}

public interface IGAgentDraftRunActorPreparationPort
{
    Task<GAgentDraftRunPreparationResult> PrepareAsync(
        GAgentDraftRunPreparationRequest request,
        CancellationToken ct = default);

    Task RollbackAsync(
        GAgentDraftRunPreparedActor preparedActor,
        CancellationToken ct = default);
}
