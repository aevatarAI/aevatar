using System.Text;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

public sealed class NyxIdSearchCapabilitiesTool : IAgentTool
{
    private readonly NyxIdSpecCatalog _catalog;

    public NyxIdSearchCapabilitiesTool(NyxIdSpecCatalog catalog) => _catalog = catalog;

    public string Name => "nyxid_search_capabilities";

    public string Description =>
        "Search NyxID API capabilities by natural language query. " +
        "Returns matching API operations with method, path, and parameter details. " +
        "Use this to discover NyxID endpoints not covered by specialized tools, " +
        "then call nyxid_proxy_execute with the operation_id to run it.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "query": {
              "type": "string",
              "description": "Natural language search query, e.g. 'create organization' or 'list sessions'"
            }
          },
          "required": ["query"]
        }
        """;

    public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var args = ToolArgs.Parse(argumentsJson);
        var query = args.Str("query");

        if (string.IsNullOrWhiteSpace(query))
            return Task.FromResult("""{"error":"'query' is required"}""");

        var totalOps = _catalog.Operations.Length;
        var results = _catalog.Search(query);

        if (results.Count == 0)
        {
            var msg = totalOps == 0
                ? "The NyxID API spec catalog is empty (spec endpoint may be unavailable). " +
                  "Try the specialized tools (nyxid_services, nyxid_api_keys, nyxid_orgs, etc.) or nyxid_proxy directly."
                : $"No matching operations found for '{query}'. " +
                  $"The NyxID API spec currently covers {totalOps} operations. " +
                  "Your query may match an endpoint not yet in the spec. " +
                  "Try the specialized tools or nyxid_proxy directly.";
            return Task.FromResult($"{{\"results\":[], \"message\":\"{EscapeJson(msg)}\"}}");
        }

        var sb = new StringBuilder();
        sb.Append($"{{\"results_count\":{results.Count}, \"total_spec_operations\":{totalOps}, \"results\":[");

        for (var i = 0; i < results.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var op = results[i];
            sb.Append('{');
            sb.Append($"\"operation_id\":\"{EscapeJson(op.OperationId)}\",");
            sb.Append($"\"method\":\"{op.Method}\",");
            sb.Append($"\"path\":\"{EscapeJson(op.Path)}\",");
            sb.Append($"\"summary\":\"{EscapeJson(op.Summary)}\"");
            if (op.Parameters != null)
                sb.Append($",\"parameters\":{op.Parameters}");
            if (op.RequestBodySchema != null)
                sb.Append($",\"request_body\":{op.RequestBodySchema}");
            sb.Append('}');
        }

        sb.Append("]}");
        return Task.FromResult(sb.ToString());
    }

    private static string EscapeJson(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");
}
