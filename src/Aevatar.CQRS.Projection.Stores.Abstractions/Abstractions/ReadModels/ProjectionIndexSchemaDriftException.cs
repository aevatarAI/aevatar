namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public sealed class ProjectionIndexSchemaDriftException : InvalidOperationException
{
    public ProjectionIndexSchemaDriftException(
        string provider,
        string indexAlias,
        string currentPhysicalIndex,
        string expectedPhysicalIndex,
        string? message = null)
        : base(message ?? BuildMessage(provider, indexAlias, currentPhysicalIndex, expectedPhysicalIndex))
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexAlias);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentPhysicalIndex);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPhysicalIndex);

        Provider = provider.Trim();
        IndexAlias = indexAlias.Trim();
        CurrentPhysicalIndex = currentPhysicalIndex.Trim();
        ExpectedPhysicalIndex = expectedPhysicalIndex.Trim();
    }

    public string Provider { get; }

    public string IndexAlias { get; }

    public string CurrentPhysicalIndex { get; }

    public string ExpectedPhysicalIndex { get; }

    public IReadOnlyDictionary<string, string> ToDiagnosticDetails() =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["provider"] = Provider,
            ["indexAlias"] = IndexAlias,
            ["currentPhysicalIndex"] = CurrentPhysicalIndex,
            ["expectedPhysicalIndex"] = ExpectedPhysicalIndex,
        };

    private static string BuildMessage(
        string provider,
        string indexAlias,
        string currentPhysicalIndex,
        string expectedPhysicalIndex) =>
        $"{provider} projection index schema drift detected for alias '{indexAlias}'. " +
        $"Current physical '{currentPhysicalIndex}' does not match expected physical '{expectedPhysicalIndex}'. " +
        "Rebuild or repoint the projection index lifecycle explicitly before accepting projection writes.";
}
