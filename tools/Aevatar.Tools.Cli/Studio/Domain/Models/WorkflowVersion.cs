namespace Aevatar.Tools.Cli.Studio.Domain.Models;

public sealed record WorkflowVersion
{
    public int VersionNumber { get; init; }

    public DateTimeOffset SavedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public string Checksum { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;
}
