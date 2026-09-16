using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.Studio.Application.Provisioning;

namespace Aevatar.AI.ToolProviders.StudioProvisioning;

internal sealed class GetStudioMemberInvocationReadinessTool(
    IStudioMemberInvocationReadinessQueryPort readinessQueryPort) : IStudioReadOnlyReceiptTool
{
    private static readonly JsonSerializerOptions s_jsonOptions = StudioQueryToolJson.Options;

    public string Name => "aevatar_get_member_invocation_readiness";

    public string Description =>
        "Read whether one Studio member endpoint is invoke-ready from backend read models. " +
        "Supply member_id and optionally endpoint_id; endpoint_id defaults to chat. " +
        "Do not provide workflow_id or published_service_id.";

    public string ParametersSchema => """
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "member_id": {
              "type": "string",
              "description": "Studio member id to inspect. Required."
            },
            "endpoint_id": {
              "type": "string",
              "description": "Published endpoint id to inspect. Defaults to chat."
            }
          },
          "required": ["member_id"]
        }
        """;

    public bool IsReadOnly => true;
    public bool IsDestructive => false;

    public IReadOnlyList<StudioQueryToolJson.ResultPropertyRequirement> ResultRequirements { get; } =
    [
        StudioQueryToolJson.StringProperty("scope_id"),
        StudioQueryToolJson.StringProperty("member_id"),
        StudioQueryToolJson.StringProperty("published_service_id"),
        StudioQueryToolJson.StringProperty("endpoint_id"),
        StudioQueryToolJson.StringProperty("revision_id"),
        StudioQueryToolJson.StringProperty("status"),
        StudioQueryToolJson.StringProperty("reason_code"),
    ];

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var scopeId = StudioToolScopeResolver.ResolveOwnerScopeOrCallerScope();
        if (scopeId is null)
        {
            return StudioQueryToolJson.ErrorJson(
                "caller_scope_unavailable",
                "scope_id is required in AgentToolRequestContext. Invocation readiness uses the caller scope from the tool execution context.");
        }

        Arguments? args;
        try
        {
            var unknownArgument = StudioQueryToolJson.FindUnknownArgument(
                argumentsJson,
                ["member_id", "endpoint_id"]);
            if (unknownArgument is not null)
                return StudioQueryToolJson.ErrorJson("invalid_arguments", $"Unknown argument: {unknownArgument}");

            args = StudioQueryToolJson.Deserialize<Arguments>(argumentsJson);
        }
        catch (JsonException ex)
        {
            return StudioQueryToolJson.ErrorJson(
                "invalid_arguments",
                $"Could not parse tool arguments: {ex.Message}");
        }

        var memberId = StudioQueryToolJson.Normalize(args?.MemberId);
        if (memberId is null)
            return StudioQueryToolJson.ErrorJson("invalid_arguments", "member_id is required.");
        var endpointId = StudioQueryToolJson.Normalize(args?.EndpointId) ?? "chat";

        try
        {
            var result = await readinessQueryPort.GetAsync(
                scopeId,
                memberId,
                endpointId,
                ct);
            return result is null
                ? StudioQueryToolJson.ErrorJson(
                    "member_invocation_readiness_unavailable",
                    $"Invocation readiness for Studio member '{memberId}' endpoint '{endpointId}' is not available.")
                : JsonSerializer.Serialize(result, s_jsonOptions);
        }
        catch (KeyNotFoundException)
        {
            return StudioQueryToolJson.ErrorJson(
                "member_not_found",
                $"Studio member '{memberId}' was not found in scope '{scopeId}'.");
        }
        catch (InvalidOperationException ex)
        {
            return StudioQueryToolJson.ErrorJson(
                "member_invocation_readiness_unavailable",
                ex.Message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return StudioQueryToolJson.ErrorJson(
                "member_invocation_readiness_query_failed",
                $"Studio member invocation readiness query failed: {ex.GetType().Name}");
        }
    }

    private sealed record Arguments(
        [property: JsonPropertyName("member_id")] string? MemberId,
        [property: JsonPropertyName("endpoint_id")] string? EndpointId);
}
