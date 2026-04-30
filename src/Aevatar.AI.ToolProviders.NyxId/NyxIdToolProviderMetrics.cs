using System.Diagnostics.Metrics;

namespace Aevatar.AI.ToolProviders.NyxId;

public static class NyxIdToolProviderMetrics
{
    public const string MeterName = "Aevatar.AI.ToolProviders.NyxId";
    public const string SpecCatalogLookupMissTotal = "spec_catalog_lookup_miss_total";

    private static readonly Meter Meter = new(MeterName, "1.0.0");
    private static readonly Counter<long> SpecCatalogLookupMisses = Meter.CreateCounter<long>(
        SpecCatalogLookupMissTotal,
        description: "NyxID spec catalog operation lookup misses.");

    public static void RecordSpecCatalogLookupMiss(string? operationId)
    {
        var normalizedOperationId = string.IsNullOrWhiteSpace(operationId)
            ? "unknown"
            : operationId.Trim();

        SpecCatalogLookupMisses.Add(
            1,
            new KeyValuePair<string, object?>("operation_id", normalizedOperationId));
    }
}
