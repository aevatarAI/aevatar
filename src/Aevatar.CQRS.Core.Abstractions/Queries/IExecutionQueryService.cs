namespace Aevatar.CQRS.Core.Abstractions.Queries;

public interface IExecutionQueryService<TExecutionSummary, TExecutionDetail>
{
    bool ExecutionQueryEnabled { get; }

    Task<IReadOnlyList<TExecutionSummary>> ListAsync(int take = 50, CancellationToken ct = default);

    Task<TExecutionDetail?> GetAsync(string executionId, CancellationToken ct = default);
}
