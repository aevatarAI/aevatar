using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.GAgentService.Abstractions.AgentProfiles;

namespace Aevatar.GAgentService.Hosting.AgentProfiles;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateAgentProfileHttpRequest(
    [property: JsonRequired]
    string ProfileSlug,
    string? OwnerHandle,
    [property: JsonRequired]
    string DisplayName,
    [property: JsonRequired]
    string Purpose,
    [property: JsonRequired]
    string Instructions,
    [property: JsonRequired]
    AgentProfileToolPolicyHttpRequest ToolPolicy,
    AgentProfileToolPolicyHttpRequest? RecoveryToolPolicy = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateAgentProfileDraftHttpRequest(
    [property: JsonRequired]
    string DisplayName,
    [property: JsonRequired]
    string Purpose,
    [property: JsonRequired]
    string Instructions,
    [property: JsonRequired]
    AgentProfileToolPolicyHttpRequest ToolPolicy,
    AgentProfileToolPolicyHttpRequest? RecoveryToolPolicy = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AgentProfileSkillBindingHttpRequest(
    [property: JsonRequired]
    [property: JsonConverter(typeof(AgentProfileSkillActivationModeJsonConverter))]
    AgentProfileSkillActivationMode ActivationMode,
    [property: JsonRequired]
    ExactOrnnSkillReferenceHttpRequest Skill,
    AgentProfileSkillRoutingPolicyHttpRequest? RoutingPolicy = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AgentProfileSkillRoutingPolicyHttpRequest(
    [property: JsonRequired]
    string IntentId,
    [property: JsonRequired]
    string RoutingDescription,
    [property: JsonRequired]
    IReadOnlyList<string> ExplicitTriggerAliases,
    [property: JsonRequired]
    AgentProfileToolPolicyHttpRequest TaskToolPolicy,
    [property: JsonRequired]
    [property: JsonConverter(typeof(AgentProfileSkillSideEffectClassJsonConverter))]
    AgentProfileSkillSideEffectClass SideEffectClass);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExactOrnnSkillReferenceHttpRequest(
    [property: JsonRequired]
    string SkillGuid,
    [property: JsonRequired]
    string LiteralVersion,
    [property: JsonRequired]
    string ExpectedName,
    [property: JsonRequired]
    string ExpectedPublisherId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AgentProfileToolPolicyHttpRequest(
    [property: JsonRequired]
    [property: JsonConverter(typeof(AgentProfileToolPolicyModeJsonConverter))]
    AgentProfileToolPolicyMode Mode,
    [property: JsonRequired]
    IReadOnlyList<string> ToolNames,
    [property: JsonRequired]
    IReadOnlyList<string> ToolSetRefs);

internal static class AgentProfileHttpRequestMapper
{
    public static bool TryMap(
        CreateAgentProfileHttpRequest? request,
        out CreateAgentProfileRequest mapped)
    {
        mapped = null!;
        if (request is null ||
            request.ProfileSlug is null ||
            request.DisplayName is null ||
            request.Purpose is null ||
            request.Instructions is null ||
            !TryMap(request.ToolPolicy, out var toolPolicy) ||
            !TryMapOptional(request.RecoveryToolPolicy, out var recoveryToolPolicy))
        {
            return false;
        }

        mapped = new CreateAgentProfileRequest(
            request.ProfileSlug,
            request.OwnerHandle,
            request.DisplayName,
            request.Purpose,
            request.Instructions,
            toolPolicy,
            recoveryToolPolicy);
        return true;
    }

    public static bool TryMap(
        UpdateAgentProfileDraftHttpRequest? request,
        out UpdateAgentProfileDraftRequest mapped)
    {
        mapped = null!;
        if (request is null ||
            request.DisplayName is null ||
            request.Purpose is null ||
            request.Instructions is null ||
            !TryMap(request.ToolPolicy, out var toolPolicy) ||
            !TryMapOptional(request.RecoveryToolPolicy, out var recoveryToolPolicy))
        {
            return false;
        }

        mapped = new UpdateAgentProfileDraftRequest(
            request.DisplayName,
            request.Purpose,
            request.Instructions,
            toolPolicy,
            recoveryToolPolicy);
        return true;
    }

    public static bool TryMap(
        AgentProfileSkillBindingHttpRequest? request,
        out UpsertAgentProfileSkillBindingRequest mapped)
    {
        mapped = null!;
        if (request?.Skill is null ||
            request.Skill.SkillGuid is null ||
            request.Skill.LiteralVersion is null ||
            request.Skill.ExpectedName is null ||
            request.Skill.ExpectedPublisherId is null ||
            !TryMapOptional(request.RoutingPolicy, out var routingPolicy))
        {
            return false;
        }

        mapped = new UpsertAgentProfileSkillBindingRequest(
            request.ActivationMode,
            new ExactOrnnSkillReference
            {
                SkillGuid = request.Skill.SkillGuid,
                LiteralVersion = request.Skill.LiteralVersion,
                ExpectedName = request.Skill.ExpectedName,
                ExpectedPublisherId = request.Skill.ExpectedPublisherId,
            },
            routingPolicy);
        return true;
    }

    private static bool TryMapOptional(
        AgentProfileSkillRoutingPolicyHttpRequest? request,
        out AgentProfileSkillRoutingPolicy? mapped)
    {
        mapped = null;
        if (request is null)
            return true;
        if (request.IntentId is null ||
            request.RoutingDescription is null ||
            request.ExplicitTriggerAliases is null ||
            request.ExplicitTriggerAliases.Any(static value => value is null) ||
            !TryMap(request.TaskToolPolicy, out var taskToolPolicy))
        {
            return false;
        }

        mapped = new AgentProfileSkillRoutingPolicy
        {
            IntentId = request.IntentId,
            RoutingDescription = request.RoutingDescription,
            TaskToolPolicy = taskToolPolicy,
            SideEffectClass = request.SideEffectClass,
        };
        mapped.ExplicitTriggerAliases.Add(request.ExplicitTriggerAliases);
        return true;
    }

    private static bool TryMapOptional(
        AgentProfileToolPolicyHttpRequest? request,
        out AgentProfileToolPolicy? mapped)
    {
        mapped = null;
        if (request is null)
            return true;
        if (!TryMap(request, out var value))
            return false;
        mapped = value;
        return true;
    }

    private static bool TryMap(
        AgentProfileToolPolicyHttpRequest? request,
        out AgentProfileToolPolicy mapped)
    {
        mapped = null!;
        if (request?.ToolNames is null ||
            request.ToolSetRefs is null ||
            request.ToolNames.Any(static value => value is null) ||
            request.ToolSetRefs.Any(static value => value is null))
        {
            return false;
        }

        mapped = new AgentProfileToolPolicy
        {
            Mode = request.Mode,
        };
        mapped.ToolNames.Add(request.ToolNames);
        mapped.ToolSetRefs.Add(request.ToolSetRefs);
        return true;
    }
}

internal sealed record AgentProfileAcceptedHttpResponse(
    bool Accepted,
    string AckStage,
    string OperationId,
    string CommandId,
    string CorrelationId,
    string ActorId,
    string ProfileId,
    string ResourceUrl);

internal sealed record AgentProfileReferenceHttpResponse(
    string OwnerHandle,
    string ProfileSlug);

internal sealed record ExactOrnnSkillReferenceHttpResponse(
    string SkillGuid,
    string LiteralVersion,
    string ExpectedName,
    string ExpectedPublisherId);

internal sealed record AgentProfileToolPolicyHttpResponse(
    string Mode,
    IReadOnlyList<string> ToolNames,
    IReadOnlyList<string> ToolSetRefs);

internal sealed record AgentProfileSkillRoutingPolicyHttpResponse(
    string IntentId,
    string RoutingDescription,
    IReadOnlyList<string> ExplicitTriggerAliases,
    AgentProfileToolPolicyHttpResponse TaskToolPolicy,
    string SideEffectClass);

internal sealed record AgentProfileSkillBindingHttpResponse(
    string BindingId,
    string ActivationMode,
    ExactOrnnSkillReferenceHttpResponse Skill,
    AgentProfileSkillRoutingPolicyHttpResponse? RoutingPolicy);

internal sealed record AgentProfileContentHttpResponse(
    string DisplayName,
    string Purpose,
    string Instructions,
    IReadOnlyList<AgentProfileSkillBindingHttpResponse> SkillBindings,
    AgentProfileToolPolicyHttpResponse ToolPolicy,
    AgentProfileToolPolicyHttpResponse RecoveryToolPolicy);

internal sealed record AgentProfileSafeDiagnosticHttpResponse(
    string Code,
    string Message,
    string Path);

internal sealed record AgentProfileMutationHttpResponse(
    string OperationId,
    string CommandId,
    string CorrelationId,
    string Status,
    AgentProfileSafeDiagnosticHttpResponse? Diagnostic,
    long DraftRevision,
    string DraftDigest,
    long PublishedRevision,
    string PublishedSnapshotDigest);

internal sealed record AgentProfileManagementHttpResponse(
    long AuthorityStateVersion,
    string ProfileId,
    AgentProfileReferenceHttpResponse Reference,
    AgentProfileContentHttpResponse Draft,
    long DraftRevision,
    string DraftDigest,
    long PublishedRevision,
    string PublishedSnapshotDigest,
    string PublishedSourceDraftDigest,
    AgentProfileMutationHttpResponse? LastMutation);

internal sealed record AgentProfileResolvedSkillHttpResponse(
    string BindingId,
    ExactOrnnSkillReferenceHttpResponse ExactReference,
    string ContentSha256);

internal sealed record AgentProfileValidationHttpResponse(
    bool Valid,
    long DraftRevision,
    string DraftDigest,
    IReadOnlyList<AgentProfileSafeDiagnosticHttpResponse> Diagnostics,
    IReadOnlyList<AgentProfileResolvedSkillHttpResponse> ResolvedSkills);

internal sealed record AgentProfileDiscoveryHttpResponse(
    AgentProfileReferenceHttpResponse Reference,
    string DisplayName,
    string Purpose,
    long PublishedRevision,
    bool Available);

internal sealed record AgentProfileHttpErrorResponse(string Code);

internal sealed class AgentProfileSkillActivationModeJsonConverter
    : JsonConverter<AgentProfileSkillActivationMode>
{
    public override AgentProfileSkillActivationMode Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("Agent Profile skill activation mode must be a string.");

        return reader.GetString() switch
        {
            "UNSPECIFIED" => AgentProfileSkillActivationMode.Unspecified,
            "ALWAYS" => AgentProfileSkillActivationMode.Always,
            "ROUTED" => AgentProfileSkillActivationMode.Routed,
            "DEFAULT_FOR_UNMATCHED_TURN" => AgentProfileSkillActivationMode.DefaultForUnmatchedTurn,
            _ => throw new JsonException("Invalid Agent Profile skill activation mode."),
        };
    }

    public override void Write(
        Utf8JsonWriter writer,
        AgentProfileSkillActivationMode value,
        JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            AgentProfileSkillActivationMode.Unspecified => "UNSPECIFIED",
            AgentProfileSkillActivationMode.Always => "ALWAYS",
            AgentProfileSkillActivationMode.Routed => "ROUTED",
            AgentProfileSkillActivationMode.DefaultForUnmatchedTurn => "DEFAULT_FOR_UNMATCHED_TURN",
            _ => throw new JsonException("Invalid Agent Profile skill activation mode."),
        });
}

internal sealed class AgentProfileToolPolicyModeJsonConverter
    : JsonConverter<AgentProfileToolPolicyMode>
{
    public override AgentProfileToolPolicyMode Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("Agent Profile tool policy mode must be a string.");

        return reader.GetString() switch
        {
            "UNSPECIFIED" => AgentProfileToolPolicyMode.Unspecified,
            "INHERIT_ROUTE_MAXIMUM" => AgentProfileToolPolicyMode.InheritRouteMaximum,
            "EXPLICIT_ALLOWLIST" => AgentProfileToolPolicyMode.ExplicitAllowlist,
            _ => throw new JsonException("Invalid Agent Profile tool policy mode."),
        };
    }

    public override void Write(
        Utf8JsonWriter writer,
        AgentProfileToolPolicyMode value,
        JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            AgentProfileToolPolicyMode.Unspecified => "UNSPECIFIED",
            AgentProfileToolPolicyMode.InheritRouteMaximum => "INHERIT_ROUTE_MAXIMUM",
            AgentProfileToolPolicyMode.ExplicitAllowlist => "EXPLICIT_ALLOWLIST",
            _ => throw new JsonException("Invalid Agent Profile tool policy mode."),
        });
}

