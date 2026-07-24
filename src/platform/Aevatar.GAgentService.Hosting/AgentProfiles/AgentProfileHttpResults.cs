using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgentService.Application.AgentProfiles;
using Google.Protobuf;
using Microsoft.AspNetCore.Http;

namespace Aevatar.GAgentService.Hosting.AgentProfiles;

internal static class AgentProfileHttpResults
{
    public static IResult Accepted(
        AgentProfileAcceptedReceipt receipt,
        string scopeId,
        string profileSlug)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var expectedReceiptResourceUrl = $"/api/scopes/{scopeId}/agent-profiles/{profileSlug}";
        var externalResourceUrl =
            $"/api/scopes/{Uri.EscapeDataString(scopeId)}/agent-profiles/{Uri.EscapeDataString(profileSlug)}";
        if (!receipt.Accepted ||
            !string.Equals(receipt.AckStage, "accepted", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(receipt.OperationId) ||
            string.IsNullOrWhiteSpace(receipt.CommandId) ||
            string.IsNullOrWhiteSpace(receipt.CorrelationId) ||
            string.IsNullOrWhiteSpace(receipt.ActorId) ||
            string.IsNullOrWhiteSpace(receipt.ProfileId) ||
            !string.Equals(receipt.ResourceUrl, expectedReceiptResourceUrl, StringComparison.Ordinal))
        {
            return Error(
                StatusCodes.Status503ServiceUnavailable,
                "AGENT_PROFILE_DISPATCH_REJECTED");
        }

        return Results.Accepted(
            externalResourceUrl,
            new AgentProfileAcceptedHttpResponse(
                receipt.Accepted,
                receipt.AckStage,
                receipt.OperationId,
                receipt.CommandId,
                receipt.CorrelationId,
                receipt.ActorId,
                receipt.ProfileId,
                externalResourceUrl));
    }

    public static IResult Management(
        HttpContext http,
        AgentProfileManagementSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(snapshot);
        http.Response.Headers.ETag = AgentProfileHttpPreconditions.Format(snapshot.AuthorityStateVersion);
        return Results.Ok(MapManagement(snapshot));
    }

    public static IResult Validation(AgentProfileValidationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return Results.Ok(new AgentProfileValidationHttpResponse(
            report.Valid,
            report.DraftRevision,
            Digest(report.DraftSha256),
            report.Diagnostics.Select(MapDiagnostic).ToArray(),
            report.ResolvedSkills.Select(static skill =>
                new AgentProfileResolvedSkillHttpResponse(
                    skill.BindingId,
                    MapExactReference(skill.ExactReference),
                    Digest(skill.ContentSha256))).ToArray()));
    }

    public static IResult Discovery(AgentProfileDiscoverySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return Results.Ok(new AgentProfileDiscoveryHttpResponse(
            MapReference(snapshot.Reference),
            snapshot.DisplayName,
            snapshot.Purpose,
            snapshot.PublishedRevision,
            snapshot.Available));
    }

    public static IResult Error(int statusCode, string code) =>
        Results.Json(new AgentProfileHttpErrorResponse(code), statusCode: statusCode);

    public static bool TryMapException(Exception exception, out IResult result)
    {
        switch (exception)
        {
            case AgentProfileRequestException request:
                result = Error(StatusCodes.Status400BadRequest, request.Code);
                return true;
            case AgentProfileNotFoundException notFound:
                result = Error(StatusCodes.Status404NotFound, notFound.Code);
                return true;
            case AgentProfilePreconditionException stale:
                result = Error(StatusCodes.Status412PreconditionFailed, stale.Code);
                return true;
            case AgentProfileAuthenticationRequiredException authentication:
                result = Error(StatusCodes.Status401Unauthorized, authentication.Code);
                return true;
            case AgentProfilePublishValidationException validation:
                result = Error(StatusCodes.Status422UnprocessableEntity, validation.Code);
                return true;
            case AgentProfileDependencyUnavailableException unavailable:
                result = Error(StatusCodes.Status503ServiceUnavailable, unavailable.Code);
                return true;
            case AgentProfileIngressProofUnavailableException unavailable:
                result = Error(StatusCodes.Status503ServiceUnavailable, unavailable.Code);
                return true;
            case AgentProfileDispatchRejectedException rejected:
                result = Error(StatusCodes.Status503ServiceUnavailable, rejected.Code);
                return true;
            default:
                result = Results.Empty;
                return false;
        }
    }

    private static AgentProfileManagementHttpResponse MapManagement(
        AgentProfileManagementSnapshot snapshot) =>
        new(
            snapshot.AuthorityStateVersion,
            snapshot.ProfileId,
            MapReference(snapshot.Identity.Reference),
            MapContent(snapshot.Draft),
            snapshot.DraftRevision,
            Digest(snapshot.DraftSha256),
            snapshot.PublishedRevision,
            Digest(snapshot.PublishedSnapshotSha256),
            Digest(snapshot.PublishedSourceDraftSha256),
            snapshot.LastMutation is null ? null : MapMutation(snapshot.LastMutation));

    private static AgentProfileContentHttpResponse MapContent(AgentProfileContent content)
    {
        var policy = content.ToolPolicy ?? new AgentProfileToolPolicy();
        return new AgentProfileContentHttpResponse(
            content.DisplayName,
            content.Purpose,
            content.Instructions,
            content.SkillBindings.Select(static binding =>
                new AgentProfileSkillBindingHttpResponse(
                    binding.BindingId,
                    ActivationMode(binding.ActivationMode),
                    MapExactReference(binding.Skill))).ToArray(),
            new AgentProfileToolPolicyHttpResponse(
                ToolPolicyMode(policy.Mode),
                policy.ToolNames.ToArray(),
                policy.ToolSetRefs.ToArray()));
    }

    private static AgentProfileMutationHttpResponse MapMutation(
        AgentProfileMutationOutcome mutation)
    {
        var operation = mutation.Operation ?? new AgentProfileOperationFact();
        return new AgentProfileMutationHttpResponse(
            operation.OperationId,
            operation.CommandId,
            operation.CorrelationId,
            MutationStatus(mutation.Status),
            mutation.Diagnostic is null ? null : MapDiagnostic(mutation.Diagnostic),
            mutation.DraftRevision,
            Digest(mutation.DraftSha256),
            mutation.PublishedRevision,
            Digest(mutation.PublishedSnapshotSha256));
    }

    private static AgentProfileReferenceHttpResponse MapReference(AgentProfileReference reference) =>
        new(reference?.OwnerHandle ?? string.Empty, reference?.ProfileSlug ?? string.Empty);

    private static ExactOrnnSkillReferenceHttpResponse MapExactReference(
        ExactOrnnSkillReference reference) =>
        new(
            reference?.SkillGuid ?? string.Empty,
            reference?.LiteralVersion ?? string.Empty,
            reference?.ExpectedName ?? string.Empty,
            reference?.ExpectedPublisherId ?? string.Empty);

    private static AgentProfileSafeDiagnosticHttpResponse MapDiagnostic(
        AgentProfileSafeDiagnostic diagnostic) =>
        new(diagnostic.Code, diagnostic.Message, diagnostic.Path);

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
}
