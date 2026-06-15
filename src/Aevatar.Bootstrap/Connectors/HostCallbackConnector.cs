using System.Text.Json;
using System.Text.Json.Nodes;
using Aevatar.Foundation.Abstractions.Connectors;

namespace Aevatar.Bootstrap.Connectors;

/// <summary>
/// Host-owned callback connector that returns structured JSON payloads to workflow steps.
/// </summary>
public sealed class HostCallbackConnector : IConnector
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _handlerName;
    private readonly IHostCallbackConnectorHandler _handler;
    private readonly HashSet<string> _allowedOperations;
    private readonly HashSet<string> _allowedInputKeys;

    public HostCallbackConnector(
        string name,
        string handlerName,
        IHostCallbackConnectorHandler handler,
        IEnumerable<string>? allowedOperations = null,
        IEnumerable<string>? allowedInputKeys = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("name is required", nameof(name));
        if (string.IsNullOrWhiteSpace(handlerName))
            throw new ArgumentException("handlerName is required", nameof(handlerName));

        Name = name;
        _handlerName = handlerName.Trim();
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _allowedOperations = new HashSet<string>(allowedOperations ?? [], StringComparer.OrdinalIgnoreCase);
        _allowedInputKeys = new HashSet<string>(allowedInputKeys ?? [], StringComparer.OrdinalIgnoreCase);
    }

    public string Name { get; }

    public string Type => "host_callback";

    public async Task<ConnectorResponse> ExecuteAsync(ConnectorRequest request, CancellationToken ct = default)
    {
        var operation = request.Operation.Trim();
        if (_allowedOperations.Count > 0)
        {
            if (string.IsNullOrWhiteSpace(operation))
            {
                return new ConnectorResponse
                {
                    Success = false,
                    Error = "operation is required for host_callback",
                    Metadata = new Dictionary<string, string>
                    {
                        ["host_callback.handler"] = _handlerName,
                    },
                };
            }

            if (!_allowedOperations.Contains(operation))
            {
                return new ConnectorResponse
                {
                    Success = false,
                    Error = $"operation '{operation}' is not allowed",
                    Metadata = new Dictionary<string, string>
                    {
                        ["host_callback.handler"] = _handlerName,
                        ["host_callback.operation"] = operation,
                    },
                };
            }
        }

        if (_allowedInputKeys.Count > 0 && !TryValidatePayloadKeys(request.Payload, _allowedInputKeys, out var schemaError))
        {
            return new ConnectorResponse
            {
                Success = false,
                Error = schemaError,
                Metadata = new Dictionary<string, string>
                {
                    ["host_callback.handler"] = _handlerName,
                    ["host_callback.operation"] = operation,
                },
            };
        }

        try
        {
            var response = await _handler.HandleAsync(
                new HostCallbackConnectorRequest
                {
                    ConnectorName = Name,
                    HandlerName = _handlerName,
                    Operation = operation,
                    RunId = request.RunId,
                    StepId = request.StepId,
                    Payload = request.Payload ?? string.Empty,
                    Parameters = request.Parameters,
                    Metadata = request.Metadata,
                },
                ct);

            var metadata = new Dictionary<string, string>(response.Metadata, StringComparer.Ordinal)
            {
                ["host_callback.handler"] = _handlerName,
            };
            if (!string.IsNullOrWhiteSpace(operation))
                metadata["host_callback.operation"] = operation;

            var output = string.Empty;
            if (response.Result != null)
            {
                output = response.Result.ToJsonString(JsonOptions);
                FlattenResult(response.Result, "host_callback.result", metadata);
            }

            return new ConnectorResponse
            {
                Success = response.Success,
                Output = output,
                Error = response.Success ? string.Empty : response.Error ?? string.Empty,
                Metadata = metadata,
            };
        }
        catch (Exception ex)
        {
            return new ConnectorResponse
            {
                Success = false,
                Error = ex.Message,
                Metadata = new Dictionary<string, string>
                {
                    ["host_callback.handler"] = _handlerName,
                    ["host_callback.operation"] = operation,
                },
            };
        }
    }

    private static void FlattenResult(
        JsonNode node,
        string path,
        IDictionary<string, string> metadata)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var property in obj)
                {
                    if (property.Value == null)
                    {
                        metadata[$"{path}.{property.Key}"] = "null";
                        continue;
                    }

                    FlattenResult(property.Value, $"{path}.{property.Key}", metadata);
                }
                return;
            case JsonArray array:
                for (var index = 0; index < array.Count; index++)
                {
                    var item = array[index];
                    if (item == null)
                    {
                        metadata[$"{path}.{index}"] = "null";
                        continue;
                    }

                    FlattenResult(item, $"{path}.{index}", metadata);
                }
                return;
            case JsonValue value:
                metadata[path] = FlattenScalar(value);
                return;
            default:
                metadata[path] = node.ToJsonString(JsonOptions);
                return;
        }
    }

    private static string FlattenScalar(JsonValue value)
    {
        if (value.TryGetValue<string>(out var text))
            return text ?? string.Empty;
        if (value.TryGetValue<bool>(out var boolean))
            return boolean ? "true" : "false";
        if (value.TryGetValue<int>(out var integer))
            return integer.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (value.TryGetValue<long>(out var longInteger))
            return longInteger.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (value.TryGetValue<double>(out var number))
            return number.ToString("G17", System.Globalization.CultureInfo.InvariantCulture);

        return value.ToJsonString(JsonOptions);
    }

    private static bool TryValidatePayloadKeys(string payload, HashSet<string> allowedKeys, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(payload))
            return true;

        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "payload schema violation: expected JSON object";
                return false;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!allowedKeys.Contains(property.Name))
                {
                    error = $"payload schema violation: key '{property.Name}' is not allowlisted";
                    return false;
                }
            }

            return true;
        }
        catch (JsonException)
        {
            error = "payload schema violation: invalid JSON";
            return false;
        }
    }
}
