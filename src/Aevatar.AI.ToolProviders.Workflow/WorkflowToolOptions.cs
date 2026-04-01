namespace Aevatar.AI.ToolProviders.Workflow;

/// <summary>Configuration for workflow inspection tools.</summary>
public sealed class WorkflowToolOptions
{
    /// <summary>Maximum number of timeline items to return per query (default: 50).</summary>
    public int MaxTimelineItems { get; set; } = 50;

    /// <summary>Maximum number of actor snapshots to list (default: 100).</summary>
    public int MaxActorSnapshots { get; set; } = 100;

    /// <summary>Maximum graph traversal depth for actor_inspect subgraph queries (default: 2).</summary>
    public int MaxGraphDepth { get; set; } = 2;
}
