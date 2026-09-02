using System.Security.Claims;
using System.Text.Json;
using Aevatar.Capabilities;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Services;
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
        if (!await TryEnsureConfiguredTemplateAsync(http, body, ct).ConfigureAwait(false))
            return;

        var resolved = body.HasValue
            ? await TryResolveConfiguredTemplateChatInputAsync(http, body.Value, ct).ConfigureAwait(false)
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
        CancellationToken ct)
    {
        if (body.ValueKind != JsonValueKind.Object ||
            !TryReadWorkflowId(body, out var workflowId) ||
            !AevatarScopeAccessGuard.TryGetCallerScopeId(http, out var scopeId))
        {
            return null;
        }

        var workflowQueryPort = http.RequestServices.GetService<IScopeWorkflowQueryPort>();
        if (workflowQueryPort is null)
            return null;

        var lookup = await workflowQueryPort.LookupByWorkflowIdAsync(scopeId, workflowId, ct).ConfigureAwait(false);
        if (!lookup.IsRunnable)
            return null;

        var definitionBinding = await TryBuildDefinitionBindingAsync(http, lookup.Workflow!, ct).ConfigureAwait(false);
        if (definitionBinding is null)
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
            : new ResolvedTemplateChatInput(input with { Workflow = definitionBinding.WorkflowName }, definitionBinding);
    }

    private static async Task<WorkflowDefinitionBinding?> TryBuildDefinitionBindingAsync(
        HttpContext http,
        ScopeWorkflowSummary workflow,
        CancellationToken ct)
    {
        var bindingReader = http.RequestServices.GetService<IWorkflowActorBindingReader>();
        WorkflowActorBinding? binding = null;
        if (bindingReader != null && !string.IsNullOrWhiteSpace(workflow.ActorId))
            binding = await bindingReader.GetAsync(workflow.ActorId, ct).ConfigureAwait(false);

        if (binding?.HasDefinitionPayload == true)
        {
            return new WorkflowDefinitionBinding(
                binding.EffectiveDefinitionActorId,
                binding.WorkflowName,
                binding.WorkflowYaml,
                binding.InlineWorkflowYamls,
                binding.ExpectedExecutionMode,
                binding.ScopeId,
                WorkflowRunOrigins.AdHocChat,
                SourceKind: binding.SourceKind,
                CapabilityAdmissionPlan: binding.CapabilityAdmissionPlan?.Clone(),
                WorkflowId: workflow.WorkflowId,
                RevisionId: binding.RevisionId,
                ToolCatalogPolicyVersion: binding.ToolCatalogPolicyVersion);
        }

        var revisionCatalogReader = http.RequestServices.GetService<IServiceRevisionCatalogQueryReader>();
        if (revisionCatalogReader is null ||
            string.IsNullOrWhiteSpace(workflow.ServiceAppId) ||
            string.IsNullOrWhiteSpace(workflow.ServiceNamespace) ||
            string.IsNullOrWhiteSpace(workflow.PublishedServiceId) ||
            string.IsNullOrWhiteSpace(workflow.ActiveRevisionId))
        {
            return null;
        }

        var revisionCatalog = await revisionCatalogReader.GetAsync(BuildWorkflowServiceIdentity(workflow), ct)
            .ConfigureAwait(false);
        var artifact = revisionCatalog?.Revisions
            .FirstOrDefault(revision => string.Equals(revision.RevisionId, workflow.ActiveRevisionId, StringComparison.Ordinal))
            ?.PreparedArtifact
            ?.Clone();
        var workflowPlan = artifact?.DeploymentPlan?.WorkflowPlan;
        if (workflowPlan is null)
            return null;

        var bindingIdentity = WorkflowServiceDeploymentPlanIntegrity.ResolveBindingIdentity(
            artifact!,
            workflow.ActiveRevisionId);
        return new WorkflowDefinitionBinding(
            workflowPlan.DefinitionActorId,
            workflowPlan.WorkflowName,
            workflowPlan.WorkflowYaml,
            workflowPlan.InlineWorkflowYamls,
            workflowPlan.ExecutionMode,
            workflow.ScopeId,
            WorkflowRunOrigins.AdHocChat,
            SourceKind: "service_revision",
            CapabilityAdmissionPlan: workflowPlan.CapabilityAdmissionPlan?.Clone(),
            WorkflowId: string.IsNullOrWhiteSpace(bindingIdentity.WorkflowId) ? workflow.WorkflowId : bindingIdentity.WorkflowId,
            RevisionId: bindingIdentity.RevisionId,
            ToolCatalogPolicyVersion: workflowPlan.ToolCatalogPolicyVersion);
    }

    private static ServiceIdentity BuildWorkflowServiceIdentity(ScopeWorkflowSummary workflow) =>
        new()
        {
            TenantId = workflow.ScopeId.Trim(),
            AppId = workflow.ServiceAppId.Trim(),
            Namespace = workflow.ServiceNamespace.Trim(),
            ServiceId = workflow.PublishedServiceId.Trim(),
        };

    private static async Task<bool> TryEnsureConfiguredTemplateAsync(
        HttpContext http,
        JsonElement? body,
        CancellationToken ct)
    {
        if (!body.HasValue || body.Value.ValueKind != JsonValueKind.Object)
            return true;

        var json = body.Value;
        if (!TryReadWorkflowId(json, out var workflowId) ||
            !AevatarScopeAccessGuard.TryGetCallerScopeId(http, out var scopeId))
        {
            return true;
        }

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
