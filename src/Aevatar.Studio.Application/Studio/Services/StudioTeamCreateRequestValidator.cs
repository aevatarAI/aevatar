using Aevatar.Studio.Application.Studio.Contracts;

namespace Aevatar.Studio.Application.Studio.Services;

/// <summary>
/// Validates <see cref="CreateStudioTeamRequest"/> input at the Application
/// boundary. Mirrors <see cref="StudioMemberCreateRequestValidator"/> in
/// shape — single static <c>Validate</c> method, throws
/// <see cref="InvalidOperationException"/> on the first failure.
/// </summary>
public static class StudioTeamCreateRequestValidator
{
    public static void Validate(CreateStudioTeamRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        ValidateDisplayName(request.DisplayName);
        ValidateDescription(request.Description);
        ValidateTeamId(request.TeamId);
    }

    private static void ValidateDisplayName(string? rawDisplayName)
    {
        var trimmed = rawDisplayName?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            throw new InvalidOperationException("displayName is required.");
        if (trimmed.Length > StudioTeamInputLimits.MaxDisplayNameLength)
            throw new InvalidOperationException(
                $"displayName must be at most {StudioTeamInputLimits.MaxDisplayNameLength} characters.");
    }

    private static void ValidateDescription(string? rawDescription)
    {
        if (rawDescription == null)
            return;

        if (rawDescription.Length > StudioTeamInputLimits.MaxDescriptionLength)
            throw new InvalidOperationException(
                $"description must be at most {StudioTeamInputLimits.MaxDescriptionLength} characters.");
    }

    private static void ValidateTeamId(string? rawTeamId)
    {
        if (string.IsNullOrWhiteSpace(rawTeamId))
            return;

        var trimmed = rawTeamId.Trim();
        if (trimmed.Length > StudioTeamInputLimits.MaxTeamIdLength)
            throw new InvalidOperationException(
                $"teamId must be at most {StudioTeamInputLimits.MaxTeamIdLength} characters.");

        if (!StudioTeamInputLimits.TeamIdPattern.IsMatch(trimmed))
            throw new InvalidOperationException(
                "teamId must match ^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$ " +
                "(alphanumeric, dash, underscore; starts with alphanumeric).");
    }
}
