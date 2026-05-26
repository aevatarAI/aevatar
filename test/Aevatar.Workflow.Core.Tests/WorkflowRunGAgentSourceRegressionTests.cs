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

    [Fact]
    public async Task WorkflowStepTargetAgentResolver_Source_ShouldNotContainRawLifecycleImplementation()
    {
        var repoRoot = FindRepositoryRoot();
        var sourcePath = Path.Combine(
            repoRoot,
            "src",
            "workflow",
            "Aevatar.Workflow.Core",
            "Primitives",
            "WorkflowStepTargetAgentResolver.cs");

        var executableSource = StripLineComments(await File.ReadAllTextAsync(sourcePath));

        executableSource.Should().NotContain("agent_type");
        executableSource.Should().NotContain("agent_id");
        executableSource.Should().NotContain("Type.GetType");
        executableSource.Should().NotContain("AppDomain.CurrentDomain");
        executableSource.Should().NotContain("IWorkflowAgentTypeAliasProvider");
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

    private static string StripLineComments(string source) =>
        string.Join(
            Environment.NewLine,
            source.Split([Environment.NewLine], StringSplitOptions.None)
                .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
}
