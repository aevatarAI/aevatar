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

    public List<PersistedWorkflowCapabilityAdmissionRequest> PersistedRequests { get; } = [];

    public Action<WorkflowExternalCapabilityAdmissionRequest>? OnAdmit { get; init; }

    public Action<PersistedWorkflowCapabilityAdmissionRequest>? OnRevalidate { get; init; }

    public Task<WorkflowCapabilityAdmissionPlan> AdmitAsync(
        WorkflowExternalCapabilityAdmissionRequest request,
        CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        OnAdmit?.Invoke(request);
        if (_failure is not null)
            return Task.FromException<WorkflowCapabilityAdmissionPlan>(_failure);

        return Task.FromResult(WorkflowCapabilityAdmissionPlanIntegrity.Create(
            request.WorkflowYaml,
            request.InlineWorkflowYamls,
            request.ExecutionMode,
            [],
            []));
    }

    public Task<WorkflowCapabilityAdmissionPlan> RevalidatePersistedAsync(
        PersistedWorkflowCapabilityAdmissionRequest request,
        CancellationToken cancellationToken = default)
    {
        PersistedRequests.Add(request);
        OnRevalidate?.Invoke(request);
        if (_failure is not null)
            return Task.FromException<WorkflowCapabilityAdmissionPlan>(_failure);

        return Task.FromResult(request.Plan.Clone());
    }
}
