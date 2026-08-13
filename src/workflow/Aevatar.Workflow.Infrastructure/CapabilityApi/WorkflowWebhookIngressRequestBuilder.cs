using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevatar.Workflow.Application.Abstractions.Runs;
using ExternalCapabilityExecutionMode = Aevatar.Workflow.Abstractions.ExternalCapabilityExecutionMode;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

internal sealed class WorkflowWebhookIngressRequestBuilder
{
    private readonly IOptions<WorkflowWebhookIngressOptions> _options;

    public WorkflowWebhookIngressRequestBuilder(IOptions<WorkflowWebhookIngressOptions> options)
    {
        _options = options;
    }

    public WorkflowWebhookIngressBuildResult Build(
        HttpRequest httpRequest,
        string routeKey,
        ReadOnlySpan<byte> rawBody,
        DateTimeOffset receivedAt,
        WorkflowWebhookIngressBindingOptions? resolvedBinding = null)
    {
        if (rawBody.Length > WorkflowWebhookIngressLimits.MaxBodyBytes)
            return WorkflowWebhookIngressBuildResult.Failure(
                "WEBHOOK_BODY_TOO_LARGE",
                "Webhook request body exceeds the supported size.",
                StatusCodes.Status413PayloadTooLarge);

        // Scope-registered dynamic bindings take precedence; the static
        // appsettings list remains as an operator fallback.
        var binding = resolvedBinding ?? ResolveBinding(routeKey);
        if (binding == null)
            return WorkflowWebhookIngressBuildResult.Failure(
                "WEBHOOK_ROUTE_NOT_FOUND",
                "Webhook route was not found.",
                StatusCodes.Status404NotFound);

        var route = WorkflowWebhookRoute.Normalize(binding.RouteKey) ?? string.Empty;
        var sourceId = Normalize(binding.SourceId) ?? route;
        var workflowName = Normalize(binding.WorkflowName);
        var definitionActorId = Normalize(binding.DefinitionActorId);
        if (workflowName == null && definitionActorId == null)
            return WorkflowWebhookIngressBuildResult.Failure(
                "WEBHOOK_WORKFLOW_REQUIRED",
                "Webhook binding workflow name or definition actor id is required.",
                StatusCodes.Status500InternalServerError);

        if (Normalize(binding.HmacSecret) == null)
            return WorkflowWebhookIngressBuildResult.Failure(
                "WEBHOOK_AUTH_CONFIG_REQUIRED",
                "Webhook HMAC secret is required.",
                StatusCodes.Status500InternalServerError);

        var auth = WorkflowWebhookIngressAuthenticator.Authenticate(httpRequest, binding, rawBody, receivedAt);
        if (!auth.Succeeded)
            return WorkflowWebhookIngressBuildResult.Failure(
                auth.ErrorCode ?? "WEBHOOK_AUTH_INVALID",
                auth.ErrorMessage ?? "Webhook authentication failed.",
                StatusCodes.Status401Unauthorized);

        JsonDocument payload;
        try
        {
            payload = JsonDocument.Parse(
                rawBody.ToArray(),
                new JsonDocumentOptions { MaxDepth = WorkflowWebhookIngressLimits.MaxJsonDepth });
        }
        catch (JsonException)
        {
            return WorkflowWebhookIngressBuildResult.Failure(
                "WEBHOOK_BODY_INVALID",
                "Webhook request body must be valid JSON.",
                StatusCodes.Status400BadRequest);
        }

        using (payload)
        {
            var delivery = ResolveDeliveryId(httpRequest, binding, payload.RootElement);
            if (!delivery.Succeeded)
                return WorkflowWebhookIngressBuildResult.Failure(
                    delivery.ErrorCode!,
                    delivery.ErrorMessage!,
                    delivery.StatusCode);

            var prompt = ResolvePrompt(binding, payload.RootElement, receivedAt);
            if (!prompt.Succeeded)
                return WorkflowWebhookIngressBuildResult.Failure(
                    prompt.ErrorCode!,
                    prompt.ErrorMessage!,
                    prompt.StatusCode);

            return BuildSuccess(
                httpRequest,
                route,
                sourceId,
                workflowName,
                definitionActorId,
                binding,
                rawBody,
                receivedAt,
                auth,
                delivery.DeliveryId!,
                prompt.Prompt!);
        }
    }

