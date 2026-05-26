namespace Aevatar.GAgents.Channel.Abstractions;

/// <summary>
/// Handles one normalized inbound activity within the channel bot pipeline.
/// </summary>
/// <remarks>
/// Per-conversation ordering is supplied by the conversation actor that invokes this contract. Bot implementations should
/// therefore assume turn-serial execution within one conversation and keep channel-specific transport details behind
/// <see cref="ITurnContext"/>.
/// </remarks>
public interface IBot
{
    /// <summary>
     /// Processes the current activity using the turn-scoped outbound helpers exposed by <paramref name="context"/>.
    /// </summary>
    /// <param name="context">The turn-scoped activity and outbound helpers for the current conversation turn.</param>
    /// <param name="ct">A token that cancels turn processing.</param>
    /// <returns>A task that completes when the bot has finished processing the inbound activity.</returns>
    Task OnActivityAsync(ITurnContext context, CancellationToken ct);
}
