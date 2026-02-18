namespace Aevatar.Maker.Application.Abstractions.Runs;

public interface IMakerRunApplicationService
{
    Task<MakerRunExecutionResult> ExecuteAsync(MakerRunRequest request, CancellationToken ct = default);
}
