namespace Aevatar.Maker.Application.Abstractions.Runs;

public interface IMakerRunExecutionPort
{
    Task<MakerRunExecutionResult> ExecuteAsync(MakerRunRequest request, CancellationToken ct = default);
}
