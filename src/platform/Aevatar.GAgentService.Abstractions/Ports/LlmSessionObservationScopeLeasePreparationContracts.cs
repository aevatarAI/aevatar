namespace Aevatar.GAgentService.Abstractions.Ports;

public sealed record LlmSessionObservationScopeLeasePreparation(
    string ActorId,
    string ResponseId);

public interface ILlmSessionObservationScopeLeasePreparationPort
{
    Task<LlmSessionObservationScopeLeasePreparation?> PrepareAsync(
        string actorId,
        string responseId,
        CancellationToken ct = default);

    Task ReleaseAsync(
        LlmSessionObservationScopeLeasePreparation preparation,
        CancellationToken ct = default);
}
