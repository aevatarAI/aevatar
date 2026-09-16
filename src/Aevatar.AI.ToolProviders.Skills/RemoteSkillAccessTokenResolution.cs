namespace Aevatar.AI.ToolProviders.Skills;

/// <summary>
/// Describes why a request-local remote-skill credential could not be resolved.
/// </summary>
public enum RemoteSkillAccessTokenFailureKind
{
    None = 0,
    ChannelBindingRequired = 1,
    ChannelBindingRefreshRequired = 2,
    Unavailable = 3,
}

/// <summary>
/// Strongly typed result of resolving one request-local remote-skill credential.
/// The access token is transient and must not be persisted or cached.
/// </summary>
public sealed class RemoteSkillAccessTokenResolution
{
    private RemoteSkillAccessTokenResolution(
        string? accessToken,
        RemoteSkillAccessTokenFailureKind failureKind)
    {
        AccessToken = accessToken;
        FailureKind = failureKind;
    }

    public string? AccessToken { get; }

    public RemoteSkillAccessTokenFailureKind FailureKind { get; }

    public bool Succeeded => AccessToken is not null;

    public static RemoteSkillAccessTokenResolution Resolved(string accessToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        return new RemoteSkillAccessTokenResolution(
            accessToken.Trim(),
            RemoteSkillAccessTokenFailureKind.None);
    }

    public static RemoteSkillAccessTokenResolution Failed(
        RemoteSkillAccessTokenFailureKind failureKind)
    {
        if (failureKind == RemoteSkillAccessTokenFailureKind.None)
            throw new ArgumentOutOfRangeException(nameof(failureKind));

        return new RemoteSkillAccessTokenResolution(null, failureKind);
    }

    public static RemoteSkillAccessTokenResolution FromAccessToken(
        string? accessToken,
        RemoteSkillAccessTokenFailureKind failureKind = RemoteSkillAccessTokenFailureKind.Unavailable) =>
        string.IsNullOrWhiteSpace(accessToken)
            ? Failed(failureKind)
            : Resolved(accessToken);
}
