using System.Text.Json;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using Microsoft.AspNetCore.Http;

namespace Aevatar.Mainnet.Host.Api.Chat;

internal static class ExternalWorkflowChatCompatibilityAdapter
{
    public static bool AcceptsForm(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.HasFormContentType;
    }

    public static bool AcceptsJson(JsonElement body) =>
        body.ValueKind == JsonValueKind.Object && !body.TryGetProperty("type", out _);

    public static Task HandleAsync(HttpContext http, CancellationToken ct) =>
        WorkflowCapabilityEndpoints.HandleChatPostAsync(http, ct);
}
