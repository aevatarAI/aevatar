namespace Aevatar.Workflow.Core.Primitives;

internal sealed record WorkflowCallLimitAdmission(bool Accepted, string Error)
{
    public static WorkflowCallLimitAdmission Accept() => new(true, string.Empty);

    public static WorkflowCallLimitAdmission Reject(string error) => new(false, error);
}

internal static class WorkflowCallLimitPolicy
{
    public const int DefaultMaxDepth = 8;
    public const int DefaultMaxActiveSubWorkflows = 64;

    public static int NormalizeMaxDepth(int configured) =>
        configured <= 0 ? DefaultMaxDepth : configured;

    public static int NormalizeMaxActiveSubWorkflows(int configured) =>
        configured <= 0 ? DefaultMaxActiveSubWorkflows : configured;

    public static int ResolveChildDepth(int requestedDepth, int parentDepth) =>
        requestedDepth > 0 ? requestedDepth : Math.Max(0, parentDepth) + 1;

    public static WorkflowCallLimitAdmission Admit(
        int requestedDepth,
        int parentDepth,
        int maxDepth,
        int activeSubWorkflowCount,
        int maxActiveSubWorkflows)
    {
        var childDepth = ResolveChildDepth(requestedDepth, parentDepth);
        var depthLimit = NormalizeMaxDepth(maxDepth);
        if (childDepth > depthLimit)
            return WorkflowCallLimitAdmission.Reject($"workflow_call depth limit exceeded: depth {childDepth} > {depthLimit}");

        var activeLimit = NormalizeMaxActiveSubWorkflows(maxActiveSubWorkflows);
        if (activeSubWorkflowCount >= activeLimit)
            return WorkflowCallLimitAdmission.Reject($"workflow_call fanout limit exceeded: active {activeSubWorkflowCount} >= {activeLimit}");

        return WorkflowCallLimitAdmission.Accept();
    }
}
