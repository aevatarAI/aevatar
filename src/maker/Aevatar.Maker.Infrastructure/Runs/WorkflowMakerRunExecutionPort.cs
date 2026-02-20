using Aevatar.Maker.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Maker.Infrastructure.Runs;

/// <summary>
/// Maker execution adapter backed by the runnable workflow actor capability.
/// </summary>
public sealed class WorkflowMakerRunExecutionPort : IMakerRunExecutionPort
{
    private readonly IRunnableWorkflowActorCapability _workflowActorCapability;

    public WorkflowMakerRunExecutionPort(IRunnableWorkflowActorCapability workflowActorCapability)
    {
        _workflowActorCapability = workflowActorCapability;
    }

    public async Task<MakerRunExecutionResult> ExecuteAsync(MakerRunRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var executionResult = await _workflowActorCapability.RunAsync(
            new RunnableWorkflowActorRequest(
                Input: request.Input,
                WorkflowName: request.WorkflowName,
                WorkflowYaml: request.WorkflowYaml,
                ActorId: request.ActorId,
                Timeout: request.Timeout,
                DestroyActorAfterRun: request.DestroyActorAfterRun),
            ct: ct);

        return new MakerRunExecutionResult(
            new MakerRunStarted(
                executionResult.ActorId,
                executionResult.WorkflowName,
                executionResult.CommandId,
                executionResult.StartedAt),
            executionResult.Output,
            executionResult.Success,
            executionResult.TimedOut,
            executionResult.Error);
    }
}
