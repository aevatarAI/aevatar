using Aevatar.AppPlatform.Abstractions;
using Aevatar.AppPlatform.Abstractions.Ports;

namespace Aevatar.AppPlatform.Application.Services;

public sealed class OperationCommandApplicationService : IOperationCommandPort
{
    private readonly IAppOperationStore _operationStore;

    public OperationCommandApplicationService(IAppOperationStore operationStore)
    {
        _operationStore = operationStore ?? throw new ArgumentNullException(nameof(operationStore));
    }

    public Task<AppOperationSnapshot> AcceptAsync(
        AppOperationSnapshot snapshot,
        CancellationToken ct = default) =>
        _operationStore.AcceptAsync(snapshot, ct);

    public Task<AppOperationSnapshot> AdvanceAsync(
        AppOperationUpdate update,
        CancellationToken ct = default) =>
        _operationStore.AdvanceAsync(update, ct);
}
