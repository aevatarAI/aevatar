using System.Text.Json;
using System.Text.Json.Nodes;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Google.Protobuf;

namespace Aevatar.AI.ToolProviders.NyxId.ConnectedServices;

internal static class NyxIdServiceTools
{
    public static IAgentTool CreateInventory(
        IReadOnlyList<NyxIdServiceInstanceBinding> bindings) =>
        new InventoryTool(bindings);

    public static IReadOnlyList<IAgentTool> Create(
        NyxIdServiceInstanceClient client,
        IReadOnlyList<NyxIdServiceInstanceBinding> bindings) =>
    [
        CreateInventory(bindings),
        new UpdateTool(client, bindings),
        new RouteTool(client, bindings),
        new DeleteTool(client, bindings),
    ];

    private abstract class ServiceToolBase(IReadOnlyList<NyxIdServiceInstanceBinding> bindings) : IAgentTool
    {
        protected IReadOnlyDictionary<string, NyxIdServiceInstanceBinding> Bindings { get; } =
            bindings.ToDictionary(
                static binding => binding.Instance.UserServiceId,
                StringComparer.Ordinal);

        public abstract string Name { get; }
        public abstract string Description { get; }
        public abstract string ParametersSchema { get; }
        public virtual ToolApprovalMode ApprovalMode => ToolApprovalMode.NeverRequire;
        public virtual bool IsReadOnly => false;
        public virtual bool IsDestructive => false;
        public virtual bool? RequiresApproval(string argumentsJson) => null;
        public virtual AgentToolCallSafety GetCallSafety(string argumentsJson) =>
            new(RequiresApproval(argumentsJson), IsReadOnly, IsDestructive);
        public virtual AgentToolReceipt? CreateResultReceipt(
            string callId,
            string toolName,
            string argumentsJson,
            string resultJson) =>
            null;
        public abstract Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default);

        protected bool TryGetBinding(
            JsonElement root,
            out NyxIdServiceInstanceBinding binding,
            out string? error)
        {
            binding = null!;
            var id = ReadString(root, "user_service_id");
            if (string.IsNullOrWhiteSpace(id) || !Bindings.TryGetValue(id, out var foundBinding))
            {
                error = NyxIdServiceInstanceClient.Error("identity_not_authorized");
                return false;
            }
            binding = foundBinding;
            error = null;
            return true;
        }

        protected string InstanceChoiceSchema(
            bool requireInstance,
            params (string Name, JsonNode Schema, bool Required)[] properties)
        {
            var propertyMap = new JsonObject
            {
                ["user_service_id"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray(Bindings.Keys.Order(StringComparer.Ordinal)
                        .Select(static id => JsonValue.Create(id)).ToArray()),
                },
            };
            var required = requireInstance ? new JsonArray("user_service_id") : [];
            foreach (var property in properties)
            {
                propertyMap[property.Name] = property.Schema;
                if (property.Required)
                    required.Add(property.Name);
            }
            return new JsonObject
            {
                ["type"] = "object",
                ["properties"] = propertyMap,
                ["required"] = required,
                ["additionalProperties"] = false,
            }.ToJsonString();
        }

