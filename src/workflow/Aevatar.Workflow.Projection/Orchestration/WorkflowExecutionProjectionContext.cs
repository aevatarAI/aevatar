using Aevatar.AI.Projection.Abstractions;
namespace Aevatar.Workflow.Projection;

/// <summary>
/// Actor-scoped projection context for CQRS read model updates.
/// </summary>
public sealed class WorkflowExecutionProjectionContext
    : IProjectionContext, IAIProjectionContext, IProjectionStreamSubscriptionContext
{
    public required string ProjectionId { get; init; }
    public required string CommandId { get; set; }
    public required string RootActorId { get; init; }
    public required string WorkflowName { get; set; }
    public required DateTimeOffset StartedAt { get; set; }
    public required string Input { get; set; }

    string IProjectionContext.ProjectionId => ProjectionId;

    public IActorStreamSubscriptionLease? StreamSubscriptionLease { get; set; }

    public void UpdateRunMetadata(
        string commandId,
        string workflowName,
        string input,
        DateTimeOffset startedAt)
    {
        if (!string.IsNullOrWhiteSpace(commandId))
            CommandId = commandId;
        if (!string.IsNullOrWhiteSpace(workflowName))
            WorkflowName = workflowName;

        Input = input;
        StartedAt = startedAt;
    }
}
