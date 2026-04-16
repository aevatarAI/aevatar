using System.Text.Json;

namespace Aevatar.AI.ToolProviders.NyxId;

public static class OpenApiSpecParser
{
    public static OperationCard[] ParseSpec(string json, string service = "nyxid")
    {
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("paths", out var paths) ||
            paths.ValueKind != JsonValueKind.Object)
            return [];

        var cards = new List<OperationCard>();
        foreach (var pathEntry in paths.EnumerateObject())
        {
            var pathStr = pathEntry.Name;
            if (pathEntry.Value.ValueKind != JsonValueKind.Object) continue;

            foreach (var methodEntry in pathEntry.Value.EnumerateObject())
            {
                var method = methodEntry.Name.ToUpperInvariant();
                if (method is "PARAMETERS" or "SERVERS" or "SUMMARY" or "DESCRIPTION")
                    continue;

                var op = methodEntry.Value;
                var operationId = op.TryGetProperty("operationId", out var oid)
                    ? oid.GetString() ?? $"{method}_{pathStr}"
                    : $"{method}_{pathStr}";

                var summary = op.TryGetProperty("summary", out var s)
                    ? s.GetString() ?? ""
                    : op.TryGetProperty("description", out var d)
                        ? (d.GetString() ?? "").Split('\n')[0]
                        : "";

                string? parameters = null;
                if (op.TryGetProperty("parameters", out var paramEl) &&
                    paramEl.ValueKind == JsonValueKind.Array)
                    parameters = paramEl.GetRawText();

                string? requestBody = null;
                if (op.TryGetProperty("requestBody", out var bodyEl))
                    requestBody = bodyEl.GetRawText();

                cards.Add(new OperationCard(
                    Service: service,
                    OperationId: operationId,
                    Method: method,
                    Path: pathStr,
                    Summary: summary,
                    Parameters: parameters,
                    RequestBodySchema: requestBody));
            }
        }

        return cards.ToArray();
    }
}
