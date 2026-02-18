namespace Aevatar.CQRS.Core.Abstractions.Queries;

public interface IAgentQueryService<TAgentSummary>
{
    Task<IReadOnlyList<TAgentSummary>> ListAgentsAsync(CancellationToken ct = default);
}
