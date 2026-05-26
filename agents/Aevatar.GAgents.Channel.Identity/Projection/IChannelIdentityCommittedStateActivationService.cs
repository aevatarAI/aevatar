namespace Aevatar.GAgents.Channel.Identity;

internal interface IChannelIdentityCommittedStateActivationService
{
    Task EnsureExternalIdentityCommittedStateActivatedAsync(
        string actorId,
        ExternalIdentityBindingState state,
        long stateVersion,
        CancellationToken ct = default);

    Task EnsureAevatarOAuthClientCommittedStateActivatedAsync(
        string actorId,
        AevatarOAuthClientState state,
        long stateVersion,
        CancellationToken ct = default);
}
