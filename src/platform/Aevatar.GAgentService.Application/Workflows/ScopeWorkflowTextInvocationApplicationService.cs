using Aevatar.AI.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Google.Protobuf.WellKnownTypes;
using CallerCredential = Aevatar.Workflow.Application.Abstractions.Runs.WorkflowCallerCredential;

namespace Aevatar.GAgentService.Application.Workflows;

public sealed class ScopeWorkflowTextInvocationApplicationService(
    IScopeWorkflowQueryPort workflowQueryPort,
    IServiceInvocationPort invocationPort) : IScopeWorkflowTextInvocationPort
{
    public async Task<ServiceInvocationAcceptedReceipt> InvokeAsync(
        ScopeWorkflowTextInvocationRequest request,
        CallerCredential? callerCredential,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);
        var scopeId = ScopeWorkflowCapabilityOptions.NormalizeRequired(request.ScopeId, nameof(request.ScopeId));
        var workflowId = ScopeWorkflowCapabilityConventions.NormalizeWorkflowId(request.WorkflowId);
        var lookup = await workflowQueryPort.LookupByWorkflowIdAsync(scopeId, workflowId, ct);
        if (!lookup.IsRunnable)
            throw new ScopeWorkflowInvocationLookupException(scopeId, workflowId, lookup);

        var workflow = lookup.Workflow!;
        if (!string.Equals(workflow.ScopeId, scopeId, StringComparison.Ordinal) ||
            !string.Equals(workflow.WorkflowId, workflowId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(workflow.ServiceAppId) ||
            string.IsNullOrWhiteSpace(workflow.ServiceNamespace) ||
            string.IsNullOrWhiteSpace(workflow.PublishedServiceId))
        {
            throw new InvalidOperationException("Workflow lookup did not provide a matching explicit published service identity.");
        }

        var revisionId = string.IsNullOrWhiteSpace(request.RevisionId)
            ? workflow.ActiveRevisionId
            : request.RevisionId.Trim();
        if (string.IsNullOrWhiteSpace(revisionId))
            throw new InvalidOperationException("Workflow lookup did not provide an active revision.");

        return await invocationPort.InvokeAsync(new ServiceInvocationRequest
        {
            Identity = new ServiceIdentity
            {
                TenantId = scopeId,
                AppId = workflow.ServiceAppId,
                Namespace = workflow.ServiceNamespace,
                ServiceId = workflow.PublishedServiceId,
            },
            EndpointId = "chat",
            RevisionId = revisionId,
            CommandId = request.CommandId?.Trim() ?? string.Empty,
            CorrelationId = request.CorrelationId?.Trim() ?? string.Empty,
            Caller = new ServiceInvocationCaller(),
            Payload = Any.Pack(BuildChatRequest(scopeId, request.Prompt, callerCredential)),
        }, ct);
    }

    private static ChatRequestEvent BuildChatRequest(string scopeId, string prompt, CallerCredential? credential)
    {
        var bearer = credential?.BearerToken?.Trim();
        var payload = new ChatRequestEvent
        {
            ScopeId = scopeId,
            Prompt = prompt,
            ConnectorHttpAuthorization = string.IsNullOrEmpty(bearer) ? string.Empty : $"Bearer {bearer}",
            CallerSourceReadableNyxIdBearerToken = credential?.SourceReadableUserBearerToken?.Trim() ?? string.Empty,
            CallerNyxIdCredentialKind = credential?.Kind switch
            {
                NyxIdCallerCredentialKind.SourceReadableUserBearer => AgentToolNyxIdCredentialKindPayload.SourceReadableUserBearer,
                NyxIdCallerCredentialKind.ProxyDelegation => AgentToolNyxIdCredentialKindPayload.ProxyDelegation,
                NyxIdCallerCredentialKind.AgentKey => AgentToolNyxIdCredentialKindPayload.AgentKey,
                _ => AgentToolNyxIdCredentialKindPayload.Unspecified,
            },
        };
        if (credential?.Kind == NyxIdCallerCredentialKind.ProxyDelegation && credential.NyxIdAuthority is { } authority)
        {
            payload.ToolContext = new AgentToolExecutionContextPayload
            {
                NyxIdAuthority = new AgentToolNyxIdAuthorityContextPayload
                {
                    Platform = authority.Platform,
                    Tenant = authority.Tenant,
                    ExternalUserId = authority.ExternalUserId,
                    Scope = authority.Scope,
                },
                SenderBinding = new AgentToolSenderBindingContextPayload { BindingId = authority.BindingId ?? string.Empty },
            };
        }

        return payload;
    }
}
