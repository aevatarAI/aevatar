namespace Aevatar.Workflow.Abstractions;

public static class WorkflowSuspensionTypes
{
    public const string HumanApprovalWireName = "human_approval";
    public const string HumanInputWireName = "human_input";
    public const string SecureInputWireName = "secure_input";

    public static string ToWireName(this WorkflowSuspensionType kind) => kind switch
    {
        WorkflowSuspensionType.HumanApproval => HumanApprovalWireName,
        WorkflowSuspensionType.HumanInput => HumanInputWireName,
        WorkflowSuspensionType.SecureInput => SecureInputWireName,
        _ => string.Empty,
    };

    public static IReadOnlyList<string> DefaultExpectedOptions(this WorkflowSuspensionType kind) => kind switch
    {
        WorkflowSuspensionType.HumanApproval => ["approve", "reject"],
        WorkflowSuspensionType.HumanInput => ["submit"],
        WorkflowSuspensionType.SecureInput => ["submit"],
        _ => [],
    };
}
