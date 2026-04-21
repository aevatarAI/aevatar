namespace Aevatar.GAgents.Channel.Abstractions;

/// <summary>
/// Handles one normalized inbound activity within the channel bot pipeline.
/// </summary>
public interface IBot
{
    /// <summary>
    /// Processes the current activity using the turn-scoped outbound helpers exposed by <paramref name="context"/>.
    /// </summary>
    Task OnActivityAsync(ITurnContext context, CancellationToken ct);
}
