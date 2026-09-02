using System.Security.Claims;
using System.Text.Json;
using Aevatar.Capabilities;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Mainnet.Host.Api.Chat;

internal static class ExternalWorkflowChatCompatibilityAdapter
{
    public static bool AcceptsForm(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.HasFormContentType;
    }

    public static bool AcceptsJson(JsonElement body) =>
        body.ValueKind == JsonValueKind.Object && !body.TryGetProperty("type", out _);

    public static async Task HandleAsync(HttpContext http, JsonElement? body, CancellationToken ct)
    {
        var workflowId = await TryReadWorkflowIdAsync(http.Request, body, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(workflowId) &&
            !await TryEnsureConfiguredTemplateAsync(http, workflowId, ct).ConfigureAwait(false))
        {
            return;
        }

        var resolved = body.HasValue && !string.IsNullOrWhiteSpace(workflowId)
            ? await TryResolveConfiguredTemplateChatInputAsync(http, body.Value, workflowId, ct).ConfigureAwait(false)
            : null;
        if (resolved != null)
        {
            await WorkflowCapabilityEndpoints.HandleHttpChat(
                    http,
                    resolved.Input,
                    http.RequestServices.GetRequiredService<IWorkflowChatRunInteractionPort>(),
                    ct,
                    resolved.DefinitionBinding)
                .ConfigureAwait(false);
            return;
        }

        await WorkflowCapabilityEndpoints.HandleChatPostAsync(http, ct).ConfigureAwait(false);
    }

    private static async Task<ResolvedTemplateChatInput?> TryResolveConfiguredTemplateChatInputAsync(
        HttpContext http,
        JsonElement body,
        string workflowId,
        CancellationToken ct)
    {
        if (body.ValueKind != JsonValueKind.Object ||
            !AevatarScopeAccessGuard.TryGetCallerScopeId(http, out var scopeId))
        {
            return null;
        }

        var bindingResolvePort = http.RequestServices.GetService<IScopeWorkflowDefinitionBindingResolvePort>();
        if (bindingResolvePort is null)
            return null;

        var resolvedBinding = await bindingResolvePort.ResolveAsync(
                new ScopeWorkflowDefinitionBindingResolveRequest(scopeId, workflowId),
                ct)
            .ConfigureAwait(false);
        if (!resolvedBinding.Succeeded)
            return null;

        HttpChatInput? input;
        try
        {
            input = body.Deserialize<HttpChatInput>(ChatJsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        return input == null
            ? null
            : new ResolvedTemplateChatInput(
                input with { Workflow = resolvedBinding.DefinitionBinding!.WorkflowName },
                resolvedBinding.DefinitionBinding);
    }

    private static async Task<bool> TryEnsureConfiguredTemplateAsync(
        HttpContext http,
        string workflowId,
        CancellationToken ct)
    {
        if (!AevatarScopeAccessGuard.TryGetCallerScopeId(http, out var scopeId))
            return true;

        var ensurePort = http.RequestServices.GetService<IScopeWorkflowTemplateEnsurePort>();
        if (ensurePort is null)
            return true;

        var callerCredential = await WorkflowCallerCredentialExtractor.ExtractAsync(http, ct).ConfigureAwait(false);
        var capabilityAdmission = callerCredential.Succeeded
            ? ToAdmissionContext(http, callerCredential.Credential)
            : null;

        var result = await ensurePort.EnsureAsync(
                new ScopeWorkflowTemplateEnsureRequest(scopeId, workflowId)
                {
                    CapabilityAdmission = capabilityAdmission,
                },
                ct)
            .ConfigureAwait(false);
        if (result.Status != ScopeWorkflowTemplateEnsureStatus.Failed)
            return true;

        http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await http.Response.WriteAsJsonAsync(new
        {
            code = "WORKFLOW_TEMPLATE_ENSURE_FAILED",
            message = "Configured workflow template could not be materialized for the authenticated scope.",
            reason = result.Reason,
        }, cancellationToken: ct).ConfigureAwait(false);
        return false;
    }

    private static async Task<string> TryReadWorkflowIdAsync(
        HttpRequest request,
        JsonElement? body,
        CancellationToken ct)
    {
        if (body.HasValue)
            return TryReadWorkflowId(body.Value, out var workflowId) ? workflowId : string.Empty;

        if (!request.HasFormContentType)
            return string.Empty;

        var form = await request.ReadFormAsync(ct).ConfigureAwait(false);
        return form.TryGetValue("workflow", out var values)
            ? values.FirstOrDefault()?.Trim() ?? string.Empty
            : string.Empty;
    }

    private static bool TryReadWorkflowId(JsonElement body, out string workflowId)
    {
        workflowId = string.Empty;
        if (!body.TryGetProperty("workflow", out var workflow) || workflow.ValueKind != JsonValueKind.String)
            return false;

        workflowId = workflow.GetString()?.Trim() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(workflowId);
    }

    private static WorkflowCapabilityAdmissionContext? ToAdmissionContext(
        HttpContext http,
        Aevatar.Workflow.Application.Abstractions.Runs.WorkflowCallerCredential? callerCredential)
    {
        var sourceReadableBearerToken = callerCredential?.Kind == NyxIdCallerCredentialKind.SourceReadableUserBearer
            ? callerCredential.BearerToken
            : callerCredential?.SourceReadableUserBearerToken;
        if (string.IsNullOrWhiteSpace(sourceReadableBearerToken))
            return null;

        return new WorkflowCapabilityAdmissionContext(
            ResolveCallerId(http),
            NyxIdCallerCredentialSelection.SourceReadableUserBearer(sourceReadableBearerToken),
            executionMode: ExternalCapabilityExecutionMode.Interactive);
    }

    private static string ResolveCallerId(HttpContext http) =>
        http.User.FindFirst("uid")?.Value?.Trim()
        ?? http.User.FindFirst("user_id")?.Value?.Trim()
        ?? http.User.FindFirst("sub")?.Value?.Trim()
        ?? http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value?.Trim()
        ?? string.Empty;

    private static readonly JsonSerializerOptions ChatJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed record ResolvedTemplateChatInput(
        HttpChatInput Input,
        WorkflowDefinitionBinding DefinitionBinding);
}
