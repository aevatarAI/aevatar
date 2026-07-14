using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aevatar.GAgents.Scheduled;

internal sealed record ScheduledSkillReference(string Name)
{
    private static readonly Regex VersionPattern = new(@"^\d+\.\d+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static ScheduledSkillReferenceParseResult Parse(string? value)
    {
        var trimmed = Normalize(value);
        if (trimmed is null)
            return ScheduledSkillReferenceParseResult.ValidationError("skill_ref is required");

        var atIndex = trimmed.LastIndexOf('@');
        if (atIndex >= 0)
        {
            var name = trimmed[..atIndex].Trim();
            var version = trimmed[(atIndex + 1)..].Trim();
            if (name.Length == 0 || version.Length == 0)
                return ScheduledSkillReferenceParseResult.ValidationError("skill_ref version syntax is invalid");

            if (VersionPattern.IsMatch(version))
                return ScheduledSkillReferenceParseResult.VersionedUnsupported(trimmed);

            return ScheduledSkillReferenceParseResult.ValidationError("skill_ref version syntax is invalid");
        }

        return new ScheduledSkillReferenceParseResult(new ScheduledSkillReference(trimmed), null, null);
    }

    private static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}

internal sealed record ScheduledSkillReferenceParseResult(
    ScheduledSkillReference? Reference,
    string? Error,
    string? ErrorJson)
{
    public static ScheduledSkillReferenceParseResult ValidationError(string error) =>
        new(null, error, JsonSerializer.Serialize(new { error = "validation_error", detail = error }));

    public static ScheduledSkillReferenceParseResult VersionedUnsupported(string skillRef) =>
        new(
            null,
            "versioned_skill_ref_not_supported_yet",
            JsonSerializer.Serialize(new
            {
                error = "versioned_skill_ref_not_supported_yet",
                skill_ref = skillRef,
            }));
}
