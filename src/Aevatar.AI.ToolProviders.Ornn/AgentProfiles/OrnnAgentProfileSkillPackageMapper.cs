using System.Text;
using Aevatar.GAgentService.Abstractions.AgentProfiles;

namespace Aevatar.AI.ToolProviders.Ornn;

internal static class OrnnAgentProfileSkillPackageMapper
{
    internal static ExactOrnnSkillResolutionResult Map(
        string requestedGuid,
        string requestedLiteralVersion,
        OrnnExactSkillDetail? detail,
        OrnnSkillJson? skillJson)
    {
        if (detail is null || skillJson is null)
            return ExactOrnnSkillResolutionResult.Failure("ORNN_SKILL_NOT_FOUND");

        if (!string.Equals(detail.Guid, requestedGuid, StringComparison.Ordinal) ||
            !string.Equals(skillJson.Version, requestedLiteralVersion, StringComparison.Ordinal) ||
            !string.Equals(detail.Name, skillJson.Name, StringComparison.Ordinal))
        {
            return ExactOrnnSkillResolutionResult.Failure("ORNN_SKILL_IDENTITY_MISMATCH");
        }

        if (string.IsNullOrWhiteSpace(detail.Name) ||
            string.IsNullOrWhiteSpace(detail.CreatedBy) ||
            !OrnnSkillSha256Parser.TryParse(detail.SkillHash, out var skillSha256))
        {
            return ExactOrnnSkillResolutionResult.Failure("ORNN_SKILL_INTEGRITY_EVIDENCE_MISSING");
        }

        var skillMarkdownEntries = (skillJson.Files ?? [])
            .Where(static entry => string.Equals(
                Path.GetFileName(entry.Key),
                "SKILL.md",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (skillMarkdownEntries.Length != 1 || string.IsNullOrWhiteSpace(skillMarkdownEntries[0].Value))
            return ExactOrnnSkillResolutionResult.Failure("INVALID_SKILL_PACKAGE");
        var skillMarkdownUtf8Bytes = Encoding.UTF8.GetByteCount(skillMarkdownEntries[0].Value);

        var declaredTools = skillJson.Metadata?.Tools?
            .Select(static declaration => declaration.Tool?.Trim() ?? string.Empty)
            .Where(static name => name.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray() ?? [];

        return ExactOrnnSkillResolutionResult.Success(new ResolvedOrnnSkillPackage
        {
            SkillGuid = detail.Guid!,
            LiteralVersion = skillJson.Version!,
            CanonicalName = detail.Name!,
            PublisherId = detail.CreatedBy!,
            SkillSha256 = skillSha256,
            SkillMarkdownUtf8Bytes = skillMarkdownUtf8Bytes,
            DeclaredToolNames = declaredTools,
        });
    }
}
