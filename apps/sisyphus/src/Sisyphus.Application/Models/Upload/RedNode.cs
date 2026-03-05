namespace Sisyphus.Application.Models.Upload;

public sealed class RedNode
{
    public required string KgId { get; init; }
    public required string Label { get; init; }
    public required string AtomType { get; init; }
    public required string TexContent { get; init; }
    public string? SourcePath { get; init; }
    public string? SourceTexLabel { get; init; }
    public string? CanonicalLabel { get; init; }
    public string? UnitEnv { get; init; }
    public string? UnitFingerprint { get; init; }
    public string? MergedSha256 { get; init; }
    public string? ExtractorVersion { get; init; }
    public bool ProofOrphan { get; init; }
    public List<RawParentEdge> ParentEdges { get; init; } = [];

    /// <summary>Assigned by chrono-graph after upload.</summary>
    public string? GraphUuid { get; set; }
}

public sealed class RawParentEdge
{
    public required string Parent { get; init; }
    public required string EdgeType { get; init; }
    public string? EdgeSource { get; init; }
    public string? EdgeReason { get; init; }
}
