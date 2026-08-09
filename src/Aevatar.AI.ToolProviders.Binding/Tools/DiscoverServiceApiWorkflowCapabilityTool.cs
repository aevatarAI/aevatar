using System.Text.Json;
using System.Text.Json.Nodes;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;

namespace Aevatar.AI.ToolProviders.Binding.Tools;

public sealed class DiscoverServiceApiWorkflowCapabilityTool(
    IServiceApiWorkflowCapabilityDiscoveryPort discoveryPort) :
    ExternalWorkflowCapabilityReadOnlyTool
{
    private const string ManagedDiscoveryPolicyVersion = "service_api_skill_discovery.v1";

    public override string Name => "discover_service_api_workflow_capability";

    public override string Description =>
        "Resolve one external Service API capability through the Application-owned descriptor, " +
        "managed discovery, readiness, and fallback policy. Returns only a typed terminal resolution " +
        "or a typed executable readiness handoff; it does not accept credentials or arbitrary prompts.";

    public override string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "target_user_service_id": { "type": "string" },
            "service_slug_snapshot": { "type": "string" },
            "service_label_snapshot": { "type": "string" },
            "capability_key": { "type": "string" },
            "admission_policy_version": { "type": "string" },
            "execution_mode": {
              "type": "string",
              "enum": ["interactive", "durable"]
            },
            "workflow_id": { "type": "string" },
            "member_id": { "type": "string" },
            "published_service_id": { "type": "string" }
          },
          "required": [
            "target_user_service_id",
            "service_slug_snapshot",
            "service_label_snapshot",
            "capability_key",
            "admission_policy_version",
            "execution_mode"
          ],
          "additionalProperties": false
        }
        """;

    public override async Task<string> ExecuteAsync(
        string argumentsJson,
        CancellationToken ct = default)
    {
        try
        {
            var args = ToolArgs.Parse(argumentsJson);
            if (args.ParseError is not null)
                return JsonDefaults.Error(args.ParseError);
            if (!TryParseExecutionMode(args.Str("execution_mode"), out var executionMode))
                return JsonDefaults.Error("execution_mode must be interactive or durable");
            if (!ExternalWorkflowCapabilityToolSupport.TryResolveAccess(out var access, out var error))
                return JsonDefaults.Error(error!);

            var result = await discoveryPort.DiscoverAsync(
                new DiscoverServiceApiWorkflowCapabilityRequest(
                    access!,
                    BuildCallerAuthority(),
                    Require(args, "target_user_service_id"),
                    Require(args, "service_slug_snapshot"),
                    Require(args, "service_label_snapshot"),
                    Require(args, "capability_key"),
                    ManagedDiscoveryPolicyVersion,
                    Require(args, "admission_policy_version"),
                    executionMode,
                    args.Str("workflow_id")?.Trim() ?? string.Empty,
                    args.Str("member_id")?.Trim() ?? string.Empty,
                    args.Str("published_service_id")?.Trim() ?? string.Empty),
                ct);
            var node = ExternalWorkflowCapabilityToolSupport.ToProtoJsonNode(result);
            NormalizeAuthoringAliases(node);
            return node?.ToJsonString() ?? JsonDefaults.Error("empty capability resolution");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArgumentException exception)
        {
            return JsonDefaults.Error(exception.Message);
        }
        catch (Exception exception)
        {
            return JsonDefaults.Error(
                $"Service API capability resolution failed: {exception.GetType().Name}");
        }
    }

    protected override bool IsVerifiedResult(JsonElement result) =>
        result.TryGetProperty("resolution", out var resolution) &&
        resolution.ValueKind == JsonValueKind.Object ||
        result.TryGetProperty("readiness_required", out var readiness) &&
        readiness.ValueKind == JsonValueKind.Object;

    private static ExternalCapabilityAuthorizationOwner BuildCallerAuthority()
    {
        var authority = AgentToolRequestContext.NyxIdAuthority;
        return new ExternalCapabilityAuthorizationOwner
        {
            Authority = authority.Platform?.Trim() ?? string.Empty,
            OwnerKind = ExternalCapabilityAuthorizationOwnerKind.Personal,
            OwnerSubject = authority.ExternalUserId?.Trim() ?? string.Empty,
        };
    }

    private static string Require(ToolArgs args, string name)
    {
        var value = args.Str(name)?.Trim();
        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"{name} is required")
            : value;
    }

    private static bool TryParseExecutionMode(
        string? value,
        out ExternalCapabilityExecutionMode executionMode)
    {
        executionMode = value?.Trim().ToLowerInvariant() switch
        {
            "interactive" => ExternalCapabilityExecutionMode.Interactive,
            "durable" => ExternalCapabilityExecutionMode.Durable,
            _ => ExternalCapabilityExecutionMode.Unspecified,
        };
        return executionMode != ExternalCapabilityExecutionMode.Unspecified;
    }

    private static void NormalizeAuthoringAliases(JsonNode? node)
    {
        if (node is JsonObject jsonObject)
        {
            Rename(jsonObject, "nyx_id_operation", "nyxid_operation");
            Rename(jsonObject, "nyx_id_request", "nyxid_request");
            foreach (var child in jsonObject.ToArray())
                NormalizeAuthoringAliases(child.Value);
            return;
        }

        if (node is JsonArray jsonArray)
        {
            foreach (var child in jsonArray)
                NormalizeAuthoringAliases(child);
        }
    }

    private static void Rename(JsonObject jsonObject, string source, string target)
    {
        if (!jsonObject.TryGetPropertyValue(source, out var value) ||
            jsonObject.ContainsKey(target))
        {
            return;
        }

        jsonObject.Remove(source);
        jsonObject[target] = value;
    }
}
