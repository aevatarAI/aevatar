namespace Aevatar.Workflow.Sagas.States;

public sealed class WorkflowExecutionSagaState : SagaStateBase
{
    public string WorkflowName { get; set; } = string.Empty;

    public string RootActorId { get; set; } = string.Empty;

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public bool? Success { get; set; }

    public string CompletionError { get; set; } = string.Empty;

    public int RequestedSteps { get; set; }

    public int CompletedSteps { get; set; }

    public int FailedSteps { get; set; }

    public Dictionary<string, int> StepTypeCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> RecentStepIds { get; set; } = [];
}
