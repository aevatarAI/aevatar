using Aevatar.GAgents.Channel.Abstractions;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.Runtime;

/// <summary>
/// Structured inbound-audit middleware. Logs activity id + conversation key + bot at
/// <see cref="LogLevel.Information"/> so dashboards can correlate with tracing spans.
/// </summary>
public sealed class LoggingMiddleware : IChannelMiddleware
{
    private readonly ILogger<LoggingMiddleware> _logger;

    /// <summary>
    /// Creates one logging middleware.
    /// </summary>
    public LoggingMiddleware(ILogger<LoggingMiddleware> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task InvokeAsync(ITurnContext context, Func<Task> next, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var activity = context.Activity;
        _logger.LogInformation(
            "Channel inbound: activity={ActivityId} conversation={Key} bot={Bot} type={Type}",
            activity?.Id,
            activity?.Conversation?.CanonicalKey,
            activity?.Bot?.Value,
            activity?.Type);

        try
        {
            await next();
        }
        catch (Exception ex) when (LogAndRethrow(ex, activity))
        {
            // Unreachable — LogAndRethrow always returns false so the throw propagates.
            throw;
        }
    }

    private bool LogAndRethrow(Exception ex, ChatActivity? activity)
    {
        _logger.LogWarning(
            ex,
            "Channel inbound pipeline threw for activity={ActivityId} conversation={Key}",
            activity?.Id,
            activity?.Conversation?.CanonicalKey);
        return false;
    }
}
