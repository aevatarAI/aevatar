namespace Aevatar.AppPlatform.Abstractions.Ports;

public interface IOperationCommandPort
{
    Task<AppOperationSnapshot> AcceptAsync(
        AppOperationSnapshot snapshot,
        CancellationToken ct = default);

    Task<AppOperationSnapshot> AdvanceAsync(
        AppOperationUpdate update,
        CancellationToken ct = default);
}
