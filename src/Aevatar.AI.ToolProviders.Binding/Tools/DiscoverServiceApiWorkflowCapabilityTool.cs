using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Google.Protobuf;

namespace Aevatar.AI.ToolProviders.Binding.Tools;

public sealed partial class DiscoverServiceApiWorkflowCapabilityTool : ExternalWorkflowCapabilityReadOnlyTool
{
    private const string ManagedDiscoveryPolicyVersion = "service_api_skill_discovery.v1";
    private readonly IServiceApiWorkflowCapabilityDiscoveryPort _discoveryPort;

    public DiscoverServiceApiWorkflowCapabilityTool(
        IServiceApiWorkflowCapabilityDiscoveryPort discoveryPort)
    {
        _discoveryPort = discoveryPort;
    }

    public override string Name => "discover_service_api_workflow_capability";

    public override string Description =>
        "Resolve a descriptor-miss NyxID UserService API request shape through the Application-owned " +
        "managed Codex, exact Ornn verification, and official Web fallback path. The tool accepts typed discovery inputs, " +
        "returns a typed capability resolution, and never accepts arbitrary prompts or credentials.";

    public override string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "target_user_service_id": {
              "type": "string",
              "description": "Exact NyxID UserService id selected after exact descriptor miss"
            },
            "service_slug_snapshot": {
              "type": "string",
              "description": "Read-only service slug snapshot for the selected UserService"
            },
            "service_label_snapshot": {
              "type": "string",
              "description": "Read-only service label snapshot for the selected UserService"
            },
            "normalized_capability": {
              "type": "string",
              "description": "Normalized requested API capability, without credentials or runtime argument values"
            },
            "descriptor_inventory": {
              "type": "array",
              "description": "Typed descriptor inventory returned by list_external_workflow_capabilities",
              "items": { "type": "object" }
            },
            "managed_discovery_policy_version": {
              "type": "string",
              "const": "service_api_skill_discovery.v1"
            },
            "admission_policy_version": {
              "type": "string"
            },
            "capability_fingerprint": {
              "type": "string",
              "pattern": "^[0-9a-f]{64}$"
            }
          },
          "required": [
            "target_user_service_id",
            "service_slug_snapshot",
            "service_label_snapshot",
            "normalized_capability",
            "descriptor_inventory",
            "managed_discovery_policy_version",
            "admission_policy_version",
            "capability_fingerprint"
          ],
          "additionalProperties": false
        }
        """;

    public override async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        try
        {
            var args = ToolArgs.Parse(argumentsJson);
            if (args.ParseError is not null)
                return JsonDefaults.Error(args.ParseError);

            if (!ExternalWorkflowCapabilityToolSupport.TryResolveAccess(out var access, out var error))
                return JsonDefaults.Error(error!);

            var input = BuildInput(args, access!);
            if (input.Error is not null)
                return JsonDefaults.Error(input.Error);

            var discoveryResult = await _discoveryPort.DiscoverAsync(
                new DiscoverServiceApiWorkflowCapabilityRequest(access!, input.Value!),
                ct);

            return FormatDiscoveryResult(access!, input.Value!, discoveryResult);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JsonException)
        {
            return JsonDefaults.Error("service API discovery input must be valid typed JSON");
        }
        catch (InvalidProtocolBufferException)
        {
            return JsonDefaults.Error("service API discovery input must use typed Protobuf fields");
        }
        catch (Exception exception)
        {
            return JsonDefaults.Error($"Service API workflow capability discovery failed: {exception.GetType().Name}");
        }
    }

    protected override bool IsVerifiedResult(JsonElement result) =>
        result.TryGetProperty("scope_id", out var scopeId) &&
        scopeId.ValueKind == JsonValueKind.String &&
        result.TryGetProperty("target_user_service_id", out var targetUserServiceId) &&
        targetUserServiceId.ValueKind == JsonValueKind.String &&
        result.TryGetProperty("result_kind", out var resultKind) &&
        resultKind.ValueKind == JsonValueKind.String &&
        result.TryGetProperty("result", out var typedResult) &&
        typedResult.ValueKind == JsonValueKind.Object;

    private static ParsedInput BuildInput(
        ToolArgs args,
        ExternalWorkflowCapabilityAccessContext access)
    {
        var targetUserServiceId = Require(args, "target_user_service_id");
        if (targetUserServiceId.Error is not null)
            return ParsedInput.Failed(targetUserServiceId.Error);

        var serviceSlugSnapshot = Require(args, "service_slug_snapshot");
        if (serviceSlugSnapshot.Error is not null)
            return ParsedInput.Failed(serviceSlugSnapshot.Error);

        var serviceLabelSnapshot = Require(args, "service_label_snapshot");
        if (serviceLabelSnapshot.Error is not null)
            return ParsedInput.Failed(serviceLabelSnapshot.Error);

        var normalizedCapability = Require(args, "normalized_capability");
        if (normalizedCapability.Error is not null)
            return ParsedInput.Failed(normalizedCapability.Error);

        var managedPolicyVersion = Require(args, "managed_discovery_policy_version");
        if (managedPolicyVersion.Error is not null)
            return ParsedInput.Failed(managedPolicyVersion.Error);
        if (!string.Equals(
                managedPolicyVersion.Value,
                ManagedDiscoveryPolicyVersion,
                StringComparison.Ordinal))
        {
            return ParsedInput.Failed(
                $"managed_discovery_policy_version must be {ManagedDiscoveryPolicyVersion}");
        }

        var admissionPolicyVersion = Require(args, "admission_policy_version");
        if (admissionPolicyVersion.Error is not null)
            return ParsedInput.Failed(admissionPolicyVersion.Error);

        var fingerprint = Require(args, "capability_fingerprint");
        if (fingerprint.Error is not null)
            return ParsedInput.Failed(fingerprint.Error);
        if (!Sha256HexPattern().IsMatch(fingerprint.Value!))
            return ParsedInput.Failed("capability_fingerprint must be 64 lowercase SHA-256 hex characters");

        var descriptorInventory = args.RawOrStr("descriptor_inventory");
        if (string.IsNullOrWhiteSpace(descriptorInventory))
            return ParsedInput.Failed("descriptor_inventory is required");

        var input = new ServiceApiSkillDiscoveryInput
        {
            CallerAuthority = BuildCallerAuthority(),
            ScopeId = access.ScopeId,
            CallerId = access.CallerId,
            TargetUserServiceId = targetUserServiceId.Value,
            ServiceSlugSnapshot = serviceSlugSnapshot.Value,
            ServiceLabelSnapshot = serviceLabelSnapshot.Value,
            NormalizedCapability = normalizedCapability.Value,
            ManagedDiscoveryPolicyVersion = managedPolicyVersion.Value,
            AdmissionPolicyVersion = admissionPolicyVersion.Value,
            CapabilityFingerprint = fingerprint.Value,
        };
        input.DescriptorInventory.Add(ParseDescriptorInventory(descriptorInventory));
        return ParsedInput.Success(input);
    }

    private static string FormatDiscoveryResult(
        ExternalWorkflowCapabilityAccessContext access,
        ServiceApiSkillDiscoveryInput input,
        ServiceApiWorkflowCapabilityDiscoveryResult discoveryResult)
    {
        var resultObject = BuildResultObject(discoveryResult);
        var response = new JsonObject
        {
            ["scope_id"] = access.ScopeId,
            ["target_user_service_id"] = input.TargetUserServiceId,
            ["capability_fingerprint"] = input.CapabilityFingerprint,
            ["result_kind"] = FormatResultKind(discoveryResult),
            ["result"] = resultObject,
        };

        var selector = discoveryResult.ResultCase ==
                       ServiceApiWorkflowCapabilityDiscoveryResult.ResultOneofCase.Resolution
            ? BuildAuthoringSelector(discoveryResult.Resolution)
            : null;
        if (selector is not null)
            response["authoring_selector"] = selector;

        return response.ToJsonString();
    }

    private static JsonObject BuildResultObject(ServiceApiWorkflowCapabilityDiscoveryResult discoveryResult)
    {
        if (discoveryResult.ResultCase ==
            ServiceApiWorkflowCapabilityDiscoveryResult.ResultOneofCase.NoReliableApiSkill)
        {
            return new JsonObject
            {
                ["no_reliable_api_skill"] =
                    ExternalWorkflowCapabilityToolSupport.ToProtoJsonNode(discoveryResult.NoReliableApiSkill),
            };
        }

        var resultNode = discoveryResult.ResultCase ==
                         ServiceApiWorkflowCapabilityDiscoveryResult.ResultOneofCase.Resolution
            ? ExternalWorkflowCapabilityToolSupport.ToProtoJsonNode(discoveryResult.Resolution)
            : null;
        return resultNode as JsonObject ?? new JsonObject();
    }

    private static JsonObject? BuildAuthoringSelector(ServiceApiCapabilityResolution resolution) =>
        resolution.ResultCase switch
        {
            ServiceApiCapabilityResolution.ResultOneofCase.NyxidOperation =>
                ExternalWorkflowCapabilityToolSupport.BuildAuthoringSelectorNode(
                    new ExternalWorkflowCapabilitySelector
                    {
                        NyxIdOperation = resolution.NyxidOperation.Selector.Clone(),
                    }),
            ServiceApiCapabilityResolution.ResultOneofCase.NyxidRequest =>
                ExternalWorkflowCapabilityToolSupport.BuildAuthoringSelectorNode(
                    new ExternalWorkflowCapabilitySelector
                    {
                        NyxIdRequest = resolution.NyxidRequest.RequestShape.Selector.Clone(),
                    }),
            _ => null,
        };

    private static string FormatResultKind(ServiceApiWorkflowCapabilityDiscoveryResult discoveryResult)
    {
        if (discoveryResult.ResultCase ==
            ServiceApiWorkflowCapabilityDiscoveryResult.ResultOneofCase.NoReliableApiSkill)
        {
            return "no_reliable_skill";
        }

        return discoveryResult.ResultCase ==
               ServiceApiWorkflowCapabilityDiscoveryResult.ResultOneofCase.Resolution
            ? FormatResolutionKind(discoveryResult.Resolution)
            : "unspecified";
    }

    private static string FormatResolutionKind(ServiceApiCapabilityResolution resolution) =>
        resolution.ResultCase switch
        {
            ServiceApiCapabilityResolution.ResultOneofCase.NyxidOperation => "nyxid_operation",
            ServiceApiCapabilityResolution.ResultOneofCase.NyxidRequest => "nyxid_request",
            ServiceApiCapabilityResolution.ResultOneofCase.FallbackExhausted => "fallback_exhausted",
            _ => "unspecified",
        };

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

    private static IReadOnlyList<ExternalWorkflowCapabilityDescriptor> ParseDescriptorInventory(
        string descriptorInventoryJson)
    {
        var node = JsonNode.Parse(descriptorInventoryJson);
        if (node is not JsonArray descriptorArray)
            throw new JsonException("descriptor_inventory must be an array");

        var descriptors = new List<ExternalWorkflowCapabilityDescriptor>(descriptorArray.Count);
        foreach (var descriptorNode in descriptorArray)
        {
            if (descriptorNode is not JsonObject descriptorObject)
                throw new JsonException("descriptor_inventory entries must be objects");

            NormalizeDescriptorAuthoringAliases(descriptorObject);
            descriptors.Add(JsonParser.Default.Parse<ExternalWorkflowCapabilityDescriptor>(
                descriptorObject.ToJsonString()));
        }

        return descriptors;
    }

    private static void NormalizeDescriptorAuthoringAliases(JsonObject descriptorObject)
    {
        if (descriptorObject["selector"] is not JsonObject selectorObject)
            return;

        NormalizeAuthoringSelectorProperty(selectorObject, "nyxid_operation", "nyx_id_operation");
        NormalizeAuthoringSelectorProperty(selectorObject, "nyxid_request", "nyx_id_request");
        if (selectorObject["nyx_id_request"] is JsonObject requestObject)
        {
            NormalizeEnum(
                requestObject,
                "method",
                static value => value.Trim().ToUpperInvariant() switch
                {
                    "GET" => "NYX_ID_REQUEST_METHOD_GET",
                    "HEAD" => "NYX_ID_REQUEST_METHOD_HEAD",
                    "OPTIONS" => "NYX_ID_REQUEST_METHOD_OPTIONS",
                    "POST" => "NYX_ID_REQUEST_METHOD_POST",
                    "PUT" => "NYX_ID_REQUEST_METHOD_PUT",
                    "PATCH" => "NYX_ID_REQUEST_METHOD_PATCH",
                    "DELETE" => "NYX_ID_REQUEST_METHOD_DELETE",
                    _ => value,
                });
            NormalizeEnum(
                requestObject,
                "body_mode",
                static value => value.Trim().ToLowerInvariant() switch
                {
                    "none" => "NYX_ID_REQUEST_BODY_MODE_NONE",
                    "json" => "NYX_ID_REQUEST_BODY_MODE_JSON",
                    _ => value,
                });
            NormalizeEnum(
                requestObject,
                "response_mode",
                static value => value.Trim().ToLowerInvariant() switch
                {
                    "text" => "NYX_ID_REQUEST_RESPONSE_MODE_TEXT",
                    "file_artifact" => "NYX_ID_REQUEST_RESPONSE_MODE_FILE_ARTIFACT",
                    _ => value,
                });
        }
    }

    private static void NormalizeAuthoringSelectorProperty(
        JsonObject selectorObject,
        string authoringPropertyName,
        string protoPropertyName)
    {
        if (!selectorObject.ContainsKey(authoringPropertyName) ||
            selectorObject.ContainsKey(protoPropertyName))
        {
            return;
        }

        var value = selectorObject[authoringPropertyName];
        selectorObject.Remove(authoringPropertyName);
        selectorObject[protoPropertyName] = value;
    }

    private static void NormalizeEnum(
        JsonObject requestObject,
        string propertyName,
        Func<string, string> normalize)
    {
        if (requestObject[propertyName] is JsonValue value &&
            value.TryGetValue<string>(out var text))
        {
            requestObject[propertyName] = normalize(text);
        }
    }

    private static RequiredString Require(ToolArgs args, string name)
    {
        var value = args.Str(name)?.Trim();
        return string.IsNullOrWhiteSpace(value)
            ? RequiredString.Failed($"{name} is required")
            : RequiredString.Success(value);
    }

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256HexPattern();

    private sealed record ParsedInput(ServiceApiSkillDiscoveryInput? Value, string? Error)
    {
        public static ParsedInput Success(ServiceApiSkillDiscoveryInput value) => new(value, null);
        public static ParsedInput Failed(string error) => new(null, error);
    }

    private sealed record RequiredString(string? Value, string? Error)
    {
        public static RequiredString Success(string value) => new(value, null);
        public static RequiredString Failed(string error) => new(null, error);
    }
}
