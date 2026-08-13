using Aevatar.Capabilities;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

/// <summary>
/// Scope-owned management of workflow webhook bindings. Bindings are data:
/// a scope member registers a route key -> workflow mapping here and the
/// ingress at /api/workflow-webhooks/{routeKey} resolves it dynamically —
/// no host configuration change or redeploy per workflow.
/// </summary>
internal static class WorkflowWebhookBindingEndpoints
{
    public static void Map(IEndpointRouteBuilder group)
    {
        group.MapPut("/scopes/{scopeId}/workflow-webhooks/{routeKey}", HandlePutAsync)
            .WithName("PutWorkflowWebhookBinding");
        group.MapGet("/scopes/{scopeId}/workflow-webhooks", HandleListAsync)
            .WithName("ListWorkflowWebhookBindings");
        group.MapDelete("/scopes/{scopeId}/workflow-webhooks/{routeKey}", HandleDeleteAsync)
            .WithName("DeleteWorkflowWebhookBinding");
    }

    public sealed record PutWorkflowWebhookBindingRequest(
        string? WorkflowName,
        string? SourceId,
        string? PromptTemplate,
        string? PromptJsonPath,
        string? DeliveryIdHeader,
        string? DeliveryIdJsonPath,
        string? HmacSecret,
        string? HmacSignatureHeader,
        string? HmacTimestampHeader,
        int? MaxTimestampSkewSeconds,
        string? DefinitionActorId = null,
        string? TargetRevisionId = null,
        string? PreviousHmacSecret = null);

    internal static async Task<IResult> HandlePutAsync(
        HttpContext http,
        string scopeId,
        string routeKey,
        PutWorkflowWebhookBindingRequest request,
        CancellationToken ct = default)
    {
        var scopeError = RequireCallerScope(http, scopeId);
        if (scopeError != null)
            return scopeError;

        var bindingStore = ResolveStore(http, out var storeUnavailable);
        if (bindingStore == null)
            return storeUnavailable!;

        var normalizedRoute = Normalize(routeKey);
        if (normalizedRoute == null)
            return BadRequest("WEBHOOK_ROUTE_REQUIRED", "Route key is required.");

        var workflowName = Normalize(request.WorkflowName);
        var definitionActorId = Normalize(request.DefinitionActorId);
        if (workflowName == null && definitionActorId == null)
            return BadRequest(
                "WEBHOOK_WORKFLOW_REQUIRED",
                "workflowName or definitionActorId is required.");

        // A definition-actor target is validated against the actor's own
        // committed binding: it must exist, be workflow-capable, belong to the
        // caller's scope, and (when pinned) match the expected revision. The
        // webhook payload itself can never choose or redirect the workflow.
        string? targetRevisionId = null;
        if (definitionActorId != null)
        {
            var bindingReader = http.RequestServices.GetService<IWorkflowActorBindingReader>();
            if (bindingReader == null)
                return Results.Json(
                    new
                    {
                        code = "WEBHOOK_TARGET_VALIDATION_UNAVAILABLE",
                        message = "Definition actor targets cannot be validated on this host.",
                    },
                    statusCode: StatusCodes.Status503ServiceUnavailable);

            var target = await bindingReader.GetAsync(definitionActorId, ct);
            if (target == null || !target.IsWorkflowCapable)
                return BadRequest("WEBHOOK_TARGET_NOT_FOUND", "Definition actor target was not found.");

            if (!string.Equals(target.ScopeId, scopeId, StringComparison.Ordinal))
                return Results.Json(
                    new
                    {
                        code = "WEBHOOK_TARGET_NOT_IN_SCOPE",
                        message = "Definition actor target belongs to another scope.",
                    },
                    statusCode: StatusCodes.Status403Forbidden);

            var expectedRevision = Normalize(request.TargetRevisionId);
            if (expectedRevision != null &&
                !string.Equals(expectedRevision, target.RevisionId, StringComparison.Ordinal))
            {
                return Results.Json(
                    new
                    {
                        code = "WEBHOOK_TARGET_REVISION_MISMATCH",
                        message = "Definition actor target revision does not match the expected revision.",
                    },
                    statusCode: StatusCodes.Status409Conflict);
            }

            targetRevisionId = Normalize(target.RevisionId);
            workflowName ??= Normalize(target.WorkflowName);
            if (workflowName == null)
                return BadRequest("WEBHOOK_TARGET_NOT_FOUND", "Definition actor target has no workflow name.");
        }

        if (Normalize(request.HmacSecret) == null)
            return BadRequest("WEBHOOK_SECRET_REQUIRED", "hmacSecret is required.");

        if (Normalize(request.PromptTemplate) == null && Normalize(request.PromptJsonPath) == null)
            return BadRequest(
                "WEBHOOK_PROMPT_MAPPING_REQUIRED",
                "promptTemplate or promptJsonPath is required.");

        if (Normalize(request.DeliveryIdHeader) == null && Normalize(request.DeliveryIdJsonPath) == null)
            return BadRequest(
                "WEBHOOK_DELIVERY_ID_MAPPING_REQUIRED",
                "deliveryIdHeader or deliveryIdJsonPath is required.");

        // Route keys are a global namespace shared by every scope: a route may
        // be (re)bound only by the scope that already owns it.
        var existing = await bindingStore.GetAsync(normalizedRoute, ct);
        if (existing != null && !string.Equals(existing.ScopeId, scopeId, StringComparison.Ordinal))
        {
            return Results.Json(
                new { code = "WEBHOOK_ROUTE_OWNED_BY_OTHER_SCOPE", message = "Route key is owned by another scope." },
                statusCode: StatusCodes.Status409Conflict);
        }

        var record = new WorkflowWebhookBindingRecord(
            RouteKey: normalizedRoute,
            ScopeId: scopeId,
            WorkflowName: workflowName!,
            SourceId: Normalize(request.SourceId),
            PromptTemplate: Normalize(request.PromptTemplate),
            PromptJsonPath: Normalize(request.PromptJsonPath),
            DeliveryIdHeader: Normalize(request.DeliveryIdHeader),
            DeliveryIdJsonPath: Normalize(request.DeliveryIdJsonPath),
            HmacSecret: request.HmacSecret!.Trim(),
            HmacSignatureHeader: Normalize(request.HmacSignatureHeader),
            HmacTimestampHeader: Normalize(request.HmacTimestampHeader),
            MaxTimestampSkewSeconds: request.MaxTimestampSkewSeconds is > 0 and <= 3600
                ? request.MaxTimestampSkewSeconds.Value
                : 300,
            UpdatedAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            DefinitionActorId: definitionActorId,
            TargetRevisionId: targetRevisionId,
            PreviousHmacSecret: Normalize(request.PreviousHmacSecret));
        await bindingStore.PutAsync(record, ct);

        return Results.Ok(ToView(record));
    }

