using System.Text.RegularExpressions;
using FluentAssertions;

namespace Aevatar.Workflow.Core.Tests;

public sealed class InlineCtsAbsenceTests
{
    [Theory]
    [InlineData("ConnectorCallModule")]
    [InlineData("HealthProbeTargetGAgent")]
    [InlineData("AgentRunGAgent")]
    public void Inline_cts_cancel_after_should_remain_deleted(string moduleName)
    {
        var srcRoot = Path.Combine(FindRepositoryRoot(), "src");
        var hits = Directory.EnumerateFiles(srcRoot, $"{moduleName}.cs", SearchOption.AllDirectories)
            .SelectMany(file => File.ReadAllLines(file)
                .Select((content, index) => (file, line: index + 1, content))
                .Where(x => Regex.IsMatch(x.content, @"CancellationTokenSource.*CancelAfter") &&
                            !x.content.TrimStart().StartsWith("//", StringComparison.Ordinal)))
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
