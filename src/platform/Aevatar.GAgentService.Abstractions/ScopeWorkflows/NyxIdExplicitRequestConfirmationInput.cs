using Aevatar.Workflow.Abstractions;

namespace Aevatar.GAgentService.Abstractions;

public sealed record NyxIdExplicitRequestConfirmationInput(
    string CallSiteId,
    string RequestContractDigest,
    string AttestedRisk)
{
    public NyxIdExplicitRequestConfirmation ToConfirmation() => new()
    {
        CallSiteId = CallSiteId ?? string.Empty,
        RequestContractDigest = RequestContractDigest ?? string.Empty,
        AttestedRisk = AttestedRisk switch
        {
            "read_only" => NyxIdOperationRisk.ReadOnly,
            "write" => NyxIdOperationRisk.Write,
            "destructive" => NyxIdOperationRisk.Destructive,
            _ => NyxIdOperationRisk.Unspecified,
        },
    };
}