    private static WorkflowWebhookIngressBuildResult BuildSuccess(
        HttpRequest httpRequest,
        string route,
        string sourceId,
        string? workflowName,
        string? definitionActorId,
        WorkflowWebhookIngressBindingOptions binding,
        ReadOnlySpan<byte> rawBody,
        DateTimeOffset receivedAt,
        WorkflowWebhookAuthenticationResult auth,
        string deliveryId,
        string prompt)
    {
        if (deliveryId.Length == 0)
            return WorkflowWebhookIngressBuildResult.Failure(
                "WEBHOOK_DELIVERY_ID_REQUIRED",
                "Webhook delivery id is required.",
                StatusCodes.Status400BadRequest);

        var fingerprint = Fingerprint(rawBody);
        var commandId = BuildSeed("webhook", route, sourceId, deliveryId);
        var externalIngress = new WorkflowExternalIngressContext(
            RouteKey: route,
            SourceId: sourceId,
            DeliveryId: deliveryId,
            ReceivedAtUnixMs: receivedAt.ToUnixTimeMilliseconds(),
            ContentType: Normalize(httpRequest.ContentType),
            PayloadFingerprint: fingerprint,
            AuthScheme: auth.AuthScheme,
            PrincipalSubject: auth.PrincipalSubject);

        // Webhook deliveries carry no user identity; the binding's caller
        // bearer (write-only, encrypted at rest) is what nyxid-brokered
        // steps execute as. Without it read-only workflows still run, but
        // any nyxid write step fails with NYXID_ACCESS_TOKEN_MISSING.
        var callerBearerToken = Normalize(binding.CallerBearerToken);

        var command = new WorkflowChatRunRequest(
            Prompt: prompt,
            // A scope-published target is addressed by its definition actor;
            // the catalog-name path stays for host-catalog workflows.
            Source: definitionActorId != null
                ? WorkflowChatSource.DefinitionActor(definitionActorId, workflowName)
                : WorkflowChatSource.CatalogWorkflow(workflowName!),
            CallerCredential: callerBearerToken == null
                ? null
                : new WorkflowCallerCredential(BearerToken: callerBearerToken),
            ExpectedExecutionMode: ExternalCapabilityExecutionMode.Interactive,
            ScopeId: Normalize(binding.ScopeId),
            CommandIdSeed: commandId,
            CorrelationIdSeed: commandId,
            ExternalIngress: externalIngress);

        var admission = new WorkflowWebhookReplayAdmissionRequest(
            route,
            sourceId,
            deliveryId,
            fingerprint,
            receivedAt,
            commandId,
            commandId);

        return WorkflowWebhookIngressBuildResult.Success(command, admission);
    }

    private WorkflowWebhookIngressBindingOptions? ResolveBinding(string routeKey)
    {
        var normalizedRoute = WorkflowWebhookRoute.Normalize(routeKey);
        if (normalizedRoute == null)
            return null;

        return _options.Value.Bindings.FirstOrDefault(binding =>
            string.Equals(
                WorkflowWebhookRoute.Normalize(binding.RouteKey),
                normalizedRoute,
                StringComparison.Ordinal));
    }

