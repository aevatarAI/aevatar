using System.Text.Json;
using System.Text.Json.Nodes;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Google.Protobuf;

namespace Aevatar.AI.ToolProviders.Binding.Tools;

public abstract class ExternalWorkflowCapabilityReadOnlyTool : IAgentTool
{
    public abstract string Name { get; }

    public abstract string Description { get; }

    public abstract string ParametersSchema { get; }

    public bool IsReadOnly => true;

    public AgentToolReceipt? CreateResultReceipt(
        string callId,
        string toolName,
        string argumentsJson,
        string resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson))
            return null;

        try
        {
            using var document = JsonDocument.Parse(resultJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            if (root.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.String)
            {
                return new AgentToolReceipt
                {
                    CallId = callId ?? string.Empty,
                    ToolName = ResolveToolName(toolName),
                    Status = AgentToolReceiptStatus.Error,
                    ApprovalMode = AgentToolReceiptApprovalMode.NeverRequire,
                    ErrorCode = "external_workflow_capability_query_failed",
                    ErrorMessage = "External workflow capability query failed.",
                    ResultJson = resultJson,
                };
            }

            if (!IsVerifiedResult(root))
                return null;

            return new AgentToolReceipt
            {
                CallId = callId ?? string.Empty,
                ToolName = ResolveToolName(toolName),
                Status = AgentToolReceiptStatus.Success,
                ApprovalMode = AgentToolReceiptApprovalMode.NeverRequire,
                ResultJson = resultJson,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public abstract Task<string> ExecuteAsync(
        string argumentsJson,
        CancellationToken ct = default);

    protected abstract bool IsVerifiedResult(JsonElement result);

    private string ResolveToolName(string? toolName) =>
        string.IsNullOrWhiteSpace(toolName) ? Name : toolName;
}

internal static class ExternalWorkflowCapabilityToolSupport
{
    public static readonly JsonFormatter ProtoJsonFormatter = new(
        JsonFormatter.Settings.Default
            .WithFormatDefaultValues(false)
            .WithPreserveProtoFieldNames(true));

    public static JsonElement ToProtoJsonElement(IMessage message)
    {
        using var document = JsonDocument.Parse(ProtoJsonFormatter.Format(message));
        return document.RootElement.Clone();
    }

    public static JsonNode? ToProtoJsonNode(IMessage? message) =>
        message is null
            ? null
            : JsonNode.Parse(ProtoJsonFormatter.Format(message));

    public static JsonObject? BuildAuthoringSelectorNode(ExternalWorkflowCapabilitySelector? selector)
    {
        if (selector is null)
            return null;

        return selector.SelectorCase switch
        {
            ExternalWorkflowCapabilitySelector.SelectorOneofCase.HostConnector =>
                new JsonObject
                {
                    ["host_connector"] = BuildHostConnectorSelectorNode(selector.HostConnector),
                },
            ExternalWorkflowCapabilitySelector.SelectorOneofCase.NyxIdOperation =>
                new JsonObject
                {
                    ["nyxid_operation"] = new JsonObject
                    {
                        ["user_service_id"] = selector.NyxIdOperation.UserServiceId,
                        ["endpoint_id"] = selector.NyxIdOperation.EndpointId,
                    },
                },
            ExternalWorkflowCapabilitySelector.SelectorOneofCase.NyxIdRequest =>
                new JsonObject
                {
                    ["nyxid_request"] = BuildNyxIdRequestSelectorNode(selector.NyxIdRequest),
                },
            ExternalWorkflowCapabilitySelector.SelectorOneofCase.CodeExecution =>
                new JsonObject
                {
                    ["code_execution"] = new JsonObject(),
                },
            _ => null,
        };
    }

    public static bool TryResolveAccess(
        out ExternalWorkflowCapabilityAccessContext? access,
        out string? error)
    {
        var scopeId = ToolOwnerScopeResolver.Resolve();
        if (scopeId is null)
        {
            access = null;
            error = ToolOwnerScopeResolver.MissingMessage;
            return false;
        }

        var authority = AgentToolRequestContext.NyxIdAuthority;
        var callerId = Normalize(authority.IsComplete ? authority.ExternalUserId : null);
        if (callerId is null)
        {
            access = null;
            error = "verified caller identity not available in request context";
            return false;
        }

        var credentials = AgentToolRequestContext.Current?.Credentials;
        access = new ExternalWorkflowCapabilityAccessContext(
            scopeId,
            callerId,
            ResolveCallerCredential(credentials),
            AgentToolRequestContext.NyxIdOrgToken);
        error = null;
        return true;
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static JsonObject BuildHostConnectorSelectorNode(HostConnectorCapabilityRef selector)
    {
        var selectorObject = new JsonObject();
        AddStringIfPresent(selectorObject, "connector_capability_ref", selector.ConnectorCapabilityRef);
        AddStringIfPresent(selectorObject, "operation_id", selector.OperationId);
        AddStringIfPresent(selectorObject, "contract_digest", selector.ContractDigest);
        return selectorObject;
    }

    private static JsonObject BuildNyxIdRequestSelectorNode(NyxIdRequestSelector request)
    {
        var selectorObject = new JsonObject
        {
            ["user_service_id"] = request.UserServiceId,
            ["method"] = FormatNyxIdRequestMethod(request.Method),
            ["path_template"] = request.PathTemplate,
            ["query_parameters"] = ToJsonArray(request.QueryParameters),
            ["header_parameters"] = ToJsonArray(request.HeaderParameters),
            ["body_mode"] = FormatNyxIdRequestBodyMode(request.BodyMode),
            ["body_required"] = request.BodyRequired,
            ["response_mode"] = FormatNyxIdRequestResponseMode(request.ResponseMode),
        };
        return selectorObject;
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
            array.Add(value);
        return array;
    }

    private static void AddStringIfPresent(JsonObject target, string propertyName, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            target[propertyName] = value;
    }

    private static string FormatNyxIdRequestMethod(NyxIdRequestMethod method) => method switch
    {
        NyxIdRequestMethod.Get => "GET",
        NyxIdRequestMethod.Head => "HEAD",
        NyxIdRequestMethod.Options => "OPTIONS",
        NyxIdRequestMethod.Post => "POST",
        NyxIdRequestMethod.Put => "PUT",
        NyxIdRequestMethod.Patch => "PATCH",
        NyxIdRequestMethod.Delete => "DELETE",
        _ => "UNSPECIFIED",
    };

    private static string FormatNyxIdRequestBodyMode(NyxIdRequestBodyMode mode) => mode switch
    {
        NyxIdRequestBodyMode.None => "none",
        NyxIdRequestBodyMode.Json => "json",
        _ => "unspecified",
    };

    private static string FormatNyxIdRequestResponseMode(NyxIdRequestResponseMode mode) => mode switch
    {
        NyxIdRequestResponseMode.Text => "text",
        NyxIdRequestResponseMode.FileArtifact => "file_artifact",
        _ => "unspecified",
    };

    private static NyxIdCallerCredentialSelection? ResolveCallerCredential(
        AgentToolCredentials? credentials)
    {
        var sourceReadableBearerToken =
            AgentToolSourceReadableNyxIdCredential.ResolveBearerToken(credentials);
        if (sourceReadableBearerToken is not null)
        {
            return NyxIdCallerCredentialSelection.SourceReadableUserBearer(
                sourceReadableBearerToken);
        }

        var bearerToken = credentials?.NyxIdAccessToken;
        return string.IsNullOrWhiteSpace(bearerToken)
            ? null
            : credentials!.NyxIdCredentialKind switch
            {
                AgentToolNyxIdCredentialKind.SourceReadableUserBearer =>
                    NyxIdCallerCredentialSelection.SourceReadableUserBearer(bearerToken),
                AgentToolNyxIdCredentialKind.ProxyDelegation =>
                    NyxIdCallerCredentialSelection.ProxyDelegation(bearerToken),
                _ => null,
            };
    }
}
