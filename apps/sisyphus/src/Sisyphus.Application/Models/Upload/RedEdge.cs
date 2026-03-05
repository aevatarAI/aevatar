namespace Sisyphus.Application.Models.Upload;

public sealed class RedEdge
{
    public required string SourceKgId { get; init; }
    public required string TargetKgId { get; init; }
    public required string EdgeType { get; init; }
    public string? EdgeSource { get; init; }
    public string? EdgeReason { get; init; }

    /// <summary>Resolved after red node upload.</summary>
    public string? SourceUuid { get; set; }

    /// <summary>Resolved after red node upload.</summary>
    public string? TargetUuid { get; set; }
}
