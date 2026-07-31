using Aevatar.Workflow.Abstractions;

namespace Aevatar.GAgentService.Abstractions;

/// <summary>
/// Transient caller authority for workflow capability admission. Credentials never enter
/// Protobuf commands, artifacts, receipts, actor state, read models, or generated output.
/// </summary>
public sealed class WorkflowCapabilityAdmissionContext
{
    private readonly IReadOnlyList<NyxIdExplicitRequestConfirmation> _explicitRequestConfirmations;

    public WorkflowCapabilityAdmissionContext(
        string callerId,
        NyxIdCallerCredentialSelection? nyxIdCallerCredential = null,
        string? nyxIdOrganizationBearerToken = null,
        ExternalCapabilityExecutionMode executionMode = ExternalCapabilityExecutionMode.Interactive,
        WorkflowCapabilityAdmissionPlan? existingPlan = null,
        IEnumerable<NyxIdExplicitRequestConfirmation>? explicitRequestConfirmations = null)
    {
        CallerId = Normalize(callerId);
        NyxIdCallerCredential = nyxIdCallerCredential;
        NyxIdOrganizationBearerToken = NormalizeOptional(nyxIdOrganizationBearerToken);
        ExecutionMode = executionMode;
        ExistingPlan = existingPlan?.Clone();
        _explicitRequestConfirmations = CloneConfirmations(explicitRequestConfirmations);
    }

    public string CallerId { get; }

    public NyxIdCallerCredentialSelection? NyxIdCallerCredential { get; }

    public string? NyxIdOrganizationBearerToken { get; }

    public ExternalCapabilityExecutionMode ExecutionMode { get; }

    public WorkflowCapabilityAdmissionPlan? ExistingPlan { get; }

    public IReadOnlyList<NyxIdExplicitRequestConfirmation> ExplicitRequestConfirmations =>
        CloneConfirmations(_explicitRequestConfirmations);

    public override string ToString() =>
        $"{nameof(WorkflowCapabilityAdmissionContext)} {{ CallerId = {CallerId}, ExecutionMode = {ExecutionMode}, Credentials = [REDACTED] }}";

    private static string Normalize(string? value) => value?.Trim() ?? string.Empty;

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IReadOnlyList<NyxIdExplicitRequestConfirmation> CloneConfirmations(
        IEnumerable<NyxIdExplicitRequestConfirmation>? confirmations) =>
        confirmations?.Select(static confirmation =>
                confirmation?.Clone() ?? throw new ArgumentException(
                    "Explicit request confirmations cannot contain null values.",
                    nameof(confirmations)))
            .ToArray() ?? [];
}
