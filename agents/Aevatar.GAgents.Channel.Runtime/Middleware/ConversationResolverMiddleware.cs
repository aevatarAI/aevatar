using Aevatar.GAgents.Channel.Abstractions;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.Runtime;

/// <summary>
/// Resolves the conversation grain key from <see cref="ChatActivity.Conversation"/> and validates
/// that the canonical key is non-empty before the turn reaches the bot. Per RFC §5.7 this keeps
/// ConversationReference routing decisions in middleware rather than the bot implementation.
/// </summary>
public sealed class ConversationResolverMiddleware : IChannelMiddleware
{
    private readonly ILogger<ConversationResolverMiddleware> _logger;

    /// <summary>
    /// Creates one resolver middleware.
    /// </summary>
    public ConversationResolverMiddleware(ILogger<ConversationResolverMiddleware> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public Task InvokeAsync(ITurnContext context, Func<Task> next, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var conversation = context.Activity?.Conversation;
        if (conversation is null || string.IsNullOrWhiteSpace(conversation.CanonicalKey))
        {
            _logger.LogWarning(
                "Dropping activity {ActivityId}: missing or empty conversation canonical key",
                context.Activity?.Id);
            return Task.CompletedTask;
        }

        return next();
    }
}
