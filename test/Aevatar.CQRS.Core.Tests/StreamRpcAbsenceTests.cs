using FluentAssertions;

namespace Aevatar.CQRS.Core.Tests;

public sealed class StreamRpcAbsenceTests
{
    [Theory]
    [InlineData("StreamActorOutcomeChannel")]
    [InlineData("DefaultCommandOutcomeDispatchService")]
    [InlineData("IActorOutcomeChannel")]
    [InlineData("ICommandOutcomeDispatchService")]
    [InlineData("DispatchAndAwaitOutcomeAsync")]
    public void StreamRpcSurface_ShouldRemainDeleted(string forbidden)
    {
        var sourceRoot = Path.Combine(FindRepositoryRoot(), "src");

        foreach (var sourcePath in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            var source = StripLineComments(File.ReadAllText(sourcePath));

            source.Should().NotContain(
                forbidden,
                $"stream-RPC abstraction {forbidden} should not be reintroduced (per PR #1165 / issue #1161) in {sourcePath}");
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var gitPath = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test base directory.");
    }

    private static string StripLineComments(string source) =>
        string.Join(
            Environment.NewLine,
            source.Split([Environment.NewLine], StringSplitOptions.None)
                .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
}
