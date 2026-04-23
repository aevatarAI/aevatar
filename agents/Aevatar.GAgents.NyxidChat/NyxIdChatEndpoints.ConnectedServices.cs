using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Studio.Application.Studio.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.NyxidChat;

public static partial class NyxIdChatEndpoints
{
    private static async Task InjectConnectedServicesAsync(
        HttpContext http,
        string accessToken,
        IDictionary<string, string> metadata,
        CancellationToken ct)
    {
        var client = http.RequestServices.GetService<NyxIdApiClient>();
        if (client is null)
            return;

        var logger = http.RequestServices.GetService<ILoggerFactory>()
            ?.CreateLogger("Aevatar.NyxId.Chat.ConnectedServices");

        try
        {
            var memCache = http.RequestServices.GetService<IMemoryCache>();
            var cacheKey = $"nyxid:services:{ComputeTokenHash(accessToken)}";

            string? servicesJson = null;
            if (memCache is not null)
                servicesJson = memCache.Get<string>(cacheKey);

            if (servicesJson is null)
            {
                servicesJson = await client.DiscoverProxyServicesAsync(accessToken, ct);
                memCache?.Set(cacheKey, servicesJson, TimeSpan.FromSeconds(60));
            }

            var specSource = http.RequestServices.GetService<IConnectedServiceSpecSource>();
            var context = await BuildConnectedServicesContextAsync(servicesJson, specSource, accessToken, ct);
            if (!string.IsNullOrWhiteSpace(context))
                metadata[LLMRequestMetadataKeys.ConnectedServicesContext] = context;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to load connected services context — continuing without capability context");
        }
    }

    internal static async Task<string> BuildConnectedServicesContextAsync(
        string servicesJson,
        IConnectedServiceSpecSource? specSource,
        string accessToken,
        CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<connected-services>");
        sb.AppendLine("Your capabilities based on connected services:");

        var hintRequests = new List<ServiceHintRequest>();

        try
        {
            using var doc = JsonDocument.Parse(servicesJson);
            var root = doc.RootElement;

            JsonElement items = root;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("services", out var svc))
                    items = svc;
                else if (root.TryGetProperty("data", out var data))
                    items = data;
            }

            if (items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    var serviceId = item.TryGetProperty("id", out var id) ? id.GetString()
                                  : item.TryGetProperty("service_id", out var sid) ? sid.GetString()
                                  : null;
                    var slug = item.TryGetProperty("slug", out var s) ? s.GetString() : null;
                    var name = item.TryGetProperty("name", out var n) ? n.GetString()
                             : item.TryGetProperty("label", out var l) ? l.GetString()
                             : slug;
                    var baseUrl = item.TryGetProperty("endpoint_url", out var e) ? e.GetString()
                                : item.TryGetProperty("base_url", out var b) ? b.GetString()
                                : null;
                    var openapiUrl = item.TryGetProperty("openapi_url", out var oa) ? oa.GetString() : null;

                    if (string.IsNullOrWhiteSpace(slug))
                        continue;

                    hintRequests.Add(new ServiceHintRequest(slug, serviceId, name, openapiUrl));

                    sb.Append($"- **{name ?? slug}** (slug: `{slug}`)");
                    if (!string.IsNullOrWhiteSpace(baseUrl))
                        sb.Append($" — base: {baseUrl}");
                    sb.AppendLine();
                }
            }
        }
        catch
        {
        }

        if (hintRequests.Count == 0)
        {
            sb.AppendLine("No services connected yet. Use nyxid_catalog to browse and connect services.");
        }

        sb.AppendLine("Use nyxid_proxy with slug + path to call any service. Use code_execute for sandbox.");
        sb.AppendLine("</connected-services>");

        string hints;
        if (specSource is not null && !string.IsNullOrWhiteSpace(accessToken))
        {
            hints = await NyxIdServiceApiHints.BuildHintsSectionAsync(hintRequests, specSource, accessToken, ct);
        }
        else
        {
            hints = NyxIdServiceApiHints.BuildHintsSection(hintRequests.Select(r => r.Slug));
        }

        if (!string.IsNullOrEmpty(hints))
        {
            sb.AppendLine();
            sb.Append(hints);
        }

        return sb.ToString();
    }

    private static string ComputeTokenHash(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexStringLower(bytes)[..16];
    }
}
