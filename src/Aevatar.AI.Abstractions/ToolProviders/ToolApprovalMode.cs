namespace Aevatar.AI.Abstractions.ToolProviders;

/// <summary>
/// Controls whether a tool requires human approval before execution.
/// Inspired by MAF's tool approval mechanism.
/// </summary>
public enum ToolApprovalMode
{
    /// <summary>Execute immediately without approval (default, backward compatible).</summary>
    NeverRequire = 0,

    /// <summary>Always pause for human approval before execution.</summary>
    AlwaysRequire = 1,

    /// <summary>Let middleware decide whether approval is needed.</summary>
    Auto = 2,
}

public static class ToolApprovalPolicies
{
    public static ToolApprovalMode CreateScopedResource => ToolApprovalMode.NeverRequire;

    public static ToolApprovalMode UpdateExistingResource => ToolApprovalMode.AlwaysRequire;

    public static ToolApprovalMode DestructiveMutation => ToolApprovalMode.AlwaysRequire;

    public static ToolApprovalMode ExternalInvocation => ToolApprovalMode.AlwaysRequire;
}
