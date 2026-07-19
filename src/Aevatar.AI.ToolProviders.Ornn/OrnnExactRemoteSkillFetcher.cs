using System.Text.RegularExpressions;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Core.AgentProfiles;

namespace Aevatar.AI.ToolProviders.Ornn;

public sealed partial class OrnnExactRemoteSkillFetcher : IExactRemoteSkillFetcher
{
    private readonly OrnnSkillClient _client;

    public OrnnExactRemoteSkillFetcher(OrnnSkillClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<ExactRemoteSkillFetchResult> FetchAsync(
        string accessToken,
        ExactRemoteSkillRef skillRef,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(skillRef);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return ExactRemoteSkillFetchResult.Failed(
                ExactRemoteSkillFetchFailureCode.AccessTokenMissing);
        }
        if (!IsCanonicalGuid(skillRef.Guid) || !LiteralVersionPattern().IsMatch(skillRef.LiteralVersion))
        {
            return ExactRemoteSkillFetchResult.Failed(
                ExactRemoteSkillFetchFailureCode.InvalidReference);
        }

        try
        {
            var detail = await _client.GetExactSkillDetailAsync(
                accessToken,
                skillRef.Guid,
                skillRef.LiteralVersion,
                ct);
            var skillJson = await _client.GetExactSkillJsonAsync(
                accessToken,
                skillRef.Guid,
                skillRef.LiteralVersion,
                ct);
            if (detail is null || skillJson is null)
            {
                return ExactRemoteSkillFetchResult.Failed(
                    ExactRemoteSkillFetchFailureCode.InvalidResponse);
            }

            if (!string.Equals(detail.Guid, skillRef.Guid, StringComparison.Ordinal) ||
                !string.Equals(skillJson.Version, skillRef.LiteralVersion, StringComparison.Ordinal) ||
                !string.Equals(detail.Name, skillJson.Name, StringComparison.Ordinal))
            {
                return ExactRemoteSkillFetchResult.Failed(
                    ExactRemoteSkillFetchFailureCode.IdentityMismatch);
            }
            if (string.IsNullOrWhiteSpace(detail.Name) ||
                string.IsNullOrWhiteSpace(detail.CreatedBy) ||
                string.IsNullOrWhiteSpace(detail.SkillHash))
            {
                return ExactRemoteSkillFetchResult.Failed(
                    ExactRemoteSkillFetchFailureCode.IntegrityEvidenceMissing);
            }

            var skillMarkdownEntries = (skillJson.Files ?? [])
                .Where(static entry => string.Equals(entry.Key, "SKILL.md", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (skillMarkdownEntries.Length != 1 || string.IsNullOrWhiteSpace(skillMarkdownEntries[0].Value))
            {
                return ExactRemoteSkillFetchResult.Failed(
                    ExactRemoteSkillFetchFailureCode.InvalidResponse,
                    "unique_skill_markdown_required");
            }

            return ExactRemoteSkillFetchResult.Success(
                skillRef.Guid,
                skillRef.LiteralVersion,
                detail.Name,
                detail.CreatedBy,
                detail.SkillHash,
                skillMarkdownEntries[0].Value);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ExactRemoteSkillFetchResult.Failed(ExactRemoteSkillFetchFailureCode.Timeout);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ExactRemoteSkillFetchResult.Failed(
                ExactRemoteSkillFetchFailureCode.Failed,
                ex.GetType().Name);
        }
    }

    private static bool IsCanonicalGuid(string? value) =>
        Guid.TryParseExact(value, "D", out var parsed) &&
        parsed != Guid.Empty &&
        string.Equals(parsed.ToString("D"), value, StringComparison.Ordinal);

    [GeneratedRegex("^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)$", RegexOptions.CultureInvariant)]
    private static partial Regex LiteralVersionPattern();
}
