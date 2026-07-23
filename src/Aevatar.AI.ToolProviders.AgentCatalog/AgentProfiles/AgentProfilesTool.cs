using System.Globalization;
using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Google.Protobuf;

namespace Aevatar.AI.ToolProviders.AgentCatalog.AgentProfiles;

public sealed class AgentProfilesTool : IAgentTool
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly Func<IAgentProfileCommandService> _commands;
    private readonly Func<IAgentProfileQueryService> _queries;

    public AgentProfilesTool(
        IAgentProfileCommandService commands,
        IAgentProfileQueryService queries)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(queries);
        _commands = () => commands;
        _queries = () => queries;
    }

    internal AgentProfilesTool(
        Func<IAgentProfileCommandService> commands,
        Func<IAgentProfileQueryService> queries)
    {
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _queries = queries ?? throw new ArgumentNullException(nameof(queries));
    }

    public string Name => "agent_profiles";

    public string Description =>
        "Manage the current caller's owner Agent Profiles through the shared Application contract. " +
        "Actions create or read a Profile, update its draft, manage exact Ornn skill bindings, validate, and publish. " +
        "Versioned mutations require the strong ETag returned by get; validate and publish require the caller's NyxID access token.";

    public string ParametersSchema => """
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "action": {
              "type": "string",
              "enum": ["create", "get", "update_draft", "upsert_skill", "remove_skill", "validate", "publish"]
            },
            "profile_slug": {
              "type": "string",
              "description": "Owner Profile slug. Required for every action."
            },
            "owner_handle": {
              "type": "string",
              "description": "Public owner handle. Accepted only by create."
            },
            "display_name": {
              "type": "string",
              "description": "Profile display name. Required by create and update_draft."
            },
            "purpose": {
              "type": "string",
              "description": "Profile purpose. Required by create and update_draft."
            },
            "instructions": {
              "type": "string",
              "description": "Profile instructions. Required by create and update_draft."
            },
            "tool_policy": {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "mode": {
                  "type": "string",
                  "enum": ["INHERIT_ROUTE_MAXIMUM", "EXPLICIT_ALLOWLIST"]
                },
                "tool_names": {
                  "type": "array",
                  "items": { "type": "string" }
                },
                "tool_set_refs": {
                  "type": "array",
                  "items": { "type": "string" }
                }
              },
              "required": ["mode", "tool_names", "tool_set_refs"],
              "description": "Complete Profile tool policy. Required by create and update_draft."
            },
            "etag": {
              "type": "string",
              "description": "Strong owner Profile ETag from get. Required by update_draft, upsert_skill, remove_skill, and publish."
            },
            "binding_id": {
              "type": "string",
              "description": "Profile skill binding ID. Required by upsert_skill and remove_skill."
            },
            "activation_mode": {
              "type": "string",
              "enum": ["ALWAYS", "ROUTED", "DEFAULT_FOR_UNMATCHED_TURN"],
              "description": "Skill activation mode. Required only by upsert_skill."
            },
            "skill": {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "skill_guid": { "type": "string" },
                "literal_version": { "type": "string", "description": "Literal major.minor Ornn version." },
                "expected_name": { "type": "string" },
                "expected_publisher_id": { "type": "string" }
              },
              "required": ["skill_guid", "literal_version", "expected_name", "expected_publisher_id"],
              "description": "Exact Ornn skill reference. Required only by upsert_skill."
            },
            "idempotency_key": {
              "type": "string",
              "description": "Optional mutation idempotency key; required by create when request context has no key."
            }
          },
          "required": ["action", "profile_slug"]
        }
        """;

    public async Task<string> ExecuteAsync(
        string argumentsJson,
        CancellationToken ct = default)
    {
        AgentProfileCallerContext caller;
        AgentProfilesToolRequest request;
        try
        {
            caller = RequireCallerContext();
            request = AgentProfilesToolRequest.Parse(argumentsJson);
        }
        catch (AgentProfilesToolInputException exception)
        {
            return SerializeError(exception.Code);
        }
        catch (JsonException)
        {
            return SerializeError("invalid_agent_profile_arguments");
        }

        try
        {
            return request.Action switch
            {
                "create" => SerializeAccepted(await CreateAsync(caller, request, ct), caller, request),
                "get" => Serialize(await GetAsync(caller, request, ct)),
                "update_draft" => SerializeAccepted(
                    await UpdateDraftAsync(caller, request, ct), caller, request),
                "upsert_skill" => SerializeAccepted(
                    await UpsertSkillAsync(caller, request, ct), caller, request),
                "remove_skill" => SerializeAccepted(
                    await RemoveSkillAsync(caller, request, ct), caller, request),
                "validate" => Serialize(await ValidateAsync(caller, request, ct)),
                "publish" => SerializeAccepted(
                    await PublishAsync(caller, request, ct), caller, request),
                _ => SerializeError("invalid_agent_profile_action"),
            };
        }
        catch (AgentProfilesToolInputException exception)
        {
            return SerializeError(exception.Code);
        }
        catch (AgentProfileBoundaryException exception)
        {
            return SerializeError(exception.Code, exception.Diagnostics);
        }
        catch (AgentProfileContractValidationException exception)
        {
            return SerializeError(
                exception.Diagnostics.FirstOrDefault()?.Code ??
                "invalid_agent_profile_contract");
        }
    }

    private Task<AgentProfileAcceptedReceipt> CreateAsync(
        AgentProfileCallerContext caller,
        AgentProfilesToolRequest request,
        CancellationToken ct)
    {
        var idempotencyKey = ResolveIdempotencyKey(request, required: true)!;
        return _commands().CreateAsync(
            caller,
            new CreateAgentProfileRequest(
                request.ProfileSlug,
                request.OwnerHandle,
                request.DisplayName!,
                request.Purpose!,
                request.Instructions!,
                request.ToolPolicy!.ToContract()),
            idempotencyKey,
            ct);
    }

    private async Task<object> GetAsync(
        AgentProfileCallerContext caller,
        AgentProfilesToolRequest request,
        CancellationToken ct)
    {
        var snapshot = await _queries().GetOwnedAsync(caller, request.ProfileSlug, ct);
        return snapshot is null
            ? new AgentProfilesToolError("agent_profile_not_found")
            : MapManagement(snapshot);
    }

    private Task<AgentProfileAcceptedReceipt> UpdateDraftAsync(
        AgentProfileCallerContext caller,
        AgentProfilesToolRequest request,
        CancellationToken ct) =>
        _commands().UpdateDraftAsync(
            caller,
            request.ProfileSlug,
            request.ExpectedAuthorityStateVersion!.Value,
            new UpdateAgentProfileDraftRequest(
                request.DisplayName!,
                request.Purpose!,
                request.Instructions!,
                request.ToolPolicy!.ToContract()),
            ResolveIdempotencyKey(request, required: false),
            ct);

    private Task<AgentProfileAcceptedReceipt> UpsertSkillAsync(
        AgentProfileCallerContext caller,
        AgentProfilesToolRequest request,
        CancellationToken ct) =>
        _commands().UpsertSkillBindingAsync(
            caller,
            request.ProfileSlug,
            request.BindingId!,
            request.ExpectedAuthorityStateVersion!.Value,
            new UpsertAgentProfileSkillBindingRequest(
                request.ActivationMode!.Value,
                request.Skill!.ToContract()),
            ResolveIdempotencyKey(request, required: false),
            ct);

    private Task<AgentProfileAcceptedReceipt> RemoveSkillAsync(
        AgentProfileCallerContext caller,
        AgentProfilesToolRequest request,
        CancellationToken ct) =>
        _commands().RemoveSkillBindingAsync(
            caller,
            request.ProfileSlug,
            request.BindingId!,
            request.ExpectedAuthorityStateVersion!.Value,
            ResolveIdempotencyKey(request, required: false),
            ct);

    private Task<AgentProfileValidationReport> ValidateAsync(
        AgentProfileCallerContext caller,
        AgentProfilesToolRequest request,
        CancellationToken ct)
    {
        RequireOrnnAccessToken(caller);
        return _commands().ValidateAsync(caller, request.ProfileSlug, ct);
    }

    private Task<AgentProfileAcceptedReceipt> PublishAsync(
        AgentProfileCallerContext caller,
        AgentProfilesToolRequest request,
        CancellationToken ct)
    {
        RequireOrnnAccessToken(caller);
        return _commands().PublishAsync(
            caller,
            request.ProfileSlug,
            request.ExpectedAuthorityStateVersion!.Value,
            ResolveIdempotencyKey(request, required: false),
            ct);
    }

    private static AgentProfileCallerContext RequireCallerContext()
    {
        var context = AgentToolRequestContext.Current;
        var bindingId = Normalize(context?.SenderBinding.BindingId);
        if (Normalize(context?.Channel.SenderId) is not null || bindingId is not null)
        {
            var senderSubjectId = Normalize(context?.SenderBinding.NyxUserId);
            var senderScopeId = Normalize(context?.Caller.OwnerScopeId);
            var senderAccessToken = Normalize(context?.Credentials.SenderNyxIdAccessToken);
            if (bindingId is null ||
                senderSubjectId is null ||
                senderScopeId is null ||
                senderAccessToken is null)
                throw new AgentProfilesToolInputException("agent_profile_sender_authority_required");

            return new AgentProfileCallerContext(
                new AgentProfileUserOwnerIdentity
                {
                    IdentityProvider = "nyxid",
                    SubjectId = senderSubjectId,
                },
                senderScopeId,
                Username: null,
                NyxIdAccessToken: senderAccessToken);
        }

        var scopeId = Normalize(context?.Caller.ScopeId);
        if (scopeId is null)
            throw new AgentProfilesToolInputException("agent_profile_scope_required");

        var subjectId = Normalize(context?.Caller.OwnerSubject);
        if (subjectId is null)
            throw new AgentProfilesToolInputException("agent_profile_subject_required");

        return new AgentProfileCallerContext(
            new AgentProfileUserOwnerIdentity
            {
                IdentityProvider = "nyxid",
                SubjectId = subjectId,
            },
            scopeId,
            Username: null,
            NyxIdAccessToken: Normalize(context?.Credentials.NyxIdAccessToken));
    }

    private static void RequireOrnnAccessToken(AgentProfileCallerContext caller)
    {
        if (string.IsNullOrWhiteSpace(caller.NyxIdAccessToken))
            throw new AgentProfilesToolInputException("ornn_access_token_required");
    }

    private static string? ResolveIdempotencyKey(
        AgentProfilesToolRequest request,
        bool required)
    {
        var key = request.IdempotencyKey ?? Normalize(AgentToolRequestContext.IdempotencyKey);
        if (required && key is null)
            throw new AgentProfilesToolInputException("idempotency_key_required");
        return key;
    }

    private static string SerializeAccepted(
        AgentProfileAcceptedReceipt receipt,
        AgentProfileCallerContext caller,
        AgentProfilesToolRequest request)
    {
        var expectedResourceUrl =
            $"/api/scopes/{caller.ScopeId}/agent-profiles/{request.ProfileSlug}";
        if (!receipt.Accepted ||
            !string.Equals(receipt.AckStage, "accepted", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(receipt.OperationId) ||
            string.IsNullOrWhiteSpace(receipt.CommandId) ||
            string.IsNullOrWhiteSpace(receipt.CorrelationId) ||
            string.IsNullOrWhiteSpace(receipt.ActorId) ||
            string.IsNullOrWhiteSpace(receipt.ProfileId) ||
            !string.Equals(receipt.ResourceUrl, expectedResourceUrl, StringComparison.Ordinal))
        {
            return SerializeError("agent_profile_dispatch_rejected");
        }

        return Serialize(new AgentProfilesToolAcceptedResult(
            receipt.Accepted,
            receipt.AckStage,
            receipt.OperationId,
            receipt.CommandId,
            receipt.CorrelationId,
            receipt.ActorId,
            receipt.ProfileId,
            receipt.ResourceUrl));
    }

    private static object MapManagement(AgentProfileManagementSnapshot snapshot) => new
    {
        snapshot.AuthorityStateVersion,
        Etag = AgentProfilesToolRequest.FormatEtag(snapshot.AuthorityStateVersion),
        snapshot.ProfileId,
        Reference = MapReference(snapshot.Identity.Reference),
        Draft = MapContent(snapshot.Draft),
        snapshot.DraftRevision,
        DraftDigest = Digest(snapshot.DraftSha256),
        snapshot.PublishedRevision,
        PublishedSnapshotDigest = Digest(snapshot.PublishedSnapshotSha256),
        PublishedSourceDraftDigest = Digest(snapshot.PublishedSourceDraftSha256),
        LastMutation = snapshot.LastMutation is null ? null : MapMutation(snapshot.LastMutation),
    };

    private static object MapContent(AgentProfileContent content)
    {
        var policy = content.ToolPolicy ?? new AgentProfileToolPolicy();
        return new
        {
            content.DisplayName,
            content.Purpose,
            content.Instructions,
            SkillBindings = content.SkillBindings.Select(static binding => new
            {
                binding.BindingId,
                ActivationMode = ActivationMode(binding.ActivationMode),
                Skill = MapExactReference(binding.Skill),
            }).ToArray(),
            ToolPolicy = new
            {
                Mode = ToolPolicyMode(policy.Mode),
                ToolNames = policy.ToolNames.ToArray(),
                ToolSetRefs = policy.ToolSetRefs.ToArray(),
            },
        };
    }

    private static object MapMutation(AgentProfileMutationOutcome mutation)
    {
        var operation = mutation.Operation ?? new AgentProfileOperationFact();
        return new
        {
            operation.OperationId,
            operation.CommandId,
            operation.CorrelationId,
            Status = MutationStatus(mutation.Status),
            Diagnostic = mutation.Diagnostic is null ? null : MapDiagnostic(mutation.Diagnostic),
            mutation.DraftRevision,
            DraftDigest = Digest(mutation.DraftSha256),
            mutation.PublishedRevision,
            PublishedSnapshotDigest = Digest(mutation.PublishedSnapshotSha256),
        };
    }

    private static object MapValidation(AgentProfileValidationReport report) => new
    {
        report.Valid,
        report.DraftRevision,
        DraftDigest = Digest(report.DraftSha256),
        Diagnostics = report.Diagnostics.Select(MapDiagnostic).ToArray(),
        ResolvedSkills = report.ResolvedSkills.Select(static skill => new
        {
            skill.BindingId,
            ExactReference = MapExactReference(skill.ExactReference),
            ContentSha256 = Digest(skill.ContentSha256),
        }).ToArray(),
    };

    private static object MapReference(AgentProfileReference? reference) => new
    {
        OwnerHandle = reference?.OwnerHandle ?? string.Empty,
        ProfileSlug = reference?.ProfileSlug ?? string.Empty,
    };

    private static object MapExactReference(ExactOrnnSkillReference? reference) => new
    {
        SkillGuid = reference?.SkillGuid ?? string.Empty,
        LiteralVersion = reference?.LiteralVersion ?? string.Empty,
        ExpectedName = reference?.ExpectedName ?? string.Empty,
        ExpectedPublisherId = reference?.ExpectedPublisherId ?? string.Empty,
    };

    private static object MapDiagnostic(AgentProfileSafeDiagnostic diagnostic) => new
    {
        diagnostic.Code,
        diagnostic.Message,
        diagnostic.Path,
    };

    private static string Digest(ByteString? value) =>
        value is null ? string.Empty : Convert.ToHexString(value.Span).ToLowerInvariant();

    private static string ActivationMode(AgentProfileSkillActivationMode value) => value switch
    {
        AgentProfileSkillActivationMode.Always => "ALWAYS",
        AgentProfileSkillActivationMode.Routed => "ROUTED",
        AgentProfileSkillActivationMode.DefaultForUnmatchedTurn => "DEFAULT_FOR_UNMATCHED_TURN",
        _ => "UNSPECIFIED",
    };

    private static string ToolPolicyMode(AgentProfileToolPolicyMode value) => value switch
    {
        AgentProfileToolPolicyMode.InheritRouteMaximum => "INHERIT_ROUTE_MAXIMUM",
        AgentProfileToolPolicyMode.ExplicitAllowlist => "EXPLICIT_ALLOWLIST",
        _ => "UNSPECIFIED",
    };

    private static string MutationStatus(AgentProfileMutationStatus value) => value switch
    {
        AgentProfileMutationStatus.Applied => "APPLIED",
        AgentProfileMutationStatus.NoChange => "NO_CHANGE",
        AgentProfileMutationStatus.Rejected => "REJECTED",
        _ => "UNSPECIFIED",
    };

    private static string Serialize(object value) => JsonSerializer.Serialize(value, s_jsonOptions);

    private static string Serialize(AgentProfileValidationReport report) => Serialize(MapValidation(report));

    private static string SerializeError(string code) => Serialize(new AgentProfilesToolError(code));

    private static string SerializeError(
        string code,
        IReadOnlyList<AgentProfileSafeDiagnostic> diagnostics) =>
        Serialize(new
        {
            Error = code,
            Diagnostics = diagnostics.Select(MapDiagnostic).ToArray(),
        });

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record AgentProfilesToolAcceptedResult(
        bool Accepted,
        string AckStage,
        string OperationId,
        string CommandId,
        string CorrelationId,
        string ActorId,
        string ProfileId,
        string ResourceUrl);

    private sealed record AgentProfilesToolError(string Error);
}

