using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;

namespace Aevatar.Studio.Tests;

internal sealed class StudioWorkflowCapabilityAdmissionTestService :
    IWorkflowExternalCapabilityAdmissionService
{
    private readonly Exception? _failure;
    private readonly IWorkflowExternalCapabilityAdmissionService? _inner;

    public StudioWorkflowCapabilityAdmissionTestService(Exception? failure = null)
    {
        _failure = failure;
    }

    public StudioWorkflowCapabilityAdmissionTestService(
        IWorkflowExternalCapabilityAdmissionService inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public List<WorkflowExternalCapabilityAdmissionRequest> Requests { get; } = [];

    public List<PersistedWorkflowCapabilityAdmissionRequest> PersistedRequests { get; } = [];

    public Action<WorkflowExternalCapabilityAdmissionRequest>? OnAdmit { get; init; }

    public Action<PersistedWorkflowCapabilityAdmissionRequest>? OnRevalidate { get; init; }

    public WorkflowCapabilityAdmissionPlan? AdmissionPlan { get; init; }

    public Task<WorkflowCapabilityAdmissionPlan> AdmitAsync(
        WorkflowExternalCapabilityAdmissionRequest request,
        CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        OnAdmit?.Invoke(request);
        if (_failure is not null)
            return Task.FromException<WorkflowCapabilityAdmissionPlan>(_failure);

        if (_inner is not null)
            return _inner.AdmitAsync(request, cancellationToken);

        if (AdmissionPlan is not null)
            return Task.FromResult(AdmissionPlan.Clone());

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

        if (_inner is not null)
            return _inner.RevalidatePersistedAsync(request, cancellationToken);

        return Task.FromResult(request.Plan.Clone());
    }
}
