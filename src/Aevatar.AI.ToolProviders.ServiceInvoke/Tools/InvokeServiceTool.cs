using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.ServiceInvoke.Schema;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.AI.ToolProviders.ServiceInvoke.Tools;

public sealed class InvokeServiceTool : IAgentTool
{
    private readonly IServiceInvocationPort _invocationPort;
    private readonly IServiceCatalogQueryReader _catalogReader;
    private readonly ServiceInvokeOptions _options;
    private readonly EndpointSchemaProvider? _schemaProvider;

    public InvokeServiceTool(
        IServiceInvocationPort invocationPort,
        IServiceCatalogQueryReader catalogReader,
        ServiceInvokeOptions options,
        EndpointSchemaProvider? schemaProvider = null)
    {
        _invocationPort = invocationPort;
        _catalogReader = catalogReader;
        _options = options;
        _schemaProvider = schemaProvider;
    }

    public string Name => "invoke_service";

    public string Description =>
        "Invoke a service endpoint. Use list_services first to discover available endpoints. " +
        "For COMMAND endpoints, the result is an async acceptance receipt; the business result arrives separately via events. " +
        "Provide payload as a JSON object matching the endpoint's request schema.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "service_id": {
              "type": "string",
              "description": "The service_id from list_services"
            },
            "endpoint_id": {
              "type": "string",
              "description": "The endpoint_id to invoke"
            },
            "payload": {
              "type": "object",
              "description": "The request payload as JSON. Structure depends on the endpoint's request type."
            }
          },
          "required": ["service_id", "endpoint_id"]
        }
        """;

    public ToolApprovalMode ApprovalMode => ToolApprovalMode.AlwaysRequire;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        try
        {
            var args = ToolArgs.Parse(argumentsJson);
            var serviceId = args.Str("service_id");
            var endpointId = args.Str("endpoint_id");

            if (string.IsNullOrWhiteSpace(serviceId))
                return """{"error":"'service_id' is required"}""";
            if (string.IsNullOrWhiteSpace(endpointId))
                return """{"error":"'endpoint_id' is required"}""";

            var (resolvedTenantId, resolvedAppId, resolvedNamespace) = ResolveScope();

            var identity = new ServiceIdentity
            {
                TenantId = resolvedTenantId ?? string.Empty,
                AppId = resolvedAppId ?? string.Empty,
                Namespace = resolvedNamespace ?? string.Empty,
                ServiceId = serviceId,
            };

            var payloadJson = args.RawOrStr("payload");
            var catalog = await _catalogReader.GetAsync(identity, ct);
            if (catalog == null)
                return JsonSerializer.Serialize(new { error = $"Service '{serviceId}' was not found in scope '{resolvedTenantId}:{resolvedAppId}:{resolvedNamespace}'." });

            var endpoint = catalog.Endpoints
                .FirstOrDefault(e => string.Equals(e.EndpointId, endpointId, StringComparison.OrdinalIgnoreCase));
            if (endpoint == null)
                return JsonSerializer.Serialize(new { error = $"Endpoint '{endpointId}' not found on service '{serviceId}'. Available: {string.Join(", ", catalog.Endpoints.Select(e => e.EndpointId))}" });

            var payload = await PackPayloadAsync(
                payloadJson, endpoint.RequestTypeUrl, catalog, ct);

            var commandId = Guid.NewGuid().ToString("N");
            var request = new ServiceInvocationRequest
            {
                Identity = identity,
                EndpointId = endpointId,
                Payload = payload,
                CommandId = commandId,
                CorrelationId = commandId,
                Caller = new ServiceInvocationCaller
                {
                    ServiceKey = string.Empty,
                    TenantId = resolvedTenantId ?? string.Empty,
                    AppId = resolvedAppId ?? string.Empty,
                },
            };

            var receipt = await _invocationPort.InvokeAsync(request, ct);

            return JsonSerializer.Serialize(new
            {
                status = "accepted",
                request_id = receipt.RequestId,
                service_key = receipt.ServiceKey,
                deployment_id = receipt.DeploymentId,
                target_actor_id = receipt.TargetActorId,
                endpoint_id = receipt.EndpointId,
                command_id = receipt.CommandId,
                correlation_id = receipt.CorrelationId,
                note = "Async acknowledgment. The business result arrives via event/projection.",
            }, new JsonSerializerOptions { WriteIndented = false, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = $"Invocation failed: {ex.Message}" });
        }
    }

    private (string? TenantId, string? AppId, string? Namespace) ResolveScope()
    {
        var tenantId = _options.TenantId;
        var appId = _options.AppId;
        var ns = _options.Namespace;

        if (_options.EnableDynamicScopeResolution &&
            string.IsNullOrWhiteSpace(tenantId))
        {
            tenantId = AgentToolRequestContext.TryGet("scope_id");
            if (!string.IsNullOrWhiteSpace(tenantId))
            {
                appId = string.IsNullOrWhiteSpace(appId) ? "default" : appId;
                ns = string.IsNullOrWhiteSpace(ns) ? "default" : ns;
            }
        }

        return (tenantId, appId, ns);
    }

    private async Task<Any> PackPayloadAsync(
        string? payloadJson,
        string? requestTypeUrl,
        ServiceCatalogSnapshot? catalog,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(payloadJson) || payloadJson == "null")
            payloadJson = "{}";

        // Try typed conversion via schema provider when RequestTypeUrl is available
        if (_schemaProvider != null &&
            !string.IsNullOrWhiteSpace(requestTypeUrl) &&
            catalog != null &&
            !string.IsNullOrWhiteSpace(catalog.ActiveServingRevisionId))
        {
            var serviceKey = ServiceKeys.Build(
                catalog.TenantId, catalog.AppId, catalog.Namespace, catalog.ServiceId);
            var typed = await _schemaProvider.TryPackTypedAsync(
                serviceKey, catalog.ActiveServingRevisionId, requestTypeUrl, payloadJson, ct);
            if (typed != null)
                return typed;
        }

        // Fallback: pack as Struct (works for endpoints that accept Struct payloads)
        var parsed = JsonParser.Default.Parse<Struct>(payloadJson);
        return Any.Pack(parsed);
    }
}