    private static DeliveryIdResolution ResolveDeliveryId(
        HttpRequest request,
        WorkflowWebhookIngressBindingOptions binding,
        JsonElement payload)
    {
        var path = Normalize(binding.DeliveryIdJsonPath);
        if (path == null || !WorkflowWebhookJsonPath.TryExtractScalar(payload, path, out var bodyValue))
            return DeliveryIdResolution.Failure(
                "WEBHOOK_DELIVERY_ID_REQUIRED",
                "Webhook payload is missing its signed delivery id.",
                StatusCodes.Status400BadRequest);

        var deliveryId = Normalize(bodyValue);
        if (deliveryId == null)
            return DeliveryIdResolution.Failure(
                "WEBHOOK_DELIVERY_ID_REQUIRED",
                "Webhook delivery id is required.",
                StatusCodes.Status400BadRequest);

        if (Encoding.UTF8.GetByteCount(deliveryId) > WorkflowWebhookIngressLimits.MaxDeliveryIdBytes)
            return DeliveryIdResolution.Failure(
                "WEBHOOK_DELIVERY_ID_TOO_LARGE",
                "Webhook delivery id exceeds the supported size.",
                StatusCodes.Status400BadRequest);

        var headerName = Normalize(binding.DeliveryIdHeader);
        if (headerName != null)
        {
            var headerValue = Normalize(request.Headers[headerName].FirstOrDefault());
            if (headerValue != null && !string.Equals(headerValue, deliveryId, StringComparison.Ordinal))
            {
                return DeliveryIdResolution.Failure(
                    "WEBHOOK_DELIVERY_ID_MISMATCH",
                    "Webhook delivery id header does not match the signed payload.",
                    StatusCodes.Status400BadRequest);
            }
        }

        return DeliveryIdResolution.Success(deliveryId);
    }

    private static WorkflowWebhookPromptRenderResult ResolvePrompt(
        WorkflowWebhookIngressBindingOptions binding,
        JsonElement payload,
        DateTimeOffset receivedAt)
    {
        var template = Normalize(binding.PromptTemplate);
        if (template != null)
            return WorkflowWebhookPromptTemplate.Render(
                template,
                payload,
                receivedAt,
                binding.TimeZoneId);

        var path = Normalize(binding.PromptJsonPath);
        if (path != null && WorkflowWebhookJsonPath.TryExtractScalar(payload, path, out var value))
        {
            var prompt = Normalize(value);
            if (prompt != null)
            {
                if (Encoding.UTF8.GetByteCount(prompt) > WorkflowWebhookIngressLimits.MaxPromptBytes)
                    return WorkflowWebhookPromptRenderResult.PayloadFailure(
                        "WEBHOOK_PROMPT_TOO_LARGE",
                        "Webhook prompt mapping exceeds the supported output size.",
                        StatusCodes.Status413PayloadTooLarge);
                return WorkflowWebhookPromptRenderResult.Success(prompt);
            }
        }

        return WorkflowWebhookPromptRenderResult.PayloadFailure(
            "WEBHOOK_PROMPT_PATH_MISSING",
            "Webhook payload is missing a required prompt value.",
            StatusCodes.Status400BadRequest);
    }

    private static string Fingerprint(ReadOnlySpan<byte> rawBody) =>
        Convert.ToHexString(SHA256.HashData(rawBody)).ToLowerInvariant();

    private static string BuildSeed(params string[] parts) =>
        "webhook:" + Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(string.Concat(parts.Select(static part =>
            {
                var normalized = Normalize(part) ?? string.Empty;
                return $"{Encoding.UTF8.GetByteCount(normalized)}:{normalized}";
            }))))).ToLowerInvariant();

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record DeliveryIdResolution(
        string? DeliveryId,
        string? ErrorCode,
        string? ErrorMessage,
        int StatusCode)
    {
        public bool Succeeded => DeliveryId != null && ErrorCode == null;

        public static DeliveryIdResolution Success(string deliveryId) =>
            new(deliveryId, null, null, StatusCodes.Status200OK);

        public static DeliveryIdResolution Failure(string code, string message, int statusCode) =>
            new(null, code, message, statusCode);
    }
}
