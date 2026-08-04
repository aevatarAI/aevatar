using Aevatar.Workflow.Abstractions;

namespace Aevatar.AI.ToolProviders.Skills;

/// <summary>
/// Mounts workflow YAML bundles extracted from a skill into the caller scope.
/// </summary>
public interface ISkillWorkflowMountPort
{
    Task<SkillWorkflowMountResult> MountAsync(
        SkillWorkflowMountRequest request,
        CancellationToken ct = default);
}

public sealed record SkillWorkflowMountRequest(
    string ScopeId,
    string SourceReadableNyxIdAccessToken,
    IReadOnlyList<SkillWorkflowDescriptor> Workflows,
    IReadOnlyList<SkillWorkflowMountConfirmation>? Confirmations = null)
{
    public string CallerId { get; init; } = string.Empty;

    public string ConfirmationToken { get; init; } = string.Empty;

    public override string ToString() =>
        $"{nameof(SkillWorkflowMountRequest)} {{ ScopeId = {ScopeId}, CallerId = {CallerId}, Credentials = [REDACTED], Workflows = [REDACTED], WorkflowCount = {Workflows.Count} }}";
}

public sealed record SkillWorkflowMountResult(
    string Status,
    bool Mounted,
    IReadOnlyList<MountedSkillWorkflow> Workflows,
    string? Message = null,
    IReadOnlyList<SkillWorkflowMountPreview>? ConfirmationRequests = null,
    string? FailureCode = null,
    string? ConfirmationToken = null);

public sealed record SkillWorkflowMountConfirmation(
    string WorkflowId,
    string RevisionId,
    string WorkflowBundleDigest,
    IReadOnlyList<SkillWorkflowExplicitRequestConfirmation> ExplicitRequests);

public sealed record SkillWorkflowExplicitRequestConfirmation(
    string CallSiteId,
    string RequestContractDigest,
    NyxIdOperationRisk AttestedRisk);

public sealed record SkillWorkflowMountPreview(
    string WorkflowId,
    string RevisionId,
    string WorkflowBundleDigest,
    IReadOnlyList<SkillWorkflowExplicitRequestPreview> ExplicitRequests,
    SkillWorkflowMountConfirmation Confirmation);

public sealed record SkillWorkflowExplicitRequestPreview(
    string CallSiteId,
    string RequestContractDigest,
    string UserServiceId,
    NyxIdRequestMethod Method,
    string PathTemplate,
    NyxIdRequestBodyMode BodyMode,
    bool BodyRequired,
    NyxIdRequestResponseMode ResponseMode,
    NyxIdOperationRisk EffectiveRisk,
    bool RuntimeApprovalRequired,
    IReadOnlyList<ExternalCapabilityExecutionMode> AllowedExecutionModes);

public sealed record MountedSkillWorkflow(
    string WorkflowId,
    string ServiceId,
    string EndpointId,
    string? RevisionId = null);

public sealed class NoOpSkillWorkflowMountPort : ISkillWorkflowMountPort
{
    public Task<SkillWorkflowMountResult> MountAsync(
        SkillWorkflowMountRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Task.FromResult(new SkillWorkflowMountResult(
            Status: "not_available",
            Mounted: false,
            Workflows: [],
            Message: "Workflow mounting is not available in this host."));
    }
}
