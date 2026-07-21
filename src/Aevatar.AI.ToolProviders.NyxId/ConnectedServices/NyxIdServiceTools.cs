using System.Text.Json;
using System.Text.Json.Nodes;
using Aevatar.AI.Abstractions.ToolProviders;
using Google.Protobuf;

namespace Aevatar.AI.ToolProviders.NyxId.ConnectedServices;

internal static class NyxIdServiceTools
{
    public static IReadOnlyList<IAgentTool> Create(
        NyxIdServiceInstanceClient client,
        IReadOnlyList<NyxIdServiceInstanceBinding> bindings) =>
    [
        new InventoryTool(bindings),
        new UpdateTool(client, bindings),
        new RouteTool(client, bindings),
        new DeleteTool(client, bindings),
        new RequestTool(client, bindings),
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
        public override string Name => "nyxid_service_inventory";
        public override string Description => "List or inspect the caller's exact NyxID connected-service instances.";
        public override string ParametersSchema => InstanceChoiceSchema(requireInstance: false);
        public override bool IsReadOnly => true;

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
                return Task.FromResult(Format(result));
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

    private sealed class RequestTool(
        NyxIdServiceInstanceClient client,
        IReadOnlyList<NyxIdServiceInstanceBinding> bindings) : ServiceToolBase(bindings)
    {
        private static readonly string[] Methods = ["GET", "HEAD", "OPTIONS", "POST", "PUT", "PATCH", "DELETE"];

        public override string Name => "nyxid_service_request";
        public override string Description => "Call a JSON endpoint through one exact NyxID connected-service instance.";
        public override ToolApprovalMode ApprovalMode => ToolApprovalMode.Auto;
        public override string ParametersSchema => InstanceChoiceSchema(
            requireInstance: true,
            ("method", new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray(Methods.Select(static method => JsonValue.Create(method)).ToArray()),
            }, true),
            ("relative_path", new JsonObject { ["type"] = "string" }, true),
            ("query", EntryArraySchema(), false),
            ("accept", new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("application/json") }, false),
            ("if_match", new JsonObject { ["type"] = "string" }, false),
            ("if_none_match", new JsonObject { ["type"] = "string" }, false),
            ("json_body", new JsonObject(), false));

        public override bool? RequiresApproval(string argumentsJson)
        {
            if (!TryParse(argumentsJson, out var document, out _))
                return true;
            using (document)
            {
                var method = ParseMethod(ReadString(document.RootElement, "method"));
                return method is not (NyxIdServiceHttpMethod.Get or
                    NyxIdServiceHttpMethod.Head or NyxIdServiceHttpMethod.Options);
            }
        }

        public override async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            if (!TryParse(argumentsJson, out var document, out var error))
                return error!;
            using (document)
            {
                var root = document.RootElement;
                if (!TryGetBinding(root, out var binding, out error))
                    return error!;
                var method = ParseMethod(ReadString(root, "method"));
                var path = ReadString(root, "relative_path");
                if (method == NyxIdServiceHttpMethod.Unspecified || string.IsNullOrWhiteSpace(path))
                    return NyxIdServiceInstanceClient.Error("invalid_request");
                var request = new NyxIdServiceRequest
                {
                    UserServiceId = binding.Instance.UserServiceId,
                    Method = method,
                    RelativePath = path,
                };
                request.Query.Add(ParseEntries(root, "query").Select(static entry =>
                    new NyxIdServiceQueryEntry { Name = entry.Key, Value = entry.Value }));
                request.Headers.Add(ParseHeaders(root));
                var jsonBody = ReadRaw(root, "json_body");
                if (jsonBody is not null)
                    request.JsonBody = jsonBody;
                return Format(await client.RequestAsync(binding, request, ct));
            }
        }

        internal static NyxIdServiceHttpMethod ParseMethod(string? value) => value?.ToUpperInvariant() switch
        {
            "GET" => NyxIdServiceHttpMethod.Get,
            "HEAD" => NyxIdServiceHttpMethod.Head,
            "OPTIONS" => NyxIdServiceHttpMethod.Options,
            "POST" => NyxIdServiceHttpMethod.Post,
            "PUT" => NyxIdServiceHttpMethod.Put,
            "PATCH" => NyxIdServiceHttpMethod.Patch,
            "DELETE" => NyxIdServiceHttpMethod.Delete,
            _ => NyxIdServiceHttpMethod.Unspecified,
        };

        internal static IReadOnlyList<KeyValuePair<string, string>> ParseEntries(JsonElement root, string property)
        {
            if (!root.TryGetProperty(property, out var entries) || entries.ValueKind != JsonValueKind.Array)
                return [];
            var result = new List<KeyValuePair<string, string>>();
            foreach (var entry in entries.EnumerateArray())
            {
                var name = ReadString(entry, "name");
                var value = ReadString(entry, "value");
                if (!string.IsNullOrWhiteSpace(name) && value is not null)
                    result.Add(new KeyValuePair<string, string>(name, value));
            }
            return result;
        }

        internal static IReadOnlyList<NyxIdServiceHeader> ParseHeaders(JsonElement root)
        {
            var headers = new List<NyxIdServiceHeader>();
            AddHeader(NyxIdServiceHeaderName.Accept, "accept");
            AddHeader(NyxIdServiceHeaderName.IfMatch, "if_match");
            AddHeader(NyxIdServiceHeaderName.IfNoneMatch, "if_none_match");
            return headers;

            void AddHeader(NyxIdServiceHeaderName headerName, string propertyName)
            {
                var value = ReadString(root, propertyName);
                if (value is not null)
                    headers.Add(new NyxIdServiceHeader { Name = headerName, Value = value });
            }
        }

        internal static string? ReadRaw(JsonElement root, string property) =>
            root.TryGetProperty(property, out var value) && value.ValueKind is not JsonValueKind.Null
                ? value.GetRawText()
                : null;

        private static JsonObject EntryArraySchema() => new()
        {
            ["type"] = "array",
            ["items"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["name"] = new JsonObject { ["type"] = "string" },
                    ["value"] = new JsonObject { ["type"] = "string" },
                },
                ["required"] = new JsonArray("name", "value"),
                ["additionalProperties"] = false,
            },
        };
    }
}
