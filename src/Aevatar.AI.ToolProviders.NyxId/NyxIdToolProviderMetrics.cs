using System.Diagnostics.Metrics;

namespace Aevatar.AI.ToolProviders.NyxId;

public static class NyxIdToolProviderMetrics
{
    public const string MeterName = "Aevatar.AI.ToolProviders.NyxId";
    public const string SpecCatalogLookupMissTotal = "spec_catalog_lookup_miss_total";
    public const string SpecCatalogLookupMissReasonUnknownOperation = "unknown_operation";

    private static readonly Meter Meter = new(MeterName, "1.0.0");
    private static readonly Counter<long> SpecCatalogLookupMisses = Meter.CreateCounter<long>(
        SpecCatalogLookupMissTotal,
        description: "NyxID spec catalog operation lookup misses.");

    public static void RecordSpecCatalogLookupMiss(string reason)
    {
        var normalizedReason = string.IsNullOrWhiteSpace(reason)
            ? "unknown"
            : reason.Trim();

        SpecCatalogLookupMisses.Add(
            1,
            new KeyValuePair<string, object?>("reason", normalizedReason));
    }
}
