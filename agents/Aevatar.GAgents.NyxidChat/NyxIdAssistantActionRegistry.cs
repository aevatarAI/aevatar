using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aevatar.AI.Abstractions;

namespace Aevatar.GAgents.NyxidChat;

public sealed class NyxIdAssistantActionRegistryException : Exception
{
    public NyxIdAssistantActionRegistryException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

public sealed record NyxIdAssistantActionValidation(
    NyxIdAssistantActionDefinitionSnapshot Definition,
    NyxIdAssistantActionParams Params);

/// <summary>
/// Immutable, startup-pinned view of the NyxID action manifest. External JSON
/// schema stays at this adapter boundary; validated requests are converted to
/// closed protobuf action definitions and params before actor dispatch.
/// </summary>
public sealed class NyxIdAssistantActionRegistry
{
    public const int SupportedSchemaVersion = 4;
    public const string LegacyRegistryRevision = "nyxid-assistant-actions.v4";
    public const string WaveOneDraftRegistryRevision = "nyxid-assistant-actions.v5";
    public const string LeastScopeRegistryRevision = "nyxid-assistant-actions.v6";
    public const string KeyRotationRegistryRevision = "nyxid-assistant-actions.v7";
    public const string SupportedRegistryRevision = "nyxid-assistant-actions.v8";
    public const string ServiceAccessReviewRegistryRevision =
        "aevatar-nyxid-actions.v1";

    private const string ServiceAccessReviewWireAction = "service.access_review";

    private const string SchemaUnsupported = "NYXID_ACTION_SCHEMA_UNSUPPORTED";
    private const string RevisionUnsupported = "NYXID_ACTION_REGISTRY_REVISION_UNSUPPORTED";
    private const string ActionUnsupported = "NYXID_ACTION_UNSUPPORTED";
    private const string TierUnsupported = "NYXID_ACTION_TIER_UNSUPPORTED";
    private const string ParamsInvalid = "NYXID_ACTION_PARAMS_INVALID";
    private const string PolicyCallerOwned = "NYXID_ACTION_POLICY_CALLER_OWNED";
    private const string RegistryInvalid = "NYXID_ACTION_REGISTRY_INVALID";

    private const string ServiceReauthorizeIdentityInvalidMessage =
        "The service reauthorization identity is invalid.";
    private const string ServiceReauthorizeScopeCountInvalidMessage =
        "Service reauthorization requires an exact nonempty scope set.";
    private const string ServiceReauthorizeScopesInvalidMessage =
        "The service reauthorization scopes are invalid.";

    private const string ServiceConnectParamsSchema = """
        {
          "oneOf": [
            {
              "type": "object",
              "additionalProperties": false,
              "required": ["catalogService"],
              "properties": {
                "catalogService": {
                  "type": "object",
                  "additionalProperties": false,
                  "required": ["serviceSlug"],
                  "properties": {
                    "serviceSlug": {"type": "string"},
                    "requestedScopes": {"type": "array", "items": {"type": "string"}},
                    "viaNodeId": {"type": "string"},
                    "targetOrgId": {"type": "string"}
                  }
                }
              }
            },
            {
              "type": "object",
              "additionalProperties": false,
              "required": ["customService"],
              "properties": {
                "customService": {
                  "type": "object",
                  "additionalProperties": false,
                  "required": ["name", "endpointUrl", "authMethod"],
                  "properties": {
                    "name": {"type": "string"},
                    "endpointUrl": {"type": "string"},
                    "authMethod": {"type": "string"},
                    "authKeyName": {"type": "string"},
                    "viaNodeId": {"type": "string"},
                    "targetOrgId": {"type": "string"}
                  }
                }
              }
            }
          ]
        }
        """;

    private const string ServiceReauthorizeParamsSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["userServiceId", "requestedScopes"],
          "properties": {
            "userServiceId": {"type": "string"},
            "requestedScopes": {"type": "array", "items": {"type": "string"}}
          }
        }
        """;

    private const string KeyCreateParamsSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["name", "platform", "allowedServiceIds"],
          "properties": {
            "name": {"type": "string"},
            "platform": {"type": "string"},
            "allowedServiceIds": {"type": "array", "items": {"type": "string"}}
          }
        }
        """;

    private const string LeastScopeKeyCreateParamsSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["name", "platform", "allowedServiceIds"],
          "properties": {
            "name": {"type": "string"},
            "platform": {"type": "string"},
            "allowedServiceIds": {
              "type": "array",
              "minItems": 1,
              "maxItems": 64,
              "uniqueItems": true,
              "items": {"type": "string"}
            }
          }
        }
        """;

    private const string KeyRotateParamsSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["keyId"],
          "properties": {
            "keyId": {"type": "string"}
          }
        }
        """;

    private static readonly FrozenDictionary<string, ActionContract> SupportedActions =
        new Dictionary<string, ActionContract>(StringComparer.Ordinal)
        {
            ["service.connect"] = new(
                NyxIdAssistantActionKind.ServiceConnect,
                ParseServiceConnect,
                ServiceConnectParamsSchema,
                NyxIdAssistantActionRisk.Grant,
                true),
            ["service.reauthorize"] = new(
                NyxIdAssistantActionKind.ServiceReauthorize,
                ParseServiceReauthorize,
                ServiceReauthorizeParamsSchema,
                NyxIdAssistantActionRisk.Grant,
                false),
            ["provider.set_app_credentials"] = new(
                NyxIdAssistantActionKind.ProviderSetAppCredentials,
                ParseProviderSetAppCredentials),
            ["key.create"] = new(
                NyxIdAssistantActionKind.KeyCreate,
                ParseKeyCreate,
                KeyCreateParamsSchema,
                NyxIdAssistantActionRisk.Grant,
                false),
            ["key.rotate"] = new(
                NyxIdAssistantActionKind.KeyRotate,
                ParseKeyRotate,
                KeyRotateParamsSchema,
                NyxIdAssistantActionRisk.Grant,
                false),
            ["node.register_token"] = new(
                NyxIdAssistantActionKind.NodeRegisterToken,
                ParseNodeRegisterToken),
            ["node.rotate_token"] = new(
                NyxIdAssistantActionKind.NodeRotateToken,
                ParseNodeRotateToken),
            ["node.inject_credential"] = new(
                NyxIdAssistantActionKind.NodeInjectCredential,
                ParseNodeInjectCredential),
            ["service_account.create"] = new(
                NyxIdAssistantActionKind.ServiceAccountCreate,
                ParseServiceAccountCreate),
            ["service_account.rotate_secret"] = new(
                NyxIdAssistantActionKind.ServiceAccountRotateSecret,
                ParseServiceAccountRotateSecret),
            ["developer_app.create"] = new(
                NyxIdAssistantActionKind.DeveloperAppCreate,
                ParseDeveloperAppCreate),
            ["developer_app.rotate_secret"] = new(
                NyxIdAssistantActionKind.DeveloperAppRotateSecret,
                ParseDeveloperAppRotateSecret),
            ["account.mfa_setup"] = new(
                NyxIdAssistantActionKind.AccountMfaSetup,
                ParseAccountMfaSetup),
            ["device.onboard"] = new(
                NyxIdAssistantActionKind.DeviceOnboard,
                ParseDeviceOnboard),
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly FrozenDictionary<string, FrozenSet<string>> PinnedActionsByRevision =
        new Dictionary<string, FrozenSet<string>>(StringComparer.Ordinal)
        {
            [LegacyRegistryRevision] = new[]
            {
                "service.connect",
            }.ToFrozenSet(StringComparer.Ordinal),
            [WaveOneDraftRegistryRevision] = new[]
            {
                "service.connect",
                "service.reauthorize",
                "key.create",
                "key.rotate",
            }.ToFrozenSet(StringComparer.Ordinal),
            [LeastScopeRegistryRevision] = new[]
            {
                "service.connect",
                "key.create",
            }.ToFrozenSet(StringComparer.Ordinal),
            [KeyRotationRegistryRevision] = new[]
            {
                "service.connect",
                "key.create",
                "key.rotate",
            }.ToFrozenSet(StringComparer.Ordinal),
            [SupportedRegistryRevision] = new[]
            {
                "service.connect",
                "service.reauthorize",
                "key.create",
                "key.rotate",
            }.ToFrozenSet(StringComparer.Ordinal),
        }.ToFrozenDictionary(StringComparer.Ordinal);

    // A manifest contract can be known without being executable by Aevatar.
    // Each revision exposes only actions that have a typed producer, wire
    // mapper, and typed postcondition reader on the canonical actor path.
    // Revision v8 pins service.reauthorize so the manifest loads, but the
    // action stays closed until the complete cross-repo path is released.
    private static readonly FrozenDictionary<string, FrozenSet<string>> ExecutableActionsByRevision =
        new Dictionary<string, FrozenSet<string>>(StringComparer.Ordinal)
        {
            [LegacyRegistryRevision] = new[] { "service.connect" }
                .ToFrozenSet(StringComparer.Ordinal),
            [WaveOneDraftRegistryRevision] = new[] { "service.connect" }
                .ToFrozenSet(StringComparer.Ordinal),
            [LeastScopeRegistryRevision] = new[] { "service.connect", "key.create" }
                .ToFrozenSet(StringComparer.Ordinal),
            [KeyRotationRegistryRevision] = new[] { "service.connect", "key.create", "key.rotate" }
                .ToFrozenSet(StringComparer.Ordinal),
            [SupportedRegistryRevision] = new[] { "service.connect", "key.create", "key.rotate" }
                .ToFrozenSet(StringComparer.Ordinal),
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly NyxIdAssistantActionDefinitionSnapshot ServiceAccessReviewDefinition =
        new()
        {
            SchemaVersion = SupportedSchemaVersion,
            RegistryRevision = ServiceAccessReviewRegistryRevision,
            Action = NyxIdAssistantActionKind.ServiceAccessReview,
            WireAction = ServiceAccessReviewWireAction,
            Description = "Review access to one exact connected NyxID service.",
            AdvisoryRisk = NyxIdAssistantActionRisk.Grant,
            Tier = NyxIdAssistantActionTier.V1,
            RememberEligible = false,
        };

    private readonly FrozenDictionary<string, RegistryEntry> _entries;
    private readonly FrozenSet<string> _executableActions;

    private NyxIdAssistantActionRegistry(
        int schemaVersion,
        string registryRevision,
        IReadOnlyDictionary<string, RegistryEntry> entries,
        FrozenSet<string> executableActions)
    {
        SchemaVersion = schemaVersion;
        RegistryRevision = registryRevision;
        _entries = entries.ToFrozenDictionary(StringComparer.Ordinal);
        _executableActions = executableActions;
    }

    public int SchemaVersion { get; }
    public string RegistryRevision { get; }

    internal static NyxIdAssistantActionRegistry CreateDisabled() =>
        new(
            SupportedSchemaVersion,
            SupportedRegistryRevision,
            new Dictionary<string, RegistryEntry>(StringComparer.Ordinal),
            Array.Empty<string>().ToFrozenSet(StringComparer.Ordinal));

    internal static bool IsSupportedRegistryRevision(string revision) =>
        string.Equals(
            revision,
            ServiceAccessReviewRegistryRevision,
            StringComparison.Ordinal) ||
        PinnedActionsByRevision.ContainsKey(revision);

    internal static bool IsActionExecutable(
        string revision,
        NyxIdAssistantActionKind action)
    {
        if (string.Equals(
                revision,
                ServiceAccessReviewRegistryRevision,
                StringComparison.Ordinal))
        {
            return action == NyxIdAssistantActionKind.ServiceAccessReview;
        }

        var wireAction = action switch
        {
            NyxIdAssistantActionKind.ServiceConnect => "service.connect",
            NyxIdAssistantActionKind.KeyCreate => "key.create",
            NyxIdAssistantActionKind.KeyRotate => "key.rotate",
            NyxIdAssistantActionKind.ServiceReauthorize => "service.reauthorize",
            _ => null,
        };
        return wireAction is not null &&
               ExecutableActionsByRevision.TryGetValue(revision, out var executableActions) &&
               executableActions.Contains(wireAction);
    }

    public static NyxIdAssistantActionRegistry Load(string registryJson)
    {
        try
        {
            using var document = JsonDocument.Parse(registryJson);
            var root = RequireObject(document.RootElement);
            var schemaVersion = RequireInt32(root, "schema_version");
            if (schemaVersion != SupportedSchemaVersion)
                throw Error(SchemaUnsupported, "The NyxID action schema version is not supported.");

            var revision = ReadRequiredString(root, "revision", 128);
            if (!PinnedActionsByRevision.TryGetValue(revision, out var pinnedActions) ||
                !ExecutableActionsByRevision.TryGetValue(revision, out var executableActions))
            {
                throw Error(RevisionUnsupported, "The NyxID action registry revision is not supported.");
            }

            var actions = RequireProperty(root, "actions");
            if (actions.ValueKind != JsonValueKind.Array)
                throw Error(RegistryInvalid, "The NyxID action registry actions field must be an array.");

            var entries = new Dictionary<string, RegistryEntry>(StringComparer.Ordinal);
            foreach (var item in actions.EnumerateArray())
            {
                var descriptor = RequireObject(item);
                var wireAction = ReadRequiredString(descriptor, "action", 128);
                if (!SupportedActions.TryGetValue(wireAction, out var contract) ||
                    !pinnedActions.Contains(wireAction))
                {
                    continue;
                }

                var tier = ParseTier(ReadRequiredString(descriptor, "tier", 32));
                if (tier != NyxIdAssistantActionTier.V1)
                    throw Error(TierUnsupported, "Only NyxID Assistant v1 actions are supported.");

                var risk = ParseRisk(ReadRequiredString(descriptor, "risk", 32));
                var rememberEligible = RequireBoolean(descriptor, "remember_eligible");
                if (risk == NyxIdAssistantActionRisk.Destructive && rememberEligible)
                    throw Error(RegistryInvalid, "Destructive actions cannot be remember eligible.");

                var paramsSchema = RequireProperty(descriptor, "params_schema").Clone();
                ValidateSchema(paramsSchema);
                ValidatePinnedContract(
                    contract,
                    revision,
                    paramsSchema,
                    risk,
                    rememberEligible);
                var definition = new NyxIdAssistantActionDefinitionSnapshot
                {
                    SchemaVersion = schemaVersion,
                    RegistryRevision = revision,
                    Action = contract.Action,
                    WireAction = wireAction,
                    Description = ReadRequiredString(descriptor, "description", 2048),
                    AdvisoryRisk = risk,
                    Tier = tier,
                    RememberEligible = rememberEligible,
                };
                if (!entries.TryAdd(
                        wireAction,
                        new RegistryEntry(definition, paramsSchema, contract.Parser)))
                {
                    throw Error(RegistryInvalid, "The NyxID action registry contains a duplicate action.");
                }
            }

            if (!pinnedActions.All(entries.ContainsKey))
            {
                throw Error(
                    RegistryInvalid,
                    "The NyxID action registry is missing an action required by its pinned revision.");
            }

            if (!executableActions.All(entries.ContainsKey))
            {
                throw Error(
                    ActionUnsupported,
                    "The NyxID action registry is missing an action required by this Aevatar version.");
            }

            return new NyxIdAssistantActionRegistry(
                schemaVersion,
                revision,
                entries,
                executableActions);
        }
        catch (NyxIdAssistantActionRegistryException)
        {
            throw;
        }
        catch (NyxIdActionSecretPolicyException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw Error(RegistryInvalid, "The NyxID action registry must be valid JSON.");
        }
    }

    public bool TryGetDefinition(
        string wireAction,
        out NyxIdAssistantActionDefinitionSnapshot definition)
    {
        var normalizedAction = wireAction?.Trim() ?? string.Empty;
        if (string.Equals(
                normalizedAction,
                ServiceAccessReviewWireAction,
                StringComparison.Ordinal))
        {
            definition = ServiceAccessReviewDefinition.Clone();
            return true;
        }

        if (_executableActions.Contains(normalizedAction) &&
            _entries.TryGetValue(normalizedAction, out var entry))
        {
            definition = entry.Definition.Clone();
            return true;
        }

        definition = null!;
        return false;
    }

    public NyxIdAssistantActionValidation ValidateRequest(
        string wireAction,
        string paramsJson,
        string? callerRisk = null,
        bool? callerRememberEligible = null)
    {
        if (callerRisk is not null || callerRememberEligible.HasValue)
        {
            throw Error(
                PolicyCallerOwned,
                "Action risk and remember policy come only from the pinned NyxID registry.");
        }

        var normalizedAction = wireAction?.Trim() ?? string.Empty;
        if (!_executableActions.Contains(normalizedAction) ||
            !_entries.TryGetValue(normalizedAction, out var entry))
            throw Error(ActionUnsupported, "The NyxID action is not present in the pinned registry.");

        NyxIdActionSecretPolicy.ValidateParamsJson(paramsJson);
        try
        {
            using var document = JsonDocument.Parse(paramsJson);
            ValidateAgainstSchema(document.RootElement, entry.ParamsSchema, "params");
            var typedParams = entry.Parser(document.RootElement);
            return new NyxIdAssistantActionValidation(
                entry.Definition.Clone(),
                typedParams);
        }
        catch (NyxIdActionSecretPolicyException)
        {
            throw;
        }
        catch (NyxIdAssistantActionRegistryException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw Error(ParamsInvalid, "The NyxID action params are invalid.");
        }
    }

    public NyxIdAssistantActionValidation ResolveCatalogServiceConnect(
        string serviceSlug,
        IEnumerable<string>? requestedScopes = null)
    {
        if (!_entries.TryGetValue("service.connect", out var entry) ||
            entry.Definition.Action != NyxIdAssistantActionKind.ServiceConnect)
        {
            throw Error(ActionUnsupported, "Catalog service connect is not present in the pinned registry.");
        }

        var normalizedSlug = NormalizeString(serviceSlug, 128, required: true);
        if (!normalizedSlug.All(static character =>
                char.IsAsciiLetterOrDigit(character) ||
                character is '-' or '_' or '.'))
        {
            throw Error(ParamsInvalid, "The catalog service slug is invalid.");
        }

        var value = new NyxIdCatalogServiceConnectParams
        {
            ServiceSlug = normalizedSlug,
        };
        if (requestedScopes is not null)
        {
            value.RequestedScopes.AddRange(requestedScopes
                .Select(scope => NormalizeString(scope, 256, required: true))
                .Distinct(StringComparer.Ordinal));
        }

        return new NyxIdAssistantActionValidation(
            entry.Definition.Clone(),
            new NyxIdAssistantActionParams { CatalogServiceConnect = value });
    }

    public NyxIdAssistantActionValidation ResolveServiceAccessReview(
        string userServiceId,
        string serviceSlug,
        string resourceUri)
    {
        var normalizedUserServiceId = NormalizeString(userServiceId, 256, required: true);
        if (!string.Equals(userServiceId, normalizedUserServiceId, StringComparison.Ordinal) ||
            normalizedUserServiceId.Any(char.IsWhiteSpace))
        {
            throw Error(ParamsInvalid, "The service access identity is invalid.");
        }

        var normalizedSlug = NormalizeString(serviceSlug, 128, required: true);
        if (!string.Equals(serviceSlug, normalizedSlug, StringComparison.Ordinal) ||
            !normalizedSlug.All(static character =>
                char.IsAsciiLetterOrDigit(character) ||
                character is '-' or '_' or '.'))
        {
            throw Error(ParamsInvalid, "The service access slug is invalid.");
        }

        var normalizedResourceUri = NormalizeString(resourceUri, 512, required: true);
        var expectedPathSuffix =
            $"/api/v1/proxy/s/{Uri.EscapeDataString(normalizedSlug)}";
        if (!string.Equals(resourceUri, normalizedResourceUri, StringComparison.Ordinal) ||
            !Uri.TryCreate(normalizedResourceUri, UriKind.Absolute, out var parsedResourceUri) ||
            !string.Equals(parsedResourceUri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(parsedResourceUri.UserInfo) ||
            !string.IsNullOrEmpty(parsedResourceUri.Query) ||
            !string.IsNullOrEmpty(parsedResourceUri.Fragment) ||
            !parsedResourceUri.AbsolutePath.EndsWith(
                expectedPathSuffix,
                StringComparison.Ordinal))
        {
            throw Error(ParamsInvalid, "The service access resource URI is invalid.");
        }

        return new NyxIdAssistantActionValidation(
            ServiceAccessReviewDefinition.Clone(),
            new NyxIdAssistantActionParams
            {
                ServiceAccessReview = new NyxIdServiceAccessReviewParams
                {
                    UserServiceId = normalizedUserServiceId,
                    ServiceSlug = normalizedSlug,
                    ResourceUri = normalizedResourceUri,
                },
            });
    }

    public NyxIdAssistantActionValidation ResolveKeyCreate(
        NyxIdKeyCreateActionRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        if (!_entries.TryGetValue("key.create", out var entry) ||
            !_executableActions.Contains("key.create") ||
            entry.Definition.Action != NyxIdAssistantActionKind.KeyCreate)
        {
            throw Error(ActionUnsupported, "Key creation is not present in the pinned registry.");
        }

        var name = NormalizeString(requirement.Name, 256, required: true);
        var platform = NormalizeString(requirement.Platform, 128, required: true);
        var allowedServiceIds = NormalizeDistinctSet(
            requirement.AllowedServiceIds,
            minCount: 1,
            maxCount: 64,
            maxItemLength: 256,
            countInvalidMessage: "Key creation requires an exact nonempty service set.",
            itemInvalidMessage: "The key creation service identities are invalid.");

        var value = new NyxIdKeyCreateParams
        {
            Name = name,
            Platform = platform,
        };
        value.AllowedServiceIds.Add(allowedServiceIds);
        return new NyxIdAssistantActionValidation(
            entry.Definition.Clone(),
            new NyxIdAssistantActionParams { KeyCreate = value });
    }

    public NyxIdAssistantActionValidation ResolveKeyRotate(
        NyxIdKeyRotateActionRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        if (!_entries.TryGetValue("key.rotate", out var entry) ||
            !_executableActions.Contains("key.rotate") ||
            entry.Definition.Action != NyxIdAssistantActionKind.KeyRotate)
        {
            throw Error(ActionUnsupported, "Key rotation is not present in the pinned registry.");
        }

        var keyId = NormalizeSafeIdentity(
            requirement.KeyId,
            256,
            "The key rotation identity is invalid.");

        return new NyxIdAssistantActionValidation(
            entry.Definition.Clone(),
            new NyxIdAssistantActionParams
            {
                KeyRotate = new NyxIdKeyRotateParams { KeyId = keyId },
            });
    }

    public NyxIdAssistantActionValidation ResolveServiceReauthorize(
        NyxIdServiceReauthorizeActionRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        if (!_entries.TryGetValue("service.reauthorize", out var entry) ||
            !_executableActions.Contains("service.reauthorize") ||
            entry.Definition.Action != NyxIdAssistantActionKind.ServiceReauthorize)
        {
            throw Error(
                ActionUnsupported,
                "Service reauthorization is not present in the pinned registry.");
        }

        var userServiceId = NormalizeSafeIdentity(
            requirement.UserServiceId,
            256,
            ServiceReauthorizeIdentityInvalidMessage);
        var requestedScopes = NormalizeDistinctSet(
            requirement.RequestedScopes,
            minCount: 1,
            maxCount: 64,
            maxItemLength: 256,
            countInvalidMessage: ServiceReauthorizeScopeCountInvalidMessage,
            itemInvalidMessage: ServiceReauthorizeScopesInvalidMessage);

        var value = new NyxIdServiceReauthorizeParams { UserServiceId = userServiceId };
        value.RequestedScopes.Add(requestedScopes);
        return new NyxIdAssistantActionValidation(
            entry.Definition.Clone(),
            new NyxIdAssistantActionParams { ServiceReauthorize = value });
    }

    private static NyxIdAssistantActionParams ParseServiceConnect(JsonElement root)
    {
        EnsureOnlyProperties(root, "catalogService", "customService");
        var hasCatalog = root.TryGetProperty("catalogService", out var catalog);
        var hasCustom = root.TryGetProperty("customService", out var custom);
        if (hasCatalog == hasCustom)
            throw Error(ParamsInvalid, "Service connect requires exactly one typed variant.");

        if (hasCatalog)
        {
            EnsureOnlyProperties(
                catalog,
                "serviceSlug",
                "requestedScopes",
                "viaNodeId",
                "targetOrgId");
            var value = new NyxIdCatalogServiceConnectParams
            {
                ServiceSlug = ReadRequiredString(catalog, "serviceSlug", 128),
                ViaNodeId = ReadOptionalString(catalog, "viaNodeId", 256),
                TargetOrgId = ReadOptionalString(catalog, "targetOrgId", 256),
            };
            value.RequestedScopes.AddRange(ReadStringArray(catalog, "requestedScopes", 64, 256));
            return new NyxIdAssistantActionParams { CatalogServiceConnect = value };
        }

        EnsureOnlyProperties(
            custom,
            "name",
            "endpointUrl",
            "authMethod",
            "authKeyName",
            "viaNodeId",
            "targetOrgId");
        return new NyxIdAssistantActionParams
        {
            CustomServiceConnect = new NyxIdCustomServiceConnectParams
            {
                Name = ReadRequiredString(custom, "name", 256),
                EndpointUrl = NyxIdActionSecretPolicy.NormalizeSafeUrl(
                    ReadRequiredString(custom, "endpointUrl", 2048)),
                AuthMethod = ReadEnumString(
                    custom,
                    "authMethod",
                    "bearer",
                    "header",
                    "query",
                    "path",
                    "basic",
                    "body",
                    "none"),
                AuthKeyName = ReadOptionalString(custom, "authKeyName", 256),
                ViaNodeId = ReadOptionalString(custom, "viaNodeId", 256),
                TargetOrgId = ReadOptionalString(custom, "targetOrgId", 256),
            },
        };
    }

    internal static NyxIdAssistantActionParams ParseServiceReauthorize(JsonElement root)
    {
        EnsureOnlyProperties(root, "userServiceId", "requestedScopes");
        var requestedScopes = ReadStringArray(
            root,
            "requestedScopes",
            64,
            256,
            rejectDuplicates: true,
            rejectNormalizationChanges: true);
        if (requestedScopes.Count == 0)
            throw Error(ParamsInvalid, ServiceReauthorizeScopeCountInvalidMessage);

        var value = new NyxIdServiceReauthorizeParams
        {
            UserServiceId = ReadSafeIdentity(
                root,
                "userServiceId",
                256,
                ServiceReauthorizeIdentityInvalidMessage),
        };
        value.RequestedScopes.AddRange(requestedScopes);
        return new NyxIdAssistantActionParams { ServiceReauthorize = value };
    }

    private static NyxIdAssistantActionParams ParseProviderSetAppCredentials(JsonElement root)
    {
        EnsureOnlyProperties(root, "providerSlug");
        return new NyxIdAssistantActionParams
        {
            ProviderSetAppCredentials = new NyxIdProviderSetAppCredentialsParams
            {
                ProviderSlug = ReadRequiredString(root, "providerSlug", 128),
            },
        };
    }

    internal static NyxIdAssistantActionParams ParseKeyCreate(JsonElement root)
    {
        EnsureOnlyProperties(root, "name", "platform", "allowedServiceIds");
        var allowedServiceIds = ReadStringArray(
            root,
            "allowedServiceIds",
            64,
            256,
            rejectDuplicates: true,
            rejectNormalizationChanges: true);
        if (allowedServiceIds.Count == 0)
        {
            throw Error(
                ParamsInvalid,
                "Key creation requires at least one exact allowed service identity.");
        }

        var value = new NyxIdKeyCreateParams
        {
            Name = ReadRequiredString(root, "name", 256),
            Platform = ReadRequiredString(root, "platform", 128),
        };
        value.AllowedServiceIds.AddRange(allowedServiceIds);
        return new NyxIdAssistantActionParams { KeyCreate = value };
    }

    private static NyxIdAssistantActionParams ParseKeyRotate(JsonElement root)
    {
        EnsureOnlyProperties(root, "keyId");
        return new NyxIdAssistantActionParams
        {
            KeyRotate = new NyxIdKeyRotateParams
            {
                KeyId = ReadRequiredString(root, "keyId", 256),
            },
        };
    }

    private static NyxIdAssistantActionParams ParseNodeRegisterToken(JsonElement root)
    {
        EnsureOnlyProperties(root, "name");
        var name = ReadRequiredString(root, "name", 64);
        if (!name.All(static character =>
                char.IsAsciiLetterLower(character) ||
                char.IsAsciiDigit(character) ||
                character == '-'))
        {
            throw Error(ParamsInvalid, "Node names must use lowercase letters, digits, and hyphens.");
        }

        return new NyxIdAssistantActionParams
        {
            NodeRegisterToken = new NyxIdNodeRegisterTokenParams { Name = name },
        };
    }

    private static NyxIdAssistantActionParams ParseNodeRotateToken(JsonElement root)
    {
        EnsureOnlyProperties(root, "nodeId");
        return new NyxIdAssistantActionParams
        {
            NodeRotateToken = new NyxIdNodeRotateTokenParams
            {
                NodeId = ReadRequiredString(root, "nodeId", 256),
            },
        };
    }

    private static NyxIdAssistantActionParams ParseNodeInjectCredential(JsonElement root)
    {
        EnsureOnlyProperties(root, "nodeId", "serviceSlug");
        return new NyxIdAssistantActionParams
        {
            NodeInjectCredential = new NyxIdNodeInjectCredentialParams
            {
                NodeId = ReadRequiredString(root, "nodeId", 256),
                ServiceSlug = ReadRequiredString(root, "serviceSlug", 128),
            },
        };
    }

    private static NyxIdAssistantActionParams ParseServiceAccountCreate(JsonElement root)
    {
        EnsureOnlyProperties(root, "name", "allowedScopes");
        var value = new NyxIdServiceAccountCreateParams
        {
            Name = ReadRequiredString(root, "name", 256),
        };
        value.AllowedScopes.AddRange(ReadStringArray(root, "allowedScopes", 128, 256));
        return new NyxIdAssistantActionParams { ServiceAccountCreate = value };
    }

    private static NyxIdAssistantActionParams ParseServiceAccountRotateSecret(JsonElement root)
    {
        EnsureOnlyProperties(root, "serviceAccountId");
        return new NyxIdAssistantActionParams
        {
            ServiceAccountRotateSecret = new NyxIdServiceAccountRotateSecretParams
            {
                ServiceAccountId = ReadRequiredString(root, "serviceAccountId", 256),
            },
        };
    }

    private static NyxIdAssistantActionParams ParseDeveloperAppCreate(JsonElement root)
    {
        EnsureOnlyProperties(root, "name", "redirectUris");
        var value = new NyxIdDeveloperAppCreateParams
        {
            Name = ReadRequiredString(root, "name", 256),
        };
        value.RedirectUris.AddRange(
            ReadStringArray(root, "redirectUris", 32, 2048)
                .Select(NyxIdActionSecretPolicy.NormalizeSafeUrl));
        return new NyxIdAssistantActionParams { DeveloperAppCreate = value };
    }

    private static NyxIdAssistantActionParams ParseDeveloperAppRotateSecret(JsonElement root)
    {
        EnsureOnlyProperties(root, "clientId");
        return new NyxIdAssistantActionParams
        {
            DeveloperAppRotateSecret = new NyxIdDeveloperAppRotateSecretParams
            {
                ClientId = ReadRequiredString(root, "clientId", 256),
            },
        };
    }

    private static NyxIdAssistantActionParams ParseAccountMfaSetup(JsonElement root)
    {
        EnsureOnlyProperties(root);
        return new NyxIdAssistantActionParams
        {
            AccountMfaSetup = new NyxIdAccountMfaSetupParams(),
        };
    }

    private static NyxIdAssistantActionParams ParseDeviceOnboard(JsonElement root)
    {
        EnsureOnlyProperties(root, "label");
        return new NyxIdAssistantActionParams
        {
            DeviceOnboard = new NyxIdDeviceOnboardParams
            {
                Label = ReadRequiredString(root, "label", 256),
            },
        };
    }

    private static void ValidateSchema(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object)
            throw Error(RegistryInvalid, "Action params_schema must be an object.");

        ValidateSchemaNode(schema);
    }

    private static void ValidatePinnedContract(
        ActionContract contract,
        string revision,
        JsonElement paramsSchema,
        NyxIdAssistantActionRisk risk,
        bool rememberEligible)
    {
        var pinnedParamsSchema = revision is LeastScopeRegistryRevision
                                     or KeyRotationRegistryRevision
                                     or SupportedRegistryRevision &&
                                 contract.Action == NyxIdAssistantActionKind.KeyCreate
            ? LeastScopeKeyCreateParamsSchema
            : contract.PinnedParamsSchema;
        if (pinnedParamsSchema is null)
            return;

        var expectedSchema = JsonNode.Parse(pinnedParamsSchema);
        var actualSchema = JsonNode.Parse(paramsSchema.GetRawText());
        if (!JsonNode.DeepEquals(expectedSchema, actualSchema) ||
            risk != contract.PinnedRisk ||
            rememberEligible != contract.PinnedRememberEligible)
        {
            throw Error(
                RegistryInvalid,
                "The NyxID action descriptor does not match the pinned registry contract.");
        }
    }

    private static void ValidateSchemaNode(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object)
            throw Error(RegistryInvalid, "Action params_schema contains an invalid node.");

        if (schema.TryGetProperty("oneOf", out var oneOf))
        {
            if (oneOf.ValueKind != JsonValueKind.Array || oneOf.GetArrayLength() == 0)
                throw Error(RegistryInvalid, "Action params_schema oneOf must contain branches.");
            foreach (var branch in oneOf.EnumerateArray())
                ValidateSchemaNode(branch);
            return;
        }

        var type = ReadRequiredString(schema, "type", 32);
        if (string.Equals(type, "object", StringComparison.Ordinal))
        {
            if (!schema.TryGetProperty("additionalProperties", out var additional) ||
                additional.ValueKind != JsonValueKind.False)
            {
                throw Error(RegistryInvalid, "Object action schemas must reject additional properties.");
            }

            if (!schema.TryGetProperty("properties", out var properties) ||
                properties.ValueKind != JsonValueKind.Object)
            {
                throw Error(RegistryInvalid, "Object action schemas must declare properties.");
            }

            foreach (var property in properties.EnumerateObject())
            {
                NyxIdActionSecretPolicy.ValidateFieldName(property.Name);
                ValidateSchemaNode(property.Value);
            }
            return;
        }

        if (string.Equals(type, "array", StringComparison.Ordinal))
        {
            var minItems = ReadOptionalSchemaCount(schema, "minItems");
            var maxItems = ReadOptionalSchemaCount(schema, "maxItems");
            if (minItems.HasValue && maxItems.HasValue && minItems > maxItems)
                throw Error(RegistryInvalid, "Action params_schema contains an invalid array range.");
            if (schema.TryGetProperty("uniqueItems", out var uniqueItems) &&
                uniqueItems.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw Error(RegistryInvalid, "Action params_schema uniqueItems must be a boolean.");
            }
            ValidateSchemaNode(RequireProperty(schema, "items"));
            return;
        }

        if (!string.Equals(type, "string", StringComparison.Ordinal))
            throw Error(RegistryInvalid, "Only closed object, array, and string action schemas are supported.");
    }

    private static void ValidateAgainstSchema(
        JsonElement instance,
        JsonElement schema,
        string path)
    {
        if (schema.TryGetProperty("oneOf", out var oneOf))
        {
            var matches = 0;
            foreach (var branch in oneOf.EnumerateArray())
            {
                try
                {
                    ValidateAgainstSchema(instance, branch, path);
                    matches++;
                }
                catch (NyxIdAssistantActionRegistryException exception)
                    when (exception.Code == ParamsInvalid)
                {
                }
            }

            if (matches != 1)
                throw Error(ParamsInvalid, "Action params must match exactly one declared variant.");
            return;
        }

        var type = ReadRequiredString(schema, "type", 32);
        switch (type)
        {
            case "object":
                if (instance.ValueKind != JsonValueKind.Object)
                    throw Error(ParamsInvalid, $"{path} must be an object.");
                var properties = RequireProperty(schema, "properties");
                if (schema.TryGetProperty("required", out var required))
                {
                    foreach (var requiredName in required.EnumerateArray())
                    {
                        var name = requiredName.GetString() ?? string.Empty;
                        if (!instance.TryGetProperty(name, out _))
                            throw Error(ParamsInvalid, $"{path} is missing a required field.");
                    }
                }

                foreach (var property in instance.EnumerateObject())
                {
                    if (!properties.TryGetProperty(property.Name, out var propertySchema))
                        throw Error(ParamsInvalid, $"{path} contains an undeclared field.");
                    ValidateAgainstSchema(property.Value, propertySchema, $"{path}.{property.Name}");
                }
                break;
            case "array":
                if (instance.ValueKind != JsonValueKind.Array)
                    throw Error(ParamsInvalid, $"{path} must be an array.");
                var itemCount = instance.GetArrayLength();
                var minItems = ReadOptionalSchemaCount(schema, "minItems");
                var maxItems = ReadOptionalSchemaCount(schema, "maxItems");
                if ((minItems.HasValue && itemCount < minItems) ||
                    (maxItems.HasValue && itemCount > maxItems))
                {
                    throw Error(ParamsInvalid, $"{path} contains an invalid number of items.");
                }
                if (schema.TryGetProperty("uniqueItems", out var uniqueItems) &&
                    uniqueItems.ValueKind == JsonValueKind.True)
                {
                    var values = instance.EnumerateArray()
                        .Select(static item => item.GetRawText())
                        .ToArray();
                    if (values.Distinct(StringComparer.Ordinal).Count() != values.Length)
                        throw Error(ParamsInvalid, $"{path} contains duplicate items.");
                }
                var items = RequireProperty(schema, "items");
                foreach (var item in instance.EnumerateArray())
                    ValidateAgainstSchema(item, items, path);
                break;
            case "string":
                if (instance.ValueKind != JsonValueKind.String)
                    throw Error(ParamsInvalid, $"{path} must be a string.");
                break;
            default:
                throw Error(ParamsInvalid, "The action params schema type is unsupported.");
        }
    }

    private static void EnsureOnlyProperties(JsonElement element, params string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw Error(ParamsInvalid, "Action params must be an object.");
        var allowedSet = allowed.ToHashSet(StringComparer.Ordinal);
        if (element.EnumerateObject().Any(property => !allowedSet.Contains(property.Name)))
            throw Error(ParamsInvalid, "Action params contain an undeclared field.");
    }

    private static JsonElement RequireObject(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object
            ? element
            : throw Error(RegistryInvalid, "The NyxID action registry contains a non-object entry.");

    private static JsonElement RequireProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
            ? value
            : throw Error(RegistryInvalid, "The NyxID action registry is missing a required field.");

    private static int RequireInt32(JsonElement element, string name)
    {
        var value = RequireProperty(element, name);
        return value.TryGetInt32(out var number)
            ? number
            : throw Error(RegistryInvalid, "The NyxID action registry contains an invalid integer field.");
    }

    private static bool RequireBoolean(JsonElement element, string name)
    {
        var value = RequireProperty(element, name);
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw Error(RegistryInvalid, "The NyxID action registry contains an invalid boolean field."),
        };
    }

    private static string ReadRequiredString(JsonElement element, string name, int maxLength)
    {
        if (!element.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            throw Error(ParamsInvalid, "A required action string is missing.");
        }

        return NormalizeString(property.GetString(), maxLength, required: true);
    }

    private static string ReadOptionalString(JsonElement element, string name, int maxLength)
    {
        if (!element.TryGetProperty(name, out var property))
            return string.Empty;
        if (property.ValueKind != JsonValueKind.String)
            throw Error(ParamsInvalid, "An action string field is invalid.");
        return NormalizeString(property.GetString(), maxLength, required: false);
    }

    private static IReadOnlyList<string> ReadStringArray(
        JsonElement element,
        string name,
        int maxCount,
        int maxItemLength,
        bool rejectDuplicates = false,
        bool rejectNormalizationChanges = false)
    {
        if (!element.TryGetProperty(name, out var property))
            return [];
        if (property.ValueKind != JsonValueKind.Array || property.GetArrayLength() > maxCount)
            throw Error(ParamsInvalid, "An action string array is invalid.");

        var values = property.EnumerateArray()
            .Select(item =>
            {
                if (item.ValueKind != JsonValueKind.String)
                    throw Error(ParamsInvalid, "An action string array item is invalid.");

                var raw = item.GetString();
                var normalized = NormalizeString(raw, maxItemLength, required: true);
                if (rejectNormalizationChanges && !string.Equals(raw, normalized, StringComparison.Ordinal))
                    throw Error(ParamsInvalid, "An action string array item is not canonical.");
                return normalized;
            })
            .ToArray();
        var distinctValues = values.Distinct(StringComparer.Ordinal).ToArray();
        if (rejectDuplicates && distinctValues.Length != values.Length)
            throw Error(ParamsInvalid, "An action string array contains duplicate identities.");
        return distinctValues;
    }

    private static int? ReadOptionalSchemaCount(JsonElement schema, string name)
    {
        if (!schema.TryGetProperty(name, out var property))
            return null;
        if (!property.TryGetInt32(out var value) || value < 0)
            throw Error(RegistryInvalid, "Action params_schema contains an invalid array count.");
        return value;
    }

    private static string ReadEnumString(
        JsonElement element,
        string name,
        params string[] allowed)
    {
        var value = ReadRequiredString(element, name, 64);
        return allowed.Contains(value, StringComparer.Ordinal)
            ? value
            : throw Error(ParamsInvalid, "An action enum value is invalid.");
    }

    private static string ReadSafeIdentity(
        JsonElement element,
        string name,
        int maxLength,
        string invalidMessage)
    {
        if (!element.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            throw Error(ParamsInvalid, "A required action string is missing.");
        }

        return NormalizeSafeIdentity(property.GetString(), maxLength, invalidMessage);
    }

    /// <summary>
    /// Identity values travel verbatim into NyxID resource paths, so they must
    /// already be canonical (no surrounding whitespace) and free of path or
    /// query delimiters.
    /// </summary>
    internal static bool IsSafeIdentity(string value) =>
        !value.Any(char.IsWhiteSpace) &&
        !value.Any(static character => character is '/' or '\\' or '?' or '#');

    private static string NormalizeSafeIdentity(
        string? raw,
        int maxLength,
        string invalidMessage)
    {
        var normalized = NormalizeString(raw, maxLength, required: true);
        if (!string.Equals(raw, normalized, StringComparison.Ordinal) ||
            !IsSafeIdentity(normalized))
        {
            throw Error(ParamsInvalid, invalidMessage);
        }

        return normalized;
    }

    private static IReadOnlyList<string> NormalizeDistinctSet(
        IReadOnlyCollection<string> values,
        int minCount,
        int maxCount,
        int maxItemLength,
        string countInvalidMessage,
        string itemInvalidMessage)
    {
        if (values.Count < minCount || values.Count > maxCount)
            throw Error(ParamsInvalid, countInvalidMessage);

        var normalizedValues = new List<string>(values.Count);
        var distinct = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            var normalized = NormalizeString(value, maxItemLength, required: true);
            if (!string.Equals(value, normalized, StringComparison.Ordinal) ||
                !distinct.Add(normalized))
            {
                throw Error(ParamsInvalid, itemInvalidMessage);
            }

            normalizedValues.Add(normalized);
        }

        return normalizedValues;
    }

    private static string NormalizeString(string? value, int maxLength, bool required)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if ((required && normalized.Length == 0) ||
            normalized.Length > maxLength ||
            normalized.Any(char.IsControl))
        {
            throw Error(ParamsInvalid, "An action string value is invalid.");
        }

        return normalized;
    }

    private static NyxIdAssistantActionTier ParseTier(string value) => value switch
    {
        "v1" => NyxIdAssistantActionTier.V1,
        "v2" => NyxIdAssistantActionTier.V2,
        _ => throw Error(TierUnsupported, "The NyxID action tier is unknown."),
    };

    private static NyxIdAssistantActionRisk ParseRisk(string value) => value switch
    {
        "low" => NyxIdAssistantActionRisk.Low,
        "grant" => NyxIdAssistantActionRisk.Grant,
        "destructive" => NyxIdAssistantActionRisk.Destructive,
        _ => throw Error(RegistryInvalid, "The NyxID action risk is unknown."),
    };

    private static NyxIdAssistantActionRegistryException Error(
        string code,
        string message) =>
        new(code, message);

    private sealed record ActionContract(
        NyxIdAssistantActionKind Action,
        Func<JsonElement, NyxIdAssistantActionParams> Parser,
        string? PinnedParamsSchema = null,
        NyxIdAssistantActionRisk? PinnedRisk = null,
        bool? PinnedRememberEligible = null);

    private sealed record RegistryEntry(
        NyxIdAssistantActionDefinitionSnapshot Definition,
        JsonElement ParamsSchema,
        Func<JsonElement, NyxIdAssistantActionParams> Parser);
}
