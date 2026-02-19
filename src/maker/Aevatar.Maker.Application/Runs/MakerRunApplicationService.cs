using Aevatar.Maker.Application.Abstractions.Runs;

namespace Aevatar.Maker.Application.Runs;

public sealed class MakerRunApplicationService : IMakerRunApplicationService
{
    private readonly IMakerRunExecutionPort _executionPort;

    public MakerRunApplicationService(
        IMakerRunExecutionPort executionPort)
    {
        _executionPort = executionPort;
    }

    public Task<MakerRunExecutionResult> ExecuteAsync(MakerRunRequest request, CancellationToken ct = default) =>
        _executionPort.ExecuteAsync(request, ct);
}
