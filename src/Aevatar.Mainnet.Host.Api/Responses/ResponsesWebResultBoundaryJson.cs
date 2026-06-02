using System.Text.Json;
using Aevatar.GAgentService.Abstractions;

namespace Aevatar.Mainnet.Host.Api.Responses;

// Refactor (iter161-cluster-001 #1251-first):
//   Old pattern: typed Responses Web results exposed boundary JSON rendering from the
//   shared Abstractions layer and projection queries could call it directly.
//   New principle: external JSON rendering stays in the Host/API adapter boundary;
//   lower layers exchange typed protobuf contracts only.
internal static class ResponsesWebResultBoundaryJson
{
    public static string ToBoundaryJson(ResponsesWebToolResult? result)
    {
        if (result == null)
            return "{}";

        return result.ResultCase switch
        {
            ResponsesWebToolResult.ResultOneofCase.Fetch => ToBoundaryJson(result.Fetch),
            ResponsesWebToolResult.ResultOneofCase.Search => ToBoundaryJson(result.Search),
            ResponsesWebToolResult.ResultOneofCase.Error => ToBoundaryJson(result.Error),
            _ => "{}",
        };
    }

    public static string ToBoundaryJson(ResponsesWebFetchToolOutput? output)
    {
        var fields = new Dictionary<string, object?>
        {
            ["url"] = output?.Url ?? string.Empty,
            ["status_code"] = output?.StatusCode ?? 0,
            ["content_type"] = output?.ContentType ?? string.Empty,
            ["content"] = output?.Content ?? string.Empty,
        };
        if (!string.IsNullOrWhiteSpace(output?.RedirectUrl))
            fields["redirect_url"] = output.RedirectUrl;

        return JsonSerializer.Serialize(fields);
    }

    public static string ToBoundaryJson(ResponsesWebSearchToolOutput? output) =>
        JsonSerializer.Serialize(new
        {
            results = (output?.Results ?? Enumerable.Empty<ResponsesWebSearchResultItem>())
                .Select(static item => new
                {
                    title = item.Title,
                    url = item.Url,
                    snippet = item.Snippet,
                }),
        });

    public static string ToBoundaryJson(ResponsesWebToolError? error) =>
        JsonSerializer.Serialize(new
        {
            error = error?.Code ?? string.Empty,
            message = error?.Message ?? string.Empty,
        });
}
