namespace Aevatar.GAgents.Channel.Testing;

/// <summary>
/// Adapter-supplied probe that drives the reaction surface for conformance tests.
/// </summary>
/// <remarks>
/// Reactions are not part of <see cref="IChannelOutboundPort"/>; when the channel supports them, the adapter exposes
/// an adapter-specific entry point. The conformance suite delegates to this probe so <c>SupportsReactions</c> adapters
/// can be exercised end-to-end without the base class needing to know the adapter's reaction API shape.
/// </remarks>
public abstract class ReactionProbe
{
    /// <summary>
    /// Adds a reaction to one previously emitted activity and returns whether the platform acknowledged the add.
    /// </summary>
    public abstract Task<bool> AddAsync(string activityId, string reaction, CancellationToken ct = default);

    /// <summary>
    /// Removes a reaction previously added to the activity and returns whether the platform acknowledged the removal.
    /// </summary>
    public abstract Task<bool> RemoveAsync(string activityId, string reaction, CancellationToken ct = default);
}
