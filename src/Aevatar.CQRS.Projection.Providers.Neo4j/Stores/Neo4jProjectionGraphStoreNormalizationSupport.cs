namespace Aevatar.CQRS.Projection.Providers.Neo4j.Stores;

internal static class Neo4jProjectionGraphStoreNormalizationSupport
{
    internal static long NormalizeTimestamp(DateTimeOffset timestamp)
    {
        if (timestamp == default)
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return timestamp.ToUnixTimeMilliseconds();
    }

    internal static DateTimeOffset FromUnixTimeMilliseconds(long value)
    {
        var safeValue = Math.Max(0, value);
        return DateTimeOffset.FromUnixTimeMilliseconds(safeValue);
    }

    internal static string NormalizeToken(string? token) => token?.Trim() ?? "";

    internal static string[] NormalizeEdgeTypes(IReadOnlyList<string> edgeTypes)
    {
        return edgeTypes
            .Select(NormalizeToken)
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    internal static bool ResolveProjectionManaged(IReadOnlyDictionary<string, string> properties)
    {
        if (!properties.TryGetValue(ProjectionGraphManagedPropertyKeys.ManagedMarkerKey, out var markerValue))
            return false;

        var normalizedMarker = NormalizeToken(markerValue);
        return string.Equals(
            normalizedMarker,
            ProjectionGraphManagedPropertyKeys.ManagedMarkerValue,
            StringComparison.Ordinal);
    }

    internal static string ResolveProjectionOwnerId(IReadOnlyDictionary<string, string> properties)
    {
        if (!properties.TryGetValue(ProjectionGraphManagedPropertyKeys.ManagedOwnerIdKey, out var ownerId))
            return "";

        return NormalizeToken(ownerId);
    }

    internal static string NormalizeLabel(string rawLabel, string fallback)
    {
        var label = (rawLabel ?? "").Trim();
        if (label.Length == 0)
            label = fallback;

        var chars = label
            .Select(ch => char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_')
            .ToArray();
        var normalized = new string(chars);
        if (normalized.Length == 0)
            normalized = fallback;
        if (char.IsDigit(normalized[0]))
            normalized = $"N_{normalized}";
        return normalized;
    }

    internal static string NormalizeConstraintName(string rawName)
    {
        var chars = rawName
            .Select(ch => char.IsLetterOrDigit(ch) || ch == '_' ? char.ToLowerInvariant(ch) : '_')
            .ToArray();
        var normalized = new string(chars);
        if (normalized.Length == 0)
            return "projection_graph_constraint";
        if (char.IsDigit(normalized[0]))
            normalized = $"c_{normalized}";
        return normalized;
    }
}
