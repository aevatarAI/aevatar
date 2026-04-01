using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.ServiceInvoke.Schema;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;

namespace Aevatar.AI.ToolProviders.ServiceInvoke.Tools;

public sealed class ListServicesTool : IAgentTool
{
    private readonly IServiceCatalogQueryReader _catalogReader;
    private readonly ServiceInvokeOptions _options;
    private readonly EndpointSchemaProvider? _schemaProvider;

    public ListServicesTool(
        IServiceCatalogQueryReader catalogReader,
        ServiceInvokeOptions options,
        EndpointSchemaProvider? schemaProvider = null)
    {
        _catalogReader = catalogReader;
        _options = options;
        _schemaProvider = schemaProvider;
    }

    public string Name => "list_services";

    public string Description =>
        "Discover available services and their endpoints. " +
        "Returns service names, endpoint IDs, kinds (COMMAND/CHAT), request/response type URLs, and descriptions. " +
        "Use this to find services before invoking them with invoke_service.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "filter": {
              "type": "string",
              "description": "Optional text filter to match against service/endpoint names or descriptions"
            },
            "service_id": {
              "type": "string",
              "description": "Optional: show details for a specific service ID only"
            }
          }
        }
        """;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        try
        {
            var args = ToolArgs.Parse(argumentsJson);
            var filter = args.Str("filter");
            var serviceId = args.Str("service_id");

            var snapshots = await QueryServicesAsync(ct);

            if (!string.IsNullOrWhiteSpace(serviceId))
            {
                snapshots = snapshots
                    .Where(s => string.Equals(s.ServiceId, serviceId, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            if (!string.IsNullOrWhiteSpace(filter))
            {
                snapshots = snapshots
                    .Where(s => MatchesFilter(s, filter))
                    .ToList();
            }

            if (snapshots.Count == 0)
                return """{"services":[],"message":"No services found matching the criteria."}""";

            return await SerializeResultsAsync(snapshots, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    private async Task<IReadOnlyList<ServiceCatalogSnapshot>> QueryServicesAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_options.TenantId) &&
            !string.IsNullOrWhiteSpace(_options.AppId) &&
            !string.IsNullOrWhiteSpace(_options.Namespace))
        {
            return await _catalogReader.QueryByScopeAsync(
                _options.TenantId, _options.AppId, _options.Namespace, _options.MaxListResults, ct);
        }

        return await _catalogReader.QueryAllAsync(_options.MaxListResults, ct);
    }

    private static bool MatchesFilter(ServiceCatalogSnapshot snapshot, string filter)
    {
        if (snapshot.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            snapshot.ServiceId.Contains(filter, StringComparison.OrdinalIgnoreCase))
            return true;

        return snapshot.Endpoints.Any(ep =>
            ep.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(ep.Description) && ep.Description.Contains(filter, StringComparison.OrdinalIgnoreCase)));
    }

    private async Task<string> SerializeResultsAsync(
        IReadOnlyList<ServiceCatalogSnapshot> snapshots,
        CancellationToken ct)
    {
        var results = new List<object>();

        foreach (var s in snapshots)
        {
            var endpointResults = new List<object>();
            foreach (var ep in s.Endpoints)
            {
                string? requestSchema = null;
                if (_schemaProvider != null &&
                    !string.IsNullOrWhiteSpace(ep.RequestTypeUrl) &&
                    !string.IsNullOrWhiteSpace(s.ActiveServingRevisionId))
                {
                    try
                    {
                        var serviceKey = ServiceKeys.Build(s.TenantId, s.AppId, s.Namespace, s.ServiceId);
                        requestSchema = await _schemaProvider.GetJsonSchemaAsync(
                            serviceKey, s.ActiveServingRevisionId, ep.RequestTypeUrl, ct);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { /* Schema resolution failed for this endpoint — continue without schema */ }
                }

                endpointResults.Add(new
                {
                    endpoint_id = ep.EndpointId,
                    display_name = ep.DisplayName,
                    kind = ep.Kind,
                    request_type_url = ep.RequestTypeUrl,
                    response_type_url = ep.ResponseTypeUrl,
                    description = ep.Description,
                    request_schema = requestSchema,
                });
            }

            results.Add(new
            {
                service_key = ServiceKeys.Build(s.TenantId, s.AppId, s.Namespace, s.ServiceId),
                service_id = s.ServiceId,
                display_name = s.DisplayName,
                deployment_status = s.DeploymentStatus,
                endpoints = endpointResults.ToArray(),
            });
        }

        return JsonSerializer.Serialize(new { services = results },
            new JsonSerializerOptions { WriteIndented = false, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
    }
}
