// Refactor (iter158/cluster-157-004-timeout-cts):
// Source-regression test for PR #1168: keep inline CTS+CancelAfter deleted from the touched caller files.
using System.Text.RegularExpressions;
using FluentAssertions;

namespace Aevatar.Workflow.Core.Tests;

public sealed class InlineCtsAbsenceTests
{
    [Theory]
    [InlineData("src/workflow/Aevatar.Workflow.Core/Modules/ConnectorCallModule.cs")]
    [InlineData("agents/Aevatar.GAgents.StatusDashboard/HealthProbeTargetGAgent.cs")]
    [InlineData("agents/Aevatar.GAgents.NyxidChat/AgentRunGAgent.cs")]
    public void Inline_cts_cancel_after_should_remain_deleted(string relativePath)
    {
        var fullPath = Path.Combine(new[] { FindRepositoryRoot() }.Concat(relativePath.Split('/')).ToArray());
        File.Exists(fullPath).Should().BeTrue($"expected file {relativePath} to exist");

        var hits = File.ReadAllLines(fullPath)
            .Select((content, index) => (file: fullPath, line: index + 1, content))
            .Where(x => Regex.IsMatch(x.content, @"CancellationTokenSource.*CancelAfter") &&
                        !x.content.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .ToList();

        hits.Should().BeEmpty(
            "inline CTS+CancelAfter forbidden per PR #1168 / issue #1160 (use actor-owned typed timeout event)");
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
