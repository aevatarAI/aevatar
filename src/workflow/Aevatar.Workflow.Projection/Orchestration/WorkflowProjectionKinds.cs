namespace Aevatar.Workflow.Projection.Orchestration;

internal static class WorkflowProjectionKinds
{
    public const string ExecutionSession = "workflow-execution-session";
    public const string ExecutionMaterialization = "workflow-execution-materialization";
    public const string ScheduleMaterialization = "scheduled-dispatch-materialization";
    public const string Binding = "workflow-binding";
}
