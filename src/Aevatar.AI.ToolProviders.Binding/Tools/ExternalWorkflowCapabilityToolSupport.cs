using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Google.Protobuf;

namespace Aevatar.AI.ToolProviders.Binding.Tools;

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
