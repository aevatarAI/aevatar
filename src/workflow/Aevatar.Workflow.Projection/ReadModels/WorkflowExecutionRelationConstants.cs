namespace Aevatar.Workflow.Projection.ReadModels;

public static class WorkflowExecutionRelationConstants
{
    public const string Scope = "workflow-execution-relations";

    public const string ActorNodeType = "Actor";

    public const string RunNodeType = "WorkflowRun";

    public const string StepNodeType = "WorkflowStep";

    public const string RelationOwns = "OWNS";

    public const string RelationContainsStep = "CONTAINS_STEP";

    public const string RelationChildOf = "CHILD_OF";
}
