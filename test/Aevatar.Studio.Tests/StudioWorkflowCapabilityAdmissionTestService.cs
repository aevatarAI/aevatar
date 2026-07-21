using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;

namespace Aevatar.Studio.Tests;

internal sealed class StudioWorkflowCapabilityAdmissionTestService :
    IWorkflowExternalCapabilityAdmissionService
{
    private readonly Exception? _failure;

    public StudioWorkflowCapabilityAdmissionTestService(Exception? failure = null)
    {
        _failure = failure;
    }

    public List<WorkflowExternalCapabilityAdmissionRequest> Requests { get; } = [];

    public Action<WorkflowExternalCapabilityAdmissionRequest>? OnAdmit { get; init; }

    public Task<WorkflowCapabilityAdmissionPlan> AdmitAsync(
        WorkflowExternalCapabilityAdmissionRequest request,
        CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        OnAdmit?.Invoke(request);
        if (_failure is not null)
            return Task.FromException<WorkflowCapabilityAdmissionPlan>(_failure);

        return Task.FromResult(request.ExistingPlan?.Clone()
            ?? WorkflowCapabilityAdmissionPlanIntegrity.Create(
                request.WorkflowYaml,
                request.InlineWorkflowYamls,
                request.ExecutionMode,
                [],
                []));
    }
}
