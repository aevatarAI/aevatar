using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.Core.Middleware;

public sealed class MissingApprovalHandler : IToolApprovalHandler
{
    public static MissingApprovalHandler Instance { get; } = new();

    private MissingApprovalHandler()
    {
    }

    public Task<ToolApprovalResult> RequestApprovalAsync(ToolApprovalRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(ToolApprovalResult.Denied("No tool approval handler is registered."));
    }
}
