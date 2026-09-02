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
        DateTimeOffset receivedAt)
    {
        var binding = ResolveBinding(routeKey);
        if (binding == null)
            return WorkflowWebhookIngressBuildResult.Failure(
                "WEBHOOK_ROUTE_NOT_FOUND",
                "Webhook route was not found.",
                StatusCodes.Status404NotFound);

        var route = Normalize(binding.RouteKey) ?? string.Empty;
        var sourceId = Normalize(binding.SourceId) ?? route;
        var workflowName = Normalize(binding.WorkflowName);
        if (workflowName == null)
            return WorkflowWebhookIngressBuildResult.Failure(
                "WEBHOOK_WORKFLOW_REQUIRED",
                "Webhook binding workflow name is required.",
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

        var deliveryId = ResolveDeliveryId(httpRequest, binding, rawBody);
        if (deliveryId == null)
            return WorkflowWebhookIngressBuildResult.Failure(
                "WEBHOOK_DELIVERY_ID_REQUIRED",
                "Webhook delivery id is required.",
                StatusCodes.Status400BadRequest);

        var prompt = ResolvePrompt(binding, rawBody);
        if (prompt == null)
            return WorkflowWebhookIngressBuildResult.Failure(
                "WEBHOOK_PROMPT_REQUIRED",
                "Webhook prompt mapping produced an empty prompt.",
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

        var command = new WorkflowChatRunRequest(
            Prompt: prompt,
            Source: WorkflowChatSource.CatalogWorkflow(workflowName),
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
        var normalizedRoute = Normalize(routeKey);
        if (normalizedRoute == null)
            return null;

        return _options.Value.Bindings.FirstOrDefault(binding =>
            string.Equals(Normalize(binding.RouteKey), normalizedRoute, StringComparison.Ordinal));
    }

    private static string? ResolveDeliveryId(
        HttpRequest request,
        WorkflowWebhookIngressBindingOptions binding,
        ReadOnlySpan<byte> rawBody)
    {
        var headerName = Normalize(binding.DeliveryIdHeader);
        if (headerName != null)
        {
            var headerValue = Normalize(request.Headers[headerName].FirstOrDefault());
            if (headerValue != null)
                return headerValue;
        }

        var path = Normalize(binding.DeliveryIdJsonPath);
        return path == null ? null : ExtractJsonString(rawBody, path);
    }

    private static string? ResolvePrompt(
        WorkflowWebhookIngressBindingOptions binding,
        ReadOnlySpan<byte> rawBody)
    {
        var template = Normalize(binding.PromptTemplate);
        if (template != null)
            return ApplyTemplate(template, rawBody);

        var path = Normalize(binding.PromptJsonPath);
        if (path != null)
            return ExtractJsonString(rawBody, path);

        return null;
    }

    private static string? ApplyTemplate(string template, ReadOnlySpan<byte> rawBody)
    {
        var result = template;
        foreach (var placeholder in EnumeratePlaceholders(template))
        {
            var value = ExtractJsonString(rawBody, placeholder);
            result = result.Replace("{{" + placeholder + "}}", value ?? string.Empty, StringComparison.Ordinal);
        }

        return Normalize(result);
    }

    private static IEnumerable<string> EnumeratePlaceholders(string template)
    {
        var index = 0;
        while (index < template.Length)
        {
            var start = template.IndexOf("{{", index, StringComparison.Ordinal);
            if (start < 0)
                yield break;
            var end = template.IndexOf("}}", start + 2, StringComparison.Ordinal);
            if (end < 0)
                yield break;

            var value = template[(start + 2)..end].Trim();
            if (value.Length > 0)
                yield return value;
            index = end + 2;
        }
    }

    private static string? ExtractJsonString(ReadOnlySpan<byte> rawBody, string path)
    {
        try
        {
            using var document = JsonDocument.Parse(rawBody.ToArray());
            var current = document.RootElement;
            foreach (var segment in NormalizeJsonPath(path))
            {
                if (current.ValueKind != JsonValueKind.Object ||
                    !current.TryGetProperty(segment, out current))
                {
                    return null;
                }
            }

            return current.ValueKind switch
            {
                JsonValueKind.String => Normalize(current.GetString()),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => current.GetRawText(),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IEnumerable<string> NormalizeJsonPath(string path)
    {
        var normalized = path.Trim();
        if (normalized.StartsWith("$.", StringComparison.Ordinal))
            normalized = normalized[2..];
        else if (normalized.StartsWith(".", StringComparison.Ordinal))
            normalized = normalized[1..];

        return normalized
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string Fingerprint(ReadOnlySpan<byte> rawBody) =>
        Convert.ToHexString(SHA256.HashData(rawBody)).ToLowerInvariant();

    private static string BuildSeed(params string[] parts) =>
        string.Join(':', parts.Select(Normalize).Where(static part => part != null));

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
