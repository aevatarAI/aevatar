namespace Aevatar.Workflow.Projection.Metadata;

/// <summary>
/// Explicit index mapping shapes shared by the workflow read-model metadata providers.
/// Policy: queried / sorted fields get an explicit keyword or date mapping; opaque payload text
/// is stored but not indexed; never-queried subtrees and proto maps are disabled objects.
/// Any change to these shapes changes the provider's schema fingerprint and rolls out through
/// the fingerprint/reindex/alias lifecycle.
/// </summary>
internal static class WorkflowDocumentMappingHelpers
{
    /// <summary>Exact-match field (ids, enums, statuses, short names).</summary>
    internal static Dictionary<string, object?> Keyword() => new(StringComparer.Ordinal)
    {
        ["type"] = "keyword",
    };

    /// <summary>
    /// Exact / wildcard searchable display text (names, bounded summaries). Values longer than
    /// the bound are still stored but are not indexed, so a pathological value never fails the
    /// document write.
    /// </summary>
    internal static Dictionary<string, object?> SearchableKeyword() => new(StringComparer.Ordinal)
    {
        ["type"] = "keyword",
        ["ignore_above"] = 1024,
    };

    internal static Dictionary<string, object?> Date() => new(StringComparer.Ordinal)
    {
        ["type"] = "date",
    };

    internal static Dictionary<string, object?> Integer() => new(StringComparer.Ordinal)
    {
        ["type"] = "integer",
    };

    /// <summary>Opaque payload text: stored in _source, never indexed or searchable.</summary>
    internal static Dictionary<string, object?> NotIndexedText() => new(StringComparer.Ordinal)
    {
        ["type"] = "text",
        ["index"] = false,
    };

    /// <summary>Never-queried subtree or proto map: stored in _source, not mapped or indexed.</summary>
    internal static Dictionary<string, object?> DisabledObject() => new(StringComparer.Ordinal)
    {
        ["type"] = "object",
        ["enabled"] = false,
    };

    /// <summary>
    /// Nested message with explicit child mappings; children left out stay dynamic so the
    /// descriptor augmenter can still add keyword/date/disabled-map mappings beneath it.
    /// </summary>
    internal static Dictionary<string, object?> ObjectWithProperties(
        IReadOnlyDictionary<string, object?> properties) => new(StringComparer.Ordinal)
    {
        ["type"] = "object",
        ["dynamic"] = true,
        ["properties"] = properties,
    };
}
