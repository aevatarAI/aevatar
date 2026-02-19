using Aevatar.Maker.Application.Abstractions.Runs;

namespace Aevatar.Maker.Application.Runs;

internal sealed class UnconfiguredMakerRunExecutionPort : IMakerRunExecutionPort
{
    public Task<MakerRunExecutionResult> ExecuteAsync(MakerRunRequest request, CancellationToken ct = default)
    {
        _ = request;
        _ = ct;
        throw new InvalidOperationException(
            "IMakerRunExecutionPort is not configured. Register maker infrastructure execution adapter.");
    }
}
