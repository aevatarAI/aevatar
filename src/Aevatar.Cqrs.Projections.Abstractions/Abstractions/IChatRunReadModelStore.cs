using Aevatar.Cqrs.Projections.Abstractions.ReadModels;

namespace Aevatar.Cqrs.Projections.Abstractions;

/// <summary>
/// Read-model store for chat run projections.
/// </summary>
public interface IChatRunReadModelStore
{
    Task UpsertAsync(ChatRunReport report, CancellationToken ct = default);

    Task MutateAsync(string runId, Action<ChatRunReport> mutate, CancellationToken ct = default);

    Task<ChatRunReport?> GetAsync(string runId, CancellationToken ct = default);

    Task<IReadOnlyList<ChatRunReport>> ListAsync(int take = 50, CancellationToken ct = default);
}
