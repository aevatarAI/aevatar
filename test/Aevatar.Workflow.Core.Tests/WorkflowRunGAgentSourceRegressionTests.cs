using FluentAssertions;

namespace Aevatar.Workflow.Core.Tests;

public sealed class WorkflowRunGAgentSourceRegressionTests
{
    [Fact]
    public async Task WorkflowRunGAgent_Source_ShouldNotUseTaskRunForBusinessProgression()
    {
        var repoRoot = FindRepositoryRoot();
        var sourcePath = Path.Combine(repoRoot, "src", "workflow", "Aevatar.Workflow.Core", "WorkflowRunGAgent.cs");

        var source = await File.ReadAllTextAsync(sourcePath);

        source.Should().NotContain("Task.Run(");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "aevatar.slnx")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root could not be resolved.");
    }
}
