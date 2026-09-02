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
    string NyxIdAccessToken,
    IReadOnlyList<SkillWorkflowDescriptor> Workflows)
{
    public string CallerId { get; init; } = string.Empty;

    public override string ToString() =>
        $"{nameof(SkillWorkflowMountRequest)} {{ ScopeId = {ScopeId}, CallerId = {CallerId}, Credentials = [REDACTED], Workflows = [REDACTED], WorkflowCount = {Workflows.Count} }}";
}

public sealed record SkillWorkflowMountResult(
    string Status,
    bool Mounted,
    IReadOnlyList<MountedSkillWorkflow> Workflows,
    string? Message = null);

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
