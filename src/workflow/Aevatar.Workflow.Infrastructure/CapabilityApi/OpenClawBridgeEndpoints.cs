using System.Security.Cryptography;
using System.Text;
using Aevatar.Workflow.Application.Abstractions.OpenClaw;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

public sealed class OpenClawBridgeOptions
{
    public bool Enabled { get; set; } = true;
    public bool RequireAuthToken { get; set; } = true;
    public string AuthHeaderName { get; set; } = "X-OpenClaw-Bridge-Token";
    public string AuthToken { get; set; } = string.Empty;
    public string DefaultWorkflow { get; set; } = "68_claw_channel_entry";
    public int CallbackTimeoutMs { get; set; } = 5000;
    public string CallbackAuthHeaderName { get; set; } = "Authorization";
    public string CallbackAuthScheme { get; set; } = "Bearer";
    public int CallbackMaxAttempts { get; set; } = 1;
    public int CallbackRetryDelayMs { get; set; } = 300;
    public List<string> CallbackAllowedHosts { get; set; } = [];
    public bool EnableIdempotency { get; set; } = true;
    public int IdempotencyTtlHours { get; set; } = 24;
}

public sealed record OpenClawAgentHookInput
{
    public string? Prompt { get; init; }
    public string? Message { get; init; }
    public string? Text { get; init; }
    public string? Workflow { get; init; }
    public string? ActorId { get; init; }
    public string? SessionId { get; init; }
    public string? ChannelId { get; init; }
    public string? UserId { get; init; }
    public string? MessageId { get; init; }
    public string? IdempotencyKey { get; init; }
    public IReadOnlyList<string>? WorkflowYamls { get; init; }
    public string? CallbackUrl { get; init; }
    public string? CallbackToken { get; init; }
    public IDictionary<string, string>? Metadata { get; init; }
}

internal static class OpenClawBridgeEndpoints
{
    internal static async Task<IResult> HandleOpenClawAgentHook(
        HttpContext http,
        OpenClawAgentHookInput input,
        [FromServices] IOpenClawBridgeOrchestrationService bridgeService,
        [FromServices] IOptions<OpenClawBridgeOptions>? bridgeOptionsAccessor = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var options = bridgeOptionsAccessor?.Value ?? new OpenClawBridgeOptions();
        if (!options.Enabled)
        {
            return Results.NotFound(new
            {
                code = "OPENCLAW_BRIDGE_DISABLED",
                message = "OpenClaw bridge endpoint is disabled.",
            });
        }

        if (!IsAuthorized(http, options, out var authError))
        {
            return Results.Json(
                new
                {
                    code = "UNAUTHORIZED",
                    message = authError,
                },
                statusCode: StatusCodes.Status401Unauthorized);
        }

        var executionResult = await bridgeService.ExecuteAsync(
            new OpenClawBridgeExecutionRequest
            {
                Prompt = input.Prompt,
                Message = input.Message,
                Text = input.Text,
                Workflow = input.Workflow,
                ActorId = input.ActorId,
                SessionId = input.SessionId,
                ChannelId = input.ChannelId,
                UserId = input.UserId,
                MessageId = input.MessageId,
                IdempotencyKey = input.IdempotencyKey,
                WorkflowYamls = input.WorkflowYamls,
                CallbackUrl = input.CallbackUrl,
                CallbackToken = input.CallbackToken,
                Metadata = input.Metadata == null
                    ? null
                    : new Dictionary<string, string>(input.Metadata, StringComparer.Ordinal),
                DefaultWorkflowName = options.DefaultWorkflow,
                EnableIdempotency = options.EnableIdempotency,
                IdempotencyTtlHours = options.IdempotencyTtlHours,
                CallbackAllowedHosts = options.CallbackAllowedHosts,
                CallbackAuthHeaderName = options.CallbackAuthHeaderName,
                CallbackAuthScheme = options.CallbackAuthScheme,
                CallbackTimeoutMs = options.CallbackTimeoutMs,
                CallbackMaxAttempts = options.CallbackMaxAttempts,
                CallbackRetryDelayMs = options.CallbackRetryDelayMs,
            },
            ct);

        return MapExecutionResult(executionResult);
    }

    private static IResult MapExecutionResult(OpenClawBridgeExecutionResult result)
    {
        if (result.Accepted)
        {
            if (result.Replayed)
            {
                return Results.Accepted(
                    $"/api/actors/{result.ActorId}",
                    new
                    {
                        accepted = true,
                        replayed = true,
                        actorId = result.ActorId,
                        commandId = result.CommandId,
                        workflow = result.WorkflowName,
                        correlationId = result.CorrelationId,
                        idempotencyKey = result.IdempotencyKey,
                        sessionKey = result.SessionKey,
                        channelId = result.ChannelId,
                        userId = result.UserId,
                    });
            }

            return Results.Accepted(
                $"/api/actors/{result.ActorId}",
                new
                {
                    accepted = true,
                    actorId = result.ActorId,
                    commandId = result.CommandId,
                    workflow = result.WorkflowName,
                    correlationId = result.CorrelationId,
                    idempotencyKey = result.IdempotencyKey,
                    sessionKey = result.SessionKey,
                    channelId = result.ChannelId,
                    userId = result.UserId,
                });
        }

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["code"] = result.Code,
            ["message"] = result.Message,
        };
        AddIfNotBlank(payload, "correlationId", result.CorrelationId);
        AddIfNotBlank(payload, "idempotencyKey", result.IdempotencyKey);
        AddIfNotBlank(payload, "sessionKey", result.SessionKey);
        AddIfNotBlank(payload, "channelId", result.ChannelId);
        AddIfNotBlank(payload, "userId", result.UserId);
        AddIfNotBlank(payload, "actorId", result.ActorId);
        AddIfNotBlank(payload, "commandId", result.CommandId);
        AddIfNotBlank(payload, "workflow", result.WorkflowName);
        return Results.Json(payload, statusCode: result.StatusCode);
    }

    private static void AddIfNotBlank(
        IDictionary<string, object?> payload,
        string key,
        string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            payload[key] = value;
    }

    private static bool IsAuthorized(HttpContext http, OpenClawBridgeOptions options, out string error)
    {
        error = string.Empty;
        var configuredToken = NormalizeToken(options.AuthToken);
        if (string.IsNullOrWhiteSpace(configuredToken))
        {
            if (!options.RequireAuthToken)
                return true;

            error = "Bridge auth token is required but not configured.";
            return false;
        }

        var headerName = string.IsNullOrWhiteSpace(options.AuthHeaderName)
            ? "X-OpenClaw-Bridge-Token"
            : options.AuthHeaderName.Trim();
        var providedToken = NormalizeToken(http.Request.Headers[headerName].ToString());
        if (string.IsNullOrWhiteSpace(providedToken))
        {
            error = $"Missing auth header '{headerName}'.";
            return false;
        }

        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(providedToken),
                Encoding.UTF8.GetBytes(configuredToken)))
        {
            error = "Invalid bridge auth token.";
            return false;
        }

        return true;
    }

    private static string NormalizeToken(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
}
