using Aevatar.Capabilities;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Credentials;
using Aevatar.Workflow.Application.Abstractions.Runs;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProtoWorkflowCallerNyxIdAuthority = Aevatar.Workflow.Abstractions.WorkflowCallerNyxIdAuthority;

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
        string? PreviousHmacSecret = null,
        string? TimeZoneId = null,
        bool EnableUnattendedEffects = false);

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

        var normalizedRoute = WorkflowWebhookRoute.Normalize(routeKey);
        if (normalizedRoute == null)
            return BadRequest("WEBHOOK_ROUTE_REQUIRED", "Route key is required.");

        var definitionActorId = Normalize(request.DefinitionActorId);
        if (definitionActorId == null)
            return BadRequest(
                "WEBHOOK_EXACT_TARGET_REQUIRED",
                "definitionActorId is required for a dynamic webhook binding.");

        // Static bindings and dynamic bindings share one public route
        // namespace. A dynamic record must never shadow a host-owned route,
        // even when the static ingress flag is currently disabled.
        var configuredBindings = http.RequestServices
            .GetService<IOptions<WorkflowWebhookIngressOptions>>()?
            .Value.Bindings;
        if (configuredBindings?.Any(binding => string.Equals(
                WorkflowWebhookRoute.Normalize(binding.RouteKey),
                normalizedRoute,
                StringComparison.Ordinal)) == true)
        {
            return Results.Json(
                new
                {
                    code = "WEBHOOK_ROUTE_RESERVED_BY_HOST",
                    message = "Route key is reserved by host configuration.",
                },
                statusCode: StatusCodes.Status409Conflict);
        }

        // Validate the exact committed definition target before persisting the
        // pin. A run actor is intentionally not accepted: otherwise a binding
        // could inherit mutable run state instead of a revisioned definition.
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
        if (target == null)
            return BadRequest("WEBHOOK_TARGET_NOT_FOUND", "Definition actor target was not found.");

        if (target.ActorKind != WorkflowActorKind.Definition ||
            !string.Equals(target.ActorId, definitionActorId, StringComparison.Ordinal))
        {
            return BadRequest(
                "WEBHOOK_TARGET_NOT_DEFINITION",
                "Webhook targets must be workflow definition actors.");
        }

        if (!string.Equals(Normalize(target.ScopeId), Normalize(scopeId), StringComparison.Ordinal))
            return Results.Json(
                new
                {
                    code = "WEBHOOK_TARGET_NOT_IN_SCOPE",
                    message = "Definition actor target belongs to another scope.",
                },
                statusCode: StatusCodes.Status403Forbidden);

        var targetRevisionId = Normalize(target.RevisionId);
        if (targetRevisionId == null)
            return BadRequest(
                "WEBHOOK_TARGET_REVISION_REQUIRED",
                "Definition actor target has no committed revision.");

        var expectedRevision = Normalize(request.TargetRevisionId);
        if (expectedRevision != null &&
            !string.Equals(expectedRevision, targetRevisionId, StringComparison.Ordinal))
        {
            return Results.Json(
                new
                {
                    code = "WEBHOOK_TARGET_REVISION_MISMATCH",
                    message = "Definition actor target revision does not match the expected revision.",
                },
                statusCode: StatusCodes.Status409Conflict);
        }

        var workflowName = Normalize(target.WorkflowName);
        if (workflowName == null)
            return BadRequest("WEBHOOK_TARGET_NOT_FOUND", "Definition actor target has no workflow name.");

        var requestedWorkflowName = Normalize(request.WorkflowName);
        if (requestedWorkflowName != null &&
            !string.Equals(requestedWorkflowName, workflowName, StringComparison.OrdinalIgnoreCase))
        {
            return Results.Json(
                new
                {
                    code = "WEBHOOK_TARGET_WORKFLOW_MISMATCH",
                    message = "Definition actor workflow does not match workflowName.",
                },
                statusCode: StatusCodes.Status409Conflict);
        }

        ProtoWorkflowCallerNyxIdAuthority? callerAuthority = null;
        WorkflowUnattendedEffectAuthorization? unattendedAuthorization = null;
        if (request.EnableUnattendedEffects)
        {
            var authorization = await ResolveUnattendedAuthorizationAsync(
                http,
                normalizedRoute,
                scopeId,
                definitionActorId,
                target,
                targetRevisionId,
                ct);
            if (authorization.Error != null)
                return authorization.Error;

            callerAuthority = authorization.CallerAuthority;
            unattendedAuthorization = authorization.Authorization;
        }

        var hmacSecret = request.HmacSecret?.Trim();
        if (hmacSecret == null || Encoding.UTF8.GetByteCount(hmacSecret) < 32)
            return BadRequest(
                "WEBHOOK_SECRET_TOO_SHORT",
                "hmacSecret must contain at least 32 UTF-8 bytes.");

        var previousHmacSecret = Normalize(request.PreviousHmacSecret);
        if (previousHmacSecret != null && Encoding.UTF8.GetByteCount(previousHmacSecret) < 32)
            return BadRequest(
                "WEBHOOK_PREVIOUS_SECRET_TOO_SHORT",
                "previousHmacSecret must contain at least 32 UTF-8 bytes when supplied.");

        if (Normalize(request.PromptTemplate) == null && Normalize(request.PromptJsonPath) == null)
            return BadRequest(
                "WEBHOOK_PROMPT_MAPPING_REQUIRED",
                "promptTemplate or promptJsonPath is required.");

        var deliveryIdJsonPath = Normalize(request.DeliveryIdJsonPath);
        if (deliveryIdJsonPath == null || !WorkflowWebhookJsonPath.IsValid(deliveryIdJsonPath))
            return BadRequest(
                "WEBHOOK_DELIVERY_ID_MAPPING_REQUIRED",
                "deliveryIdJsonPath is required for a signed payload delivery id.");

        var promptJsonPath = Normalize(request.PromptJsonPath);
        if (promptJsonPath != null && !WorkflowWebhookJsonPath.IsValid(promptJsonPath))
            return BadRequest(
                "WEBHOOK_PROMPT_JSON_PATH_INVALID",
                "promptJsonPath is invalid.");

        var promptTemplate = Normalize(request.PromptTemplate);
        if (promptTemplate != null)
        {
            var templateValidation = WorkflowWebhookPromptTemplate.Validate(promptTemplate);
            if (!templateValidation.Succeeded)
                return BadRequest(templateValidation.ErrorCode!, templateValidation.ErrorMessage!);
        }

        var timeZoneId = Normalize(request.TimeZoneId) ?? TimeZoneInfo.Utc.Id;
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return BadRequest("WEBHOOK_TIME_ZONE_INVALID", "timeZoneId is not recognized.");
        }
        catch (InvalidTimeZoneException)
        {
            return BadRequest("WEBHOOK_TIME_ZONE_INVALID", "timeZoneId is invalid.");
        }

        IWorkflowWebhookAgentKeyMaterializer? credentialMaterializer = null;
        DurableCallerCredentialRef? durableCredential = null;
        if (request.EnableUnattendedEffects)
        {
            credentialMaterializer = ResolveCredentialMaterializer(http, out var materializerUnavailable);
            if (credentialMaterializer == null)
                return materializerUnavailable!;

            var materialized = await credentialMaterializer.MaterializeAsync(
                callerAuthority!,
                target.CapabilityAdmissionPlan!,
                scopeId,
                normalizedRoute,
                ct);
            if (!materialized.Succeeded)
            {
                return Results.Json(
                    new
                    {
                        code = materialized.ErrorCode,
                        message = "A dedicated webhook caller credential could not be issued.",
                    },
                    statusCode: materialized.StatusCode);
            }
            durableCredential = materialized.Credential;
        }
        else
        {
            var existing = await bindingStore.GetAsync(normalizedRoute, ct);
            if (existing?.CallerDurableCredential != null)
            {
                credentialMaterializer = ResolveCredentialMaterializer(
                    http,
                    out var materializerUnavailable);
                if (credentialMaterializer == null)
                    return materializerUnavailable!;
            }
        }

        var record = new WorkflowWebhookBindingRecord(
            RouteKey: normalizedRoute,
            ScopeId: scopeId,
            WorkflowName: workflowName!,
            SourceId: Normalize(request.SourceId),
            PromptTemplate: promptTemplate,
            PromptJsonPath: promptJsonPath,
            TimeZoneId: timeZoneId,
            DeliveryIdHeader: Normalize(request.DeliveryIdHeader),
            DeliveryIdJsonPath: deliveryIdJsonPath,
            HmacSecret: hmacSecret,
            HmacSignatureHeader: Normalize(request.HmacSignatureHeader),
            HmacTimestampHeader: Normalize(request.HmacTimestampHeader),
            MaxTimestampSkewSeconds: request.MaxTimestampSkewSeconds is > 0 and <= 3600
                ? request.MaxTimestampSkewSeconds.Value
                : 300,
            UpdatedAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            DefinitionActorId: definitionActorId,
            TargetRevisionId: targetRevisionId,
            PreviousHmacSecret: previousHmacSecret,
            CallerAuthority: callerAuthority,
            UnattendedEffectAuthorization: unattendedAuthorization,
            CallerDurableCredential: durableCredential);
        WorkflowWebhookBindingPutResult put;
        try
        {
            put = await bindingStore.PutOwnedAsync(record, ct);
        }
        catch
        {
            await RevokeBindingCredentialAsync(
                credentialMaterializer,
                callerAuthority,
                durableCredential,
                "workflow-webhook-binding-write-failed",
                logger: null,
                CancellationToken.None);
            throw;
        }

        if (!put.Succeeded)
        {
            await RevokeBindingCredentialAsync(
                credentialMaterializer,
                callerAuthority,
                durableCredential,
                "workflow-webhook-binding-write-rejected",
                logger: null,
                CancellationToken.None);
            return Results.Json(
                new { code = "WEBHOOK_ROUTE_OWNED_BY_OTHER_SCOPE", message = "Route key is owned by another scope." },
                statusCode: StatusCodes.Status409Conflict);
        }

        var cleanupLogger = http.RequestServices.GetService<ILoggerFactory>()?
            .CreateLogger("Aevatar.Workflow.WebhookBinding");
        await RevokeBindingCredentialAsync(
            credentialMaterializer ?? ResolveCredentialMaterializer(http, out _),
            put.PreviousRecord?.CallerAuthority,
            put.PreviousRecord?.CallerDurableCredential,
            "workflow-webhook-binding-replaced",
            cleanupLogger,
            CancellationToken.None);

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

        var normalizedRoute = WorkflowWebhookRoute.Normalize(routeKey);
        if (normalizedRoute == null)
            return BadRequest("WEBHOOK_ROUTE_REQUIRED", "Route key is required.");

        var existing = await bindingStore.GetAsync(normalizedRoute, ct);
        IWorkflowWebhookAgentKeyMaterializer? credentialMaterializer = null;
        if (existing?.CallerDurableCredential != null)
        {
            credentialMaterializer = ResolveCredentialMaterializer(
                http,
                out var materializerUnavailable);
            if (credentialMaterializer == null)
                return materializerUnavailable!;
        }

        var deleted = await bindingStore.DeleteOwnedAsync(normalizedRoute, scopeId, ct);
        if (!deleted.Succeeded)
            return Results.NotFound();

        var logger = http.RequestServices.GetService<ILoggerFactory>()?
            .CreateLogger("Aevatar.Workflow.WebhookBinding");
        await RevokeBindingCredentialAsync(
            credentialMaterializer ?? ResolveCredentialMaterializer(http, out _),
            deleted.RemovedRecord?.CallerAuthority,
            deleted.RemovedRecord?.CallerDurableCredential,
            "workflow-webhook-binding-deleted",
            logger,
            CancellationToken.None);

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
        timeZoneId = record.TimeZoneId,
        deliveryIdHeader = record.DeliveryIdHeader,
        deliveryIdJsonPath = record.DeliveryIdJsonPath,
        hmacSecretSet = !string.IsNullOrWhiteSpace(record.HmacSecret),
        previousHmacSecretSet = !string.IsNullOrWhiteSpace(record.PreviousHmacSecret),
        callerCredentialSet = record.CallerAuthority is not null,
        durableCallerCredentialSet = record.CallerDurableCredential is not null,
        unattendedEffectsEnabled = record.CallerAuthority is not null &&
                                   record.UnattendedEffectAuthorization is not null,
        hmacSignatureHeader = record.HmacSignatureHeader,
        hmacTimestampHeader = record.HmacTimestampHeader,
        maxTimestampSkewSeconds = record.MaxTimestampSkewSeconds,
        updatedAtUnixMs = record.UpdatedAtUnixMs,
    };

    private static async Task<UnattendedAuthorizationResolution> ResolveUnattendedAuthorizationAsync(
        HttpContext http,
        string routeKey,
        string scopeId,
        string definitionActorId,
        WorkflowActorBinding target,
        string targetRevisionId,
        CancellationToken ct)
    {
        if (!AevatarScopeAccessGuard.IsAuthenticationEnabled(http.RequestServices))
        {
            return UnattendedAuthorizationResolution.Failure(Results.Json(
                new
                {
                    code = "WEBHOOK_UNATTENDED_AUTHENTICATION_REQUIRED",
                    message = "Unattended webhook effects require authenticated binding management.",
                },
                statusCode: StatusCodes.Status409Conflict));
        }

        if (target.ExpectedExecutionMode != ExternalCapabilityExecutionMode.Durable ||
            target.CapabilityAdmissionPlan is null ||
            target.SourceVersion < 1)
        {
            return UnattendedAuthorizationResolution.Failure(Results.Json(
                new
                {
                    code = "WEBHOOK_UNATTENDED_DURABLE_TARGET_REQUIRED",
                    message = "Unattended webhook effects require a versioned durable workflow definition.",
                },
                statusCode: StatusCodes.Status409Conflict));
        }

        var bindingQuery = http.RequestServices.GetService<IExternalIdentityBindingQueryPort>();
        if (bindingQuery is null)
        {
            return UnattendedAuthorizationResolution.Failure(Results.Json(
                new
                {
                    code = "WEBHOOK_CALLER_BINDING_LOOKUP_UNAVAILABLE",
                    message = "Caller NyxID binding lookup is unavailable.",
                },
                statusCode: StatusCodes.Status503ServiceUnavailable));
        }

        if (!AevatarPrincipalSubjectResolver.TryResolveNyxIdSubject(http.User, out var subject))
            return UnattendedAuthorizationResolution.Failure(Results.Unauthorized());

        var logger = http.RequestServices
            .GetService<ILoggerFactory>()?
            .CreateLogger("Aevatar.Workflow.WebhookBinding");
        var extracted = await WorkflowCallerCredentialExtractor.ExtractAsync(
            http,
            bindingQuery,
            callerAccessTokenProvider: null,
            logger: logger,
            ct: ct);
        if (!extracted.Succeeded ||
            extracted.Credential?.NyxIdAuthority is not { } extractedAuthority ||
            !string.Equals(subject, extractedAuthority.ExternalUserId, StringComparison.Ordinal))
        {
            return UnattendedAuthorizationResolution.Failure(Results.Unauthorized());
        }

        BindingId? bindingId;
        try
        {
            bindingId = await bindingQuery.ResolveAsync(
                new ExternalSubjectRef
                {
                    Platform = extractedAuthority.Platform,
                    Tenant = extractedAuthority.Tenant,
                    ExternalUserId = subject,
                },
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return UnattendedAuthorizationResolution.Failure(Results.Json(
                new
                {
                    code = "WEBHOOK_CALLER_BINDING_LOOKUP_UNAVAILABLE",
                    message = "Caller NyxID binding lookup is unavailable.",
                },
                statusCode: StatusCodes.Status503ServiceUnavailable));
        }

        if (string.IsNullOrWhiteSpace(bindingId?.Value))
        {
            return UnattendedAuthorizationResolution.Failure(Results.Json(
                new
                {
                    code = "WEBHOOK_CALLER_BINDING_REQUIRED",
                    message = "Reconnect NyxID before enabling unattended webhook effects.",
                },
                statusCode: StatusCodes.Status409Conflict));
        }

        var exactBindingId = bindingId.Value.Trim();
        var canManageWithDirectHumanBearer =
            extracted.NyxIdCredentialSelection?.CanManageUserServices == true;
        var callerAuthority = new ProtoWorkflowCallerNyxIdAuthority
        {
            Platform = extractedAuthority.Platform,
            Tenant = extractedAuthority.Tenant,
            ExternalUserId = subject,
            Scope = extractedAuthority.Scope,
            BindingId = exactBindingId,
        };
        var canManageWithBoundProxyDelegation = false;
        if (
            extracted.Credential.Kind == NyxIdCallerCredentialKind.ProxyDelegation &&
            !string.IsNullOrWhiteSpace(extractedAuthority.BindingId) &&
            string.Equals(extractedAuthority.BindingId, exactBindingId, StringComparison.Ordinal) &&
            http.RequestServices.GetService<IWorkflowCallerAccessTokenProvider>() is { } tokenProvider)
        {
            try
            {
                var issuedToken = await tokenProvider.IssueAsync(callerAuthority, ct);
                canManageWithBoundProxyDelegation =
                    WorkflowCallerCredentialTokens.ParseOptional(issuedToken).IsValid;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger?.LogWarning(
                    ex,
                    "Caller NyxID source-readable token exchange failed while managing webhook binding.");
            }
        }

        var canManageWithForwardedAgentKey = TryGetForwardedAgentKey(http, out _);
        if (!canManageWithDirectHumanBearer &&
            !canManageWithBoundProxyDelegation &&
            !canManageWithForwardedAgentKey)
            return UnattendedAuthorizationResolution.Failure(Results.Unauthorized());

        try
        {
            var authorization = WorkflowUnattendedEffectAuthorizationIntegrity.Create(
                definitionActorId,
                scopeId,
                target.WorkflowId,
                targetRevisionId,
                routeKey,
                subject,
                target.SourceVersion,
                callerAuthority,
                target.CapabilityAdmissionPlan);
            return UnattendedAuthorizationResolution.Success(callerAuthority, authorization);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return UnattendedAuthorizationResolution.Failure(Results.Json(
                new
                {
                    code = "WEBHOOK_UNATTENDED_AUTHORIZATION_INVALID",
                    message = "The workflow definition is not eligible for unattended effects.",
                },
                statusCode: StatusCodes.Status409Conflict));
        }
    }

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
                        "The Redis-backed store needs a connection string " +
                        "(WorkflowWebhookIngress:RedisConnectionString, defaulting to " +
                        "ActorRuntime:OrleansGarnetConnectionString) and a secret encryption key " +
                        "(WorkflowWebhookIngress:BindingSecretEncryptionKey, defaulting to a key " +
                        "derived from ActorRuntime:SecretStoreKeyringPath).",
                },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        return store;
    }

    private static IWorkflowWebhookAgentKeyMaterializer? ResolveCredentialMaterializer(
        HttpContext http,
        out IResult? unavailable)
    {
        IWorkflowWebhookAgentKeyMaterializer? materializer;
        try
        {
            materializer = http.RequestServices.GetService<IWorkflowWebhookAgentKeyMaterializer>();
        }
        catch (InvalidOperationException)
        {
            materializer = null;
        }

        unavailable = materializer != null
            ? null
            : Results.Json(
                new
                {
                    code = "WEBHOOK_CALLER_CREDENTIAL_ISSUANCE_UNAVAILABLE",
                    message = "Dedicated webhook caller credential issuance is unavailable.",
                },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        return materializer;
    }

    private static bool TryGetForwardedAgentKey(HttpContext http, out string agentKey)
    {
        agentKey = string.Empty;
        if (http.User.Identity?.IsAuthenticated != true ||
            !AevatarPrincipalSubjectResolver.TryResolveNyxIdSubject(http.User, out _) ||
            !http.Request.Headers.TryGetValue("Authorization", out var values) ||
            values.Count != 1)
        {
            return false;
        }

        var authorization = values[0]?.Trim();
        const string prefix = "Bearer ";
        if (authorization?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) != true)
            return false;

        var candidate = authorization[prefix.Length..].Trim();
        var parsed = WorkflowCallerCredentialTokens.ParseOptional(candidate);
        if (!parsed.IsValid ||
            parsed.NormalizedBearerToken?.StartsWith("nyxid_ag_", StringComparison.Ordinal) != true)
        {
            return false;
        }

        agentKey = parsed.NormalizedBearerToken;
        return true;
    }

    private static async Task<bool> RevokeBindingCredentialAsync(
        IWorkflowWebhookAgentKeyMaterializer? materializer,
        ProtoWorkflowCallerNyxIdAuthority? callerAuthority,
        DurableCallerCredentialRef? reference,
        string auditReason,
        ILogger? logger,
        CancellationToken ct)
    {
        if (reference == null)
            return true;
        if (materializer == null)
        {
            logger?.LogError(
                "Webhook binding caller credential cleanup is unavailable. credentialRef={CredentialRef}",
                reference.Ref);
            return false;
        }

        try
        {
            var revoked = await materializer.RevokeAsync(
                callerAuthority,
                reference,
                auditReason,
                ct);
            if (!revoked)
            {
                logger?.LogError(
                    "Webhook binding caller credential cleanup was not committed. credentialRef={CredentialRef} reason={AuditReason}",
                    reference.Ref,
                    auditReason);
            }
            return revoked;
        }
        catch (Exception ex)
        {
            logger?.LogError(
                ex,
                "Webhook binding caller credential cleanup failed. credentialRef={CredentialRef}",
                reference.Ref);
            return false;
        }
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

    private sealed record UnattendedAuthorizationResolution(
        ProtoWorkflowCallerNyxIdAuthority? CallerAuthority,
        WorkflowUnattendedEffectAuthorization? Authorization,
        IResult? Error)
    {
        public static UnattendedAuthorizationResolution Success(
            ProtoWorkflowCallerNyxIdAuthority callerAuthority,
            WorkflowUnattendedEffectAuthorization authorization) =>
            new(callerAuthority, authorization, null);

        public static UnattendedAuthorizationResolution Failure(IResult error) =>
            new(null, null, error);
    }
}