internal sealed record AgentProfilesToolRequest(string Action, string ProfileSlug)
{
    private const string ETagPrefix = "\"agent-profile-v";

    public string? OwnerHandle { get; init; }
    public string? DisplayName { get; init; }
    public string? Purpose { get; init; }
    public string? Instructions { get; init; }
    public AgentProfilesToolPolicyInput? ToolPolicy { get; init; }
    public long? ExpectedAuthorityStateVersion { get; init; }
    public string? BindingId { get; init; }
    public AgentProfileSkillActivationMode? ActivationMode { get; init; }
    public ExactOrnnSkillReferenceInput? Skill { get; init; }
    public string? IdempotencyKey { get; init; }

    public static AgentProfilesToolRequest Parse(string argumentsJson)
    {
        using var document = JsonDocument.Parse(argumentsJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("Agent Profile tool arguments must be an object.");

        RejectDuplicateProperties(root);
        var action = ReadRequiredNonEmptyString(root, "action", "invalid_agent_profile_action");
        var profileSlug = ReadRequiredNonEmptyString(
            root,
            "profile_slug",
            "agent_profile_profile_slug_required");
        RejectUnknownFields(root, AllowedFields(action));
        var request = new AgentProfilesToolRequest(action, profileSlug);

        return action switch
        {
            "create" => request with
            {
                OwnerHandle = ReadOptionalString(root, "owner_handle"),
                DisplayName = ReadRequiredString(root, "display_name"),
                Purpose = ReadRequiredString(root, "purpose"),
                Instructions = ReadRequiredString(root, "instructions"),
                ToolPolicy = ReadToolPolicy(root),
                IdempotencyKey = ReadOptionalIdempotencyKey(root),
            },
            "update_draft" => request with
            {
                DisplayName = ReadRequiredString(root, "display_name"),
                Purpose = ReadRequiredString(root, "purpose"),
                Instructions = ReadRequiredString(root, "instructions"),
                ToolPolicy = ReadToolPolicy(root),
                ExpectedAuthorityStateVersion = ReadEtag(root),
                IdempotencyKey = ReadOptionalIdempotencyKey(root),
            },
            "upsert_skill" => request with
            {
                ExpectedAuthorityStateVersion = ReadEtag(root),
                BindingId = ReadRequiredNonEmptyString(
                    root,
                    "binding_id",
                    "invalid_agent_profile_arguments"),
                ActivationMode = ReadActivationMode(root),
                Skill = ReadExactSkill(root),
                IdempotencyKey = ReadOptionalIdempotencyKey(root),
            },
            "remove_skill" => request with
            {
                ExpectedAuthorityStateVersion = ReadEtag(root),
                BindingId = ReadRequiredNonEmptyString(
                    root,
                    "binding_id",
                    "invalid_agent_profile_arguments"),
                IdempotencyKey = ReadOptionalIdempotencyKey(root),
            },
            "publish" => request with
            {
                ExpectedAuthorityStateVersion = ReadEtag(root),
                IdempotencyKey = ReadOptionalIdempotencyKey(root),
            },
            _ => request,
        };
    }

    public static string FormatEtag(long version) =>
        $"\"agent-profile-v{version.ToString(CultureInfo.InvariantCulture)}\"";

    private static IReadOnlyList<string> AllowedFields(string action) => action switch
    {
        "create" =>
        [
            "action", "profile_slug", "owner_handle", "display_name", "purpose",
            "instructions", "tool_policy", "idempotency_key",
        ],
        "get" or "validate" => ["action", "profile_slug"],
        "update_draft" =>
        [
            "action", "profile_slug", "etag", "display_name", "purpose", "instructions",
            "tool_policy", "idempotency_key",
        ],
        "upsert_skill" =>
        [
            "action", "profile_slug", "etag", "binding_id", "activation_mode", "skill",
            "idempotency_key",
        ],
        "remove_skill" =>
        ["action", "profile_slug", "etag", "binding_id", "idempotency_key"],
        "publish" => ["action", "profile_slug", "etag", "idempotency_key"],
        _ => ["action", "profile_slug"],
    };

    private static void RejectDuplicateProperties(JsonElement element)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Add(property.Name))
                throw new JsonException("Duplicate Agent Profile tool field.");
        }
    }

    private static void RejectUnknownFields(
        JsonElement root,
        IReadOnlyList<string> allowedFields)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!allowedFields.Contains(property.Name, StringComparer.Ordinal))
                throw new JsonException("Unknown Agent Profile tool field.");
        }
    }

    private static string ReadRequiredString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
            throw new JsonException("Missing Agent Profile tool string field.");
        return property.GetString()!;
    }

    private static string ReadRequiredNonEmptyString(
        JsonElement root,
        string name,
        string errorCode)
    {
        var value = ReadRequiredString(root, name);
        if (string.IsNullOrWhiteSpace(value))
            throw new AgentProfilesToolInputException(errorCode);
        return value;
    }

    private static string? ReadOptionalString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var property))
            return null;
        if (property.ValueKind != JsonValueKind.String)
            throw new JsonException("Invalid optional Agent Profile tool string field.");
        return property.GetString();
    }

    private static string? ReadOptionalIdempotencyKey(JsonElement root)
    {
        var value = ReadOptionalString(root, "idempotency_key");
        if (value is null)
            return null;
        if (value.Length == 0 ||
            char.IsWhiteSpace(value[0]) ||
            char.IsWhiteSpace(value[^1]))
        {
            throw new AgentProfilesToolInputException("invalid_idempotency_key");
        }

        return value;
    }

    private static AgentProfilesToolPolicyInput ReadToolPolicy(JsonElement root)
    {
        if (!root.TryGetProperty("tool_policy", out var policy) ||
            policy.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Agent Profile tool policy is required.");
        }

        RejectDuplicateProperties(policy);
        RejectUnknownFields(policy, ["mode", "tool_names", "tool_set_refs"]);
        var mode = ReadRequiredString(policy, "mode") switch
        {
            "INHERIT_ROUTE_MAXIMUM" => AgentProfileToolPolicyMode.InheritRouteMaximum,
            "EXPLICIT_ALLOWLIST" => AgentProfileToolPolicyMode.ExplicitAllowlist,
            _ => throw new JsonException("Invalid Agent Profile tool policy mode."),
        };
        return new AgentProfilesToolPolicyInput(
            mode,
            ReadStringArray(policy, "tool_names"),
            ReadStringArray(policy, "tool_set_refs"));
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Array)
            throw new JsonException("Agent Profile tool string array is required.");

        var values = new List<string>();
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new JsonException("Agent Profile tool array items must be strings.");
            values.Add(item.GetString()!);
        }

        return values;
    }

    private static long ReadEtag(JsonElement root)
    {
        if (!root.TryGetProperty("etag", out var property))
            throw new AgentProfilesToolInputException("agent_profile_etag_required");
        if (property.ValueKind != JsonValueKind.String || !TryParseEtag(property.GetString(), out var version))
            throw new AgentProfilesToolInputException("invalid_agent_profile_etag");
        return version;
    }

    private static bool TryParseEtag(string? wireValue, out long version)
    {
        version = 0;
        if (string.IsNullOrEmpty(wireValue) ||
            !wireValue.StartsWith(ETagPrefix, StringComparison.Ordinal) ||
            !wireValue.EndsWith('"'))
        {
            return false;
        }

        var decimalValue = wireValue.AsSpan(
            ETagPrefix.Length,
            wireValue.Length - ETagPrefix.Length - 1);
        if (decimalValue.IsEmpty ||
            (decimalValue.Length > 1 && decimalValue[0] == '0') ||
            decimalValue.IndexOfAnyExceptInRange('0', '9') >= 0 ||
            !long.TryParse(
                decimalValue,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out version))
        {
            return false;
        }

        return string.Equals(FormatEtag(version), wireValue, StringComparison.Ordinal);
    }

    private static AgentProfileSkillActivationMode ReadActivationMode(JsonElement root) =>
        ReadRequiredString(root, "activation_mode") switch
        {
            "ALWAYS" => AgentProfileSkillActivationMode.Always,
            "ROUTED" => AgentProfileSkillActivationMode.Routed,
            "DEFAULT_FOR_UNMATCHED_TURN" => AgentProfileSkillActivationMode.DefaultForUnmatchedTurn,
            _ => throw new JsonException("Invalid Agent Profile skill activation mode."),
        };

    private static ExactOrnnSkillReferenceInput ReadExactSkill(JsonElement root)
    {
        if (!root.TryGetProperty("skill", out var skill) || skill.ValueKind != JsonValueKind.Object)
            throw new JsonException("Exact Ornn skill reference is required.");

        RejectDuplicateProperties(skill);
        RejectUnknownFields(
            skill,
            ["skill_guid", "literal_version", "expected_name", "expected_publisher_id"]);
        return new ExactOrnnSkillReferenceInput(
            ReadRequiredNonEmptyString(skill, "skill_guid", "invalid_agent_profile_arguments"),
            ReadRequiredNonEmptyString(skill, "literal_version", "invalid_agent_profile_arguments"),
            ReadRequiredNonEmptyString(skill, "expected_name", "invalid_agent_profile_arguments"),
            ReadRequiredNonEmptyString(
                skill,
                "expected_publisher_id",
                "invalid_agent_profile_arguments"));
    }
}

internal sealed record AgentProfilesToolPolicyInput(
    AgentProfileToolPolicyMode Mode,
    IReadOnlyList<string> ToolNames,
    IReadOnlyList<string> ToolSetRefs)
{
    public AgentProfileToolPolicy ToContract()
    {
        var policy = new AgentProfileToolPolicy { Mode = Mode };
        policy.ToolNames.Add(ToolNames);
        policy.ToolSetRefs.Add(ToolSetRefs);
        return policy;
    }
}

internal sealed record ExactOrnnSkillReferenceInput(
    string SkillGuid,
    string LiteralVersion,
    string ExpectedName,
    string ExpectedPublisherId)
{
    public ExactOrnnSkillReference ToContract() => new()
    {
        SkillGuid = SkillGuid,
        LiteralVersion = LiteralVersion,
        ExpectedName = ExpectedName,
        ExpectedPublisherId = ExpectedPublisherId,
    };
}

internal sealed class AgentProfilesToolInputException : JsonException
{
    public AgentProfilesToolInputException(string code)
        : base(code)
    {
        Code = code;
    }

    public string Code { get; }
}
