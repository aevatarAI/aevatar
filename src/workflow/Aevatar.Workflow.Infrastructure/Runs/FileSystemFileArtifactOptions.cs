namespace Aevatar.Workflow.Infrastructure.Runs;

public sealed class FileSystemFileArtifactOptions
{
    public string RootDirectory { get; set; } =
        Path.Combine(AppContext.BaseDirectory, "workflow-file-artifacts");

    public TimeSpan TimeToLive { get; set; } = TimeSpan.FromDays(1);

    public TimeSpan IncompleteArtifactAge { get; set; } = TimeSpan.FromHours(1);
}
