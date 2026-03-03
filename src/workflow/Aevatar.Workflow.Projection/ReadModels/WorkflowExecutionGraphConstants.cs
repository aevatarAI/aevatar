namespace Aevatar.Workflow.Projection.ReadModels;

public static class WorkflowExecutionGraphConstants
{
    public const string Scope = "workflow-execution-graph";

    public const string ActorNodeType = "Actor";

    public const string RunNodeType = "WorkflowRun";

    public const string StepNodeType = "WorkflowStep";

    public const string EdgeTypeOwns = "OWNS";

    public const string EdgeTypeContainsStep = "CONTAINS_STEP";

    public const string EdgeTypeChildOf = "CHILD_OF";
}
