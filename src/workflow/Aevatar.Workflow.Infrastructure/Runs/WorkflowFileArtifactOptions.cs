namespace Aevatar.Workflow.Infrastructure.Runs;

public sealed class WorkflowFileArtifactOptions
{
    public const string SectionName = "WorkflowFileArtifacts";

    public string? Backend { get; set; }

    public WorkflowFileArtifactPolicyOptions Policies { get; set; } = new();

    public bool CleanupEnabled { get; set; } = true;

    public bool CleanupOnStart { get; set; } = true;

    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(1);
}

public sealed class WorkflowFileArtifactPolicyOptions
{
    public string? Environment { get; set; }
}
