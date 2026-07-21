using Aevatar.Workflow.Abstractions;

namespace Aevatar.GAgentService.Abstractions;

/// <summary>
/// Transient caller authority for workflow capability admission. Credentials never enter
/// Protobuf commands, artifacts, receipts, actor state, read models, or generated output.
/// </summary>
public sealed class WorkflowCapabilityAdmissionContext
{
    public WorkflowCapabilityAdmissionContext(
        string callerId,
        string? nyxIdCallerBearerToken = null,
        string? nyxIdOrganizationBearerToken = null,
        ExternalCapabilityExecutionMode executionMode = ExternalCapabilityExecutionMode.Interactive,
        WorkflowCapabilityAdmissionPlan? existingPlan = null)
    {
        CallerId = Normalize(callerId);
        NyxIdCallerBearerToken = NormalizeOptional(nyxIdCallerBearerToken);
        NyxIdOrganizationBearerToken = NormalizeOptional(nyxIdOrganizationBearerToken);
        ExecutionMode = executionMode;
        ExistingPlan = existingPlan?.Clone();
    }

    public string CallerId { get; }

    public string? NyxIdCallerBearerToken { get; }

    public string? NyxIdOrganizationBearerToken { get; }

    public ExternalCapabilityExecutionMode ExecutionMode { get; }

    public WorkflowCapabilityAdmissionPlan? ExistingPlan { get; }

    public override string ToString() =>
        $"{nameof(WorkflowCapabilityAdmissionContext)} {{ CallerId = {CallerId}, ExecutionMode = {ExecutionMode}, Credentials = [REDACTED] }}";

    private static string Normalize(string? value) => value?.Trim() ?? string.Empty;

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
