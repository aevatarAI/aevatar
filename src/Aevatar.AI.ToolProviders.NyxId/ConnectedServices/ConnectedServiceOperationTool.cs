using System.Text.Json;
using System.Text.Json.Nodes;
using Aevatar.AI.Abstractions.ToolProviders;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.AI.ToolProviders.NyxId.ConnectedServices;

internal sealed class ConnectedServiceOperationTool : IAgentTool, IAgentToolContinuationCapability
{
    private readonly NyxIdServiceInstanceClient _client;
    private readonly ConnectedServiceToolOperation _operation;
    private readonly IReadOnlyDictionary<string, NyxIdServiceInstanceBinding> _bindings;

    public ConnectedServiceOperationTool(
        NyxIdServiceInstanceClient client,
        string name,
        ConnectedServiceToolOperation operation,
        IReadOnlyList<NyxIdServiceInstanceBinding> bindings)
    {
        _client = client;
        Name = name;
        _operation = operation;
        _bindings = bindings.ToDictionary(
            static binding => binding.Instance.UserServiceId,
            StringComparer.Ordinal);
        ParametersSchema = BuildParametersSchema();
    }

    public string Name { get; }
    public string Description =>
        $"{_operation.Summary ?? _operation.OperationId} ({_operation.Method} {_operation.PathTemplate})";
    public string ParametersSchema { get; }
    public ToolApprovalMode ApprovalMode => _operation.ApprovalMode;
    public bool IsReadOnly => _operation.IsReadOnly;
    public bool IsDestructive => _operation.IsDestructive;
    public bool? RequiresApproval(string argumentsJson) => ApprovalMode == ToolApprovalMode.AlwaysRequire;

    public Any CaptureContinuationCapability() =>
        Any.Pack(NyxIdServiceTools.BuildContinuationCapability(_bindings.Values, _operation));

    public bool MatchesContinuationCapability(Any capability) =>
        NyxIdServiceTools.MatchesContinuationCapability(
            capability,
            NyxIdServiceTools.BuildContinuationCapability(_bindings.Values, _operation));

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        }
        catch (JsonException)
        {
            return NyxIdServiceInstanceClient.Error("invalid_arguments");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return NyxIdServiceInstanceClient.Error("invalid_arguments");
            var root = document.RootElement;
            var userServiceId = ReadScalar(root, "user_service_id");
            if (string.IsNullOrWhiteSpace(userServiceId) || !_bindings.TryGetValue(userServiceId, out var binding))
                return NyxIdServiceInstanceClient.Error("identity_not_authorized");
            if (!TryBuildPath(root, out var path, out var error))
                return NyxIdServiceInstanceClient.Error(error!);
            if (!TryBuildEntries(root, ParameterLocation.Query, out var query, out error))
                return NyxIdServiceInstanceClient.Error(error!);
            if (!TryBuildEntries(root, ParameterLocation.Header, out var headers, out error))
                return NyxIdServiceInstanceClient.Error(error!);
            var body = _operation.BodyPropertyName is { } bodyName ? ReadRaw(root, bodyName) : null;
            if (_operation.RequestBodyRequired && body is null)
                return NyxIdServiceInstanceClient.Error("missing_required_body");

            var request = new NyxIdServiceRequest
            {
                UserServiceId = binding.Instance.UserServiceId,
                Method = ParseMethod(_operation.Method),
                RelativePath = path,
            };
            request.Query.Add(query.Select(static entry =>
                new NyxIdServiceQueryEntry { Name = entry.Key, Value = entry.Value }));
            request.Headers.Add(headers.Select(static entry => new NyxIdServiceHeader
            {
                Name = ParseHeaderName(entry.Key),
                Value = entry.Value,
            }));
            if (body is not null)
                request.JsonBody = body;
            return JsonFormatter.Default.Format(await _client.RequestAsync(binding, request, ct));
        }
    }

    private string BuildParametersSchema()
    {
        var root = JsonNode.Parse(_operation.BuildParametersSchema())!.AsObject();
        var properties = root["properties"]!.AsObject();
        properties.Insert(0, "user_service_id", new JsonObject
        {
            ["type"] = "string",
            ["enum"] = new JsonArray(_bindings.Keys.Order(StringComparer.Ordinal)
                .Select(static id => JsonValue.Create(id)).ToArray()),
        });
        var required = root["required"] as JsonArray ?? [];
        required.Insert(0, "user_service_id");
        root["required"] = required;
        root["additionalProperties"] = false;
        return root.ToJsonString();
    }

    private bool TryBuildPath(JsonElement root, out string path, out string? error)
    {
        path = _operation.PathTemplate;
        error = null;
        foreach (var parameter in _operation.Parameters.Where(static parameter => parameter.In == ParameterLocation.Path))
        {
            var value = ReadScalar(root, parameter.Name);
            if (value is null)
            {
                error = "missing_required_path_parameter";
                return false;
            }
            path = path.Replace($"{{{parameter.Name}}}", Uri.EscapeDataString(value), StringComparison.Ordinal);
        }
        if (path.Contains('{') || path.Contains('}'))
        {
            error = "unresolved_path_template";
            return false;
        }
        return true;
    }

    private bool TryBuildEntries(
        JsonElement root,
        ParameterLocation location,
        out IReadOnlyList<KeyValuePair<string, string>> entries,
        out string? error)
    {
        var result = new List<KeyValuePair<string, string>>();
        foreach (var parameter in _operation.Parameters.Where(parameter => parameter.In == location))
        {
            var value = ReadScalar(root, parameter.Name);
            if (value is null)
            {
                if (parameter.Required)
                {
                    entries = [];
                    error = location == ParameterLocation.Header
                        ? "missing_required_header"
                        : "missing_required_query_parameter";
                    return false;
                }
                continue;
            }
            result.Add(new KeyValuePair<string, string>(parameter.Name, value));
        }
        entries = result;
        error = null;
        return true;
    }

    private static string? ReadScalar(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
    }

    private static string? ReadRaw(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetRawText()
            : null;

    private static NyxIdServiceHttpMethod ParseMethod(string method) => method switch
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

    private static NyxIdServiceHeaderName ParseHeaderName(string name) => name.ToUpperInvariant() switch
    {
        "ACCEPT" => NyxIdServiceHeaderName.Accept,
        "CONTENT-TYPE" => NyxIdServiceHeaderName.ContentType,
        "IF-MATCH" => NyxIdServiceHeaderName.IfMatch,
        "IF-NONE-MATCH" => NyxIdServiceHeaderName.IfNoneMatch,
        _ => NyxIdServiceHeaderName.Unspecified,
    };
}
