namespace Aevatar.GAgentService.Abstractions.ScopeGAgents;

public sealed record GAgentDraftRunObservationScopeActivation(
    string ActorId,
    string CommandId,
    string CorrelationId);

public interface IGAgentDraftRunObservationScopeActivationPort
{
    Task<GAgentDraftRunObservationScopeActivation?> ActivateAsync(
        string actorId,
        string commandId,
        string correlationId,
        CancellationToken ct = default);

    Task ReleaseAsync(
        GAgentDraftRunObservationScopeActivation activation,
        CancellationToken ct = default);
}
