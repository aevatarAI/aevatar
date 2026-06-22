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

    // Step -> next-step execution flow (from the step trace's NextStepId), so the topology graph carries the
    // real run order instead of only run -> step containment. The branch taken is on the edge's branchKey.
    public const string EdgeTypeNext = "NEXT";
}