        protected static bool TryParse(string argumentsJson, out JsonDocument document, out string? error)
        {
            try
            {
                document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    document.Dispose();
                    document = null!;
                    error = NyxIdServiceInstanceClient.Error("invalid_arguments");
                    return false;
                }
                error = null;
                return true;
            }
            catch (JsonException)
            {
                document = null!;
                error = NyxIdServiceInstanceClient.Error("invalid_arguments");
                return false;
            }
        }

        protected static string? ReadString(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        protected static bool? ReadBool(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.GetBoolean()
                : null;

        protected static string Format(IMessage message) => JsonFormatter.Default.Format(message);
    }

    private sealed class InventoryTool(IReadOnlyList<NyxIdServiceInstanceBinding> bindings)
        : ServiceToolBase(bindings)
    {
        private static readonly JsonFormatter ResultFormatter = new(
            JsonFormatter.Settings.Default.WithFormatDefaultValues(true));
        private static readonly string EmptyInventoryParametersSchema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject(),
            ["required"] = new JsonArray(),
            ["additionalProperties"] = false,
        }.ToJsonString();

        public override string Name => "nyxid_service_inventory";
        public override string Description => "List or inspect the caller's exact NyxID connected-service instances.";
        public override string ParametersSchema => Bindings.Count == 0
            ? EmptyInventoryParametersSchema
            : InstanceChoiceSchema(requireInstance: false);
        public override bool IsReadOnly => true;

        public override AgentToolReceipt? CreateResultReceipt(
            string callId,
            string toolName,
            string argumentsJson,
            string resultJson) =>
            NyxIdServiceInventoryReceiptFactory.Create(callId, toolName, resultJson);

        public override Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            if (!TryParse(argumentsJson, out var document, out var error))
                return Task.FromResult(error!);
            using (document)
            {
                var requestedId = ReadString(document.RootElement, "user_service_id");
                var result = new NyxIdServiceInventoryResult();
                if (string.IsNullOrWhiteSpace(requestedId))
                {
                    result.Instances.Add(Bindings.Values.Select(static binding => binding.Instance.Clone()));
                }
                else if (Bindings.TryGetValue(requestedId, out var binding))
                {
                    result.Instances.Add(binding.Instance.Clone());
                }
                else
                {
                    return Task.FromResult(NyxIdServiceInstanceClient.Error("identity_not_authorized"));
                }
                return Task.FromResult(ResultFormatter.Format(result));
            }
        }
    }

    private sealed class UpdateTool(
        NyxIdServiceInstanceClient client,
        IReadOnlyList<NyxIdServiceInstanceBinding> bindings) : ServiceToolBase(bindings)
    {
        public override string Name => "nyxid_service_update";
        public override string Description => "Update mutable fields on one exact NyxID connected-service instance.";
        public override ToolApprovalMode ApprovalMode => ToolApprovalMode.AlwaysRequire;
        public override string ParametersSchema => InstanceChoiceSchema(
            requireInstance: true,
            ("label", new JsonObject { ["type"] = "string" }, false),
            ("endpoint_url", new JsonObject { ["type"] = "string" }, false),
            ("openapi_spec_url", new JsonObject
            {
                ["type"] = "string",
                ["description"] = "NyxID-owned OpenAPI override URL. Use an empty string to clear the override.",
            }, false),
            ("is_active", new JsonObject { ["type"] = "boolean" }, false));

        public override async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            if (!TryParse(argumentsJson, out var document, out var error))
                return error!;
            using (document)
            {
                if (!TryGetBinding(document.RootElement, out var binding, out error))
                    return error!;
                var request = new NyxIdServiceUpdateRequest { UserServiceId = binding.Instance.UserServiceId };
                var label = ReadString(document.RootElement, "label");
                if (label is not null)
                    request.Label = label;
                var endpointUrl = ReadString(document.RootElement, "endpoint_url");
                if (endpointUrl is not null)
                    request.EndpointUrl = endpointUrl;
                var openApiSpecUrl = ReadString(document.RootElement, "openapi_spec_url");
                if (openApiSpecUrl is not null)
                    request.OpenapiSpecUrl = openApiSpecUrl;
                var active = ReadBool(document.RootElement, "is_active");
                if (active.HasValue)
                    request.IsActive = active.Value;
                return Format(await client.UpdateAsync(binding, request, ct));
            }
        }
    }

    private sealed class RouteTool(
        NyxIdServiceInstanceClient client,
        IReadOnlyList<NyxIdServiceInstanceBinding> bindings) : ServiceToolBase(bindings)
    {
        public override string Name => "nyxid_service_route";
        public override string Description => "Route one exact NyxID connected-service instance directly or through a node.";
        public override ToolApprovalMode ApprovalMode => ToolApprovalMode.AlwaysRequire;
        public override string ParametersSchema => InstanceChoiceSchema(
            requireInstance: true,
            ("route", new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("direct", "node") }, true),
            ("node_id", new JsonObject { ["type"] = "string" }, false));

        public override async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            if (!TryParse(argumentsJson, out var document, out var error))
                return error!;
            using (document)
            {
                if (!TryGetBinding(document.RootElement, out var binding, out error))
                    return error!;
                var request = new NyxIdServiceRouteRequest { UserServiceId = binding.Instance.UserServiceId };
                var route = ReadString(document.RootElement, "route");
                if (string.Equals(route, "direct", StringComparison.OrdinalIgnoreCase))
                    request.Direct = true;
                else if (string.Equals(route, "node", StringComparison.OrdinalIgnoreCase))
                    request.NodeId = ReadString(document.RootElement, "node_id") ?? string.Empty;
                else
                    return NyxIdServiceInstanceClient.Error("invalid_route");
                return Format(await client.RouteAsync(binding, request, ct));
            }
        }
    }

    private sealed class DeleteTool(
        NyxIdServiceInstanceClient client,
        IReadOnlyList<NyxIdServiceInstanceBinding> bindings) : ServiceToolBase(bindings)
    {
        public override string Name => "nyxid_service_delete";
        public override string Description => "Delete one exact NyxID connected-service instance.";
        public override string ParametersSchema => InstanceChoiceSchema(requireInstance: true);
        public override ToolApprovalMode ApprovalMode => ToolApprovalMode.AlwaysRequire;
        public override bool IsDestructive => true;

        public override async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            if (!TryParse(argumentsJson, out var document, out var error))
                return error!;
            using (document)
            {
                if (!TryGetBinding(document.RootElement, out var binding, out error))
                    return error!;
                return Format(await client.DeleteAsync(binding, ct));
            }
        }
    }

}

public static class NyxIdServiceInventoryReceiptFactory
{
    private const string FailureCode = "NYXID_SERVICE_INVENTORY_FAILED";
    private const string FailureMessage = "The connected-service inventory request failed.";

    public static AgentToolReceipt? Create(
        string callId,
        string toolName,
        string resultJson)
    {
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            if (root.TryGetProperty("error", out var error) &&
                error.ValueKind is not (JsonValueKind.Null or JsonValueKind.False))
            {
                return new AgentToolReceipt
                {
                    CallId = callId ?? string.Empty,
                    ToolName = toolName ?? string.Empty,
                    Status = AgentToolReceiptStatus.Error,
                    ApprovalMode = AgentToolReceiptApprovalMode.NeverRequire,
                    ErrorCode = FailureCode,
                    ErrorMessage = FailureMessage,
                    ResultJson = JsonSerializer.Serialize(new
                    {
                        error = FailureCode,
                        message = FailureMessage,
                    }),
                };
            }

            if (!root.TryGetProperty("instances", out var instances) ||
                instances.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            return new AgentToolReceipt
            {
                CallId = callId ?? string.Empty,
                ToolName = toolName ?? string.Empty,
                Status = AgentToolReceiptStatus.Success,
                ApprovalMode = AgentToolReceiptApprovalMode.NeverRequire,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