internal sealed class AgentProfileSkillSideEffectClassJsonConverter
    : JsonConverter<AgentProfileSkillSideEffectClass>
{
    public override AgentProfileSkillSideEffectClass Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String
            ? reader.GetString() switch
            {
                "UNSPECIFIED" => AgentProfileSkillSideEffectClass.Unspecified,
                "READ_ONLY" => AgentProfileSkillSideEffectClass.ReadOnly,
                "EXTERNAL_HANDOFF" => AgentProfileSkillSideEffectClass.ExternalHandoff,
                "SERVICE_CALL" => AgentProfileSkillSideEffectClass.ServiceCall,
                "MAINTENANCE" => AgentProfileSkillSideEffectClass.Maintenance,
                _ => throw new JsonException(),
            }
            : throw new JsonException();

    public override void Write(
        Utf8JsonWriter writer,
        AgentProfileSkillSideEffectClass value,
        JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            AgentProfileSkillSideEffectClass.Unspecified => "UNSPECIFIED",
            AgentProfileSkillSideEffectClass.ReadOnly => "READ_ONLY",
            AgentProfileSkillSideEffectClass.ExternalHandoff => "EXTERNAL_HANDOFF",
            AgentProfileSkillSideEffectClass.ServiceCall => "SERVICE_CALL",
            AgentProfileSkillSideEffectClass.Maintenance => "MAINTENANCE",
            _ => throw new JsonException(),
        });
}
