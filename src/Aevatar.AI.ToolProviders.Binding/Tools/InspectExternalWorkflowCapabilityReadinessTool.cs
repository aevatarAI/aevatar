using System.Text.Json;
using System.Text.Json.Nodes;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Google.Protobuf;

namespace Aevatar.AI.ToolProviders.Binding.Tools;

public sealed class InspectExternalWorkflowCapabilityReadinessTool : ExternalWorkflowCapabilityReadOnlyTool
{
    private readonly IExternalWorkflowCapabilityReadinessPort _readinessPort;

    public InspectExternalWorkflowCapabilityReadinessTool(
        IExternalWorkflowCapabilityReadinessPort readinessPort)
    {
        _readinessPort = readinessPort;
    }

    public override string Name => "inspect_external_workflow_capability_readiness";

    public override string Description =>
        "Inspect point-in-time readiness for one exact workflow capability and execution mode. " +
        "Returns typed blockers and trusted remediation locators without returning credentials. " +
        "This selector diagnostic does not allocate a workflow revision or return bind confirmations, " +
        "so it must not replace preview_workflow_explicit_requests for authored workflow YAML.";

    public override string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "selector": {
              "type": "object",
              "description": "Exact selector object from list_external_workflow_capabilities. NyxID selector fields use workflow YAML names.",
              "properties": {
                "host_connector": { "type": "object" },
                "nyxid_operation": {
                  "type": "object",
                  "properties": {
                    "user_service_id": { "type": "string" },
                    "endpoint_id": { "type": "string" }
                  },
                  "required": ["user_service_id", "endpoint_id"],
                  "additionalProperties": false
                },
                "nyxid_request": {
                  "type": "object",
                  "description": "Canonical NyxID UserService HTTP request authored from an official API contract when no exact operation descriptor exists",
                  "properties": {
                    "user_service_id": { "type": "string" },
                    "method": {
                      "type": "string",
                      "enum": ["GET", "HEAD", "OPTIONS", "POST", "PUT", "PATCH", "DELETE"]
                    },
                    "path_template": { "type": "string" },
                    "query_parameters": {
                      "type": "array",
                      "items": { "type": "string" }
                    },
                    "header_parameters": {
                      "type": "array",
                      "items": { "type": "string" }
                    },
                    "body_mode": {
                      "type": "string",
                      "enum": ["none", "json"]
                    },
                    "body_required": { "type": "boolean" },
                    "response_mode": {
                      "type": "string",
                      "enum": ["text", "file_artifact"]
                    }
                  },
                  "required": [
                    "user_service_id",
                    "method",
                    "path_template",
                    "query_parameters",
                    "header_parameters",
                    "body_mode",
                    "body_required",
                    "response_mode"
                  ],
                  "additionalProperties": false
                }
              },
              "minProperties": 1,
              "maxProperties": 1
            },
            "execution_mode": {
              "type": "string",
              "enum": ["interactive", "durable"]
            }
          },
          "required": ["selector", "execution_mode"],
          "additionalProperties": false
        }
        """;

    public override async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        try
        {
            var args = ToolArgs.Parse(argumentsJson);
            if (args.ParseError is not null)
                return JsonDefaults.Error(args.ParseError);

            if (!TryParseExecutionMode(args.Str("execution_mode"), out var executionMode))
                return JsonDefaults.Error("execution_mode must be interactive or durable");

            var selectorJson = args.RawOrStr("selector");
            if (string.IsNullOrWhiteSpace(selectorJson))
                return JsonDefaults.Error("selector is required");

            ExternalWorkflowCapabilitySelector selector;
            try
            {
                selector = ParseSelector(selectorJson);
            }
            catch (InvalidProtocolBufferException)
            {
                return JsonDefaults.Error("selector must be an exact typed capability selector");
            }

            if (selector.SelectorCase == ExternalWorkflowCapabilitySelector.SelectorOneofCase.None)
                return JsonDefaults.Error("selector must select exactly one capability kind");

            if (!ExternalWorkflowCapabilityToolSupport.TryResolveAccess(out var access, out var error))
                return JsonDefaults.Error(error!);

            var readiness = await _readinessPort.InspectAsync(
                new InspectExternalWorkflowCapabilityReadinessRequest(
                    access!,
                    selector,
                    executionMode),
                ct);
            return FormatReadiness(readiness);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return JsonDefaults.Error($"External capability readiness inspection failed: {exception.GetType().Name}");
        }
    }

    private static string FormatReadiness(ExternalCapabilityReadiness readiness)
    {
        var readinessNode = ExternalWorkflowCapabilityToolSupport.ToProtoJsonNode(readiness);
        if (readinessNode is not JsonObject readinessObject)
            return ExternalWorkflowCapabilityToolSupport.ProtoJsonFormatter.Format(readiness);

        var authoringSelector = ExternalWorkflowCapabilityToolSupport.BuildAuthoringSelectorNode(readiness.SelectedSelector);
        if (authoringSelector is not null)
            readinessObject["selected_selector"] = authoringSelector;

        return readinessObject.ToJsonString();
    }

    private static ExternalWorkflowCapabilitySelector ParseSelector(string selectorJson)
    {
        var selectorNode = JsonNode.Parse(selectorJson);
        if (selectorNode is JsonObject selectorObject)
        {
            NormalizeAuthoringSelectorProperty(selectorObject, "nyxid_operation", "nyx_id_operation");
            NormalizeAuthoringSelectorProperty(selectorObject, "nyxid_request", "nyx_id_request");
            if (selectorObject["nyx_id_request"] is JsonObject requestObject)
            {
                NormalizeEnum(
                    requestObject,
                    "method",
                    static value => value.Trim().ToUpperInvariant() switch
                    {
                        "GET" => "NYX_ID_REQUEST_METHOD_GET",
                        "HEAD" => "NYX_ID_REQUEST_METHOD_HEAD",
                        "OPTIONS" => "NYX_ID_REQUEST_METHOD_OPTIONS",
                        "POST" => "NYX_ID_REQUEST_METHOD_POST",
                        "PUT" => "NYX_ID_REQUEST_METHOD_PUT",
                        "PATCH" => "NYX_ID_REQUEST_METHOD_PATCH",
                        "DELETE" => "NYX_ID_REQUEST_METHOD_DELETE",
                        _ => value,
                    });
                NormalizeEnum(
                    requestObject,
                    "body_mode",
                    static value => value.Trim().ToLowerInvariant() switch
                    {
                        "none" => "NYX_ID_REQUEST_BODY_MODE_NONE",
                        "json" => "NYX_ID_REQUEST_BODY_MODE_JSON",
                        _ => value,
                    });
                NormalizeEnum(
                    requestObject,
                    "response_mode",
                    static value => value.Trim().ToLowerInvariant() switch
                    {
                        "text" => "NYX_ID_REQUEST_RESPONSE_MODE_TEXT",
                        "file_artifact" => "NYX_ID_REQUEST_RESPONSE_MODE_FILE_ARTIFACT",
                        _ => value,
                    });
            }

            selectorJson = selectorObject.ToJsonString();
        }

        return JsonParser.Default.Parse<ExternalWorkflowCapabilitySelector>(selectorJson);
    }

    private static void NormalizeAuthoringSelectorProperty(
        JsonObject selectorObject,
        string authoringPropertyName,
        string protoPropertyName)
    {
        if (!selectorObject.ContainsKey(authoringPropertyName) ||
            selectorObject.ContainsKey(protoPropertyName))
        {
            return;
        }

        var value = selectorObject[authoringPropertyName];
        selectorObject.Remove(authoringPropertyName);
        selectorObject[protoPropertyName] = value;
    }

    private static void NormalizeEnum(
        JsonObject requestObject,
        string propertyName,
        Func<string, string> normalize)
    {
        if (requestObject[propertyName] is JsonValue value &&
            value.TryGetValue<string>(out var text))
        {
            requestObject[propertyName] = normalize(text);
        }
    }

    protected override bool IsVerifiedResult(JsonElement result) =>
        result.TryGetProperty("execution_mode", out var executionMode) &&
        executionMode.ValueKind == JsonValueKind.String &&
        result.TryGetProperty("status", out var status) &&
        status.ValueKind == JsonValueKind.String &&
        result.TryGetProperty("selected_selector", out var selectedSelector) &&
        selectedSelector.ValueKind == JsonValueKind.Object;

    private static bool TryParseExecutionMode(
        string? value,
        out ExternalCapabilityExecutionMode executionMode)
    {
        executionMode = value?.Trim().ToLowerInvariant() switch
        {
            "interactive" => ExternalCapabilityExecutionMode.Interactive,
            "durable" => ExternalCapabilityExecutionMode.Durable,
            _ => ExternalCapabilityExecutionMode.Unspecified,
        };
        return executionMode != ExternalCapabilityExecutionMode.Unspecified;
    }
}
