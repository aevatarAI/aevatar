using System.Security.Cryptography;
using System.Text;
using Aevatar.Studio.Application.Provisioning;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;

namespace Aevatar.Studio.Application.Studio.Services;

public sealed class StudioMemberWorkflowBindingPort : IStudioMemberWorkflowBindingPort
{
    private readonly IStudioMemberService _memberService;

    public StudioMemberWorkflowBindingPort(IStudioMemberService memberService)
    {
        _memberService = memberService ?? throw new ArgumentNullException(nameof(memberService));
    }

    public async Task<StudioMemberWorkflowBindingResult> BindAsync(
        StudioMemberWorkflowBindingRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var receipt = await _memberService.BindAsync(
            request.ScopeId,
            request.MemberId,
            new UpdateStudioMemberBindingRequest(
                Workflow: new StudioMemberWorkflowBindingSpec(
                    ResolveWorkflowId(request),
                    [request.WorkflowYaml])),
            ct);

        return new StudioMemberWorkflowBindingResult(
            Success: true,
            ScopeId: receipt.ScopeId,
            MemberId: receipt.MemberId,
            BindingRunId: receipt.BindingRunId,
            Status: receipt.Status,
            AckStage: receipt.AckStage,
            BindingRunRole: receipt.BindingRunRole);
    }

    private static string ResolveWorkflowId(StudioMemberWorkflowBindingRequest request) =>
        string.IsNullOrWhiteSpace(request.WorkflowId)
            ? $"workflow-{BuildWorkflowKey(request.ScopeId, request.MemberId)}"
            : request.WorkflowId.Trim();

    private static string BuildWorkflowKey(string scopeId, string memberId)
    {
        var identity = Encoding.UTF8.GetBytes($"{scopeId}\n{memberId}");
        var hash = SHA256.HashData(identity);
        return Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
    }
}
