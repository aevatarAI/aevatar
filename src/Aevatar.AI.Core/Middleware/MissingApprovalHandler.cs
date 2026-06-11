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
        return Task.FromResult(ToolApprovalResult.Denied(
            "This execution surface has no approval channel, so approval-gated tools cannot run here. " +
            "Tell the user plainly that this tool is unavailable in this context and suggest an " +
            "alternative path; do not claim an approval request was sent or is pending."));
    }
}
