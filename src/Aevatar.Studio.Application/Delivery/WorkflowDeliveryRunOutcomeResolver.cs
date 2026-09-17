using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Queries;

namespace Aevatar.Studio.Application.Delivery;

internal enum WorkflowDeliveryRunOutcomeStatus
{
    Pending = 1,
    Completed = 2,
    Failed = 3,
    Stopped = 4,
    TimedOut = 5,
    OutcomeUncertain = 6,
}

internal sealed record WorkflowDeliveryRunOutcome(
    ServiceRunSnapshot RegistryRun,
    WorkflowDeliveryRunOutcomeStatus Status,
    long CommittedStateVersion,
    string Output,
    string Error);

internal static class WorkflowDeliveryRunOutcomeResolver
{
    public static async Task<WorkflowDeliveryRunOutcome> ResolveAsync(
        ServiceRunSnapshot run,
        IWorkflowExecutionCurrentStateQueryPort workflowCurrentStates,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(workflowCurrentStates);

        if (run.ImplementationKind != ServiceImplementationKind.Workflow)
            return FromServiceRun(run);

        if (string.IsNullOrWhiteSpace(run.TargetActorId))
        {
            return OutcomeUncertain(
                run,
                "The workflow Service Run does not identify its authoritative workflow Run actor.");
        }
        if (!workflowCurrentStates.WorkflowActorCurrentStateQueryEnabled)
        {
            return OutcomeUncertain(
                run,
                "The workflow Run current-state query is disabled.");
        }

        var snapshot = await workflowCurrentStates.GetWorkflowActorCurrentStateAsync(
            run.TargetActorId.Trim(),
            ct);
        if (snapshot == null)
            return Pending(run);
        if (!Same(snapshot.ActorId, run.TargetActorId) ||
            !Same(snapshot.RunId, run.RunId) ||
            !Same(snapshot.ScopeId, run.ScopeId))
        {
            return OutcomeUncertain(
                run,
                "The workflow Run current-state identity does not match the Service Run registry.");
        }
        if (snapshot.StateVersion <= 0)
            return Pending(run);

        var output = snapshot.LastOutput ?? string.Empty;
        var error = snapshot.LastError ?? string.Empty;
        return snapshot.CompletionStatus switch
        {
            WorkflowRunCompletionStatus.Completed when snapshot.LastSuccess == true =>
                new WorkflowDeliveryRunOutcome(
                    run,
                    WorkflowDeliveryRunOutcomeStatus.Completed,
                    snapshot.StateVersion,
                    output,
                    string.Empty),
            WorkflowRunCompletionStatus.Completed => OutcomeUncertain(
                run,
                "The workflow Run completed without successful terminal evidence.",
                snapshot.StateVersion,
                output),
            WorkflowRunCompletionStatus.Failed => Terminal(
                run,
                WorkflowDeliveryRunOutcomeStatus.Failed,
                snapshot.StateVersion,
                output,
                error),
            WorkflowRunCompletionStatus.Stopped => Terminal(
                run,
                WorkflowDeliveryRunOutcomeStatus.Stopped,
                snapshot.StateVersion,
                output,
                error),
            WorkflowRunCompletionStatus.TimedOut => Terminal(
                run,
                WorkflowDeliveryRunOutcomeStatus.TimedOut,
                snapshot.StateVersion,
                output,
                error),
            WorkflowRunCompletionStatus.Running or
                WorkflowRunCompletionStatus.AwaitingToolApproval or
                WorkflowRunCompletionStatus.WaitingForSignal => Pending(run),
            WorkflowRunCompletionStatus.NotFound => OutcomeUncertain(
                run,
                "The workflow Run current-state reports that the Run was not found.",
                snapshot.StateVersion,
                output),
            WorkflowRunCompletionStatus.Disabled => OutcomeUncertain(
                run,
                "The workflow Run current-state query is disabled.",
                snapshot.StateVersion,
                output),
            _ => OutcomeUncertain(
                run,
                string.IsNullOrWhiteSpace(error)
                    ? "The workflow Run terminal outcome is unknown."
                    : error,
                snapshot.StateVersion,
                output),
        };
    }

    private static WorkflowDeliveryRunOutcome FromServiceRun(ServiceRunSnapshot run)
    {
        if (run.StateVersion <= 0)
            return Pending(run);

        return run.Status switch
        {
            ServiceRunStatus.Completed => new WorkflowDeliveryRunOutcome(
                run,
                WorkflowDeliveryRunOutcomeStatus.Completed,
                run.StateVersion,
                run.LastOutput ?? string.Empty,
                string.Empty),
            ServiceRunStatus.Failed => Terminal(
                run,
                WorkflowDeliveryRunOutcomeStatus.Failed,
                run.StateVersion,
                run.LastOutput,
                run.LastError),
            ServiceRunStatus.Stopped => Terminal(
                run,
                WorkflowDeliveryRunOutcomeStatus.Stopped,
                run.StateVersion,
                run.LastOutput,
                run.LastError),
            ServiceRunStatus.OutcomeUncertain => OutcomeUncertain(
                run,
                run.LastError,
                run.StateVersion,
                run.LastOutput),
            _ => Pending(run),
        };
    }

    private static WorkflowDeliveryRunOutcome Pending(ServiceRunSnapshot run) =>
        new(run, WorkflowDeliveryRunOutcomeStatus.Pending, 0, string.Empty, string.Empty);

    private static WorkflowDeliveryRunOutcome Terminal(
        ServiceRunSnapshot run,
        WorkflowDeliveryRunOutcomeStatus status,
        long stateVersion,
        string? output,
        string? error) =>
        new(
            run,
            status,
            stateVersion,
            output ?? string.Empty,
            error ?? string.Empty);

    private static WorkflowDeliveryRunOutcome OutcomeUncertain(
        ServiceRunSnapshot run,
        string? error,
        long stateVersion = 0,
        string? output = null) =>
        Terminal(
            run,
            WorkflowDeliveryRunOutcomeStatus.OutcomeUncertain,
            stateVersion,
            output,
            string.IsNullOrWhiteSpace(error)
                ? "The Run outcome is uncertain."
                : error);

    private static bool Same(string? left, string? right) =>
        string.Equals(left?.Trim(), right?.Trim(), StringComparison.Ordinal);
}
