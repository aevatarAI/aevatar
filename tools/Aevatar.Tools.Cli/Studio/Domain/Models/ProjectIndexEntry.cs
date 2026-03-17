namespace Aevatar.Tools.Cli.Studio.Domain.Models;

public sealed record ProjectIndexEntry
{
    public string BundleId { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string EntryWorkflowName { get; init; } = string.Empty;

    public IReadOnlyList<string> Tags { get; init; } = [];

    public int WorkflowCount { get; init; }

    public DateTimeOffset UpdatedAtUtc { get; init; }

    public int LatestVersion { get; init; }
}
