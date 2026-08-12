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

    [Fact]
    public async Task WorkflowInteractiveActionIdentity_Source_ShouldBranchOnTypedParamsCase()
    {
        var repoRoot = FindRepositoryRoot();
        var sourcePath = Path.Combine(repoRoot, "src", "workflow", "Aevatar.Workflow.Core", "WorkflowRunGAgent.cs");

        var source = await File.ReadAllTextAsync(sourcePath);

        source.Should().Contain("wireParams.ActionParamsCase");
        source.Should().NotContain("wireAction == \"service.connect\"");
    }

    [Fact]
    public async Task WorkflowInteractiveActionProducer_Source_ShouldNotAdvertiseKeyRotate()
    {
        var repoRoot = FindRepositoryRoot();
        var sourcePath = Path.Combine(repoRoot, "src", "workflow", "Aevatar.Workflow.Core", "WorkflowRunGAgent.cs");

        var source = await File.ReadAllTextAsync(sourcePath);
        var handoffStart = source.IndexOf(
            "private bool TryBuildInteractiveActionHandoff(",
            StringComparison.Ordinal);
        var requestPartsStart = source.IndexOf(
            "private static bool TryBuildInteractiveActionRequestParts(",
            StringComparison.Ordinal);
        var producerEnd = source.IndexOf(
            "private async Task EnsureInteractiveActionActorHandoffAsync(",
            StringComparison.Ordinal);

        handoffStart.Should().BeGreaterThanOrEqualTo(0);
        requestPartsStart.Should().BeGreaterThan(handoffStart);
        producerEnd.Should().BeGreaterThan(requestPartsStart);

        var producerSource = source[handoffStart..producerEnd];
        producerSource.Should().NotContain("ActionParamsOneofCase.KeyRotate");
        producerSource.Should().NotContain("\"key.rotate\"");
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
