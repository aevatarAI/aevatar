using System.Text.RegularExpressions;
using Aevatar.AI.Abstractions;
using Aevatar.GAgentService.Abstractions.AgentProfiles;

namespace Aevatar.AI.ToolProviders.Ornn;

/// <summary>
/// Profile-only Ornn resolver: it reads a GUID and literal version exactly and returns no raw upstream payload.
/// </summary>
public sealed partial class OrnnExactAgentProfileSkillResolver : IExactOrnnSkillResolver
{
    private readonly OrnnSkillClient _client;

    public OrnnExactAgentProfileSkillResolver(OrnnSkillClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<ExactOrnnSkillResolutionResult> ResolveAsync(
        string nyxIdAccessToken,
        ExactRemoteSkillRef reference,
        CancellationToken ct = default)
    {
        if (reference is null || !IsCanonicalGuid(reference.Guid) ||
            !LiteralVersionPattern().IsMatch(reference.LiteralVersion))
        {
            return ExactOrnnSkillResolutionResult.Failure("ORNN_SKILL_INVALID_REFERENCE");
        }
        if (string.IsNullOrWhiteSpace(nyxIdAccessToken))
            return ExactOrnnSkillResolutionResult.Failure("ORNN_DEPENDENCY_UNAVAILABLE");

        try
        {
            var detailRead = await _client.GetExactSkillDetailAsync(
                nyxIdAccessToken, reference.Guid, reference.LiteralVersion, ct);
            var detailFailure = MapReadFailure(detailRead);
            if (detailFailure is not null)
                return detailFailure;

            var skillJsonRead = await _client.GetExactSkillJsonAsync(
                nyxIdAccessToken, reference.Guid, reference.LiteralVersion, ct);
            var skillJsonFailure = MapReadFailure(skillJsonRead);
            if (skillJsonFailure is not null)
                return skillJsonFailure;

            return OrnnAgentProfileSkillPackageMapper.Map(
                reference.Guid,
                reference.LiteralVersion,
                detailRead.Value,
                skillJsonRead.Value);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return ExactOrnnSkillResolutionResult.Failure("ORNN_DEPENDENCY_UNAVAILABLE");
        }
        catch
        {
            return ExactOrnnSkillResolutionResult.Failure("ORNN_DEPENDENCY_UNAVAILABLE");
        }
    }

    private static ExactOrnnSkillResolutionResult? MapReadFailure<T>(OrnnExactSkillReadResult<T> read)
        where T : class =>
        read.ProxyStatus switch
        {
            403 => ExactOrnnSkillResolutionResult.Failure("ORNN_SKILL_ACCESS_DENIED"),
            404 => ExactOrnnSkillResolutionResult.Failure("ORNN_SKILL_NOT_FOUND"),
            null => null,
            _ => ExactOrnnSkillResolutionResult.Failure("ORNN_DEPENDENCY_UNAVAILABLE"),
        };

    private static bool IsCanonicalGuid(string? value) =>
        Guid.TryParseExact(value, "D", out var parsed) && parsed != Guid.Empty &&
        string.Equals(parsed.ToString("D"), value, StringComparison.Ordinal);

    [GeneratedRegex("^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)$", RegexOptions.CultureInvariant)]
    private static partial Regex LiteralVersionPattern();
}
