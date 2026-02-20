using Aevatar.Maker.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Maker.Infrastructure.Runs;

/// <summary>
/// Maker execution adapter backed by the workflow execution capability facade.
/// </summary>
public sealed class WorkflowMakerRunExecutionPort : IMakerRunExecutionPort
{
    private readonly IWorkflowExecutionCapability _workflowExecutionCapability;

    public WorkflowMakerRunExecutionPort(IWorkflowExecutionCapability workflowExecutionCapability)
    {
        _workflowExecutionCapability = workflowExecutionCapability;
    }

    public async Task<MakerRunExecutionResult> ExecuteAsync(MakerRunRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var executionResult = await _workflowExecutionCapability.ExecuteAsync(
            new WorkflowExecutionRequest(
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
