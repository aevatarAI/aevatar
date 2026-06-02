using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.Core.Middleware;

/// <summary>refactor helper, no behavior change: fail-closed fallback when no approval handler is registered.</summary>
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
