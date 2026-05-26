using FluentAssertions;

namespace Aevatar.CQRS.Projection.Core.Tests;

public sealed class ElasticsearchProjectionElapsedLoggingSourceRegressionTests
{
    [Fact]
    public void ElasticsearchElapsedLoggingSources_ShouldUseMonotonicStopwatchClock()
    {
        var repositoryRoot = FindRepositoryRoot();
        var elapsedLoggingSources = new[]
        {
            "src/Aevatar.CQRS.Projection.Providers.Elasticsearch/Stores/ElasticsearchProjectionDocumentStore.cs",
            "src/Aevatar.CQRS.Projection.Providers.Elasticsearch/Stores/ElasticsearchOptimisticWriter.cs",
        };

        foreach (var relativePath in elapsedLoggingSources)
        {
            var source = File.ReadAllText(Path.Combine(repositoryRoot, relativePath));

            source.Should().NotContain(
                "DateTimeOffset.UtcNow -",
                $"{relativePath} elapsedMs logging must not subtract wall-clock timestamps");
            source.Should().NotContain(
                "DateTimeOffset.UtcNow.Subtract",
                $"{relativePath} elapsedMs logging must not subtract wall-clock timestamps");
            source.Should().Contain(
                "Stopwatch.GetTimestamp()",
                $"{relativePath} elapsedMs logging must start from a monotonic timestamp");
            source.Should().Contain(
                "Stopwatch.GetElapsedTime(",
                $"{relativePath} elapsedMs logging must calculate duration from the monotonic timestamp");
        }
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

        throw new DirectoryNotFoundException("Could not locate repository root from test base directory.");
    }
}
