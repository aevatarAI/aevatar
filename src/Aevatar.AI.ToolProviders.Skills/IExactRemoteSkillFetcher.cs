using Aevatar.AI.Abstractions;

namespace Aevatar.AI.ToolProviders.Skills;

/// <summary>Fetches immutable remote releases by stable GUID and literal version.</summary>
public interface IExactRemoteSkillFetcher
{
    Task<ExactRemoteSkillRelease> FetchExactSkillAsync(
        string accessToken,
        ExactRemoteSkillRef reference,
        CancellationToken ct = default);

    Task<ExactRemoteSkillsetRelease> FetchExactSkillsetAsync(
        string accessToken,
        ExactRemoteSkillsetRef reference,
        CancellationToken ct = default);
}
