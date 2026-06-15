using Aevatar.Studio.Application.Studio.Contracts;

namespace Aevatar.Studio.Application.Studio.Services;

/// <summary>
/// Application-layer enforcement of <see cref="StudioMemberInputLimits"/>.
/// Lives next to <see cref="StudioMemberService"/> so a swap of the
/// projection-side command port (alternate impl, in-memory test variant,
/// migration tool) cannot silently drop the bounds with it.
///
/// The Projection-layer command service is intentionally lenient on these
/// fields — it trusts the caller already validated. The Application layer
/// is the single boundary where length caps + slug pattern are
/// enforced.
/// </summary>
internal static class StudioMemberCreateRequestValidator
{
    public static void Validate(CreateStudioMemberRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        ValidateDisplayName(request.DisplayName);
        ValidateDescription(request.Description);
        ValidateMemberId(request.MemberId);
        ValidateTeamId(request.TeamId);
    }

    private static void ValidateDisplayName(string? displayName)
    {
        var trimmed = displayName?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new InvalidOperationException(
                "displayName is required when creating a member.");
        }

        if (trimmed.Length > StudioMemberInputLimits.MaxDisplayNameLength)
        {
            throw new InvalidOperationException(
                $"displayName must be at most {StudioMemberInputLimits.MaxDisplayNameLength} characters.");
        }
    }

    private static void ValidateDescription(string? description)
    {
        var trimmed = description?.Trim() ?? string.Empty;
        if (trimmed.Length > StudioMemberInputLimits.MaxDescriptionLength)
        {
            throw new InvalidOperationException(
                $"description must be at most {StudioMemberInputLimits.MaxDescriptionLength} characters.");
        }
    }

    private static void ValidateMemberId(string? rawMemberId)
    {
        if (string.IsNullOrWhiteSpace(rawMemberId))
        {
            // Empty is allowed — the command service generates a random
            // member id when the caller leaves it empty.
            return;
        }

        var trimmed = rawMemberId.Trim();
        if (trimmed.Length > StudioMemberInputLimits.MaxMemberIdLength)
        {
            throw new InvalidOperationException(
                $"memberId must be at most {StudioMemberInputLimits.MaxMemberIdLength} characters.");
        }

        if (!StudioMemberInputLimits.MemberIdPattern.IsMatch(trimmed))
        {
            throw new InvalidOperationException(
                "memberId must match ^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$ " +
                "(alphanumeric, dash, underscore; starts with alphanumeric).");
        }
    }

    private static void ValidateTeamId(string? rawTeamId)
    {
        if (rawTeamId == null)
        {
            // Absent / null = "do not assign". This is the typical case — a
            // member can be created without joining any team.
            return;
        }

        // Empty string is rejected (ADR-0017 §Q6). The wire layer should
        // already have caught it; defending here so a hand-crafted client
        // cannot silently bypass the rule via the application port.
        if (rawTeamId.Length == 0)
        {
            throw new InvalidOperationException(
                "teamId must not be empty when present (use null / absent to mean 'do not assign').");
        }

        var trimmed = rawTeamId.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new InvalidOperationException(
                "teamId must not be whitespace when present.");
        }

        if (trimmed.Length > StudioTeamInputLimits.MaxTeamIdLength)
        {
            throw new InvalidOperationException(
                $"teamId must be at most {StudioTeamInputLimits.MaxTeamIdLength} characters.");
        }

        if (!StudioTeamInputLimits.TeamIdPattern.IsMatch(trimmed))
        {
            throw new InvalidOperationException(
                "teamId must match ^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$ " +
                "(alphanumeric, dash, underscore; starts with alphanumeric).");
        }
    }
}
