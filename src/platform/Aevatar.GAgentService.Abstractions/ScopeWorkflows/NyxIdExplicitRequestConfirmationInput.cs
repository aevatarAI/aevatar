using Aevatar.Workflow.Abstractions;

namespace Aevatar.GAgentService.Abstractions;

public sealed record NyxIdExplicitRequestConfirmationInput(
    string CallSiteId,
    string RequestContractDigest,
    string AttestedRisk,
    string WorkflowId = "",
    string RevisionId = "")
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
        WorkflowId = WorkflowId ?? string.Empty,
        RevisionId = RevisionId ?? string.Empty,
    };
}

public static class NyxIdExplicitRequestConfirmationInputs
{
    public static IReadOnlyList<NyxIdExplicitRequestConfirmation> ToConfirmations(
        IEnumerable<NyxIdExplicitRequestConfirmationInput>? inputs) =>
        inputs?.Select(static input =>
                input?.ToConfirmation() ?? throw new NyxIdExplicitRequestConfirmationInputException())
            .ToArray() ?? [];
}

public sealed class NyxIdExplicitRequestConfirmationInputException : InvalidOperationException
{
    public const string ErrorCode = "INVALID_EXPLICIT_REQUEST_CONFIRMATION";

    public NyxIdExplicitRequestConfirmationInputException()
        : base("Explicit request confirmations cannot contain null values.")
    {
    }
}
