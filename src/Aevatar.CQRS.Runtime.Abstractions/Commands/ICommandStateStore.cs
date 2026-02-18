namespace Aevatar.CQRS.Runtime.Abstractions.Commands;

public interface ICommandStateStore
{
    Task UpsertAsync(CommandExecutionState state, CancellationToken ct = default);

    Task<CommandExecutionState?> GetAsync(string commandId, CancellationToken ct = default);

    Task<IReadOnlyList<CommandExecutionState>> ListAsync(int take = 100, CancellationToken ct = default);
}
