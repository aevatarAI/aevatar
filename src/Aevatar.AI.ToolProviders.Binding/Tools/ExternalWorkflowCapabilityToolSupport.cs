using System.Text.Json;
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

        access = new ExternalWorkflowCapabilityAccessContext(
            scopeId,
            callerId,
            ResolveCallerCredential(
                AgentToolRequestContext.NyxIdCredentialKind,
                AgentToolRequestContext.NyxIdAccessToken),
            AgentToolRequestContext.NyxIdOrgToken);
        error = null;
        return true;
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static NyxIdCallerCredentialSelection? ResolveCallerCredential(
        AgentToolNyxIdCredentialKind kind,
        string? bearerToken) =>
        string.IsNullOrWhiteSpace(bearerToken)
            ? null
            : kind switch
            {
                AgentToolNyxIdCredentialKind.SourceReadableUserBearer =>
                    NyxIdCallerCredentialSelection.SourceReadableUserBearer(bearerToken),
                AgentToolNyxIdCredentialKind.ProxyDelegation =>
                    NyxIdCallerCredentialSelection.ProxyDelegation(bearerToken),
                _ => null,
            };
}