    internal static async Task<IResult> HandleListAsync(
        HttpContext http,
        string scopeId,
        CancellationToken ct = default)
    {
        var scopeError = RequireCallerScope(http, scopeId);
        if (scopeError != null)
            return scopeError;

        var bindingStore = ResolveStore(http, out var storeUnavailable);
        if (bindingStore == null)
            return storeUnavailable!;

        var records = await bindingStore.ListByScopeAsync(scopeId, ct);
        return Results.Ok(new { bindings = records.Select(ToView).ToArray() });
    }

    internal static async Task<IResult> HandleDeleteAsync(
        HttpContext http,
        string scopeId,
        string routeKey,
        CancellationToken ct = default)
    {
        var scopeError = RequireCallerScope(http, scopeId);
        if (scopeError != null)
            return scopeError;

        var bindingStore = ResolveStore(http, out var storeUnavailable);
        if (bindingStore == null)
            return storeUnavailable!;

        var normalizedRoute = Normalize(routeKey);
        if (normalizedRoute == null)
            return BadRequest("WEBHOOK_ROUTE_REQUIRED", "Route key is required.");

        var existing = await bindingStore.GetAsync(normalizedRoute, ct);
        if (existing == null || !string.Equals(existing.ScopeId, scopeId, StringComparison.Ordinal))
            return Results.NotFound();

        await bindingStore.DeleteAsync(normalizedRoute, ct);
        return Results.NoContent();
    }

    // The secret is write-only: views prove its presence, never its value.
    private static object ToView(WorkflowWebhookBindingRecord record) => new
    {
        routeKey = record.RouteKey,
        scopeId = record.ScopeId,
        workflowName = record.WorkflowName,
        definitionActorId = record.DefinitionActorId,
        targetRevisionId = record.TargetRevisionId,
        sourceId = record.SourceId,
        promptTemplate = record.PromptTemplate,
        promptJsonPath = record.PromptJsonPath,
        deliveryIdHeader = record.DeliveryIdHeader,
        deliveryIdJsonPath = record.DeliveryIdJsonPath,
        hmacSecretSet = !string.IsNullOrWhiteSpace(record.HmacSecret),
        previousHmacSecretSet = !string.IsNullOrWhiteSpace(record.PreviousHmacSecret),
        hmacSignatureHeader = record.HmacSignatureHeader,
        hmacTimestampHeader = record.HmacTimestampHeader,
        maxTimestampSkewSeconds = record.MaxTimestampSkewSeconds,
        updatedAtUnixMs = record.UpdatedAtUnixMs,
    };

    private static IWorkflowWebhookBindingStore? ResolveStore(HttpContext http, out IResult? unavailable)
    {
        var store = http.RequestServices.GetService<IWorkflowWebhookBindingStore>();
        unavailable = store != null
            ? null
            : Results.Json(
                new
                {
                    code = "WEBHOOK_BINDING_STORE_UNAVAILABLE",
                    message = "Workflow webhook binding store is not configured on this host. " +
                        "The Redis-backed store requires WorkflowWebhookIngress:RedisConnectionString " +
                        "and WorkflowWebhookIngress:BindingSecretEncryptionKey.",
                },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        return store;
    }

    private static IResult? RequireCallerScope(HttpContext http, string scopeId)
    {
        if (!AevatarScopeAccessGuard.IsAuthenticationEnabled(http.RequestServices))
            return null;

        if (!AevatarScopeAccessGuard.TryGetCallerScopeId(http, out var callerScopeId))
            return Results.Unauthorized();

        return string.Equals(callerScopeId, scopeId, StringComparison.Ordinal)
            ? null
            : Results.Forbid();
    }

    private static IResult BadRequest(string code, string message) =>
        Results.Json(new { code, message }, statusCode: StatusCodes.Status400BadRequest);

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
